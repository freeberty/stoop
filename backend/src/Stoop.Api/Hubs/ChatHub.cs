using Stoop.Abstractions;
using Stoop.Api.Auth;
using Stoop.Common;
using Stoop.Contracts;
using Stoop.Domain;
using Stoop.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Stoop.Api.Hubs;

/// <summary>Which live connections are currently subscribed to which conversation (per user).</summary>
public sealed class ConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Conversation, Guid User), HashSet<string>> _byConversation = new();
    private readonly Dictionary<string, HashSet<(Guid Conversation, Guid User)>> _byConnection = new();

    public void Join(string connectionId, Guid userId, Guid conversationId)
    {
        lock (_gate)
        {
            var key = (conversationId, userId);
            if (!_byConversation.TryGetValue(key, out var set)) _byConversation[key] = set = new();
            set.Add(connectionId);
            if (!_byConnection.TryGetValue(connectionId, out var keys)) _byConnection[connectionId] = keys = new();
            keys.Add(key);
        }
    }

    public void Leave(string connectionId, Guid userId, Guid conversationId)
    {
        lock (_gate)
        {
            var key = (conversationId, userId);
            if (_byConversation.TryGetValue(key, out var set)) { set.Remove(connectionId); if (set.Count == 0) _byConversation.Remove(key); }
            if (_byConnection.TryGetValue(connectionId, out var keys)) keys.Remove(key);
        }
    }

    /// <summary>Removes and returns every connection of the user in the conversation.</summary>
    public IReadOnlyList<string> RemoveAll(Guid conversationId, Guid userId)
    {
        lock (_gate)
        {
            var key = (conversationId, userId);
            if (!_byConversation.Remove(key, out var set)) return [];
            foreach (var conn in set) if (_byConnection.TryGetValue(conn, out var keys)) keys.Remove(key);
            return set.ToList();
        }
    }

    public void RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var keys)) return;
            foreach (var key in keys)
                if (_byConversation.TryGetValue(key, out var set)) { set.Remove(connectionId); if (set.Count == 0) _byConversation.Remove(key); }
        }
    }
}

/// <summary>Maps application exceptions to HubException so clients receive the error code.</summary>
public sealed class AppExceptionHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try { return await next(context); }
        catch (AppException ex) { throw new HubException($"{ex.Code}: {ex.Message}"); }
    }
}

[Authorize(AuthenticationSchemes = AuthDefaults.Scheme)]
public sealed class ChatHub(ChatService chat, ConnectionRegistry registry) : Hub
{
    public static string Group(Guid conversationId) => $"conv:{conversationId}";
    public static string UserGroup(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(Context.User!.GetUserId()));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Authorized against the database on every call, never by group name alone.</summary>
    public async Task JoinConversation(Guid conversationId)
    {
        var userId = Context.User!.GetUserId();
        await chat.RequireAccessAsync(conversationId, userId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(conversationId));
        registry.Join(Context.ConnectionId, userId, conversationId);

        // Close the race with a concurrent removal: re-check after subscribing.
        try { await chat.RequireAccessAsync(conversationId, userId); }
        catch (AppException)
        {
            registry.Leave(Context.ConnectionId, userId, conversationId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(conversationId));
            throw;
        }
    }

    public async Task LeaveConversation(Guid conversationId)
    {
        var userId = Context.User!.GetUserId();
        registry.Leave(Context.ConnectionId, userId, conversationId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(conversationId));
    }

    /// <summary>The sender is always the authenticated user; delivery to the group is done by ChatService's notifier.</summary>
    public Task<MessageDto> SendMessage(Guid conversationId, string content)
        => chat.SendAsync(conversationId, Context.User!.GetUserId(), content);

    public async Task Typing(Guid conversationId)
    {
        var userId = Context.User!.GetUserId();
        await chat.RequireAccessAsync(conversationId, userId);
        await Clients.OthersInGroup(Group(conversationId))
            .SendAsync("Typing", new { conversationId, userId, displayName = Context.User!.Identity?.Name });
    }
}

public sealed class SignalRRealtimeNotifier(IHubContext<ChatHub> hub, ConnectionRegistry registry) : IRealtimeNotifier
{
    public Task MessageCreatedAsync(Guid conversationId, MessageDto message, CancellationToken ct = default)
        => hub.Clients.Group(ChatHub.Group(conversationId)).SendAsync("ReceiveMessage", message, ct);

    public Task MessageEditedAsync(Guid conversationId, MessageDto message, CancellationToken ct = default)
        => hub.Clients.Group(ChatHub.Group(conversationId)).SendAsync("MessageEdited", message, ct);

    public Task MessageDeletedAsync(Guid conversationId, Guid messageId, CancellationToken ct = default)
        => hub.Clients.Group(ChatHub.Group(conversationId)).SendAsync("MessageDeleted", new { conversationId, messageId }, ct);

    /// <summary>Takes every connection of the user out of the conversation group before telling them.</summary>
    public async Task AccessRevokedAsync(Guid conversationId, Guid userId, string reason, CancellationToken ct = default)
    {
        foreach (var connectionId in registry.RemoveAll(conversationId, userId))
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, ChatHub.Group(conversationId), ct);
            await hub.Clients.Client(connectionId).SendAsync("AccessRevoked", new { conversationId, reason }, ct);
        }
    }

    public Task MembershipChangedAsync(Guid userId, Guid communityId, MemberStatus status, CancellationToken ct = default)
        => hub.Clients.Group(ChatHub.UserGroup(userId)).SendAsync("MembershipChanged", new { communityId, status = status.ToString() }, ct);
}
