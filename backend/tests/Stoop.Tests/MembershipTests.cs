using Stoop.Common;
using Stoop.Contracts;
using Stoop.Domain;
using Xunit;

namespace Stoop.Tests;

public class MembershipTests
{
    private static async Task<(World w, Guid owner, Guid user, Guid community)> Setup(
        AccessMode access = AccessMode.Open, Visibility vis = Visibility.Public,
        PricingMode pricing = PricingMode.Free, decimal? price = null)
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync("Owner");
        var user = await w.NewEmailUserAsync("Member");
        var c = await w.NewCommunityAsync(owner, World.Req("Club", access: access, vis: vis, pricing: pricing, price: price));
        return (w, owner, user, c.Community.Id);
    }

    private static async Task<bool> CanChat(World w, Guid community, Guid user)
    {
        var conv = await w.Store.GetByCommunityAsync(community);
        try { await w.Chat.RequireAccessAsync(conv!.Id, user); return true; }
        catch (AppException) { return false; }
    }

    [Fact]
    public async Task Open_membership_grants_access_and_updates_count()
    {
        var (w, _, user, c) = await Setup();
        var res = await w.Membership.JoinAsync(user, c);
        Assert.Equal(MemberStatus.Active, res.Status);
        Assert.True(await CanChat(w, c, user));
        Assert.Equal(2, (await w.Communities.GetAsync(user, c.ToString())).Community.MemberCount);

        await w.Membership.JoinAsync(user, c); // idempotent
        Assert.Equal(2, (await w.Communities.GetAsync(user, c.ToString())).Community.MemberCount);
    }

    [Fact]
    public async Task Approval_workflow()
    {
        var (w, owner, user, c) = await Setup(AccessMode.ApprovalRequired);
        await Err.Expect("approval_required", () => w.Membership.JoinAsync(user, c));

        var req = await w.Membership.RequestAccessAsync(user, c);
        Assert.Equal(MemberStatus.Pending, req.Status);
        Assert.False(await CanChat(w, c, user));

        var stranger = await w.NewEmailUserAsync();
        await Err.Expect("not_admin", () => w.Membership.ApproveAsync(user, c, user));
        await Err.Expect("not_admin", () => w.Membership.ApproveAsync(stranger, c, user));

        var pending = await w.Membership.ListMembersAsync(owner, c, MemberStatus.Pending, 0, 20);
        Assert.Equal(user, pending.Single().UserId);

        var approved = await w.Membership.ApproveAsync(owner, c, user);
        Assert.Equal(MemberStatus.Active, approved.Status);
        Assert.True(await CanChat(w, c, user));
        Assert.Contains("member.approve", w.Store.AuditEntries.Select(a => a.Action));
    }

    [Fact]
    public async Task Request_access_is_only_for_approval_communities_and_is_idempotent()
    {
        var (w, _, user, c) = await Setup();
        await Err.Expect("approval_not_required", () => w.Membership.RequestAccessAsync(user, c));

        var (w2, _, user2, c2) = await Setup(AccessMode.ApprovalRequired);
        await w2.Membership.RequestAccessAsync(user2, c2);
        var again = await w2.Membership.RequestAccessAsync(user2, c2);
        Assert.Equal(MemberStatus.Pending, again.Status);
    }

    [Fact]
    public async Task Invitation_workflow()
    {
        var (w, owner, user, c) = await Setup(AccessMode.InvitationOnly);
        await Err.Expect("invitation_required", () => w.Membership.JoinAsync(user, c));
        await Err.Expect("not_admin", () => w.Membership.InviteAsync(user, c, new InviteRequest(user, null)));

        var invited = await w.Membership.InviteAsync(owner, c, new InviteRequest(user, null));
        Assert.Equal(MemberStatus.Invited, invited.Status);
        Assert.False(await CanChat(w, c, user));

        var joined = await w.Membership.JoinAsync(user, c);
        Assert.Equal(MemberStatus.Active, joined.Status);
        Assert.True(await CanChat(w, c, user));
    }

    [Fact]
    public async Task Private_community_is_reachable_by_invitation_only()
    {
        var (w, owner, user, c) = await Setup(AccessMode.InvitationOnly, Visibility.Private);
        await Err.Expect("community_not_found", () => w.Membership.JoinAsync(user, c));
        await w.Membership.InviteAsync(owner, c, new InviteRequest(user, null));
        Assert.Equal(MemberStatus.Active, (await w.Membership.JoinAsync(user, c)).Status);
    }

    [Fact]
    public async Task Invitation_by_nostr_public_key()
    {
        var (w, owner, _, c) = await Setup(AccessMode.InvitationOnly);
        var nostrUser = await w.NostrLoginAsync("bob");
        var res = await w.Membership.InviteAsync(owner, c, new InviteRequest(null, nostrUser.User.NostrPublicKey));
        Assert.Equal(MemberStatus.Invited, res.Status);
        await Err.Expect("missing_target", () => w.Membership.InviteAsync(owner, c, new InviteRequest(null, null)));
        await Err.Expect("user_not_found", () => w.Membership.InviteAsync(owner, c, new InviteRequest(Guid.NewGuid(), null)));
    }

    [Fact]
    public async Task Removal_revokes_chat_access_immediately_and_notifies_realtime()
    {
        var (w, owner, user, c) = await Setup();
        await w.Membership.JoinAsync(user, c);
        Assert.True(await CanChat(w, c, user));

        await w.Membership.RemoveAsync(owner, c, user, ban: false);

        Assert.False(await CanChat(w, c, user));
        var conv = (await w.Store.GetByCommunityAsync(c))!;
        Assert.Equal(1, w.Notifier.Revoked.Count(r => r.User == user && r.Conversation == conv.Id));
        Assert.Equal(1, (await w.Communities.GetAsync(owner, c.ToString())).Community.MemberCount);
        Assert.Equal(ConversationMemberStatus.Removed, (await w.Store.GetMemberAsync(conv.Id, user))!.Status);
        Assert.Contains("member.remove", w.Store.AuditEntries.Select(a => a.Action));

        // community stays discoverable according to its visibility
        Assert.Equal(1, (await w.Discovery.GetViewportAsync(null, -9.5, 38.5, -8.9, 39.0, 12, new(null, null, null, null, false))).Features.Count);
    }

    [Fact]
    public async Task Removed_member_can_rejoin_an_open_community_but_banned_member_cannot()
    {
        var (w, owner, user, c) = await Setup();
        await w.Membership.JoinAsync(user, c);
        await w.Membership.RemoveAsync(owner, c, user, ban: false);
        Assert.Equal(MemberStatus.Active, (await w.Membership.JoinAsync(user, c)).Status);

        await w.Membership.RemoveAsync(owner, c, user, ban: true);
        Assert.False(await CanChat(w, c, user));
        await Err.Expect("banned", () => w.Membership.JoinAsync(user, c));
        await Err.Expect("not_a_member", () => w.Membership.LeaveAsync(user, c)); // a banned user has nothing to leave
        Assert.Contains("member.ban", w.Store.AuditEntries.Select(a => a.Action));
    }

    [Fact]
    public async Task Banned_user_cannot_request_access_or_be_invited()
    {
        var (w, owner, user, c) = await Setup(AccessMode.ApprovalRequired);
        await w.Membership.RequestAccessAsync(user, c);
        await w.Membership.RemoveAsync(owner, c, user, ban: true);
        await Err.Expect("banned", () => w.Membership.RequestAccessAsync(user, c));
        await Err.Expect("user_banned", () => w.Membership.InviteAsync(owner, c, new InviteRequest(user, null)));
    }

    [Fact]
    public async Task Removal_permission_rules()
    {
        var (w, owner, user, c) = await Setup();
        var other = await w.NewEmailUserAsync();
        await w.Membership.JoinAsync(user, c);
        await w.Membership.JoinAsync(other, c);

        await Err.Expect("not_admin", () => w.Membership.RemoveAsync(user, c, other, false));
        await Err.Expect("cannot_remove_self", () => w.Membership.RemoveAsync(owner, c, owner, false));
        await Err.Expect("member_not_found", () => w.Membership.RemoveAsync(owner, c, Guid.NewGuid(), false));

        // Administrators can remove members, but never the owner or other administrators.
        (await w.Store.GetAsync(c, user))!.Role = MemberRole.Admin;
        (await w.Store.GetAsync(c, other))!.Role = MemberRole.Admin;
        await Err.Expect("cannot_remove_owner", () => w.Membership.RemoveAsync(user, c, owner, false));
        await Err.Expect("owner_required", () => w.Membership.RemoveAsync(user, c, other, false));
        await w.Membership.RemoveAsync(owner, c, other, false); // owner may remove an admin
    }

    [Fact]
    public async Task Leave_and_owner_cannot_leave()
    {
        var (w, owner, user, c) = await Setup();
        await w.Membership.JoinAsync(user, c);
        await w.Membership.LeaveAsync(user, c);
        Assert.False(await CanChat(w, c, user));
        Assert.Equal(1, (await w.Communities.GetAsync(owner, c.ToString())).Community.MemberCount);
        await Err.Expect("owner_cannot_leave", () => w.Membership.LeaveAsync(owner, c));
        await Err.Expect("not_a_member", () => w.Membership.LeaveAsync(user, c));
    }

    [Fact]
    public async Task Member_lists_are_not_exposed_to_non_admins()
    {
        var (w, _, user, c) = await Setup();
        await w.Membership.JoinAsync(user, c);
        await Err.Expect("not_admin", () => w.Membership.ListMembersAsync(user, c, null, 0, 20));
    }

    [Fact]
    public async Task Paid_community_requires_payment_on_top_of_other_rules()
    {
        var (w, owner, user, c) = await Setup(pricing: PricingMode.Paid, price: 9);
        await Err.Expect(ErrorKind.PaymentRequired, "payment_required", () => w.Membership.JoinAsync(user, c));
        Assert.False(await CanChat(w, c, user));
        Assert.Equal(MemberStatus.AwaitingPayment, (await w.Communities.GetAsync(user, c.ToString())).MyMembership!.Status);

        await w.Membership.ConfirmPaymentAsync(c, user, w.Time.GetUtcNow().AddDays(30));
        Assert.True(await CanChat(w, c, user));
        Assert.True(await CanChat(w, c, owner)); // owner is exempt

        // subscription lapses -> access revoked immediately
        w.Time.Advance(TimeSpan.FromDays(31));
        Assert.False(await CanChat(w, c, user));
        var (_, lapsed) = await w.Lifecycle.RunOnceAsync();
        Assert.Equal(1, lapsed);
        Assert.Equal(1, w.Notifier.Revoked.Count(r => r.User == user && r.Reason == "subscription_expired"));
        Assert.Equal(MemberStatus.AwaitingPayment, (await w.Store.GetAsync(c, user))!.Status);
    }

    [Fact]
    public async Task Paid_and_approval_rules_combine()
    {
        var (w, owner, user, c) = await Setup(AccessMode.ApprovalRequired, pricing: PricingMode.Paid, price: 5);
        await w.Membership.RequestAccessAsync(user, c);
        var approved = await w.Membership.ApproveAsync(owner, c, user);
        Assert.Equal(MemberStatus.AwaitingPayment, approved.Status); // approved but not yet paid
        Assert.False(await CanChat(w, c, user));
        await w.Membership.ConfirmPaymentAsync(c, user, w.Time.GetUtcNow().AddDays(30));
        Assert.True(await CanChat(w, c, user));
    }

    [Fact]
    public async Task Verified_communities_need_a_nostr_identity()
    {
        var (w, _, emailUser, c) = await Setup(AccessMode.Verified);
        await Err.Expect("verification_required", () => w.Membership.JoinAsync(emailUser, c));

        var nostr = await w.NostrLoginAsync("carol");
        Assert.Equal(MemberStatus.Active, (await w.Membership.JoinAsync(nostr.User.Id, c)).Status);
    }
}
