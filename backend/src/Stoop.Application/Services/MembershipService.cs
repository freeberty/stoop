using Stoop.Abstractions;
using Stoop.Common;
using Stoop.Contracts;
using Stoop.Domain;
using Stoop.Nostr;

namespace Stoop.Services;

/// <summary>
/// Single source of truth for community membership AND community chat access.
/// Every transition updates the conversation membership and notifies realtime clients
/// only AFTER the change is persisted, so an immediate reconnect sees the new state.
/// </summary>
public sealed class MembershipService(
    ICommunityRepository communities,
    IMembershipRepository members,
    IConversationRepository conversations,
    IUserRepository users,
    IAuditLog audit,
    IUnitOfWork uow,
    IRealtimeNotifier notifier,
    TimeProvider time)
{
    // ------------------------------------------------------------ user actions

    public async Task<MembershipResultDto> JoinAsync(Guid userId, Guid communityId, CancellationToken ct = default)
    {
        var c = await LoadViewableAsync(communityId, userId, ct);
        RequireLive(c);
        await RequireActiveUserAsync(userId, ct);

        var m = await members.GetAsync(communityId, userId, ct);
        if (m?.Status == MemberStatus.Banned) throw AppException.Forbidden("banned", "You are banned from this community.");
        if (m?.Status == MemberStatus.Active) return Result(m);

        var invited = m?.Status == MemberStatus.Invited;
        switch (c.AccessMode)
        {
            case AccessMode.Open:
                break;
            case AccessMode.ApprovalRequired:
                if (!invited) throw AppException.Forbidden("approval_required", "This community requires approval. Use request-access.");
                break;
            case AccessMode.InvitationOnly:
                if (!invited) throw AppException.Forbidden("invitation_required", "This community is invitation only.");
                break;
            case AccessMode.Verified:
                if (!await HasVerifiedIdentityAsync(userId, ct))
                    throw AppException.Forbidden("verification_required", "A verified identity (Nostr) is required to join.");
                break;
        }

        var result = await ActivateAsync(c, m, userId, userId, "member.join", ct);
        if (result.Status == MemberStatus.AwaitingPayment) throw PaymentRequired(c);
        return result;
    }

    public async Task<MembershipResultDto> RequestAccessAsync(Guid userId, Guid communityId, CancellationToken ct = default)
    {
        var c = await LoadViewableAsync(communityId, userId, ct);
        RequireLive(c);
        await RequireActiveUserAsync(userId, ct);

        if (c.AccessMode != AccessMode.ApprovalRequired)
            throw AppException.Conflict("approval_not_required", "This community does not use approval requests.");

        var m = await members.GetAsync(communityId, userId, ct);
        switch (m?.Status)
        {
            case MemberStatus.Banned: throw AppException.Forbidden("banned", "You are banned from this community.");
            case MemberStatus.Active: throw AppException.Conflict("already_member", "You are already a member.");
            case MemberStatus.Invited: throw AppException.Conflict("already_invited", "You have an invitation. Use join.");
            case MemberStatus.Pending: return Result(m);
        }

        var now = time.GetUtcNow();
        if (m is null)
        {
            m = new CommunityMember { CommunityId = communityId, UserId = userId, Role = MemberRole.Member };
            members.Add(m);
        }
        m.Status = MemberStatus.Pending;
        m.RequestedAt = now;
        m.UpdatedAt = now;
        audit.Add(Audit("member.request_access", userId, communityId, userId));
        await uow.SaveChangesAsync(ct);
        await notifier.MembershipChangedAsync(userId, communityId, m.Status, ct);
        return Result(m);
    }

    public async Task LeaveAsync(Guid userId, Guid communityId, CancellationToken ct = default)
    {
        var c = await communities.GetByIdAsync(communityId, ct) ?? throw CommunityNotFound();
        var m = await members.GetAsync(communityId, userId, ct);
        if (m is null || m.Status is MemberStatus.Left or MemberStatus.Removed or MemberStatus.Banned)
            throw AppException.NotFound("not_a_member", "You are not a member of this community.");
        if (m.Role == MemberRole.Owner)
            throw AppException.Conflict("owner_cannot_leave", "The owner cannot leave the community.");

        var wasActive = m.Status == MemberStatus.Active;
        m.Status = MemberStatus.Left;
        m.UpdatedAt = time.GetUtcNow();
        if (wasActive) c.MemberCount = Math.Max(0, c.MemberCount - 1);
        await SetChatAccessAsync(c, userId, false, ct);
        audit.Add(Audit("member.leave", userId, communityId, userId));
        await uow.SaveChangesAsync(ct);
        await AfterRevocationAsync(c, userId, m.Status, "left", ct);
    }

    // ------------------------------------------------------------ admin actions

    public async Task<MembershipResultDto> ApproveAsync(Guid actorId, Guid communityId, Guid targetUserId, CancellationToken ct = default)
    {
        var c = await RequireAdminAsync(actorId, communityId, ct);
        RequireLive(c);
        var m = await members.GetAsync(communityId, targetUserId, ct);
        if (m is null || m.Status != MemberStatus.Pending)
            throw AppException.Conflict("not_pending", "This user has no pending request.");
        return await ActivateAsync(c, m, targetUserId, actorId, "member.approve", ct);
    }

    public async Task<MembershipResultDto> InviteAsync(Guid actorId, Guid communityId, InviteRequest req, CancellationToken ct = default)
    {
        var c = await RequireAdminAsync(actorId, communityId, ct);
        RequireLive(c);

        User? target = null;
        if (req.UserId is { } uid) target = await users.GetByIdAsync(uid, ct);
        else if (req.NostrPublicKey is { } pk)
        {
            if (!NostrEventValidator.IsLowerHex(pk, 64)) throw AppException.Validation("invalid_pubkey", "Invalid Nostr public key.");
            target = await users.GetByExternalIdentityAsync(ExternalIdentity.NostrProvider, pk, ct);
        }
        else throw AppException.Validation("missing_target", "Provide userId or nostrPublicKey.");

        if (target is null || target.Status != UserStatus.Active) throw AppException.NotFound("user_not_found", "User not found.");

        var m = await members.GetAsync(communityId, target.Id, ct);
        if (m?.Status == MemberStatus.Banned) throw AppException.Conflict("user_banned", "This user is banned.");
        if (m?.Status == MemberStatus.Active) throw AppException.Conflict("already_member", "This user is already a member.");

        var now = time.GetUtcNow();
        if (m is null)
        {
            m = new CommunityMember { CommunityId = communityId, UserId = target.Id, Role = MemberRole.Member };
            members.Add(m);
        }
        m.Status = MemberStatus.Invited;
        m.InvitedByUserId = actorId;
        m.UpdatedAt = now;
        audit.Add(Audit("member.invite", actorId, communityId, target.Id));
        await uow.SaveChangesAsync(ct);
        await notifier.MembershipChangedAsync(target.Id, communityId, m.Status, ct);
        return Result(m);
    }

    public async Task RemoveAsync(Guid actorId, Guid communityId, Guid targetUserId, bool ban, CancellationToken ct = default)
    {
        var c = await RequireAdminAsync(actorId, communityId, ct);
        var actor = (await members.GetAsync(communityId, actorId, ct))!;
        if (targetUserId == actorId) throw AppException.Validation("cannot_remove_self", "Use leave to leave a community.");

        var m = await members.GetAsync(communityId, targetUserId, ct) ?? throw AppException.NotFound("member_not_found", "Member not found.");
        if (m.Role == MemberRole.Owner) throw AppException.Forbidden("cannot_remove_owner", "The owner cannot be removed.");
        if (m.Role == MemberRole.Admin && actor.Role != MemberRole.Owner)
            throw AppException.Forbidden("owner_required", "Only the owner can remove administrators.");

        var wasActive = m.Status == MemberStatus.Active;
        m.Status = ban ? MemberStatus.Banned : MemberStatus.Removed;
        m.Role = MemberRole.Member;
        m.UpdatedAt = time.GetUtcNow();
        if (wasActive) c.MemberCount = Math.Max(0, c.MemberCount - 1);
        await SetChatAccessAsync(c, targetUserId, false, ct);
        audit.Add(Audit(ban ? "member.ban" : "member.remove", actorId, communityId, targetUserId));
        await uow.SaveChangesAsync(ct);
        await AfterRevocationAsync(c, targetUserId, m.Status, ban ? "banned" : "removed", ct);
    }

    public async Task<IReadOnlyList<MemberDto>> ListMembersAsync(
        Guid actorId, Guid communityId, MemberStatus? status, int skip, int take, CancellationToken ct = default)
    {
        await RequireAdminAsync(actorId, communityId, ct); // member lists are never exposed to non-admins
        var rows = await members.ListAsync(communityId, status, Math.Max(0, skip), Math.Clamp(take <= 0 ? 50 : take, 1, 200), ct);
        var names = await users.GetByIdsAsync(rows.Select(r => r.UserId), ct);
        return rows.Select(r => new MemberDto(r.UserId, names.TryGetValue(r.UserId, out var u) ? u.DisplayName : "unknown",
            r.Role, r.Status, r.JoinedAt, r.RequestedAt)).ToList();
    }

    // ------------------------------------------------------------ payment / subscription (no payment processing in the POC)

    /// <summary>Called by a future payment integration (and tests) once payment is confirmed.</summary>
    public async Task<MembershipResultDto> ConfirmPaymentAsync(Guid communityId, Guid userId, DateTimeOffset paidUntil, CancellationToken ct = default)
    {
        var c = await communities.GetByIdAsync(communityId, ct) ?? throw CommunityNotFound();
        var m = await members.GetAsync(communityId, userId, ct);
        if (m is null || m.Status is not (MemberStatus.AwaitingPayment or MemberStatus.Active))
            throw AppException.Conflict("no_payment_due", "No payment is due for this user.");

        m.PaidUntil = paidUntil;
        if (m.Status == MemberStatus.AwaitingPayment)
        {
            m.Status = MemberStatus.Active;
            m.JoinedAt ??= time.GetUtcNow();
            c.MemberCount++;
            await SetChatAccessAsync(c, userId, true, ct);
        }
        m.UpdatedAt = time.GetUtcNow();
        audit.Add(Audit("member.payment_confirmed", null, communityId, userId));
        await uow.SaveChangesAsync(ct);
        await notifier.MembershipChangedAsync(userId, communityId, m.Status, ct);
        return Result(m);
    }

    /// <summary>Subscription lapsed: access is revoked immediately.</summary>
    public async Task ExpireSubscriptionAsync(CommunityMember m, CancellationToken ct = default)
    {
        var c = await communities.GetByIdAsync(m.CommunityId, ct);
        if (c is null || m.Status != MemberStatus.Active) return;
        m.Status = MemberStatus.AwaitingPayment;
        m.UpdatedAt = time.GetUtcNow();
        c.MemberCount = Math.Max(0, c.MemberCount - 1);
        await SetChatAccessAsync(c, m.UserId, false, ct);
        audit.Add(Audit("member.subscription_expired", null, m.CommunityId, m.UserId));
        await uow.SaveChangesAsync(ct);
        await AfterRevocationAsync(c, m.UserId, m.Status, "subscription_expired", ct);
    }

    // ------------------------------------------------------------ internals

    private async Task<MembershipResultDto> ActivateAsync(
        Community c, CommunityMember? m, Guid userId, Guid actorId, string action, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (m is null)
        {
            m = new CommunityMember { CommunityId = c.Id, UserId = userId, Role = MemberRole.Member };
            members.Add(m);
        }

        var needsPayment = c.PricingMode == PricingMode.Paid && m.Role != MemberRole.Owner && !(m.PaidUntil > now);
        if (needsPayment)
        {
            m.Status = MemberStatus.AwaitingPayment;
        }
        else
        {
            if (m.Status != MemberStatus.Active) c.MemberCount++;
            m.Status = MemberStatus.Active;
            m.JoinedAt ??= now;
            await SetChatAccessAsync(c, userId, true, ct);
        }
        m.UpdatedAt = now;
        audit.Add(Audit(action, actorId, c.Id, userId));
        await uow.SaveChangesAsync(ct);
        await notifier.MembershipChangedAsync(userId, c.Id, m.Status, ct);
        return Result(m);
    }

    /// <summary>Keeps the conversation membership in step with the community membership.</summary>
    private async Task SetChatAccessAsync(Community c, Guid userId, bool grant, CancellationToken ct)
    {
        var conv = await conversations.GetByCommunityAsync(c.Id, ct);
        if (conv is null) return;
        var cm = await conversations.GetMemberAsync(conv.Id, userId, ct);
        var status = grant ? ConversationMemberStatus.Active : ConversationMemberStatus.Removed;
        if (cm is null)
        {
            if (grant)
                conversations.AddMember(new ConversationMember
                {
                    ConversationId = conv.Id, UserId = userId, JoinedAt = time.GetUtcNow(), Status = status,
                });
        }
        else cm.Status = status;
    }

    private async Task AfterRevocationAsync(Community c, Guid userId, MemberStatus status, string reason, CancellationToken ct)
    {
        var conv = await conversations.GetByCommunityAsync(c.Id, ct);
        if (conv is not null) await notifier.AccessRevokedAsync(conv.Id, userId, reason, ct);
        await notifier.MembershipChangedAsync(userId, c.Id, status, ct);
    }

    private async Task<Community> LoadViewableAsync(Guid communityId, Guid userId, CancellationToken ct)
    {
        var c = await communities.GetByIdAsync(communityId, ct) ?? throw CommunityNotFound();
        var m = await members.GetAsync(communityId, userId, ct);
        if (!AccessPolicy.CanView(c, m)) throw CommunityNotFound();
        return c;
    }

    private async Task<Community> RequireAdminAsync(Guid actorId, Guid communityId, CancellationToken ct)
    {
        var c = await communities.GetByIdAsync(communityId, ct) ?? throw CommunityNotFound();
        var m = await members.GetAsync(communityId, actorId, ct);
        if (!AccessPolicy.CanView(c, m)) throw CommunityNotFound();
        if (!AccessPolicy.IsAdmin(m)) throw AppException.Forbidden("not_admin", "Administrator permission required.");
        return c;
    }

    private async Task RequireActiveUserAsync(Guid userId, CancellationToken ct)
    {
        var u = await users.GetByIdAsync(userId, ct) ?? throw AppException.Unauthorized("unknown_user", "Account not found.");
        if (u.Status != UserStatus.Active) throw AppException.Forbidden("account_suspended", "This account is suspended.");
    }

    private async Task<bool> HasVerifiedIdentityAsync(Guid userId, CancellationToken ct)
        => (await users.GetIdentitiesAsync(userId, ct)).Any(i => i.Provider == ExternalIdentity.NostrProvider);

    private void RequireLive(Community c)
    {
        if (!AccessPolicy.IsLive(c, time.GetUtcNow()))
            throw AppException.Conflict("community_not_active", "This community is not active.");
    }

    private static AppException CommunityNotFound() => AppException.NotFound("community_not_found", "Community not found.");

    private static AppException PaymentRequired(Community c) => AppException.PaymentRequired(
        "payment_required", "Payment is required to access this community.",
        new Dictionary<string, object?> { ["amount"] = c.PriceAmount, ["currency"] = c.PriceCurrency });

    private static MembershipResultDto Result(CommunityMember m) => new(m.CommunityId, m.Status, m.Role);

    private AuditLogEntry Audit(string action, Guid? actor, Guid communityId, Guid target) => new()
    {
        Id = Guid.CreateVersion7(), At = time.GetUtcNow(), ActorUserId = actor, CommunityId = communityId,
        TargetUserId = target, Action = action,
    };
}
