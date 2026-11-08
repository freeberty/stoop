using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Intersect.Abstractions;
using Intersect.Common;
using Intersect.Contracts;
using Intersect.Domain;
using Microsoft.Extensions.Options;

namespace Intersect.Services;

public sealed partial class CommunityService(
    ICommunityRepository communities,
    IMembershipRepository members,
    IConversationRepository conversations,
    IUserRepository users,
    IAuditLog audit,
    IUnitOfWork uow,
    IRealtimeNotifier notifier,
    TimeProvider time)
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    public async Task<CommunityDetailsDto> CreateAsync(Guid userId, CreateCommunityRequest req, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(userId, ct) ?? throw AppException.Unauthorized("unknown_user", "Account not found.");
        if (user.Status != UserStatus.Active) throw AppException.Forbidden("account_suspended", "This account is suspended.");

        var now = time.GetUtcNow();
        Validate(req, now);

        var pricing = req.PricingMode ?? PricingMode.Free;
        var community = new Community
        {
            Id = Guid.CreateVersion7(),
            Name = req.Name.Trim(),
            Slug = await UniqueSlugAsync(req.Name, ct),
            Description = (req.Description ?? "").Trim(),
            Category = req.Category,
            Topic = Clean(req.Topic),
            CoverImageUrl = Clean(req.CoverImageUrl),
            LocationName = Clean(req.LocationName),
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            ContextStartDate = req.ContextStartDate,
            ContextEndDate = req.ContextEndDate,
            CreatedAt = now,
            ExpiresAt = req.ExpiresAt,
            Visibility = req.Visibility ?? Visibility.Public,
            AccessMode = req.AccessMode ?? AccessMode.Open,
            PricingMode = pricing,
            PriceAmount = pricing == PricingMode.Paid ? req.PriceAmount : null,
            PriceCurrency = pricing == PricingMode.Paid ? (Clean(req.PriceCurrency) ?? "EUR").ToUpperInvariant() : null,
            Status = CommunityStatus.Active,
            CreatedByUserId = userId,
            MemberCount = 1,
        };
        communities.Add(community);

        members.Add(new CommunityMember
        {
            CommunityId = community.Id, UserId = userId, Role = MemberRole.Owner,
            Status = MemberStatus.Active, JoinedAt = now, UpdatedAt = now,
        });

        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(), Type = ConversationType.Community, CommunityId = community.Id,
            CreatedAt = now, Status = ConversationStatus.Active,
        };
        conversations.Add(conversation);
        conversations.AddMember(new ConversationMember
        {
            ConversationId = conversation.Id, UserId = userId, JoinedAt = now, Status = ConversationMemberStatus.Active,
        });

        audit.Add(new AuditLogEntry
        {
            Id = Guid.CreateVersion7(), At = now, ActorUserId = userId, CommunityId = community.Id, Action = "community.create",
            Details = $"visibility={community.Visibility};access={community.AccessMode};pricing={community.PricingMode}",
        });

        await uow.SaveChangesAsync(ct);
        var mine = await members.GetAsync(community.Id, userId, ct);
        return Mappers.ToDetails(community, mine, conversation.Id);
    }

    /// <summary>Accepts a GUID or a slug. Communities the viewer may not see are reported as not found.</summary>
    public async Task<CommunityDetailsDto> GetAsync(Guid? viewerId, string idOrSlug, CancellationToken ct = default)
    {
        var community = Guid.TryParse(idOrSlug, out var id)
            ? await communities.GetByIdAsync(id, ct)
            : await communities.GetBySlugAsync(idOrSlug.ToLowerInvariant(), ct);
        if (community is null) throw NotFound();

        CommunityMember? member = viewerId is null ? null : await members.GetAsync(community.Id, viewerId.Value, ct);
        if (!AccessPolicy.CanView(community, member)) throw NotFound();

        Guid? conversationId = null;
        if (member is { Status: MemberStatus.Active })
            conversationId = (await conversations.GetByCommunityAsync(community.Id, ct))?.Id;
        return Mappers.ToDetails(community, member, conversationId);
    }

    /// <summary>
    /// Owner only. Archives the community: it disappears from discovery and its page, the chat closes,
    /// and every member's live chat access is revoked. Data is kept for the audit trail.
    /// </summary>
    public async Task DeleteAsync(Guid actorId, Guid communityId, CancellationToken ct = default)
    {
        var community = await communities.GetByIdAsync(communityId, ct);
        if (community is null || community.Status == CommunityStatus.Archived) throw NotFound();
        var actor = await members.GetAsync(communityId, actorId, ct);
        if (!AccessPolicy.CanView(community, actor)) throw NotFound();
        if (actor is not { Status: MemberStatus.Active, Role: MemberRole.Owner })
            throw AppException.Forbidden("owner_required", "Only the owner can delete this community.");

        var now = time.GetUtcNow();
        community.Status = CommunityStatus.Archived;
        var conversation = await conversations.GetByCommunityAsync(communityId, ct);
        if (conversation is not null) conversation.Status = ConversationStatus.Closed;
        var active = await members.ListAsync(communityId, MemberStatus.Active, 0, int.MaxValue, ct);
        audit.Add(new AuditLogEntry
        {
            Id = Guid.CreateVersion7(), At = now, ActorUserId = actorId, CommunityId = communityId, Action = "community.delete",
        });
        await uow.SaveChangesAsync(ct);

        if (conversation is not null)
            foreach (var m in active) await notifier.AccessRevokedAsync(conversation.Id, m.UserId, "community_deleted", ct);
    }

    private static AppException NotFound() => AppException.NotFound("community_not_found", "Community not found.");

    // ------------------------------------------------------------ validation

    internal static void Validate(CreateCommunityRequest req, DateTimeOffset now)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length is < 3 or > 80) throw AppException.Validation("invalid_name", "Name must be 3 to 80 characters.");
        if ((req.Description ?? "").Length > 2000) throw AppException.Validation("invalid_description", "Description must be at most 2000 characters.");
        if (!Enum.IsDefined(req.Category)) throw AppException.Validation("invalid_category", "Unknown category.");
        if (req.Topic is { Length: > 60 }) throw AppException.Validation("invalid_topic", "Topic must be at most 60 characters.");
        if (req.LocationName is { Length: > 120 }) throw AppException.Validation("invalid_location_name", "Location name must be at most 120 characters.");

        if (req.Latitude.HasValue != req.Longitude.HasValue)
            throw AppException.Validation("invalid_location", "Latitude and longitude must be provided together.");
        if (req.Latitude is { } lat && (double.IsNaN(lat) || lat is < -90 or > 90))
            throw AppException.Validation("invalid_latitude", "Latitude must be between -90 and 90.");
        if (req.Longitude is { } lon && (double.IsNaN(lon) || lon is < -180 or > 180))
            throw AppException.Validation("invalid_longitude", "Longitude must be between -180 and 180.");

        if (req.ContextStartDate is { } s && req.ContextEndDate is { } e && s > e)
            throw AppException.Validation("invalid_context_dates", "Context start must not be after context end.");
        if (req.ExpiresAt is { } exp && exp <= now)
            throw AppException.Validation("invalid_expiry", "Expiry must be in the future.");

        if (!string.IsNullOrWhiteSpace(req.CoverImageUrl))
        {
            if (!Uri.TryCreate(req.CoverImageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || req.CoverImageUrl.Length > 500)
                throw AppException.Validation("invalid_cover_image", "Cover image must be an https URL.");
        }
        if (req.Visibility is { } v && !Enum.IsDefined(v)) throw AppException.Validation("invalid_visibility", "Unknown visibility.");
        if (req.AccessMode is { } a && !Enum.IsDefined(a)) throw AppException.Validation("invalid_access_mode", "Unknown access mode.");
        if (req.PricingMode is { } p)
        {
            if (!Enum.IsDefined(p)) throw AppException.Validation("invalid_pricing_mode", "Unknown pricing mode.");
            if (p == PricingMode.Paid && req.PriceAmount is not > 0)
                throw AppException.Validation("invalid_price", "Paid communities need a positive price.");
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ------------------------------------------------------------ slugs

    public static string Slugify(string name)
    {
        var decomposed = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        var slug = NonSlug().Replace(sb.ToString(), "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "community" : slug;
    }

    private async Task<string> UniqueSlugAsync(string name, CancellationToken ct)
    {
        var baseSlug = Slugify(name);
        var slug = baseSlug;
        for (var i = 0; i < 6; i++)
        {
            if (!await communities.SlugExistsAsync(slug, ct)) return slug;
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            slug = $"{baseSlug}-{suffix}";
        }
        return $"{baseSlug}-{Guid.NewGuid():N}";
    }
}
