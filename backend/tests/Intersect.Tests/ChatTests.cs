using Intersect.Common;
using Intersect.Contracts;
using Intersect.Domain;
using Xunit;

namespace Intersect.Tests;

public class ChatTests
{
    private sealed record Ctx(World W, Guid Owner, Guid Member, Guid Community, Guid Conversation);

    private static async Task<Ctx> Setup(Action<Common.AuthOptions, Common.ChatOptions>? configure = null)
    {
        var w = new World(configure);
        var owner = await w.NewEmailUserAsync("Owner");
        var member = await w.NewEmailUserAsync("Member");
        var c = await w.NewCommunityAsync(owner, World.Req("Chat Club"));
        await w.Membership.JoinAsync(member, c.Community.Id);
        return new Ctx(w, owner, member, c.Community.Id, c.ConversationId!.Value);
    }


    [Fact]
    public async Task Message_is_persisted_delivered_in_real_time_and_attributed_to_the_authenticated_sender()
    {
        var x = await Setup();
        var sent = await x.W.Chat.SendAsync(x.Conversation, x.Owner, "  hello Lisbon  ");

        Assert.Equal("hello Lisbon", sent.Content);
        Assert.Equal(x.Owner, sent.SenderUserId);
        Assert.Equal("Owner", sent.SenderDisplayName);
        Assert.Equal(x.W.Time.GetUtcNow(), sent.CreatedAt);

        var live = Assert.Single(x.W.Notifier.Created);
        Assert.Equal(x.Conversation, live.Conversation);
        Assert.Equal(sent.Id, live.Message.Id);

        var history = await x.W.Chat.GetMessagesAsync(x.Conversation, x.Member, null, 50);
        Assert.Equal(sent.Id, history.Items.Single().Id);
    }

    [Fact]
    public async Task History_is_paginated_newest_first_by_cursor_and_returned_in_chronological_order()
    {
        var x = await Setup(configure: (_, chat) => { chat.RateLimitMessages = 1000; });
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            ids.Add((await x.W.Chat.SendAsync(x.Conversation, i % 2 == 0 ? x.Owner : x.Member, $"m{i}")).Id);
            x.W.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var p1 = await x.W.Chat.GetMessagesAsync(x.Conversation, x.Member, null, 10);
        Assert.Equal(10, p1.Items.Count);
        Assert.Equal("m15", p1.Items[0].Content);
        Assert.Equal("m24", p1.Items[^1].Content);
        Assert.NotNull(p1.NextBefore);

        var p2 = await x.W.Chat.GetMessagesAsync(x.Conversation, x.Member, p1.NextBefore, 10);
        Assert.Equal("m5", p2.Items[0].Content);
        Assert.Equal("m14", p2.Items[^1].Content);

        var p3 = await x.W.Chat.GetMessagesAsync(x.Conversation, x.Member, p2.NextBefore, 10);
        Assert.Equal(5, p3.Items.Count);
        Assert.Null(p3.NextBefore);
        Assert.Equal("m0", p3.Items[0].Content);
    }

    [Fact]
    public async Task Outsiders_cannot_read_or_send_and_do_not_learn_the_conversation_exists()
    {
        var x = await Setup();
        var outsider = await x.W.NewEmailUserAsync();
        await Err.Expect(ErrorKind.NotFound, "conversation_not_found", () => x.W.Chat.SendAsync(x.Conversation, outsider, "hi"));
        await Err.Expect("conversation_not_found", () => x.W.Chat.GetMessagesAsync(x.Conversation, outsider, null, 10));
        await Err.Expect("conversation_not_found", () => x.W.Chat.RequireAccessAsync(x.Conversation, outsider));
    }

    [Fact]
    public async Task Removed_member_loses_history_send_and_reconnect_access_immediately()
    {
        var x = await Setup();
        await x.W.Chat.SendAsync(x.Conversation, x.Owner, "secret");
        await x.W.Membership.RemoveAsync(x.Owner, x.Community, x.Member, ban: false);

        await Err.Expect(ErrorKind.Forbidden, "chat_access_denied", () => x.W.Chat.SendAsync(x.Conversation, x.Member, "still here?"));
        await Err.Expect("chat_access_denied", () => x.W.Chat.GetMessagesAsync(x.Conversation, x.Member, null, 10));
        // This is exactly what the SignalR hub calls on JoinConversation, so reconnecting is rejected.
        await Err.Expect("chat_access_denied", () => x.W.Chat.RequireAccessAsync(x.Conversation, x.Member));
        Assert.Empty(await x.W.Chat.ListConversationsAsync(x.Member));
        Assert.Equal(1, x.W.Notifier.Revoked.Count(r => r.User == x.Member));
    }

    [Fact]
    public async Task Banned_member_cannot_chat_even_after_trying_to_rejoin()
    {
        var x = await Setup();
        await x.W.Membership.RemoveAsync(x.Owner, x.Community, x.Member, ban: true);
        await Err.Expect("banned", () => x.W.Membership.JoinAsync(x.Member, x.Community));
        await Err.Expect("chat_access_denied", () => x.W.Chat.SendAsync(x.Conversation, x.Member, "hello"));
    }

    [Fact]
    public async Task Pending_and_invited_users_have_no_chat_access()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var pending = await w.NewEmailUserAsync();
        var invited = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Gated", access: AccessMode.ApprovalRequired));
        await w.Membership.RequestAccessAsync(pending, c.Community.Id);
        await w.Membership.InviteAsync(owner, c.Community.Id, new InviteRequest(invited, null));

        await Err.Expect("chat_access_denied", () => w.Chat.SendAsync(c.ConversationId!.Value, pending, "let me in"));
        await Err.Expect("chat_access_denied", () => w.Chat.SendAsync(c.ConversationId!.Value, invited, "hi"));
    }

    [Fact]
    public async Task Messages_are_rate_limited_per_user_with_a_sliding_window()
    {
        var x = await Setup(configure: (_, chat) => { chat.RateLimitMessages = 3; chat.RateLimitWindowSeconds = 10; });
        for (var i = 0; i < 3; i++) await x.W.Chat.SendAsync(x.Conversation, x.Owner, $"m{i}");
        await Err.Expect(ErrorKind.RateLimited, "message_rate_limited", () => x.W.Chat.SendAsync(x.Conversation, x.Owner, "too many"));

        await x.W.Chat.SendAsync(x.Conversation, x.Member, "different user is unaffected");

        x.W.Time.Advance(TimeSpan.FromSeconds(11));
        await x.W.Chat.SendAsync(x.Conversation, x.Owner, "after the window");
    }

    [Fact]
    public async Task Message_content_validation()
    {
        var x = await Setup();
        await Err.Expect("empty_message", () => x.W.Chat.SendAsync(x.Conversation, x.Owner, "   "));
        await Err.Expect("message_too_long", () => x.W.Chat.SendAsync(x.Conversation, x.Owner, new string('a', 4001)));
        await Err.Expect("invalid_message", () => x.W.Chat.SendAsync(x.Conversation, x.Owner, "bad\0byte"));
    }

    [Fact]
    public async Task Sender_can_edit_but_others_cannot()
    {
        var x = await Setup();
        var msg = await x.W.Chat.SendAsync(x.Conversation, x.Member, "typo");
        x.W.Time.Advance(TimeSpan.FromMinutes(1));

        var edited = await x.W.Chat.EditAsync(msg.Id, x.Member, "fixed");
        Assert.Equal("fixed", edited.Content);
        Assert.NotNull(edited.EditedAt);
        Assert.Contains(msg.Id, x.W.Notifier.Edited);

        await Err.Expect("not_sender", () => x.W.Chat.EditAsync(msg.Id, x.Owner, "hijack"));
    }

    [Fact]
    public async Task Delete_by_sender_and_moderation_delete_by_admin_is_audited()
    {
        var x = await Setup(configure: (_, chat) => chat.RateLimitMessages = 100);
        var mine = await x.W.Chat.SendAsync(x.Conversation, x.Member, "mine");
        var theirs = await x.W.Chat.SendAsync(x.Conversation, x.Member, "offensive");
        var ownerMsg = await x.W.Chat.SendAsync(x.Conversation, x.Owner, "owner says hi");

        await x.W.Chat.DeleteAsync(mine.Id, x.Member);
        await Err.Expect("not_allowed", () => x.W.Chat.DeleteAsync(ownerMsg.Id, x.Member)); // member cannot moderate
        await x.W.Chat.DeleteAsync(theirs.Id, x.Owner);                                    // admin can

        var history = await x.W.Chat.GetMessagesAsync(x.Conversation, x.Owner, null, 50);
        var byId = history.Items.ToDictionary(m => m.Id);
        Assert.True(byId[mine.Id].Deleted);
        Assert.Equal("", byId[theirs.Id].Content);
        Assert.False(byId[ownerMsg.Id].Deleted);
        Assert.Contains("message.moderate_delete", x.W.Store.AuditEntries.Select(a => a.Action));
        Assert.Equal(2, x.W.Notifier.Deleted.Count);

        await Err.Expect("message_not_found", () => x.W.Chat.EditAsync(mine.Id, x.Member, "resurrect"));
    }

    [Fact]
    public async Task Chat_stops_when_the_community_expires()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Short Lived", expires: w.Time.GetUtcNow().AddHours(1)));
        await w.Chat.SendAsync(c.ConversationId!.Value, owner, "alive");
        w.Time.Advance(TimeSpan.FromHours(2));
        await Err.Expect("chat_access_denied", () => w.Chat.SendAsync(c.ConversationId!.Value, owner, "dead"));
    }

    [Fact]
    public async Task Conversation_list_shows_community_and_direct_conversations()
    {
        var x = await Setup();
        var direct = await x.W.Chat.GetOrCreateDirectAsync(x.Owner, x.Member);
        var list = await x.W.Chat.ListConversationsAsync(x.Owner);
        Assert.Equal(2, list.Count);
        Assert.Contains("Chat Club", list.Select(c => c.Title));
        Assert.Contains("Member", list.Select(c => c.Title));
        Assert.Equal(direct.Id, (await x.W.Chat.GetOrCreateDirectAsync(x.Member, x.Owner)).Id); // idempotent, order independent
    }

    [Fact]
    public async Task Direct_messages_need_a_shared_community_and_are_private()
    {
        var x = await Setup();
        var stranger = await x.W.NewEmailUserAsync();
        await Err.Expect("no_shared_community", () => x.W.Chat.GetOrCreateDirectAsync(stranger, x.Owner));
        await Err.Expect("invalid_target", () => x.W.Chat.GetOrCreateDirectAsync(x.Owner, x.Owner));

        var dm = await x.W.Chat.GetOrCreateDirectAsync(x.Owner, x.Member);
        await x.W.Chat.SendAsync(dm.Id, x.Owner, "just us");
        Assert.Equal("just us", (await x.W.Chat.GetMessagesAsync(dm.Id, x.Member, null, 10)).Items.Single().Content);
        await Err.Expect("conversation_not_found", () => x.W.Chat.GetMessagesAsync(dm.Id, stranger, null, 10));
        await Err.Expect("conversation_not_found", () => x.W.Chat.SendAsync(dm.Id, stranger, "intrude"));
    }
}
