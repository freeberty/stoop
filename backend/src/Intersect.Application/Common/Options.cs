namespace Intersect.Common;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>HMAC secret for application tokens (min 32 bytes). Never commit a real value.</summary>
    public string JwtSecret { get; set; } = "";
    public string Issuer { get; set; } = "intersect";
    public string Audience { get; set; } = "intersect-clients";
    public int TokenLifetimeMinutes { get; set; } = 60;
    public int ChallengeLifetimeSeconds { get; set; } = 120;
    /// <summary>Domain the signer must put in the "domain" tag of the login event.</summary>
    public string ExpectedDomain { get; set; } = "localhost";
    /// <summary>Allowed difference between event created_at and server time.</summary>
    public int ClockSkewSeconds { get; set; } = 120;
}

public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    public int MaxMessageLength { get; set; } = 4000;
    public int RateLimitMessages { get; set; } = 20;
    public int RateLimitWindowSeconds { get; set; } = 10;
    public int MaxPageSize { get; set; } = 100;
}

public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    public int MaxFeatures { get; set; } = 2000;
}
