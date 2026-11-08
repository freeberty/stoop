using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Intersect.Abstractions;
using Intersect.Common;
using Intersect.Domain;
using Intersect.Nostr;
using Intersect.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Intersect.Api.Auth;

public static class AuthDefaults
{
    public const string Scheme = "Intersect";
}

/// <summary>
/// Accepts (1) the application token as "Authorization: Bearer", also as ?access_token= for SignalR
/// WebSockets, and (2) NIP-98 "Authorization: Nostr &lt;base64 event&gt;" for already-registered Nostr users.
/// </summary>
public sealed class IntersectAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITokenService tokens,
    IUserRepository users,
    ISchnorrVerifier verifier,
    IMemoryCache replayCache,
    IOptions<AuthOptions> authOptions,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const int Nip98SkewSeconds = 60;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        string? bearer = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) bearer = header["Bearer ".Length..].Trim();
        else if (Request.Path.StartsWithSegments("/hubs") && Request.Query.TryGetValue("access_token", out var qs)) bearer = qs.ToString();

        if (bearer is not null)
        {
            var claims = tokens.Validate(bearer);
            return claims is null
                ? AuthenticateResult.Fail("Invalid or expired token.")
                : AuthenticateResult.Success(Ticket(claims.UserId, claims.DisplayName));
        }

        if (header.StartsWith("Nostr ", StringComparison.OrdinalIgnoreCase))
            return await HandleNip98Async(header["Nostr ".Length..].Trim());

        return AuthenticateResult.NoResult();
    }

    private async Task<AuthenticateResult> HandleNip98Async(string encoded)
    {
        NostrEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<NostrEvent>(Convert.FromBase64String(encoded));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return AuthenticateResult.Fail("Malformed Nostr authorization.");
        }
        if (ev is null) return AuthenticateResult.Fail("Malformed Nostr authorization.");

        var error = NostrEventValidator.ValidateHttpAuth(ev, Request.Method, Request.Path.Value ?? "/",
            Request.QueryString.Value, authOptions.Value.ExpectedDomain, time.GetUtcNow(), Nip98SkewSeconds, verifier);
        if (error is not null) return AuthenticateResult.Fail($"Invalid Nostr authorization: {error}");

        // Replay protection: each signed request may be used once within its validity window.
        if (replayCache.TryGetValue("nip98:" + ev.Id, out _)) return AuthenticateResult.Fail("Replayed Nostr authorization.");
        replayCache.Set("nip98:" + ev.Id, true, TimeSpan.FromSeconds(Nip98SkewSeconds * 2 + 5));

        var user = await users.GetByExternalIdentityAsync(ExternalIdentity.NostrProvider, ev.PubKey);
        if (user is null || user.Status != UserStatus.Active)
            return AuthenticateResult.Fail("No active account for this Nostr key. Log in first.");
        return AuthenticateResult.Success(Ticket(user.Id, user.DisplayName));
    }

    private AuthenticationTicket Ticket(Guid userId, string name)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, name)],
            AuthDefaults.Scheme);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), AuthDefaults.Scheme);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? TryGetUserId(this ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid GetUserId(this ClaimsPrincipal user)
        => user.TryGetUserId() ?? throw AppException.Unauthorized("login_required", "Authentication is required.");
}
