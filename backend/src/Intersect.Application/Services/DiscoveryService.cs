using Intersect.Abstractions;
using Intersect.Common;
using Intersect.Contracts;
using Intersect.Domain;
using Microsoft.Extensions.Options;

namespace Intersect.Services;

public sealed record DiscoveryQuery(
    DateOnly? From, DateOnly? To, CommunityCategory? Category, string? Search, bool IncludeUndated);

public sealed class DiscoveryService(ICommunityRepository communities, IOptions<DiscoveryOptions> options, TimeProvider time)
{
    private const int MaxPage = 100;

    /// <summary>Viewport discovery as GeoJSON. Only communities the viewer may discover are returned.</summary>
    public async Task<GeoJsonFeatureCollection> GetViewportAsync(
        Guid? viewerId, double west, double south, double east, double north, int? zoom,
        DiscoveryQuery q, CancellationToken ct = default)
    {
        if (double.IsNaN(west) || double.IsNaN(east) || double.IsNaN(south) || double.IsNaN(north))
            throw AppException.Validation("invalid_bbox", "Bounding box values must be numbers.");
        if (south < -90 || north > 90 || south >= north)
            throw AppException.Validation("invalid_bbox", "south/north must be within -90..90 and south < north.");
        if (west < -180 || west > 180 || east < -180 || east > 180)
            throw AppException.Validation("invalid_bbox", "west/east must be within -180..180.");
        if (zoom is < 0 or > 24) throw AppException.Validation("invalid_zoom", "zoom must be between 0 and 24.");

        var filter = BuildFilter(viewerId, q);
        var max = options.Value.MaxFeatures;
        var limit = zoom is < 4 ? Math.Min(max, 1000) : max;

        var found = await communities.QueryViewportAsync(new ViewportQuery(west, south, east, north, filter, limit + 1), ct);
        var truncated = found.Count > limit;
        var features = found.Take(limit).Where(c => c.HasLocation).Select(Mappers.ToFeature).ToList();
        return new GeoJsonFeatureCollection("FeatureCollection", features, features.Count, truncated);
    }

    /// <summary>Communities without a location. They never get artificial coordinates.</summary>
    public async Task<CommunityListDto> GetGlobalAsync(Guid? viewerId, DiscoveryQuery q, int skip, int take, CancellationToken ct = default)
    {
        (skip, take) = Page(skip, take);
        var rows = await communities.QueryGlobalAsync(BuildFilter(viewerId, q), skip, take + 1, ct);
        return new CommunityListDto(rows.Take(take).Select(Mappers.ToSummary).ToList(), skip, take, rows.Count > take);
    }

    public async Task<CommunityListDto> SearchAsync(Guid? viewerId, string? query, int skip, int take, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw AppException.Validation("missing_query", "Query parameter q is required.");
        (skip, take) = Page(skip, take);
        var filter = BuildFilter(viewerId, new DiscoveryQuery(null, null, null, query, true));
        var rows = await communities.SearchAsync(filter, skip, take + 1, ct);
        return new CommunityListDto(rows.Take(take).Select(Mappers.ToSummary).ToList(), skip, take, rows.Count > take);
    }

    private CommunityFilter BuildFilter(Guid? viewerId, DiscoveryQuery q)
    {
        if (q.From is { } f && q.To is { } t && f > t)
            throw AppException.Validation("invalid_time_range", "'from' must not be after 'to'.");
        var search = string.IsNullOrWhiteSpace(q.Search) ? null : q.Search.Trim();
        if (search is { Length: > 100 }) throw AppException.Validation("invalid_search", "Search text is too long.");
        return new CommunityFilter(search, q.Category, q.From, q.To, q.IncludeUndated, time.GetUtcNow(), viewerId);
    }

    private static (int skip, int take) Page(int skip, int take)
        => (Math.Max(0, skip), Math.Clamp(take <= 0 ? 20 : take, 1, MaxPage));
}
