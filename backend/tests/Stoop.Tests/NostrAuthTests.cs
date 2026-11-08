using Stoop.Contracts;
using Stoop.Domain;
using Stoop.Nostr;
using Xunit;

namespace Stoop.Tests;

public class NostrAuthTests
{
    [Fact]
    public async Task Valid_signer_authentication_creates_account_and_issues_token()
    {
        var w = new World();
        var res = await w.NostrLoginAsync("alice");

        var key = NostrEventValidator.ToHex(Secp256k1.GetPublicKey(World.KeyFor("alice")));
        Assert.Equal(key, res.User.NostrPublicKey);
        var claims = w.Tokens.Validate(res.Token);
        Assert.NotNull(claims);
        Assert.Equal(res.User.Id, claims!.UserId);
    }

    [Fact]
    public async Task Same_key_logs_into_same_account()
    {
        var w = new World();
        var a = await w.NostrLoginAsync("alice");
        var b = await w.NostrLoginAsync("alice");
        Assert.Equal(a.User.Id, b.User.Id);
    }

    [Fact]
    public async Task Tampered_content_is_rejected()
    {
        var w = new World();
        var ev = await w.SignLoginAsync(World.KeyFor("alice"), tamper: e => e.Content = "evil");
        await Err.Expect("id_mismatch", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Invalid_signature_is_rejected()
    {
        var w = new World();
        var ev = await w.SignLoginAsync(World.KeyFor("alice"), tamper: e =>
        {
            var chars = e.Sig.ToCharArray();
            chars[10] = chars[10] == 'a' ? 'b' : 'a';
            e.Sig = new string(chars);
        });
        await Err.Expect("bad_signature", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Event_claiming_someone_elses_pubkey_is_rejected()
    {
        var w = new World();
        var victim = NostrEventValidator.ToHex(Secp256k1.GetPublicKey(World.KeyFor("victim")));
        var ev = await w.SignLoginAsync(World.KeyFor("attacker"), tamper: e =>
        {
            e.PubKey = victim;
            e.Id = NostrEventValidator.ComputeId(e); // attacker even fixes the id; signature still cannot verify
        });
        await Err.Expect("bad_signature", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Challenge_bound_to_a_different_public_key_is_rejected()
    {
        var w = new World();
        var expected = NostrEventValidator.ToHex(Secp256k1.GetPublicKey(World.KeyFor("alice")));
        var ev = await w.SignLoginAsync(World.KeyFor("mallory"), pubkeyForChallenge: expected);
        await Err.Expect("public_key_mismatch", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Expired_challenge_is_rejected()
    {
        var w = new World((a, _) => a.ClockSkewSeconds = 3600);
        var ev = await w.SignLoginAsync(World.KeyFor("alice"));
        w.Time.Advance(TimeSpan.FromSeconds(w.AuthOpt.ChallengeLifetimeSeconds + 5));
        await Err.Expect("challenge_expired", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Replayed_event_is_rejected()
    {
        var w = new World();
        var ev = await w.SignLoginAsync(World.KeyFor("alice"));
        await w.Auth.LoginWithNostrAsync(ev);
        await Err.Expect("challenge_replayed", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Unknown_challenge_is_rejected()
    {
        var w = new World();
        var ev = NostrEventValidator.Sign(World.KeyFor("alice"), NostrKinds.LoginAuth, w.Now,
            new[] { new[] { "challenge", "never-issued" }, new[] { "domain", World.Domain } });
        await Err.Expect("challenge_unknown", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Wrong_event_kind_is_rejected()
    {
        var w = new World();
        var ev = await w.SignLoginAsync(World.KeyFor("alice"), kind: 1);
        await Err.Expect("invalid_kind", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Wrong_domain_is_rejected()
    {
        var w = new World();
        var ev = await w.SignLoginAsync(World.KeyFor("alice"), domain: "evil.example");
        await Err.Expect("domain_mismatch", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Missing_challenge_tag_is_rejected()
    {
        var w = new World();
        var ev = await w.SignLoginAsync(World.KeyFor("alice"), includeChallenge: false);
        await Err.Expect("challenge_missing", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Stale_event_timestamp_is_rejected()
    {
        var w = new World();
        var ev = await w.SignLoginAsync(World.KeyFor("alice"), createdAt: w.Now - 3600);
        await Err.Expect("event_expired", () => w.Auth.LoginWithNostrAsync(ev));
    }

    [Fact]
    public async Task Missing_event_is_rejected()
    {
        var w = new World();
        await Err.Expect("invalid_event", () => w.Auth.LoginWithNostrAsync(null));
    }

    // ---------------------------------------------------------------- linking

    [Fact]
    public async Task Nostr_identity_can_be_linked_to_an_email_account()
    {
        var w = new World();
        var userId = await w.NewEmailUserAsync();
        var ev = await SignLinkAsync(w, "alice", userId);

        var dto = await w.Auth.LinkNostrAsync(userId, ev);
        Assert.NotNull(dto.NostrPublicKey);

        // Logging in with Nostr now yields the same account.
        var login = await w.NostrLoginAsync("alice");
        Assert.Equal(userId, login.User.Id);
    }

    [Fact]
    public async Task Linking_a_key_that_belongs_to_another_account_is_rejected()
    {
        var w = new World();
        await w.NostrLoginAsync("alice");
        var userId = await w.NewEmailUserAsync();
        var ev = await SignLinkAsync(w, "alice", userId);
        await Err.Expect("identity_already_linked", () => w.Auth.LinkNostrAsync(userId, ev));
    }

    [Fact]
    public async Task Link_challenge_belongs_to_the_requesting_user()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var other = await w.NewEmailUserAsync();
        var ev = await SignLinkAsync(w, "alice", owner);
        await Err.Expect("challenge_user_mismatch", () => w.Auth.LinkNostrAsync(other, ev));
    }

    [Fact]
    public async Task Login_challenge_cannot_be_used_for_linking()
    {
        var w = new World();
        var userId = await w.NewEmailUserAsync();
        var loginEvent = await w.SignLoginAsync(World.KeyFor("alice"));
        await Err.Expect("challenge_purpose_mismatch", () => w.Auth.LinkNostrAsync(userId, loginEvent));
    }

    private static async Task<NostrEvent> SignLinkAsync(World w, string seed, Guid userId)
    {
        var ch = await w.Auth.CreateChallengeAsync(null, ChallengePurpose.Link, userId);
        return NostrEventValidator.Sign(World.KeyFor(seed), NostrKinds.LoginAuth, w.Now,
            new[] { new[] { "challenge", ch.Challenge }, new[] { "domain", World.Domain } });
    }

    // ---------------------------------------------------------------- email accounts

    [Fact]
    public async Task Email_registration_and_login_work()
    {
        var w = new World();
        var reg = await w.Auth.RegisterAsync(new RegisterRequest("Mixed.Case@Example.test", "correct horse battery", "Mixed"));
        var login = await w.Auth.LoginAsync(new LoginRequest("mixed.case@example.test", "correct horse battery"));
        Assert.Equal(reg.User.Id, login.User.Id);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_look_identical()
    {
        var w = new World();
        await w.NewEmailUserAsync();
        await Err.Expect("invalid_credentials", () => w.Auth.LoginAsync(new LoginRequest("user1@example.test", "wrong password!!")));
        await Err.Expect("invalid_credentials", () => w.Auth.LoginAsync(new LoginRequest("nobody@example.test", "wrong password!!")));
    }

    [Fact]
    public async Task Duplicate_email_and_weak_password_are_rejected()
    {
        var w = new World();
        await w.NewEmailUserAsync();
        await Err.Expect("email_taken", () => w.Auth.RegisterAsync(new RegisterRequest("user1@example.test", "correct horse battery", "Dup")));
        await Err.Expect("weak_password", () => w.Auth.RegisterAsync(new RegisterRequest("new@example.test", "short", "Short")));
        await Err.Expect("invalid_email", () => w.Auth.RegisterAsync(new RegisterRequest("not-an-email", "correct horse battery", "Bad")));
    }

    // ---------------------------------------------------------------- tokens / crypto / NIP-98

    [Fact]
    public async Task Tokens_reject_tampering_expiry_and_alg_none()
    {
        var w = new World();
        var res = await w.Auth.RegisterAsync(new RegisterRequest("t@example.test", "correct horse battery", "Tok"));
        Assert.NotNull(w.Tokens.Validate(res.Token));

        var parts = res.Token.Split('.');
        var tampered = parts[0] + "." + parts[1][..^2] + "AA." + parts[2];
        Assert.Null(w.Tokens.Validate(tampered));

        var algNone = System.Buffers.Text.Base64Url.EncodeToString("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8) + "." + parts[1] + ".";
        Assert.Null(w.Tokens.Validate(algNone));
        Assert.Null(w.Tokens.Validate("garbage"));

        w.Time.Advance(TimeSpan.FromMinutes(w.AuthOpt.TokenLifetimeMinutes + 1));
        Assert.Null(w.Tokens.Validate(res.Token));
    }

    [Fact]
    public void Schnorr_sign_and_verify_roundtrip_and_rejects_wrong_message()
    {
        var sk = World.KeyFor("roundtrip");
        var pk = Secp256k1.GetPublicKey(sk);
        var msg = System.Security.Cryptography.SHA256.HashData("hello"u8);
        var sig = Secp256k1.SignSchnorr(sk, msg, new byte[32]);
        Assert.True(Secp256k1.VerifySchnorr(pk, msg, sig));
        Assert.False(Secp256k1.VerifySchnorr(pk, System.Security.Cryptography.SHA256.HashData("other"u8), sig));
    }

    [Fact]
    public void Nip98_http_auth_validation()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var key = World.KeyFor("http");
        var ev = NostrEventValidator.Sign(key, NostrKinds.HttpAuth, now.ToUnixTimeSeconds(),
            new[] { new[] { "u", "https://stoop.test/api/v1/me" }, new[] { "method", "GET" } });

        Assert.Null(NostrEventValidator.ValidateHttpAuth(ev, "GET", "/api/v1/me", null, World.Domain, now, 60));
        Assert.Equal("method_mismatch", NostrEventValidator.ValidateHttpAuth(ev, "POST", "/api/v1/me", null, World.Domain, now, 60));
        Assert.Equal("url_mismatch", NostrEventValidator.ValidateHttpAuth(ev, "GET", "/api/v1/other", null, World.Domain, now, 60));
        Assert.Equal("domain_mismatch", NostrEventValidator.ValidateHttpAuth(ev, "GET", "/api/v1/me", null, "evil.test", now, 60));
        Assert.Equal("event_expired", NostrEventValidator.ValidateHttpAuth(ev, "GET", "/api/v1/me", null, World.Domain, now.AddMinutes(5), 60));
    }
}
