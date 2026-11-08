using System.Security.Cryptography;
using System.Text;
using Intersect.Abstractions;
using Intersect.Common;
using Intersect.Contracts;
using Intersect.Domain;
using Intersect.InMemory;
using Intersect.Nostr;
using Intersect.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Intersect.Tests;

public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class RecordingNotifier : IRealtimeNotifier
{
    public List<(Guid Conversation, MessageDto Message)> Created { get; } = new();
    public List<(Guid Conversation, Guid User, string Reason)> Revoked { get; } = new();
    public List<(Guid User, Guid Community, MemberStatus Status)> Membership { get; } = new();
    public List<Guid> Deleted { get; } = new();
    public List<Guid> Edited { get; } = new();

    public Task MessageCreatedAsync(Guid conversationId, MessageDto message, CancellationToken ct = default) { Created.Add((conversationId, message)); return Task.CompletedTask; }
    public Task MessageEditedAsync(Guid conversationId, MessageDto message, CancellationToken ct = default) { Edited.Add(message.Id); return Task.CompletedTask; }
    public Task MessageDeletedAsync(Guid conversationId, Guid messageId, CancellationToken ct = default) { Deleted.Add(messageId); return Task.CompletedTask; }
    public Task AccessRevokedAsync(Guid conversationId, Guid userId, string reason, CancellationToken ct = default) { Revoked.Add((conversationId, userId, reason)); return Task.CompletedTask; }
    public Task MembershipChangedAsync(Guid userId, Guid communityId, MemberStatus status, CancellationToken ct = default) { Membership.Add((userId, communityId, status)); return Task.CompletedTask; }
}

/// <summary>Wires the real services over the in-memory store with a controllable clock.</summary>
public sealed class World
{
    public const string Domain = "intersect.test";

    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    public InMemoryStore Store { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public AuthOptions AuthOpt { get; } = new() { JwtSecret = new string('s', 48), ExpectedDomain = Domain };
    public ChatOptions ChatOpt { get; } = new();

    public ITokenService Tokens { get; }
    public AuthService Auth { get; }
    public CommunityService Communities { get; }
    public DiscoveryService Discovery { get; }
    public MembershipService Membership { get; }
    public ChatService Chat { get; }
    public LifecycleService Lifecycle { get; }

    public World(Action<AuthOptions, ChatOptions>? configure = null)
    {
        configure?.Invoke(AuthOpt, ChatOpt);
        var auth = Options.Create(AuthOpt);
        var chat = Options.Create(ChatOpt);
        Tokens = new JwtTokenService(auth, Time);
        Auth = new AuthService(Store, Store, Store, Tokens, new Pbkdf2PasswordHasher(1_000), new ManagedSchnorrVerifier(), Store, auth, Time);
        Communities = new CommunityService(Store, Store, Store, Store, Store, Store, Notifier, Time);
        Discovery = new DiscoveryService(Store, Options.Create(new DiscoveryOptions()), Time);
        Membership = new MembershipService(Store, Store, Store, Store, Store, Store, Notifier, Time);
        Chat = new ChatService(Store, Store, Store, Store, Store, Store, Store, Notifier, new MessageRateLimiter(chat, Time), chat, Time);
        Lifecycle = new LifecycleService(Store, Store, Store, Membership, Store, Store, Time);
    }

    public long Now => Time.GetUtcNow().ToUnixTimeSeconds();

    // ---- users

    private int _n;

    public async Task<Guid> NewEmailUserAsync(string? name = null)
    {
        var i = ++_n;
        var res = await Auth.RegisterAsync(new RegisterRequest($"user{i}@example.test", "correct horse battery", name ?? $"User {i}"));
        return res.User.Id;
    }

    public static byte[] KeyFor(string seed) => SHA256.HashData(Encoding.UTF8.GetBytes("test-key:" + seed));

    public async Task<NostrEvent> SignLoginAsync(byte[] key, string? pubkeyForChallenge = null, Action<NostrEvent>? tamper = null,
        int kind = NostrKinds.LoginAuth, string domain = Domain, long? createdAt = null, bool includeChallenge = true)
    {
        var ch = await Auth.CreateChallengeAsync(pubkeyForChallenge, ChallengePurpose.Login, null);
        var tags = new List<List<string>> { new() { "domain", domain } };
        if (includeChallenge) tags.Add(new() { "challenge", ch.Challenge });
        var ev = NostrEventValidator.Sign(key, kind, createdAt ?? Now, tags);
        tamper?.Invoke(ev);
        return ev;
    }

    public async Task<AuthResponse> NostrLoginAsync(string seed)
        => await Auth.LoginWithNostrAsync(await SignLoginAsync(KeyFor(seed)));

    // ---- communities

    public static CreateCommunityRequest Req(string name = "Test Community", double? lat = 38.72, double? lon = -9.14,
        Visibility vis = Visibility.Public, AccessMode access = AccessMode.Open, PricingMode pricing = PricingMode.Free,
        CommunityCategory cat = CommunityCategory.Social, DateOnly? start = null, DateOnly? end = null,
        DateTimeOffset? expires = null, decimal? price = null)
        => new(name, "A test community", cat, null, null, lat is null ? null : "Somewhere", lat, lon, start, end, expires, vis, access, pricing, price, null);

    public async Task<CommunityDetailsDto> NewCommunityAsync(Guid owner, CreateCommunityRequest? req = null)
        => await Communities.CreateAsync(owner, req ?? Req());

    public static Guid Id(CommunityDetailsDto d) => d.Community.Id;
}

public static class Err
{
    public static async Task Expect(string code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<AppException>(action);
        Assert.Equal(code, ex.Code);
    }

    public static async Task Expect(ErrorKind kind, string code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<AppException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Equal(kind, ex.Kind);
    }
}
