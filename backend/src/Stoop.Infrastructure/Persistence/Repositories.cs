using Stoop.Abstractions;
using Stoop.Common;
using Stoop.Domain;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using Npgsql;

namespace Stoop.Infrastructure.Persistence;

public sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw AppException.Conflict("unique_violation", $"A record with the same unique value already exists ({pg.ConstraintName}).");
        }
    }
}

public sealed class EfUserRepository(AppDbContext db) : IUserRepository
{
    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) => await db.Users.FindAsync([id], ct);

    public Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public Task<User?> GetByExternalIdentityAsync(string provider, string externalId, CancellationToken ct = default)
        => (from i in db.ExternalIdentities
            where i.Provider == provider && i.ExternalIdentifier == externalId
            join u in db.Users on i.UserId equals u.Id
            select u).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, User>();
        return (await db.Users.Where(u => list.Contains(u.Id)).ToListAsync(ct)).ToDictionary(u => u.Id);
    }

    public async Task<IReadOnlyList<ExternalIdentity>> GetIdentitiesAsync(Guid userId, CancellationToken ct = default)
        => await db.ExternalIdentities.AsNoTracking().Where(i => i.UserId == userId).ToListAsync(ct);

    public void Add(User user) => db.Users.Add(user);
    public void AddIdentity(ExternalIdentity identity) => db.ExternalIdentities.Add(identity);
}

public sealed class EfCommunityRepository(AppDbContext db) : ICommunityRepository
{
    private static readonly NetTopologySuite.Geometries.GeometryFactory Factory =
        NtsGeometryServices.Instance.CreateGeometryFactory(4326);

    public async Task<Community?> GetByIdAsync(Guid id, CancellationToken ct = default) => await db.Communities.FindAsync([id], ct);

    public Task<Community?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => db.Communities.FirstOrDefaultAsync(c => c.Slug == slug, ct);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default) => db.Communities.AnyAsync(c => c.Slug == slug, ct);

    public async Task<IReadOnlyDictionary<Guid, Community>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, Community>();
        return (await db.Communities.Where(c => list.Contains(c.Id)).ToListAsync(ct)).ToDictionary(c => c.Id);
    }

    public Task<long> CountAsync(CancellationToken ct = default) => db.Communities.LongCountAsync(ct);

    public void Add(Community community) => db.Communities.Add(community);

    /// <summary>Live + discoverable for this viewer + category/time/text filters. Mirrors AccessPolicy and InMemoryStore.</summary>
    private IQueryable<Community> Discoverable(IQueryable<Community> q, CommunityFilter f)
    {
        var now = f.Now;
        q = q.Where(c => c.Status == CommunityStatus.Active && (c.ExpiresAt == null || c.ExpiresAt > now));

        if (f.ViewerUserId is { } viewer)
            q = q.Where(c => c.Visibility == Visibility.Public
                || db.CommunityMembers.Any(m => m.CommunityId == c.Id && m.UserId == viewer && m.Status == MemberStatus.Active));
        else
            q = q.Where(c => c.Visibility == Visibility.Public);

        if (f.Category is { } category) q = q.Where(c => c.Category == category);

        if (f.From is not null || f.To is not null)
        {
            var from = f.From ?? DateOnly.MinValue;
            var to = f.To ?? DateOnly.MaxValue;
            var includeUndated = f.IncludeUndated;
            q = q.Where(c =>
                (c.ContextStartDate == null && c.ContextEndDate == null && includeUndated)
                || ((c.ContextStartDate != null || c.ContextEndDate != null)
                    && (c.ContextStartDate == null || c.ContextStartDate <= to)
                    && (c.ContextEndDate == null || c.ContextEndDate >= from)));
        }

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var pattern = LikePattern(f.Search);
            q = q.Where(c => EF.Functions.ILike(c.Name, pattern) || EF.Functions.ILike(c.Description, pattern)
                || (c.LocationName != null && EF.Functions.ILike(c.LocationName, pattern))
                || (c.Topic != null && EF.Functions.ILike(c.Topic, pattern)));
        }
        return q;
    }

    private static string LikePattern(string text)
        => "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public async Task<IReadOnlyList<Community>> QueryViewportAsync(ViewportQuery query, CancellationToken ct = default)
    {
        var q = Discoverable(db.Communities.AsNoTracking().Where(c => c.Latitude != null && c.Longitude != null), query.Filter);

        // PostGIS: geometry && envelope, served by the GiST index on location_point.
        if (query.West <= query.East)
        {
            var box = Box(query.West, query.South, query.East, query.North);
            q = q.Where(c => EF.Property<Point>(c, "LocationPoint").Intersects(box));
        }
        else // box crosses the antimeridian: split in two
        {
            var left = Box(query.West, query.South, 180, query.North);
            var right = Box(-180, query.South, query.East, query.North);
            q = q.Where(c => EF.Property<Point>(c, "LocationPoint").Intersects(left)
                          || EF.Property<Point>(c, "LocationPoint").Intersects(right));
        }

        return await q.OrderByDescending(c => c.MemberCount).ThenBy(c => c.Name).Take(query.Limit).ToListAsync(ct);
    }

    private static Geometry Box(double west, double south, double east, double north)
        => Factory.ToGeometry(new Envelope(west, east, south, north));

    public async Task<IReadOnlyList<Community>> QueryGlobalAsync(CommunityFilter filter, int skip, int take, CancellationToken ct = default)
        => await Discoverable(db.Communities.AsNoTracking().Where(c => c.Latitude == null || c.Longitude == null), filter)
            .OrderByDescending(c => c.MemberCount).ThenBy(c => c.Name).Skip(skip).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<Community>> SearchAsync(CommunityFilter filter, int skip, int take, CancellationToken ct = default)
    {
        var pattern = LikePattern(filter.Search ?? "");
        return await Discoverable(db.Communities.AsNoTracking(), filter)
            .OrderByDescending(c => EF.Functions.ILike(c.Name, pattern))
            .ThenByDescending(c => c.MemberCount).ThenBy(c => c.Name)
            .Skip(skip).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Community>> GetExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct = default)
        => await db.Communities.Where(c => c.Status == CommunityStatus.Active && c.ExpiresAt != null && c.ExpiresAt <= now)
            .Take(take).ToListAsync(ct);
}

public sealed class EfMembershipRepository(AppDbContext db) : IMembershipRepository
{
    public async Task<CommunityMember?> GetAsync(Guid communityId, Guid userId, CancellationToken ct = default)
        => await db.CommunityMembers.FindAsync([communityId, userId], ct);

    public void Add(CommunityMember member) => db.CommunityMembers.Add(member);

    public async Task<IReadOnlyList<CommunityMember>> ListAsync(Guid communityId, MemberStatus? status, int skip, int take, CancellationToken ct = default)
    {
        var q = db.CommunityMembers.AsNoTracking().Where(m => m.CommunityId == communityId);
        if (status is { } s) q = q.Where(m => m.Status == s);
        return await q.OrderBy(m => m.UpdatedAt).ThenBy(m => m.UserId).Skip(skip).Take(take).ToListAsync(ct);
    }

    public Task<bool> ShareActiveCommunityAsync(Guid userA, Guid userB, CancellationToken ct = default)
    {
        var mine = db.CommunityMembers.Where(m => m.UserId == userA && m.Status == MemberStatus.Active).Select(m => m.CommunityId);
        return db.CommunityMembers.AnyAsync(m => m.UserId == userB && m.Status == MemberStatus.Active && mine.Contains(m.CommunityId), ct);
    }

    public async Task<IReadOnlyList<CommunityMember>> ListExpiredSubscriptionsAsync(DateTimeOffset now, int take, CancellationToken ct = default)
        => await (from m in db.CommunityMembers
                  join c in db.Communities on m.CommunityId equals c.Id
                  where m.Status == MemberStatus.Active && m.Role != MemberRole.Owner
                        && c.PricingMode == PricingMode.Paid && m.PaidUntil != null && m.PaidUntil <= now
                  select m).Take(take).ToListAsync(ct);
}

public sealed class EfConversationRepository(AppDbContext db) : IConversationRepository
{
    public async Task<Conversation?> GetByIdAsync(Guid id, CancellationToken ct = default) => await db.Conversations.FindAsync([id], ct);

    public Task<Conversation?> GetByCommunityAsync(Guid communityId, CancellationToken ct = default)
        => db.Conversations.FirstOrDefaultAsync(c => c.CommunityId == communityId, ct);

    public Task<Conversation?> GetDirectAsync(string directKey, CancellationToken ct = default)
        => db.Conversations.FirstOrDefaultAsync(c => c.DirectKey == directKey, ct);

    public async Task<IReadOnlyList<Conversation>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = db.ConversationMembers.Where(m => m.UserId == userId && m.Status == ConversationMemberStatus.Active).Select(m => m.ConversationId);
        return await db.Conversations.AsNoTracking().Where(c => ids.Contains(c.Id)).OrderBy(c => c.CreatedAt).ToListAsync(ct);
    }

    public async Task<ConversationMember?> GetMemberAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
        => await db.ConversationMembers.FindAsync([conversationId, userId], ct);

    public async Task<IReadOnlyList<ConversationMember>> GetMembersAsync(Guid conversationId, CancellationToken ct = default)
        => await db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == conversationId).ToListAsync(ct);

    public void Add(Conversation conversation) => db.Conversations.Add(conversation);
    public void AddMember(ConversationMember member) => db.ConversationMembers.Add(member);
}

public sealed class EfMessageRepository(AppDbContext db) : IMessageRepository
{
    public async Task<Message?> GetByIdAsync(Guid id, CancellationToken ct = default) => await db.Messages.FindAsync([id], ct);

    public void Add(Message message) => db.Messages.Add(message);

    public async Task<IReadOnlyList<Message>> PageAsync(Guid conversationId, Guid? beforeMessageId, int limit, CancellationToken ct = default)
    {
        var q = db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (beforeMessageId is { } before)
        {
            var anchor = await db.Messages.AsNoTracking()
                .Where(m => m.Id == before && m.ConversationId == conversationId)
                .Select(m => new { m.CreatedAt, m.Id }).FirstOrDefaultAsync(ct);
            if (anchor is not null)
            {
                var at = anchor.CreatedAt;
                var id = anchor.Id;
                // Served by ix_messages_conversation_chronological (conversation_id, created_at DESC, id DESC).
                q = q.Where(m => m.CreatedAt < at || (m.CreatedAt == at && m.Id.CompareTo(id) < 0));
            }
        }
        return await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(limit).ToListAsync(ct);
    }
}

public sealed class EfAuditLog(AppDbContext db) : IAuditLog
{
    public void Add(AuditLogEntry entry) => db.AuditLog.Add(entry);
}

public sealed class EfNostrChallengeStore(AppDbContext db) : INostrChallengeStore
{
    public async Task SaveAsync(NostrChallenge challenge, CancellationToken ct = default)
    {
        db.NostrChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Single conditional UPDATE: exactly one concurrent caller can flip consumed_at from NULL.</summary>
    public async Task<ChallengeConsumeResult> TryConsumeAsync(string value, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.NostrChallenges
            .Where(c => c.Value == value && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, (DateTimeOffset?)now), ct);

        var challenge = await db.NostrChallenges.AsNoTracking().FirstOrDefaultAsync(c => c.Value == value, ct);
        if (challenge is null) return new ChallengeConsumeResult(ChallengeConsumeStatus.Unknown, null);
        if (rows == 1) return new ChallengeConsumeResult(ChallengeConsumeStatus.Consumed, challenge);
        return challenge.ConsumedAt is not null
            ? new ChallengeConsumeResult(ChallengeConsumeStatus.AlreadyUsed, challenge)
            : new ChallengeConsumeResult(ChallengeConsumeStatus.Expired, challenge);
    }

    public async Task PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
        => await db.NostrChallenges.Where(c => c.ExpiresAt < olderThan).ExecuteDeleteAsync(ct);
}
