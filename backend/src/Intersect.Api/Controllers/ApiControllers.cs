using Intersect.Api.Auth;
using Intersect.Common;
using Intersect.Contracts;
using Intersect.Domain;
using Intersect.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Intersect.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthDefaults.Scheme)]
public abstract class ApiControllerBase : ControllerBase
{
    protected Guid UserId => User.GetUserId();
    protected Guid? ViewerId => User.TryGetUserId();
}

// ------------------------------------------------------------------ auth

[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController(AuthService auth) : ApiControllerBase
{
    [AllowAnonymous, HttpPost("register")]
    public async Task<AuthResponse> Register(RegisterRequest req, CancellationToken ct) => await auth.RegisterAsync(req, ct);

    [AllowAnonymous, HttpPost("login")]
    public async Task<AuthResponse> Login(LoginRequest req, CancellationToken ct) => await auth.LoginAsync(req, ct);

    [AllowAnonymous, HttpPost("nostr/challenge")]
    public async Task<NostrChallengeResponse> NostrChallenge(NostrChallengeRequest? req, CancellationToken ct)
        => await auth.CreateChallengeAsync(req?.PublicKey, ChallengePurpose.Login, null, ct);

    [AllowAnonymous, HttpPost("nostr/verify")]
    public async Task<AuthResponse> NostrVerify(NostrVerifyRequest req, CancellationToken ct)
        => await auth.LoginWithNostrAsync(req?.Event, ct);

    [HttpPost("nostr/link/challenge")]
    public async Task<NostrChallengeResponse> NostrLinkChallenge(CancellationToken ct)
        => await auth.CreateChallengeAsync(null, ChallengePurpose.Link, UserId, ct);

    [HttpPost("nostr/link")]
    public async Task<UserDto> NostrLink(NostrVerifyRequest req, CancellationToken ct)
        => await auth.LinkNostrAsync(UserId, req?.Event, ct);

    [HttpPost("email/link")]
    public async Task<UserDto> EmailLink(LinkEmailRequest req, CancellationToken ct)
        => await auth.LinkEmailAsync(UserId, req, ct);

    [HttpGet("~/api/v1/me")]
    public async Task<UserDto> Me(CancellationToken ct) => await auth.GetMeAsync(UserId, ct);
}

// ------------------------------------------------------------------ communities

[Route("api/v1/communities")]
public sealed class CommunitiesController(CommunityService communities, MembershipService membership, DiscoveryService discovery) : ApiControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateCommunityRequest req, CancellationToken ct)
    {
        var created = await communities.CreateAsync(UserId, req, ct);
        return CreatedAtAction(nameof(Get), new { idOrSlug = created.Community.Id.ToString() }, created);
    }

    [AllowAnonymous, HttpGet("{idOrSlug}")]
    public async Task<CommunityDetailsDto> Get(string idOrSlug, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        return await communities.GetAsync(ViewerId, idOrSlug, ct);
    }

    /// <summary>Owner only. Archives the community and closes its chat.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await communities.DeleteAsync(UserId, id, ct);
        return NoContent();
    }

    [AllowAnonymous, HttpGet("search")]
    public async Task<CommunityListDto> Search([FromQuery] string? q, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        return await discovery.SearchAsync(ViewerId, q, skip, take, ct);
    }

    [HttpPost("{id:guid}/join")]
    public async Task<MembershipResultDto> Join(Guid id, CancellationToken ct) => await membership.JoinAsync(UserId, id, ct);

    [HttpPost("{id:guid}/leave")]
    public async Task<IActionResult> Leave(Guid id, CancellationToken ct)
    {
        await membership.LeaveAsync(UserId, id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/request-access")]
    public async Task<MembershipResultDto> RequestAccess(Guid id, CancellationToken ct) => await membership.RequestAccessAsync(UserId, id, ct);

    [HttpPost("{id:guid}/approve/{userId:guid}")]
    public async Task<MembershipResultDto> Approve(Guid id, Guid userId, CancellationToken ct) => await membership.ApproveAsync(UserId, id, userId, ct);

    [HttpPost("{id:guid}/invite")]
    public async Task<MembershipResultDto> Invite(Guid id, InviteRequest req, CancellationToken ct) => await membership.InviteAsync(UserId, id, req, ct);

    /// <summary>Removes a member. Add ?ban=true to also ban them.</summary>
    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> Remove(Guid id, Guid userId, [FromQuery] bool ban = false, CancellationToken ct = default)
    {
        await membership.RemoveAsync(UserId, id, userId, ban, ct);
        return NoContent();
    }

    /// <summary>Administrators only. Member lists are never public.</summary>
    [HttpGet("{id:guid}/members")]
    public async Task<IReadOnlyList<MemberDto>> Members(Guid id, [FromQuery] MemberStatus? status, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => await membership.ListMembersAsync(UserId, id, status, skip, take, ct);
}

// ------------------------------------------------------------------ discovery

[Route("api/v1/discovery")]
public sealed class DiscoveryController(DiscoveryService discovery) : ApiControllerBase
{
    /// <summary>GeoJSON FeatureCollection of communities inside the viewport.</summary>
    [AllowAnonymous, HttpGet("communities")]
    [Produces("application/geo+json", "application/json")]
    public async Task<GeoJsonFeatureCollection> Communities(
        [FromQuery] double? west, [FromQuery] double? south, [FromQuery] double? east, [FromQuery] double? north,
        [FromQuery] int? zoom, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] CommunityCategory? category, [FromQuery] string? search,
        [FromQuery] bool includeUndated = false, CancellationToken ct = default)
    {
        if (west is null || south is null || east is null || north is null)
            throw AppException.Validation("invalid_bbox", "west, south, east and north are required.");

        var viewer = ViewerId;
        // Anonymous responses are identical for everyone, so short HTTP caching is safe.
        Response.Headers.CacheControl = viewer is null ? "public, max-age=10" : "private, no-store";
        return await discovery.GetViewportAsync(viewer, west.Value, south.Value, east.Value, north.Value, zoom,
            new DiscoveryQuery(from, to, category, search, includeUndated), ct);
    }

    /// <summary>Communities with no location, listed separately (never given artificial coordinates).</summary>
    [AllowAnonymous, HttpGet("global")]
    public async Task<CommunityListDto> Global(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] CommunityCategory? category, [FromQuery] string? search,
        [FromQuery] bool includeUndated = true, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        var viewer = ViewerId;
        Response.Headers.CacheControl = viewer is null ? "public, max-age=10" : "private, no-store";
        return await discovery.GetGlobalAsync(viewer, new DiscoveryQuery(from, to, category, search, includeUndated), skip, take, ct);
    }
}

// ------------------------------------------------------------------ chat (REST; live delivery is /hubs/chat)

[Route("api/v1/conversations")]
public sealed class ConversationsController(ChatService chat) : ApiControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ConversationDto>> List(CancellationToken ct) => await chat.ListConversationsAsync(UserId, ct);

    [HttpGet("{id:guid}")]
    public async Task<ConversationDto> Get(Guid id, CancellationToken ct) => await chat.GetConversationAsync(id, UserId, ct);

    [HttpPost("direct")]
    public async Task<ConversationDto> Direct(CreateDirectConversationRequest req, CancellationToken ct)
        => await chat.GetOrCreateDirectAsync(UserId, req.UserId, ct);

    [HttpGet("{id:guid}/messages")]
    public async Task<MessagePageDto> Messages(Guid id, [FromQuery] Guid? before, [FromQuery] int limit = 50, CancellationToken ct = default)
        => await chat.GetMessagesAsync(id, UserId, before, limit, ct);

    [HttpPost("{id:guid}/messages")]
    public async Task<MessageDto> Send(Guid id, SendMessageRequest req, CancellationToken ct)
        => await chat.SendAsync(id, UserId, req.Content, ct);
}

[Route("api/v1/messages")]
public sealed class MessagesController(ChatService chat) : ApiControllerBase
{
    [HttpPut("{id:guid}")]
    public async Task<MessageDto> Edit(Guid id, EditMessageRequest req, CancellationToken ct) => await chat.EditAsync(id, UserId, req.Content, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await chat.DeleteAsync(id, UserId, ct);
        return NoContent();
    }
}
