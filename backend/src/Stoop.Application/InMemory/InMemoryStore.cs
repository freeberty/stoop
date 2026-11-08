using Stoop.Abstractions;
using Stoop.Common;
using Stoop.Domain;
using Stoop.Services;

namespace Stoop.InMemory;

/// <summary>
/// Thread-safe in-memory implementation of every repository. Used by tests and by
/// <c>Persistence:Provider=InMemory</c> for running the API without PostgreSQL.
/// Entities are shared instances, so mutations are visible immediately and SaveChanges is a no-op.
/// </summary>
public sealed class InMemoryStore :
    IUnitOfWork, IUserRepository, ICommunityRepository, IMembershipRepository,
    IConversationRepository, IMessageRepository, IAuditLog, INostrChallengeStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, User> _users = new();
    private readonly List<ExternalIdentity> _identities = new();
    private readonly Dictionary<Guid, Community> _communities = new();
    private readonly Dictionary<(Guid, Guid), CommunityMember> _members = new();
    private readonly Dictionary<Guid, Conversation> _conversations = new();
    private readonly List<ConversationMember> _conversationMembers = new();
    private readonly List<Message> _messages = new();
    private readonly List<AuditLogEntry> _audit = new();
    private readonly Dictionary<string, NostrChallenge> _challenges = new();

    public IReadOnlyList<AuditLogEntry> AuditEntries { get { lock (_gate) return _audit.ToList(); } }
    public IReadOnlyList<Community> AllCommunities { get { lock (_gate) return _communities.Values.ToList(); } }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ------------------------------------------------------------ users

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) { lock (_gate) return Task.FromResult(_users.GetValueOrDefault(id)); }

    public Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_users.Values.FirstOrDefault(u => u.Email == normalizedEmail)); }

    public Task<User?> GetByExternalIdentityAsync(string provider, string externalId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var identity = _identities.FirstOrDefault(i => i.Provider == provider && i.ExternalIdentifier == externalId);
            return Task.FromResult(identity is null ? null : _users.GetValueOrDefault(identity.UserId));
        }
    }

    Task<IReadOnlyDictionary<Guid, User>> IUserRepository.GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<Guid, User> map = ids.Distinct().Where(_users.ContainsKey).ToDictionary(i => i, i => _users[i]);
            return Task.FromResult(map);
        }
    }

    public Task<IReadOnlyList<ExternalIdentity>> GetIdentitiesAsync(Guid userId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<ExternalIdentity>>(_identities.Where(i => i.UserId == userId).ToList()); }

    public void Add(User user)
    {
        lock (_gate)
        {
            if (user.Email is not null && _users.Values.Any(u => u.Email == user.Email))
                throw AppException.Conflict("email_taken", "An account with this email already exists.");
            _users[user.Id] = user;
        }
    }

    public void AddIdentity(ExternalIdentity identity)
    {
        lock (_gate)
        {
            if (_identities.Any(i => i.Provider == identity.Provider && i.ExternalIdentifier == identity.ExternalIdentifier))
                throw AppException.Conflict("identity_already_linked", "This identity is already linked.");
            _identities.Add(identity);
        }
    }

    // ------------------------------------------------------------ communities

    Task<Community?> ICommunityRepository.GetByIdAsync(Guid id, CancellationToken ct) { lock (_gate) return Task.FromResult(_communities.GetValueOrDefault(id)); }

    public Task<Community?> GetBySlugAsync(string slug, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_communities.Values.FirstOrDefault(c => c.Slug == slug)); }

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_communities.Values.Any(c => c.Slug == slug)); }

    Task<IReadOnlyDictionary<Guid, Community>> ICommunityRepository.GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<Guid, Community> map = ids.Distinct().Where(_communities.ContainsKey).ToDictionary(i => i, i => _communities[i]);
            return Task.FromResult(map);
        }
    }

    public Task<long> CountAsync(CancellationToken ct = default) { lock (_gate) return Task.FromResult((long)_communities.Count); }

    public void Add(Community community)
    {
        lock (_gate)
        {
            if (_communities.Values.Any(c => c.Slug == community.Slug))
                throw AppException.Conflict("slug_taken", "Slug already exists.");
            _communities[community.Id] = community;
        }
    }

    private bool Discoverable(Community c, CommunityFilter f)
    {
        if (!AccessPolicy.IsLive(c, f.Now)) return false;
        var visible = c.Visibility == Visibility.Public
            || (f.ViewerUserId is { } v && _members.TryGetValue((c.Id, v), out var m) && m.Status == MemberStatus.Active);
        if (!visible) return false;
        if (f.Category is { } cat && c.Category != cat) return false;
        return AccessPolicy.MatchesContext(c, f.From, f.To, f.IncludeUndated) && AccessPolicy.MatchesSearch(c, f.Search);
    }

    public Task<IReadOnlyList<Community>> QueryViewportAsync(ViewportQuery q, CancellationToken ct = default)
    {
        lock (_gate)
        {
            bool InBox(Community c)
            {
                if (!c.HasLocation) return false;
                var lat = c.Latitude!.Value;
                var lon = c.Longitude!.Value;
                if (lat < q.South || lat > q.North) return false;
                return q.West <= q.East ? lon >= q.West && lon <= q.East : lon >= q.West || lon <= q.East; // antimeridian wrap
            }
            var list = _communities.Values.Where(c => InBox(c) && Discoverable(c, q.Filter))
                .OrderByDescending(c => c.MemberCount).ThenBy(c => c.Name, StringComparer.Ordinal).Take(q.Limit).ToList();
            return Task.FromResult<IReadOnlyList<Community>>(list);
        }
    }

    public Task<IReadOnlyList<Community>> QueryGlobalAsync(CommunityFilter f, int skip, int take, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _communities.Values.Where(c => !c.HasLocation && Discoverable(c, f))
                .OrderByDescending(c => c.MemberCount).ThenBy(c => c.Name, StringComparer.Ordinal).Skip(skip).Take(take).ToList();
            return Task.FromResult<IReadOnlyList<Community>>(list);
        }
    }

    public Task<IReadOnlyList<Community>> SearchAsync(CommunityFilter f, int skip, int take, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _communities.Values.Where(c => Discoverable(c, f))
                .OrderByDescending(c => c.Name.Contains(f.Search ?? "", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => c.MemberCount).ThenBy(c => c.Name, StringComparer.Ordinal)
                .Skip(skip).Take(take).ToList();
            return Task.FromResult<IReadOnlyList<Community>>(list);
        }
    }

    public Task<IReadOnlyList<Community>> GetExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _communities.Values.Where(c => c.Status == CommunityStatus.Active && c.ExpiresAt is { } e && e <= now).Take(take).ToList();
            return Task.FromResult<IReadOnlyList<Community>>(list);
        }
    }

    // ------------------------------------------------------------ memberships

    public Task<CommunityMember?> GetAsync(Guid communityId, Guid userId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_members.GetValueOrDefault((communityId, userId))); }

    public void Add(CommunityMember member) { lock (_gate) _members[(member.CommunityId, member.UserId)] = member; }

    public Task<IReadOnlyList<CommunityMember>> ListAsync(Guid communityId, MemberStatus? status, int skip, int take, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _members.Values.Where(m => m.CommunityId == communityId && (status is null || m.Status == status))
                .OrderBy(m => m.UpdatedAt).ThenBy(m => m.UserId).Skip(skip).Take(take).ToList();
            return Task.FromResult<IReadOnlyList<CommunityMember>>(list);
        }
    }

    public Task<bool> ShareActiveCommunityAsync(Guid a, Guid b, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var mine = _members.Values.Where(m => m.UserId == a && m.Status == MemberStatus.Active).Select(m => m.CommunityId).ToHashSet();
            return Task.FromResult(_members.Values.Any(m => m.UserId == b && m.Status == MemberStatus.Active && mine.Contains(m.CommunityId)));
        }
    }

    public Task<IReadOnlyList<CommunityMember>> ListExpiredSubscriptionsAsync(DateTimeOffset now, int take, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var paid = _communities.Values.Where(c => c.PricingMode == PricingMode.Paid).Select(c => c.Id).ToHashSet();
            var list = _members.Values.Where(m => m.Status == MemberStatus.Active && m.Role != MemberRole.Owner
                && paid.Contains(m.CommunityId) && m.PaidUntil is { } p && p <= now).Take(take).ToList();
            return Task.FromResult<IReadOnlyList<CommunityMember>>(list);
        }
    }

    // ------------------------------------------------------------ conversations

    Task<Conversation?> IConversationRepository.GetByIdAsync(Guid id, CancellationToken ct) { lock (_gate) return Task.FromResult(_conversations.GetValueOrDefault(id)); }

    public Task<Conversation?> GetByCommunityAsync(Guid communityId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_conversations.Values.FirstOrDefault(c => c.CommunityId == communityId)); }

    public Task<Conversation?> GetDirectAsync(string directKey, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_conversations.Values.FirstOrDefault(c => c.DirectKey == directKey)); }

    public Task<IReadOnlyList<Conversation>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var ids = _conversationMembers.Where(m => m.UserId == userId && m.Status == ConversationMemberStatus.Active).Select(m => m.ConversationId).ToHashSet();
            return Task.FromResult<IReadOnlyList<Conversation>>(_conversations.Values.Where(c => ids.Contains(c.Id)).OrderBy(c => c.CreatedAt).ToList());
        }
    }

    public Task<ConversationMember?> GetMemberAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_conversationMembers.FirstOrDefault(m => m.ConversationId == conversationId && m.UserId == userId)); }

    public Task<IReadOnlyList<ConversationMember>> GetMembersAsync(Guid conversationId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<ConversationMember>>(_conversationMembers.Where(m => m.ConversationId == conversationId).ToList()); }

    public void Add(Conversation conversation) { lock (_gate) _conversations[conversation.Id] = conversation; }
    public void AddMember(ConversationMember member) { lock (_gate) _conversationMembers.Add(member); }

    // ------------------------------------------------------------ messages

    Task<Message?> IMessageRepository.GetByIdAsync(Guid id, CancellationToken ct) { lock (_gate) return Task.FromResult(_messages.FirstOrDefault(m => m.Id == id)); }

    public void Add(Message message) { lock (_gate) _messages.Add(message); }

    public Task<IReadOnlyList<Message>> PageAsync(Guid conversationId, Guid? beforeMessageId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var q = _messages.Where(m => m.ConversationId == conversationId);
            if (beforeMessageId is { } bid)
            {
                var anchor = _messages.FirstOrDefault(m => m.Id == bid && m.ConversationId == conversationId);
                if (anchor is not null)
                    q = q.Where(m => m.CreatedAt < anchor.CreatedAt || (m.CreatedAt == anchor.CreatedAt && m.Id.CompareTo(anchor.Id) < 0));
            }
            var list = q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(limit).ToList();
            return Task.FromResult<IReadOnlyList<Message>>(list);
        }
    }

    // ------------------------------------------------------------ audit / challenges

    public void Add(AuditLogEntry entry) { lock (_gate) _audit.Add(entry); }

    public Task SaveAsync(NostrChallenge challenge, CancellationToken ct = default)
    { lock (_gate) _challenges[challenge.Value] = challenge; return Task.CompletedTask; }

    public Task<ChallengeConsumeResult> TryConsumeAsync(string value, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_challenges.TryGetValue(value, out var c)) return Task.FromResult(new ChallengeConsumeResult(ChallengeConsumeStatus.Unknown, null));
            if (c.ConsumedAt is not null) return Task.FromResult(new ChallengeConsumeResult(ChallengeConsumeStatus.AlreadyUsed, c));
            if (c.ExpiresAt <= now) return Task.FromResult(new ChallengeConsumeResult(ChallengeConsumeStatus.Expired, c));
            c.ConsumedAt = now;
            return Task.FromResult(new ChallengeConsumeResult(ChallengeConsumeStatus.Consumed, c));
        }
    }

    public Task PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var key in _challenges.Where(kv => kv.Value.ExpiresAt < olderThan).Select(kv => kv.Key).ToList()) _challenges.Remove(key);
        }
        return Task.CompletedTask;
    }
}
