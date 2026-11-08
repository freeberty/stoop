using Intersect.Common;
using Intersect.Contracts;
using Intersect.Domain;
using Intersect.Services;
using Xunit;

namespace Intersect.Tests;

public class CommunityTests
{
    private static DiscoveryQuery NoFilter => new(null, null, null, null, false);

    private static Task<GeoJsonFeatureCollection> Lisbon(World w, Guid? viewer = null, DiscoveryQuery? q = null)
        => w.Discovery.GetViewportAsync(viewer, -9.5, 38.5, -8.9, 39.0, 12, q ?? NoFilter);

    [Fact]
    public async Task Create_community_makes_owner_member_and_chat_conversation()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var created = await w.NewCommunityAsync(owner, World.Req("Digital Nomads Lisbon"));

        Assert.Equal("digital-nomads-lisbon", created.Community.Slug);
        Assert.Equal(1, created.Community.MemberCount);
        Assert.Equal(MemberRole.Owner, created.MyMembership!.Role);
        Assert.NotNull(created.ConversationId);
        Assert.Contains("community.create", w.Store.AuditEntries.Select(a => a.Action));
    }

    [Fact]
    public async Task Duplicate_names_get_unique_slugs()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var a = await w.NewCommunityAsync(owner, World.Req("Same Name"));
        var b = await w.NewCommunityAsync(owner, World.Req("Same Name"));
        Assert.NotEqual(a.Community.Slug, b.Community.Slug);
    }

    [Fact]
    public async Task Creation_validation()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        await Err.Expect("invalid_name", () => w.Communities.CreateAsync(owner, World.Req("ab")));
        await Err.Expect("invalid_location", () => w.Communities.CreateAsync(owner, World.Req(lat: 10, lon: null)));
        await Err.Expect("invalid_latitude", () => w.Communities.CreateAsync(owner, World.Req(lat: 95, lon: 10)));
        await Err.Expect("invalid_longitude", () => w.Communities.CreateAsync(owner, World.Req(lat: 10, lon: 190)));
        await Err.Expect("invalid_context_dates", () => w.Communities.CreateAsync(owner, World.Req(start: new(2020, 1, 1), end: new(2019, 1, 1))));
        await Err.Expect("invalid_expiry", () => w.Communities.CreateAsync(owner, World.Req(expires: w.Time.GetUtcNow().AddDays(-1))));
        await Err.Expect("invalid_price", () => w.Communities.CreateAsync(owner, World.Req(pricing: PricingMode.Paid)));
        await Err.Expect("invalid_cover_image", () => w.Communities.CreateAsync(owner, World.Req() with { CoverImageUrl = "http://insecure.example/x.png" }));
    }

    [Fact]
    public async Task Community_with_neither_location_nor_dates_is_valid()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Indie Game Developers", lat: null, lon: null));
        Assert.Null(c.Community.Latitude);
        Assert.Null(c.Community.ContextStartDate);
    }

    // ---------------------------------------------------------------- discovery & privacy

    [Fact]
    public async Task Public_discovery_returns_only_public_live_located_communities()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        await w.NewCommunityAsync(owner, World.Req("Public One"));
        await w.NewCommunityAsync(owner, World.Req("Private One", vis: Visibility.Private));
        await w.NewCommunityAsync(owner, World.Req("Unlisted One", vis: Visibility.Unlisted));
        await w.NewCommunityAsync(owner, World.Req("Global One", lat: null, lon: null));
        await w.NewCommunityAsync(owner, World.Req("Far Away", lat: 52.5, lon: 13.4));

        var fc = await Lisbon(w);
        Assert.Equal("FeatureCollection", fc.Type);
        var names = fc.Features.Select(f => f.Properties.Name).ToList();
        Assert.Equal(1, names.Count);
        Assert.Contains("Public One", names);
        Assert.Equal(2, fc.Features[0].Geometry.Coordinates.Length);
        Assert.Equal(-9.14, fc.Features[0].Geometry.Coordinates[0]); // GeoJSON order is [lon, lat]
    }

    [Fact]
    public async Task Members_discover_their_own_private_communities_but_outsiders_do_not()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var outsider = await w.NewEmailUserAsync();
        await w.NewCommunityAsync(owner, World.Req("Secret Club", vis: Visibility.Private));

        Assert.Equal(1, (await Lisbon(w, owner)).Features.Count);
        Assert.Equal(0, (await Lisbon(w, outsider)).Features.Count);
        Assert.Equal(0, (await Lisbon(w)).Features.Count);
    }

    [Fact]
    public async Task Private_community_does_not_exist_for_outsiders()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var outsider = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Secret Club", vis: Visibility.Private));

        await Err.Expect("community_not_found", () => w.Communities.GetAsync(outsider, c.Community.Id.ToString()));
        await Err.Expect("community_not_found", () => w.Communities.GetAsync(null, c.Community.Slug));
        await Err.Expect("community_not_found", () => w.Membership.JoinAsync(outsider, c.Community.Id));
        Assert.NotNull(await w.Communities.GetAsync(owner, c.Community.Id.ToString()));
        Assert.Empty((await w.Discovery.SearchAsync(outsider, "Secret", 0, 20)).Items);
    }

    [Fact]
    public async Task Unlisted_is_viewable_by_link_but_not_searchable_or_on_the_map()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var visitor = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Hidden Gem", vis: Visibility.Unlisted));

        Assert.NotNull(await w.Communities.GetAsync(visitor, c.Community.Slug));
        Assert.Empty((await w.Discovery.SearchAsync(null, "Hidden", 0, 20)).Items);
        Assert.Empty((await Lisbon(w)).Features);
    }

    [Fact]
    public async Task Private_details_never_include_conversation_for_non_members()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var visitor = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Open Club"));
        var asVisitor = await w.Communities.GetAsync(visitor, c.Community.Id.ToString());
        Assert.Null(asVisitor.ConversationId);
        Assert.Null(asVisitor.MyMembership);
    }

    [Fact]
    public async Task Global_communities_are_discoverable_separately_and_never_get_coordinates()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        await w.NewCommunityAsync(owner, World.Req("Indie Game Developers", lat: null, lon: null, cat: CommunityCategory.Gaming));
        await w.NewCommunityAsync(owner, World.Req("Lisbon Walkers"));

        var global = await w.Discovery.GetGlobalAsync(null, NoFilter, 0, 20);
        Assert.Equal(1, global.Items.Count);
        Assert.Null(global.Items[0].Latitude);
        Assert.Null(global.Items[0].Longitude);

        var world = await w.Discovery.GetViewportAsync(null, -180, -90, 180, 90, 1, NoFilter);
        Assert.Equal(1, world.Features.Count); // only the located one

        Assert.Equal(2, (await w.Discovery.SearchAsync(null, "i", 0, 20)).Items.Count); // search spans both
    }

    [Fact]
    public async Task Category_and_text_filters()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        await w.NewCommunityAsync(owner, World.Req("Photo Walks", cat: CommunityCategory.Photography));
        await w.NewCommunityAsync(owner, World.Req("Chess Night", cat: CommunityCategory.Social));

        var photo = await Lisbon(w, q: new DiscoveryQuery(null, null, CommunityCategory.Photography, null, false));
        Assert.Equal("Photo Walks", photo.Features.Single().Properties.Name);

        var text = await Lisbon(w, q: new DiscoveryQuery(null, null, null, "chess", false));
        Assert.Equal("Chess Night", text.Features.Single().Properties.Name);
    }

    // ---------------------------------------------------------------- time filtering (context dates only)

    [Fact]
    public async Task Time_filter_uses_context_dates_not_creation_or_expiry()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        // Created in 2026 but ABOUT 1974-1975.
        await w.NewCommunityAsync(owner, World.Req("Graduates 1974", start: new(1974, 1, 1), end: new(1975, 12, 31)));
        await w.NewCommunityAsync(owner, World.Req("Summer 2020", start: new(2020, 6, 1), end: new(2020, 9, 30)));
        await w.NewCommunityAsync(owner, World.Req("Upcoming Conf", start: new(2027, 3, 1), end: new(2027, 3, 3), expires: w.Time.GetUtcNow().AddDays(10)));
        await w.NewCommunityAsync(owner, World.Req("Undated Walkers"));
        await w.NewCommunityAsync(owner, World.Req("Ongoing Since 2018", start: new(2018, 1, 1)));

        async Task<string> Names(DateOnly? from, DateOnly? to, bool undated = false)
            => string.Join("|", (await Lisbon(w, q: new DiscoveryQuery(from, to, null, null, undated)))
                .Features.Select(f => f.Properties.Name).OrderBy(x => x, StringComparer.Ordinal));

        // About 1974-1975 even though it was created in 2026.
        Assert.Equal("Graduates 1974", await Names(new(1970, 1, 1), new(1980, 1, 1)));
        Assert.Equal("Ongoing Since 2018|Summer 2020", await Names(new(2020, 7, 1), new(2020, 7, 31)));
        // Open-ended communities ("ongoing since 2018") overlap every later range.
        Assert.Equal("Ongoing Since 2018|Upcoming Conf", await Names(new(2027, 1, 1), new(2027, 12, 31)));
        // No filter: everything.
        Assert.Equal(5, (await Lisbon(w)).Features.Count);
        // Undated communities only appear when explicitly included.
        Assert.Equal("Ongoing Since 2018|Summer 2020|Undated Walkers", await Names(new(2020, 7, 1), new(2020, 7, 31), undated: true));
    }

    [Fact]
    public async Task Invalid_time_range_and_bbox_are_rejected()
    {
        var w = new World();
        await Err.Expect("invalid_time_range", () => Lisbon(w, q: new DiscoveryQuery(new(2021, 1, 1), new(2020, 1, 1), null, null, false)));
        await Err.Expect("invalid_bbox", () => w.Discovery.GetViewportAsync(null, -9, 40, -8, 39, 5, NoFilter));
        await Err.Expect("invalid_bbox", () => w.Discovery.GetViewportAsync(null, -200, 38, -8, 39, 5, NoFilter));
        await Err.Expect("invalid_bbox", () => w.Discovery.GetViewportAsync(null, -9, -95, -8, 39, 5, NoFilter));
    }

    [Fact]
    public async Task Bounding_box_can_wrap_the_antimeridian()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        await w.NewCommunityAsync(owner, World.Req("Fiji", lat: -17.7, lon: 178.0));
        await w.NewCommunityAsync(owner, World.Req("Samoa", lat: -13.8, lon: -172.0));
        await w.NewCommunityAsync(owner, World.Req("Lisbon", lat: 38.7, lon: -9.1));
        var fc = await w.Discovery.GetViewportAsync(null, 170, -30, -160, 0, 4, NoFilter);
        Assert.Equal(2, fc.Features.Count);
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public async Task Expired_community_disappears_immediately_and_is_marked_by_the_sweep()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Pop-up", expires: w.Time.GetUtcNow().AddDays(2)));
        Assert.Equal(1, (await Lisbon(w)).Features.Count);

        w.Time.Advance(TimeSpan.FromDays(3));
        Assert.Empty((await Lisbon(w)).Features); // before any sweep ran
        var (expired, _) = await w.Lifecycle.RunOnceAsync();
        Assert.Equal(1, expired);
        Assert.Equal(CommunityStatus.Expired, (await w.Communities.GetAsync(owner, c.Community.Id.ToString())).Community.Status);
        var late = await w.NewEmailUserAsync();
        await Err.Expect("community_not_active", () => w.Membership.JoinAsync(late, c.Community.Id));
    }

    // ---------------------------------------------------------------- seed data

    [Fact]
    public void Seed_data_is_synthetic_in_range_and_covers_every_required_kind()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var seed = SeedDataGenerator.Generate(now);

        Assert.InRange(seed.Communities.Count, 200, 500);
        Assert.All(seed.Users, u => Assert.Contains("@example.invalid", u.Email!));
        Assert.All(seed.Users, u => Assert.Null(u.PasswordHash));

        var located = seed.Communities.Where(c => c.HasLocation).ToList();
        var global = seed.Communities.Where(c => !c.HasLocation).ToList();
        Assert.True(located.Count >= 200, "need at least 200 markers");
        Assert.True(global.Count >= 20, "need global communities");
        Assert.All(global, c => Assert.Null(c.Longitude));
        Assert.All(located, c => { Assert.InRange(c.Latitude!.Value, -90, 90); Assert.InRange(c.Longitude!.Value, -180, 180); });

        Assert.Equal(seed.Communities.Count, seed.Communities.Select(c => c.Slug).Distinct().Count());
        Assert.True(located.Count(c => c.Visibility == Visibility.Public && AccessPolicy.IsLive(c, now)) >= 200);
        Assert.True(seed.Communities.Any(c => c.ContextEndDate is { Year: < 2000 }), "historical communities");
        Assert.True(seed.Communities.Any(c => c.ContextStartDate is { } s && s > DateOnly.FromDateTime(now.UtcDateTime).AddDays(30)), "future events");
        Assert.True(seed.Communities.Any(c => c.ExpiresAt is not null), "temporary communities");
        Assert.True(seed.Communities.Any(c => c.ExpiresAt is null), "permanent communities");
        Assert.True(seed.Communities.Any(c => c.Status == CommunityStatus.Expired));
        Assert.True(seed.Communities.Any(c => c.Visibility != Visibility.Public));
        Assert.True(seed.Communities.Any(c => c.PricingMode == PricingMode.Paid));
        foreach (var name in new[] { "Digital Nomads Lisbon", "University of Lisbon Graduates", "Web Summit Attendees",
                     "Algarve Summer Memories", "Indie Game Developers", "Photography Enthusiasts", "Madeira Digital Nomads" })
            Assert.True(seed.Communities.Any(c => c.Name == name), name);
    }

    [Fact]
    public async Task Seed_service_is_idempotent_and_discoverable()
    {
        var w = new World();
        var service = new SeedService(w.Store, w.Store, w.Store, w.Store, w.Store, w.Time);
        var added = await service.SeedIfEmptyAsync();
        Assert.True(added >= 200);
        Assert.Equal(0, await service.SeedIfEmptyAsync());

        var lisbon = await Lisbon(w);
        Assert.True(lisbon.Features.Count > 10);
        Assert.True((await w.Discovery.GetGlobalAsync(null, NoFilter, 0, 100)).Items.Count > 10);
    }

    [Fact]
    public async Task Owner_can_delete_community_which_disappears_and_closes_chat()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var member = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Doomed"));
        await w.Membership.JoinAsync(member, World.Id(c));
        var conversation = c.ConversationId!.Value;

        await w.Communities.DeleteAsync(owner, World.Id(c));

        Assert.Empty((await Lisbon(w, owner)).Features);
        await Err.Expect(ErrorKind.NotFound, "community_not_found", () => w.Communities.GetAsync(owner, c.Community.Slug));
        await Err.Expect(ErrorKind.NotFound, "community_not_found", () => w.Communities.GetAsync(null, World.Id(c).ToString()));
        await Err.Expect("conversation_closed", () => w.Chat.SendAsync(conversation, member, "anyone?"));
        Assert.Contains(w.Notifier.Revoked, r => r.User == member && r.Conversation == conversation && r.Reason == "community_deleted");
        Assert.Contains(w.Notifier.Revoked, r => r.User == owner && r.Reason == "community_deleted");
        Assert.Contains("community.delete", w.Store.AuditEntries.Select(a => a.Action));
        await Err.Expect(ErrorKind.NotFound, "community_not_found", () => w.Communities.DeleteAsync(owner, World.Id(c)));
    }

    [Fact]
    public async Task Only_the_owner_can_delete_a_community()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var member = await w.NewEmailUserAsync();
        var outsider = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner);
        await w.Membership.JoinAsync(member, World.Id(c));

        await Err.Expect(ErrorKind.Forbidden, "owner_required", () => w.Communities.DeleteAsync(member, World.Id(c)));
        await Err.Expect(ErrorKind.Forbidden, "owner_required", () => w.Communities.DeleteAsync(outsider, World.Id(c)));
        Assert.Single((await Lisbon(w)).Features);
    }

    [Fact]
    public async Task Private_community_delete_by_outsider_does_not_leak_existence()
    {
        var w = new World();
        var owner = await w.NewEmailUserAsync();
        var outsider = await w.NewEmailUserAsync();
        var c = await w.NewCommunityAsync(owner, World.Req("Hidden", vis: Visibility.Private));
        await Err.Expect(ErrorKind.NotFound, "community_not_found", () => w.Communities.DeleteAsync(outsider, World.Id(c)));
    }
}
