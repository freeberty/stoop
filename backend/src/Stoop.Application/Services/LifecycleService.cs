using Stoop.Abstractions;
using Stoop.Domain;

namespace Stoop.Services;

/// <summary>Periodic housekeeping: expire communities, lapse subscriptions, purge old challenges.</summary>
public sealed class LifecycleService(
    ICommunityRepository communities,
    IMembershipRepository members,
    INostrChallengeStore challenges,
    MembershipService membership,
    IAuditLog audit,
    IUnitOfWork uow,
    TimeProvider time)
{
    public async Task<(int Communities, int Subscriptions)> RunOnceAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();

        var expired = await communities.GetExpiredActiveAsync(now, 200, ct);
        foreach (var c in expired)
        {
            // Chat access is also checked live (AccessPolicy.IsLive), so this is bookkeeping.
            c.Status = CommunityStatus.Expired;
            audit.Add(new AuditLogEntry { Id = Guid.CreateVersion7(), At = now, CommunityId = c.Id, Action = "community.expired" });
        }
        if (expired.Count > 0) await uow.SaveChangesAsync(ct);

        var lapsed = await members.ListExpiredSubscriptionsAsync(now, 200, ct);
        foreach (var m in lapsed) await membership.ExpireSubscriptionAsync(m, ct);

        await challenges.PurgeExpiredAsync(now.AddHours(-1), ct);
        return (expired.Count, lapsed.Count);
    }
}
