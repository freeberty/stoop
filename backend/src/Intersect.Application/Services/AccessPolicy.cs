using Intersect.Abstractions;
using Intersect.Contracts;
using Intersect.Domain;

namespace Intersect.Services;

/// <summary>Pure rules. Everything that decides who can see or chat lives here or in MembershipService.</summary>
public static class AccessPolicy
{
    public static bool IsLive(Community c, DateTimeOffset now)
        => c.Status == CommunityStatus.Active && (c.ExpiresAt is null || c.ExpiresAt > now);

    public static bool IsAdmin(CommunityMember? m)
        => m is { Status: MemberStatus.Active, Role: MemberRole.Admin or MemberRole.Owner };

    /// <summary>Anonymous public discovery: public + live only.</summary>
    public static bool IsPubliclyDiscoverable(Community c, DateTimeOffset now)
        => c.Visibility == Visibility.Public && IsLive(c, now);

    /// <summary>Can this viewer open the community details? Private communities do not leak existence.</summary>
    public static bool CanView(Community c, CommunityMember? m)
    {
        if (c.Status == CommunityStatus.Archived) return false; // deleted by its owner
        if (c.Status == CommunityStatus.Draft) return IsAdmin(m);
        switch (c.Visibility)
        {
            case Visibility.Public:
            case Visibility.Unlisted:
                return true;
            default: // Private
                return m is { Status: MemberStatus.Active or MemberStatus.Pending or MemberStatus.Invited or MemberStatus.AwaitingPayment };
        }
    }

    /// <summary>The single rule for community chat access.</summary>
    public static bool CanAccessChat(Community c, CommunityMember? m, DateTimeOffset now)
    {
        if (m is not { Status: MemberStatus.Active }) return false;
        if (!IsLive(c, now)) return false;
        if (c.PricingMode == PricingMode.Paid && m.Role != MemberRole.Owner)
            return m.PaidUntil is not null && m.PaidUntil > now;
        return true;
    }

    public static bool MatchesContext(Community c, DateOnly? from, DateOnly? to, bool includeUndated)
    {
        if (from is null && to is null) return true;
        if (c.ContextStartDate is null && c.ContextEndDate is null) return includeUndated;
        var start = c.ContextStartDate ?? DateOnly.MinValue;
        var end = c.ContextEndDate ?? DateOnly.MaxValue;
        var f = from ?? DateOnly.MinValue;
        var t = to ?? DateOnly.MaxValue;
        return start <= t && end >= f;
    }

    public static bool MatchesSearch(Community c, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return Contains(c.Name, search) || Contains(c.Description, search)
            || Contains(c.LocationName, search) || Contains(c.Topic, search);
    }

    private static bool Contains(string? hay, string needle)
        => hay is not null && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

public static class Mappers
{
    public static CommunitySummaryDto ToSummary(Community c) => new(
        c.Id, c.Name, c.Slug, c.Description, c.Category, c.Topic, c.CoverImageUrl, c.LocationName,
        c.Latitude, c.Longitude, c.ContextStartDate, c.ContextEndDate, c.ExpiresAt,
        c.Visibility, c.AccessMode, c.PricingMode, c.Status, c.MemberCount);

    public static CommunityDetailsDto ToDetails(Community c, CommunityMember? m, Guid? conversationId)
    {
        // Only members get the conversation id.
        var myConv = m is { Status: MemberStatus.Active } ? conversationId : null;
        MyMembershipDto? mine = m is null || m.Status is MemberStatus.Left or MemberStatus.Removed
            ? null
            : new MyMembershipDto(m.Role, m.Status, m.PaidUntil);
        return new CommunityDetailsDto(ToSummary(c), c.PriceAmount, c.PriceCurrency, c.CreatedAt, myConv, mine);
    }

    public static GeoJsonFeature ToFeature(Community c) => new(
        "Feature",
        c.Id.ToString(),
        new GeoJsonPoint("Point", [c.Longitude!.Value, c.Latitude!.Value]),
        new CommunityFeatureProperties(c.Id, c.Name, c.Slug, c.Category, c.MemberCount, c.LocationName,
            c.ContextStartDate, c.ContextEndDate, c.AccessMode, c.PricingMode, c.Visibility, Excerpt(c.Description)));

    private static string Excerpt(string? text, int max = 160)
    {
        var t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }
}
