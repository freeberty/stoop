using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Intersect.Common;
using Intersect.Domain;
using Microsoft.Extensions.Options;

namespace Intersect.Services;

public sealed record TokenClaims(Guid UserId, string DisplayName);

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) Issue(User user);
    /// <summary>Returns null for any invalid, tampered or expired token.</summary>
    TokenClaims? Validate(string token);
}

/// <summary>Minimal HS256 JWT. Swap for Microsoft.AspNetCore.Authentication.JwtBearer in production if preferred.</summary>
public sealed class JwtTokenService : ITokenService
{
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;
    private readonly byte[] _key;

    public JwtTokenService(IOptions<AuthOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        _key = Encoding.UTF8.GetBytes(_options.JwtSecret ?? "");
        if (_key.Length < 32) throw new InvalidOperationException("Auth:JwtSecret must be at least 32 bytes.");
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(User user)
    {
        var now = _time.GetUtcNow();
        var expires = now.AddMinutes(_options.TokenLifetimeMinutes);
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT" });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            ["name"] = user.DisplayName,
            ["iss"] = _options.Issuer,
            ["aud"] = _options.Audience,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        });
        var signingInput = Base64Url.EncodeToString(header) + "." + Base64Url.EncodeToString(payload);
        var sig = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(signingInput));
        return (signingInput + "." + Base64Url.EncodeToString(sig), expires);
    }

    public TokenClaims? Validate(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            using (var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])))
            {
                if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256") return null;
            }

            var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            var actual = Base64Url.DecodeFromChars(parts[2]);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = payload.RootElement;
            if (root.GetProperty("iss").GetString() != _options.Issuer) return null;
            if (root.GetProperty("aud").GetString() != _options.Audience) return null;
            if (root.GetProperty("exp").GetInt64() <= _time.GetUtcNow().ToUnixTimeSeconds()) return null;
            if (!Guid.TryParse(root.GetProperty("sub").GetString(), out var id)) return null;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            return new TokenClaims(id, name);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string? stored);
}

/// <summary>PBKDF2-SHA256, per-password salt, iteration count stored with the hash.</summary>
public sealed class Pbkdf2PasswordHasher(int iterations = 600_000) : IPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string? stored)
    {
        // Always do comparable work so a missing account is not distinguishable by timing.
        if (string.IsNullOrEmpty(stored))
        {
            Rfc2898DeriveBytes.Pbkdf2(password, new byte[16], iterations, HashAlgorithmName.SHA256, 32);
            return false;
        }
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != Prefix) return false;
            var iter = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }
}
