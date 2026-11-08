using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Intersect.Abstractions;
using Intersect.Common;
using Intersect.Contracts;
using Intersect.Domain;
using Intersect.Nostr;
using Microsoft.Extensions.Options;

namespace Intersect.Services;

public sealed partial class AuthService(
    IUserRepository users,
    INostrChallengeStore challenges,
    IUnitOfWork uow,
    ITokenService tokens,
    IPasswordHasher hasher,
    ISchnorrVerifier verifier,
    IAuditLog audit,
    IOptions<AuthOptions> options,
    TimeProvider time)
{
    private readonly AuthOptions _opt = options.Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    // ------------------------------------------------------------ email accounts

    public async Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
        var email = NormalizeEmail(req.Email);
        ValidatePassword(req.Password);
        var name = ValidateDisplayName(req.DisplayName);

        if (await users.GetByEmailAsync(email, ct) is not null)
            throw AppException.Conflict("email_taken", "An account with this email already exists.");

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            DisplayName = name,
            Email = email,
            PasswordHash = hasher.Hash(req.Password),
            CreatedAt = time.GetUtcNow(),
            Status = UserStatus.Active,
        };
        users.Add(user);
        audit.Add(Audit("user.register", user.Id));
        await uow.SaveChangesAsync(ct);
        return Issue(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await users.GetByEmailAsync(email, ct);
        var ok = hasher.Verify(req.Password ?? "", user?.PasswordHash);
        if (user is null || !ok)
            throw AppException.Unauthorized("invalid_credentials", "Invalid email or password.");
        EnsureActive(user);
        return Issue(user);
    }

    public async Task<UserDto> LinkEmailAsync(Guid userId, LinkEmailRequest req, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        if (user.Email is not null) throw AppException.Conflict("email_already_set", "This account already has an email.");
        var email = NormalizeEmail(req.Email);
        ValidatePassword(req.Password);
        if (await users.GetByEmailAsync(email, ct) is not null)
            throw AppException.Conflict("email_taken", "An account with this email already exists.");

        user.Email = email;
        user.PasswordHash = hasher.Hash(req.Password);
        audit.Add(Audit("user.link_email", user.Id));
        await uow.SaveChangesAsync(ct);
        return ToDto(user);
    }

    // ------------------------------------------------------------ nostr

    public async Task<NostrChallengeResponse> CreateChallengeAsync(
        string? publicKey, ChallengePurpose purpose, Guid? userId, CancellationToken ct = default)
    {
        if (publicKey is not null && !NostrEventValidator.IsLowerHex(publicKey, 64))
            throw AppException.Validation("invalid_pubkey", "publicKey must be 64 lowercase hex characters.");
        if (purpose == ChallengePurpose.Link && userId is null)
            throw AppException.Unauthorized("login_required", "Linking requires an authenticated user.");

        var now = time.GetUtcNow();
        var challenge = new NostrChallenge
        {
            Value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Purpose = purpose,
            BoundPublicKey = publicKey,
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(_opt.ChallengeLifetimeSeconds),
        };
        await challenges.SaveAsync(challenge, ct);
        return new NostrChallengeResponse(challenge.Value, challenge.ExpiresAt, NostrKinds.LoginAuth, _opt.ExpectedDomain);
    }

    public async Task<AuthResponse> LoginWithNostrAsync(NostrEvent? ev, CancellationToken ct = default)
    {
        await VerifyAndConsumeAsync(ev, ChallengePurpose.Login, null, ct);
        var pub = ev!.PubKey;

        var user = await users.GetByExternalIdentityAsync(ExternalIdentity.NostrProvider, pub, ct);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.CreateVersion7(),
                DisplayName = "nostr:" + pub[..8],
                PrimaryNostrPublicKey = pub,
                CreatedAt = time.GetUtcNow(),
                Status = UserStatus.Active,
            };
            users.Add(user);
            users.AddIdentity(NewIdentity(user.Id, pub));
            audit.Add(Audit("user.register_nostr", user.Id));
            await uow.SaveChangesAsync(ct);
        }
        EnsureActive(user);
        return Issue(user);
    }

    public async Task<UserDto> LinkNostrAsync(Guid userId, NostrEvent? ev, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        await VerifyAndConsumeAsync(ev, ChallengePurpose.Link, userId, ct);
        var pub = ev!.PubKey;

        var existing = await users.GetByExternalIdentityAsync(ExternalIdentity.NostrProvider, pub, ct);
        if (existing is not null)
        {
            if (existing.Id == user.Id) return ToDto(user);
            throw AppException.Conflict("identity_already_linked", "This Nostr identity belongs to another account.");
        }

        users.AddIdentity(NewIdentity(user.Id, pub));
        user.PrimaryNostrPublicKey ??= pub;
        audit.Add(Audit("user.link_nostr", user.Id));
        await uow.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task<UserDto> GetMeAsync(Guid userId, CancellationToken ct = default)
        => ToDto(await RequireUserAsync(userId, ct));

    /// <summary>
    /// The backend never trusts a public key without a valid signature. Order: cheap structural
    /// checks, signature, then atomic single-use consumption of the challenge (replay protection).
    /// </summary>
    private async Task VerifyAndConsumeAsync(NostrEvent? ev, ChallengePurpose purpose, Guid? userId, CancellationToken ct)
    {
        if (ev is null) throw AppException.Validation("invalid_event", "A signed event is required.");

        var integrity = NostrEventValidator.VerifyIntegrity(ev, verifier);
        if (integrity is not null) throw AppException.Unauthorized(integrity, "The signed event is not valid.");

        if (ev.Kind != NostrKinds.LoginAuth)
            throw AppException.Unauthorized("invalid_kind", $"Expected event kind {NostrKinds.LoginAuth}.");

        var now = time.GetUtcNow();
        var created = DateTimeOffset.FromUnixTimeSeconds(ev.CreatedAt);
        if (Math.Abs((now - created).TotalSeconds) > _opt.ClockSkewSeconds)
            throw AppException.Unauthorized("event_expired", "The event timestamp is outside the allowed window.");

        if (NostrEventValidator.CountTags(ev, "challenge") != 1)
            throw AppException.Unauthorized("challenge_missing", "Exactly one challenge tag is required.");
        var challengeValue = NostrEventValidator.GetTag(ev, "challenge")!;

        var domain = NostrEventValidator.GetTag(ev, "domain");
        if (!string.Equals(domain, _opt.ExpectedDomain, StringComparison.OrdinalIgnoreCase))
            throw AppException.Unauthorized("domain_mismatch", "The event was signed for a different domain.");

        var result = await challenges.TryConsumeAsync(challengeValue, now, ct);
        switch (result.Status)
        {
            case ChallengeConsumeStatus.Unknown: throw AppException.Unauthorized("challenge_unknown", "Unknown challenge.");
            case ChallengeConsumeStatus.Expired: throw AppException.Unauthorized("challenge_expired", "The challenge has expired.");
            case ChallengeConsumeStatus.AlreadyUsed: throw AppException.Unauthorized("challenge_replayed", "The challenge was already used.");
        }

        var ch = result.Challenge!;
        if (ch.Purpose != purpose) throw AppException.Unauthorized("challenge_purpose_mismatch", "Challenge was issued for a different purpose.");
        if (ch.BoundPublicKey is not null && !string.Equals(ch.BoundPublicKey, ev.PubKey, StringComparison.Ordinal))
            throw AppException.Unauthorized("public_key_mismatch", "The event was signed by a different key than the challenge was issued for.");
        if (purpose == ChallengePurpose.Link && ch.UserId != userId)
            throw AppException.Unauthorized("challenge_user_mismatch", "This challenge belongs to another user.");
    }

    // ------------------------------------------------------------ helpers

    private AuthResponse Issue(User user)
    {
        var (token, expires) = tokens.Issue(user);
        return new AuthResponse(token, expires, ToDto(user));
    }

    private ExternalIdentity NewIdentity(Guid userId, string pub) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Provider = ExternalIdentity.NostrProvider,
        ExternalIdentifier = pub,
        LinkedAt = time.GetUtcNow(),
    };

    private AuditLogEntry Audit(string action, Guid userId) => new()
    {
        Id = Guid.CreateVersion7(), At = time.GetUtcNow(), ActorUserId = userId, TargetUserId = userId, Action = action,
    };

    private async Task<User> RequireUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(userId, ct) ?? throw AppException.Unauthorized("unknown_user", "Account not found.");
        EnsureActive(user);
        return user;
    }

    private static void EnsureActive(User user)
    {
        if (user.Status != UserStatus.Active) throw AppException.Forbidden("account_suspended", "This account is suspended.");
    }

    private static string NormalizeEmail(string? email)
    {
        var e = (email ?? "").Trim().ToLowerInvariant();
        if (e.Length is 0 or > 254 || !EmailRegex().IsMatch(e))
            throw AppException.Validation("invalid_email", "A valid email address is required.");
        return e;
    }

    private static void ValidatePassword(string? password)
    {
        if (password is null || password.Length < 10 || password.Length > 200)
            throw AppException.Validation("weak_password", "Password must be 10 to 200 characters.");
    }

    private static string ValidateDisplayName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is < 2 or > 40) throw AppException.Validation("invalid_display_name", "Display name must be 2 to 40 characters.");
        return n;
    }

    public static UserDto ToDto(User u) => new(u.Id, u.DisplayName, u.Email, u.PrimaryNostrPublicKey, u.CreatedAt);
}
