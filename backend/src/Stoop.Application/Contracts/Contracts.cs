using System.Text.Json.Serialization;
using Stoop.Domain;
using Stoop.Nostr;

namespace Stoop.Contracts;

// ---------------------------------------------------------------- auth

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record LinkEmailRequest(string Email, string Password);
public sealed record NostrChallengeRequest(string? PublicKey);
public sealed record NostrChallengeResponse(string Challenge, DateTimeOffset ExpiresAt, int Kind, string Domain);
public sealed record NostrVerifyRequest(NostrEvent Event);

public sealed record UserDto(Guid Id, string DisplayName, string? Email, string? NostrPublicKey, DateTimeOffset CreatedAt);
public sealed record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);

// ---------------------------------------------------------------- communities

public sealed record CreateCommunityRequest(
    string Name,
    string? Description,
    CommunityCategory Category,
    string? Topic,
    string? CoverImageUrl,
    string? LocationName,
    double? Latitude,
    double? Longitude,
    DateOnly? ContextStartDate,
    DateOnly? ContextEndDate,
    DateTimeOffset? ExpiresAt,
    Visibility? Visibility,
    AccessMode? AccessMode,
    PricingMode? PricingMode,
    decimal? PriceAmount,
    string? PriceCurrency);

public sealed record CommunitySummaryDto(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    CommunityCategory Category,
    string? Topic,
    string? CoverImageUrl,
    string? LocationName,
    double? Latitude,
    double? Longitude,
    DateOnly? ContextStartDate,
    DateOnly? ContextEndDate,
    DateTimeOffset? ExpiresAt,
    Visibility Visibility,
    AccessMode AccessMode,
    PricingMode PricingMode,
    CommunityStatus Status,
    int MemberCount);

public sealed record MyMembershipDto(MemberRole Role, MemberStatus Status, DateTimeOffset? PaidUntil);

public sealed record CommunityDetailsDto(
    CommunitySummaryDto Community,
    decimal? PriceAmount,
    string? PriceCurrency,
    DateTimeOffset CreatedAt,
    Guid? ConversationId,
    MyMembershipDto? MyMembership);

public sealed record MembershipResultDto(Guid CommunityId, MemberStatus Status, MemberRole Role);
public sealed record MemberDto(Guid UserId, string DisplayName, MemberRole Role, MemberStatus Status, DateTimeOffset? JoinedAt, DateTimeOffset? RequestedAt);
public sealed record InviteRequest(Guid? UserId, string? NostrPublicKey);

// ---------------------------------------------------------------- discovery (GeoJSON)

public sealed record GeoJsonPoint(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coordinates")] double[] Coordinates);

public sealed record CommunityFeatureProperties(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("category")] CommunityCategory Category,
    [property: JsonPropertyName("memberCount")] int MemberCount,
    [property: JsonPropertyName("locationName")] string? LocationName,
    [property: JsonPropertyName("contextStartDate")] DateOnly? ContextStartDate,
    [property: JsonPropertyName("contextEndDate")] DateOnly? ContextEndDate,
    [property: JsonPropertyName("accessMode")] AccessMode AccessMode,
    [property: JsonPropertyName("pricingMode")] PricingMode PricingMode,
    [property: JsonPropertyName("visibility")] Visibility Visibility,
    /// <summary>Short excerpt (at most 160 characters) for list cards.</summary>
    [property: JsonPropertyName("description")] string Description);

public sealed record GeoJsonFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("geometry")] GeoJsonPoint Geometry,
    [property: JsonPropertyName("properties")] CommunityFeatureProperties Properties);

public sealed record GeoJsonFeatureCollection(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("features")] IReadOnlyList<GeoJsonFeature> Features,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record CommunityListDto(IReadOnlyList<CommunitySummaryDto> Items, int Skip, int Take, bool HasMore);

// ---------------------------------------------------------------- chat

public sealed record ConversationDto(Guid Id, ConversationType Type, Guid? CommunityId, string Title, DateTimeOffset CreatedAt);
public sealed record CreateDirectConversationRequest(Guid UserId);
public sealed record SendMessageRequest(string Content);
public sealed record EditMessageRequest(string Content);

public sealed record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderUserId,
    string SenderDisplayName,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool Deleted);

public sealed record MessagePageDto(IReadOnlyList<MessageDto> Items, Guid? NextBefore);
