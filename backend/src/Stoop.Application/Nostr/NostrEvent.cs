using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Stoop.Nostr;

/// <summary>NIP-01 event.</summary>
public sealed class NostrEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("pubkey")] public string PubKey { get; set; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("tags")] public List<List<string>> Tags { get; set; } = new();
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("sig")] public string Sig { get; set; } = "";
}

public static class NostrKinds
{
    /// <summary>Kind used for the login challenge event (same kind as NIP-42 AUTH).</summary>
    public const int LoginAuth = 22242;
    /// <summary>NIP-98 HTTP auth.</summary>
    public const int HttpAuth = 27235;
}

public static class NostrEventValidator
{
    public const int MaxTags = 50;
    public const int MaxTagItems = 8;
    public const int MaxContentLength = 2048;

    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    public static bool IsLowerHex(string? value, int length)
    {
        if (value is null || value.Length != length) return false;
        foreach (var c in value)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>NIP-01 id: sha256 of the compact JSON array [0,pubkey,created_at,kind,tags,content].</summary>
    public static string ComputeId(NostrEvent ev)
    {
        var sb = new StringBuilder(256);
        sb.Append("[0,");
        AppendString(sb, ev.PubKey);
        sb.Append(',').Append(ev.CreatedAt).Append(',').Append(ev.Kind).Append(',');
        sb.Append('[');
        for (var i = 0; i < ev.Tags.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[');
            var tag = ev.Tags[i];
            for (var j = 0; j < tag.Count; j++)
            {
                if (j > 0) sb.Append(',');
                AppendString(sb, tag[j]);
            }
            sb.Append(']');
        }
        sb.Append("],");
        AppendString(sb, ev.Content);
        sb.Append(']');
        return ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // Matches JSON.stringify: only quote, backslash and control characters are escaped.
    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Structural + cryptographic validation. Returns an error code, or null when the event is
    /// well-formed, its id matches its content and the signature verifies for its pubkey.
    /// </summary>
    public static string? VerifyIntegrity(NostrEvent? ev, ISchnorrVerifier? verifier = null)
    {
        if (ev is null) return "invalid_event";
        if (!IsLowerHex(ev.PubKey, 64)) return "invalid_pubkey";
        if (!IsLowerHex(ev.Id, 64)) return "invalid_id";
        if (!IsLowerHex(ev.Sig, 128)) return "invalid_signature_format";
        if (ev.Content is null || ev.Content.Length > MaxContentLength) return "invalid_content";
        if (ev.Tags is null || ev.Tags.Count > MaxTags) return "invalid_tags";
        foreach (var tag in ev.Tags)
        {
            if (tag is null || tag.Count == 0 || tag.Count > MaxTagItems) return "invalid_tags";
            foreach (var item in tag) if (item is null || item.Length > 512) return "invalid_tags";
        }

        var computed = ComputeId(ev);
        if (!string.Equals(computed, ev.Id, StringComparison.Ordinal)) return "id_mismatch";

        verifier ??= new ManagedSchnorrVerifier();
        var ok = verifier.Verify(FromHex(ev.PubKey), FromHex(ev.Id), FromHex(ev.Sig));
        return ok ? null : "bad_signature";
    }

    public static string? GetTag(NostrEvent ev, string name)
    {
        foreach (var tag in ev.Tags)
            if (tag.Count >= 2 && tag[0] == name) return tag[1];
        return null;
    }

    public static int CountTags(NostrEvent ev, string name) => ev.Tags.Count(t => t.Count >= 1 && t[0] == name);

    /// <summary>
    /// NIP-98: validates an HTTP auth event for the given request. Host is compared with the
    /// expected domain (ignoring port); path and query must match exactly.
    /// </summary>
    public static string? ValidateHttpAuth(NostrEvent ev, string method, string path, string? query,
        string expectedDomain, DateTimeOffset now, int skewSeconds, ISchnorrVerifier? verifier = null)
    {
        var integrity = VerifyIntegrity(ev, verifier);
        if (integrity is not null) return integrity;
        if (ev.Kind != NostrKinds.HttpAuth) return "invalid_kind";

        var created = DateTimeOffset.FromUnixTimeSeconds(ev.CreatedAt);
        if (Math.Abs((now - created).TotalSeconds) > skewSeconds) return "event_expired";

        var u = GetTag(ev, "u");
        var m = GetTag(ev, "method");
        if (u is null || m is null) return "missing_tags";
        if (!string.Equals(m, method, StringComparison.OrdinalIgnoreCase)) return "method_mismatch";
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return "invalid_url";
        if (!string.Equals(uri.Host, expectedDomain, StringComparison.OrdinalIgnoreCase)) return "domain_mismatch";
        var expectedQuery = string.IsNullOrEmpty(query) ? "" : query;
        if (uri.AbsolutePath != path || uri.Query != expectedQuery) return "url_mismatch";
        return null;
    }

    /// <summary>Builds a signed event. Tests and developer tooling only (needs a secret key).</summary>
    public static NostrEvent Sign(ReadOnlySpan<byte> secretKey32, int kind, long createdAt,
        IEnumerable<IEnumerable<string>> tags, string content = "")
    {
        var ev = new NostrEvent
        {
            PubKey = ToHex(Secp256k1.GetPublicKey(secretKey32)),
            CreatedAt = createdAt,
            Kind = kind,
            Tags = tags.Select(t => t.ToList()).ToList(),
            Content = content,
        };
        ev.Id = ComputeId(ev);
        var aux = RandomNumberGenerator.GetBytes(32);
        ev.Sig = ToHex(Secp256k1.SignSchnorr(secretKey32, FromHex(ev.Id), aux));
        return ev;
    }
}
