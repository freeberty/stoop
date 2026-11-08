using Intersect.Domain;

namespace Intersect.Abstractions;

/// <summary>Filter shared by viewport discovery, global discovery and text search.</summary>
public sealed record CommunityFilter(
    string? Search,
    CommunityCategory? Category,
    DateOnly? From,
    DateOnly? To,
    bool IncludeUndated,
    DateTimeOffset Now,
    /// <summary>When set, the viewer's own active communities are discoverable even if not public.</summary>
    Guid? ViewerUserId);

/// <summary>Bounding box. If West &gt; East the box wraps the antimeridian.</summary>
public sealed record ViewportQuery(double West, double South, double East, double North, CommunityFilter Filter, int Limit);

public interface IUnitOfWork
{
    /// <summary>Persists tracked changes. Unique-constraint violations surface as AppException.Conflict.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default);
    Task<User?> GetByExternalIdentityAsync(string provider, string externalId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalIdentity>> GetIdentitiesAsync(Guid userId, CancellationToken ct = default);
    void Add(User user);
    void AddIdentity(ExternalIdentity identity);
}

public interface ICommunityRepository
{
    Task<Community?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Community?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, Community>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);
    void Add(Community community);

    /// <summary>Located, live, discoverable communities inside the box.</summary>
    Task<IReadOnlyList<Community>> QueryViewportAsync(ViewportQuery query, CancellationToken ct = default);
    /// <summary>Live, discoverable communities that have NO location.</summary>
    Task<IReadOnlyList<Community>> QueryGlobalAsync(CommunityFilter filter, int skip, int take, CancellationToken ct = default);
    /// <summary>Text search across located and global communities.</summary>
    Task<IReadOnlyList<Community>> SearchAsync(CommunityFilter filter, int skip, int take, CancellationToken ct = default);
    Task<IReadOnlyList<Community>> GetExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct = default);
}

public interface IMembershipRepository
{
    Task<CommunityMember?> GetAsync(Guid communityId, Guid userId, CancellationToken ct = default);
    void Add(CommunityMember member);
    Task<IReadOnlyList<CommunityMember>> ListAsync(Guid communityId, MemberStatus? status, int skip, int take, CancellationToken ct = default);
    Task<bool> ShareActiveCommunityAsync(Guid userA, Guid userB, CancellationToken ct = default);
    Task<IReadOnlyList<CommunityMember>> ListExpiredSubscriptionsAsync(DateTimeOffset now, int take, CancellationToken ct = default);
}

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Conversation?> GetByCommunityAsync(Guid communityId, CancellationToken ct = default);
    Task<Conversation?> GetDirectAsync(string directKey, CancellationToken ct = default);
    Task<IReadOnlyList<Conversation>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<ConversationMember?> GetMemberAsync(Guid conversationId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationMember>> GetMembersAsync(Guid conversationId, CancellationToken ct = default);
    void Add(Conversation conversation);
    void AddMember(ConversationMember member);
}

public interface IMessageRepository
{
    Task<Message?> GetByIdAsync(Guid id, CancellationToken ct = default);
    void Add(Message message);
    /// <summary>
    /// Newest-first page strictly older than <paramref name="beforeMessageId"/> (or from the newest
    /// message when null). Ordered by (CreatedAt, Id) descending.
    /// </summary>
    Task<IReadOnlyList<Message>> PageAsync(Guid conversationId, Guid? beforeMessageId, int limit, CancellationToken ct = default);
}

public interface IAuditLog
{
    void Add(AuditLogEntry entry);
}

public enum ChallengeConsumeStatus { Consumed, Unknown, Expired, AlreadyUsed }

public sealed record ChallengeConsumeResult(ChallengeConsumeStatus Status, NostrChallenge? Challenge);

public interface INostrChallengeStore
{
    /// <summary>Persist immediately (not part of a unit of work).</summary>
    Task SaveAsync(NostrChallenge challenge, CancellationToken ct = default);
    /// <summary>Atomically marks the challenge consumed. Exactly one concurrent caller can win.</summary>
    Task<ChallengeConsumeResult> TryConsumeAsync(string value, DateTimeOffset now, CancellationToken ct = default);
    Task PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>
/// Pushes real-time events to connected clients. The default implementation does nothing;
/// the API project provides a SignalR implementation.
/// </summary>
public interface IRealtimeNotifier
{
    Task MessageCreatedAsync(Guid conversationId, Contracts.MessageDto message, CancellationToken ct = default);
    Task MessageEditedAsync(Guid conversationId, Contracts.MessageDto message, CancellationToken ct = default);
    Task MessageDeletedAsync(Guid conversationId, Guid messageId, CancellationToken ct = default);
    /// <summary>Removes the user from live delivery of the conversation and tells their connections.</summary>
    Task AccessRevokedAsync(Guid conversationId, Guid userId, string reason, CancellationToken ct = default);
    Task MembershipChangedAsync(Guid userId, Guid communityId, MemberStatus status, CancellationToken ct = default);
}

public sealed class NoOpRealtimeNotifier : IRealtimeNotifier
{
    public Task MessageCreatedAsync(Guid conversationId, Contracts.MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageEditedAsync(Guid conversationId, Contracts.MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageDeletedAsync(Guid conversationId, Guid messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task AccessRevokedAsync(Guid conversationId, Guid userId, string reason, CancellationToken ct = default) => Task.CompletedTask;
    public Task MembershipChangedAsync(Guid userId, Guid communityId, MemberStatus status, CancellationToken ct = default) => Task.CompletedTask;
}
