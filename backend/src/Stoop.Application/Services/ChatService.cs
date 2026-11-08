using System.Collections.Concurrent;
using Stoop.Abstractions;
using Stoop.Common;
using Stoop.Contracts;
using Stoop.Domain;
using Microsoft.Extensions.Options;

namespace Stoop.Services;

/// <summary>Sliding-window per-user limiter for chat messages.</summary>
public sealed class MessageRateLimiter(IOptions<ChatOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    public bool TryAcquire(Guid userId)
    {
        var opt = options.Value;
        var now = time.GetUtcNow();
        var window = TimeSpan.FromSeconds(opt.RateLimitWindowSeconds);
        var q = _hits.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= opt.RateLimitMessages) return false;
            q.Enqueue(now);
            return true;
        }
    }
}

public sealed class ChatService(
    IConversationRepository conversations,
    IMessageRepository messages,
    ICommunityRepository communities,
    IMembershipRepository members,
    IUserRepository users,
    IAuditLog audit,
    IUnitOfWork uow,
    IRealtimeNotifier notifier,
    MessageRateLimiter limiter,
    IOptions<ChatOptions> options,
    TimeProvider time)
{
    private readonly ChatOptions _opt = options.Value;

    // ------------------------------------------------------------ authorization (used by REST and the hub)

    /// <summary>
    /// Authoritative check, run on every operation (never cached in a SignalR group name).
    /// Community chats defer to AccessPolicy/CommunityMember; direct chats use conversation membership.
    /// </summary>
    public async Task<Conversation> RequireAccessAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conv = await conversations.GetByIdAsync(conversationId, ct) ?? throw ConvNotFound();
        if (conv.Status != ConversationStatus.Active) throw AppException.Conflict("conversation_closed", "This conversation is closed.");

        if (conv.Type == ConversationType.Direct)
        {
            var cm = await conversations.GetMemberAsync(conversationId, userId, ct);
            if (cm is not { Status: ConversationMemberStatus.Active }) throw ConvNotFound();
            return conv;
        }

        var community = await communities.GetByIdAsync(conv.CommunityId ?? Guid.Empty, ct) ?? throw ConvNotFound();
        var m = await members.GetAsync(community.Id, userId, ct);
        if (m is null) throw ConvNotFound(); // do not reveal the conversation to outsiders
        if (!AccessPolicy.CanAccessChat(community, m, time.GetUtcNow()))
            throw AppException.Forbidden("chat_access_denied", "You do not have access to this conversation.");
        return conv;
    }

    // ------------------------------------------------------------ queries

    public async Task<IReadOnlyList<ConversationDto>> ListConversationsAsync(Guid userId, CancellationToken ct = default)
    {
        var list = await conversations.ListForUserAsync(userId, ct);
        var result = new List<ConversationDto>();
        foreach (var conv in list)
        {
            if (conv.Status != ConversationStatus.Active) continue;
            if (conv.Type == ConversationType.Community)
            {
                var community = await communities.GetByIdAsync(conv.CommunityId ?? Guid.Empty, ct);
                var m = community is null ? null : await members.GetAsync(community.Id, userId, ct);
                if (community is null || !AccessPolicy.CanAccessChat(community, m, time.GetUtcNow())) continue;
                result.Add(new ConversationDto(conv.Id, conv.Type, conv.CommunityId, community.Name, conv.CreatedAt));
            }
            else
            {
                var others = (await conversations.GetMembersAsync(conv.Id, ct)).Where(x => x.UserId != userId).ToList();
                var names = await users.GetByIdsAsync(others.Select(o => o.UserId), ct);
                var title = string.Join(", ", names.Values.Select(u => u.DisplayName));
                result.Add(new ConversationDto(conv.Id, conv.Type, null, title.Length == 0 ? "Direct message" : title, conv.CreatedAt));
            }
        }
        return result;
    }

    public async Task<ConversationDto> GetConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conv = await RequireAccessAsync(conversationId, userId, ct);
        var all = await ListConversationsAsync(userId, ct);
        return all.FirstOrDefault(c => c.Id == conv.Id) ?? throw ConvNotFound();
    }

    public async Task<MessagePageDto> GetMessagesAsync(Guid conversationId, Guid userId, Guid? before, int limit, CancellationToken ct = default)
    {
        await RequireAccessAsync(conversationId, userId, ct);
        limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, _opt.MaxPageSize);
        var rows = await messages.PageAsync(conversationId, before, limit + 1, ct); // newest first
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var names = await users.GetByIdsAsync(page.Select(p => p.SenderUserId), ct);
        var items = page.AsEnumerable().Reverse().Select(m => ToDto(m, names)).ToList();
        return new MessagePageDto(items, hasMore ? page[^1].Id : null);
    }

    // ------------------------------------------------------------ commands

    public async Task<MessageDto> SendAsync(Guid conversationId, Guid senderUserId, string? content, CancellationToken ct = default)
    {
        var text = ValidateContent(content);
        await RequireAccessAsync(conversationId, senderUserId, ct);
        if (!limiter.TryAcquire(senderUserId))
            throw AppException.RateLimited("message_rate_limited", "You are sending messages too quickly.");

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            SenderUserId = senderUserId, // always the authenticated user, never client supplied
            Content = text,
            CreatedAt = time.GetUtcNow(),
            Status = MessageStatus.Active,
        };
        messages.Add(message);
        await uow.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(message, ct);
        await notifier.MessageCreatedAsync(conversationId, dto, ct);
        return dto;
    }

    public async Task<MessageDto> EditAsync(Guid messageId, Guid userId, string? content, CancellationToken ct = default)
    {
        var text = ValidateContent(content);
        var message = await messages.GetByIdAsync(messageId, ct) ?? throw MsgNotFound();
        await RequireAccessAsync(message.ConversationId, userId, ct);
        if (message.Status == MessageStatus.Deleted) throw MsgNotFound();
        if (message.SenderUserId != userId) throw AppException.Forbidden("not_sender", "Only the sender can edit a message.");

        message.Content = text;
        message.EditedAt = time.GetUtcNow();
        await uow.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(message, ct);
        await notifier.MessageEditedAsync(message.ConversationId, dto, ct);
        return dto;
    }

    public async Task DeleteAsync(Guid messageId, Guid userId, CancellationToken ct = default)
    {
        var message = await messages.GetByIdAsync(messageId, ct) ?? throw MsgNotFound();
        var conv = await RequireAccessAsync(message.ConversationId, userId, ct);
        if (message.Status == MessageStatus.Deleted) return;

        var moderation = false;
        if (message.SenderUserId != userId)
        {
            var adminMember = conv.CommunityId is { } cid ? await members.GetAsync(cid, userId, ct) : null;
            if (!AccessPolicy.IsAdmin(adminMember))
                throw AppException.Forbidden("not_allowed", "Only the sender or a community administrator can delete this message.");
            moderation = true;
        }

        var now = time.GetUtcNow();
        message.Status = MessageStatus.Deleted;
        message.DeletedAt = now;
        message.DeletedByUserId = userId;
        message.Content = "";
        if (moderation)
            audit.Add(new AuditLogEntry
            {
                Id = Guid.CreateVersion7(), At = now, ActorUserId = userId, CommunityId = conv.CommunityId,
                TargetUserId = message.SenderUserId, Action = "message.moderate_delete", Details = $"message={message.Id}",
            });
        await uow.SaveChangesAsync(ct);
        await notifier.MessageDeletedAsync(message.ConversationId, message.Id, ct);
    }

    /// <summary>Direct messages are only allowed between users who share an active community (anti-spam).</summary>
    public async Task<ConversationDto> GetOrCreateDirectAsync(Guid userId, Guid otherUserId, CancellationToken ct = default)
    {
        if (userId == otherUserId) throw AppException.Validation("invalid_target", "You cannot message yourself.");
        var other = await users.GetByIdAsync(otherUserId, ct);
        if (other is null || other.Status != UserStatus.Active) throw AppException.NotFound("user_not_found", "User not found.");
        if (!await members.ShareActiveCommunityAsync(userId, otherUserId, ct))
            throw AppException.Forbidden("no_shared_community", "You can only message people you share a community with.");

        var key = string.CompareOrdinal(userId.ToString(), otherUserId.ToString()) < 0
            ? $"{userId}:{otherUserId}" : $"{otherUserId}:{userId}";
        var conv = await conversations.GetDirectAsync(key, ct);
        var now = time.GetUtcNow();
        if (conv is null)
        {
            conv = new Conversation { Id = Guid.CreateVersion7(), Type = ConversationType.Direct, DirectKey = key, CreatedAt = now, Status = ConversationStatus.Active };
            conversations.Add(conv);
            foreach (var id in new[] { userId, otherUserId })
                conversations.AddMember(new ConversationMember { ConversationId = conv.Id, UserId = id, JoinedAt = now, Status = ConversationMemberStatus.Active });
            await uow.SaveChangesAsync(ct);
        }
        return new ConversationDto(conv.Id, conv.Type, null, other.DisplayName, conv.CreatedAt);
    }

    // ------------------------------------------------------------ helpers

    private string ValidateContent(string? content)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0) throw AppException.Validation("empty_message", "Message cannot be empty.");
        if (text.Length > _opt.MaxMessageLength) throw AppException.Validation("message_too_long", $"Message must be at most {_opt.MaxMessageLength} characters.");
        if (text.Contains('\0')) throw AppException.Validation("invalid_message", "Message contains invalid characters.");
        return text;
    }

    private async Task<MessageDto> ToDtoAsync(Message m, CancellationToken ct)
        => ToDto(m, await users.GetByIdsAsync([m.SenderUserId], ct));

    private static MessageDto ToDto(Message m, IReadOnlyDictionary<Guid, User> names)
    {
        var deleted = m.Status == MessageStatus.Deleted;
        return new MessageDto(m.Id, m.ConversationId, m.SenderUserId,
            names.TryGetValue(m.SenderUserId, out var u) ? u.DisplayName : "unknown",
            deleted ? "" : m.Content, m.CreatedAt, m.EditedAt, deleted);
    }

    private static AppException ConvNotFound() => AppException.NotFound("conversation_not_found", "Conversation not found.");
    private static AppException MsgNotFound() => AppException.NotFound("message_not_found", "Message not found.");
}
