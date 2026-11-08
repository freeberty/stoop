using Stoop.Abstractions;
using Stoop.Domain;

namespace Stoop.Services;

public sealed record SeedResult(
    List<User> Users,
    List<Community> Communities,
    List<CommunityMember> Members,
    List<Conversation> Conversations,
    List<ConversationMember> ConversationMembers);

/// <summary>Deterministic synthetic data. No real people, no real contact details.</summary>
public static class SeedDataGenerator
{
    private sealed record City(string Name, double Lat, double Lon, (string Hood, double Lat, double Lon)[] Hoods);

    private static readonly City[] Cities =
    [
        new("Lisbon", 38.7223, -9.1393, [("Alfama", 38.7110, -9.1280), ("Baixa", 38.7100, -9.1393), ("Príncipe Real", 38.7160, -9.1500), ("Belém", 38.6976, -9.2063), ("Parque das Nações", 38.7685, -9.0945), ("Alcântara", 38.7032, -9.1779), ("Campo de Ourique", 38.7168, -9.1670)]),
        new("Porto", 41.1579, -8.6291, [("Ribeira", 41.1406, -8.6118), ("Foz do Douro", 41.1500, -8.6760), ("Boavista", 41.1580, -8.6290), ("Bonfim", 41.1490, -8.5900), ("Cedofeita", 41.1570, -8.6200)]),
        new("London", 51.5072, -0.1276, [("Shoreditch", 51.5253, -0.0780), ("Camden", 51.5390, -0.1426), ("Brixton", 51.4613, -0.1156), ("Greenwich", 51.4826, -0.0077), ("King's Cross", 51.5309, -0.1233), ("Soho", 51.5136, -0.1365)]),
        new("Berlin", 52.5200, 13.4050, [("Kreuzberg", 52.4990, 13.4030), ("Neukölln", 52.4811, 13.4350), ("Mitte", 52.5200, 13.4050), ("Prenzlauer Berg", 52.5388, 13.4244), ("Friedrichshain", 52.5159, 13.4540)]),
        new("Amsterdam", 52.3676, 4.9041, [("Jordaan", 52.3740, 4.8800), ("De Pijp", 52.3560, 4.8940), ("Oost", 52.3600, 4.9250), ("Noord", 52.3980, 4.9150), ("Centrum", 52.3730, 4.8920)]),
        new("New York", 40.7128, -74.0060, [("Williamsburg", 40.7081, -73.9571), ("Lower East Side", 40.7150, -73.9843), ("Chelsea", 40.7465, -74.0014), ("Astoria", 40.7644, -73.9235), ("Harlem", 40.8116, -73.9465), ("Park Slope", 40.6710, -73.9777)]),
        new("Madeira", 32.7607, -16.9595, [("Funchal", 32.6669, -16.9241), ("Calheta", 32.7213, -17.1766), ("Machico", 32.7160, -16.7690), ("Ponta do Sol", 32.6800, -17.1030), ("Santana", 32.8040, -16.8860)]),
        new("Algarve", 37.0179, -7.9304, [("Lagos", 37.1028, -8.6730), ("Faro", 37.0194, -7.9304), ("Albufeira", 37.0891, -8.2479), ("Tavira", 37.1273, -7.6500), ("Portimão", 37.1366, -8.5377), ("Sagres", 37.0080, -8.9400)]),
    ];

    private static readonly string[] FirstNames = ["Alex", "Sam", "Jordan", "Taylor", "Robin", "Casey", "Morgan", "Jamie", "Riley", "Quinn", "Avery", "Dana", "Noor", "Mateo", "Ines", "Luca", "Mira", "Tomas", "Sofia", "Kai"];
    private static readonly string[] Hobbies = ["Street Photography", "Film Photography", "Trail Running", "Board Games", "Sourdough Baking", "Open Mic Music", "Cycling", "Sketching", "Language Exchange", "Chess", "Surf", "Climbing", "Book Club", "Vegan Cooking", "Pottery"];
    private static readonly string[] TechTopics = ["AI", "Web3", "Rust", "DevOps", "Design Systems", "Startups", "Data Science", "Open Source", "Cybersecurity"];

    private const int UserCount = 60;
    private const int TargetLocated = 360;
    private const int TargetGlobal = 60;

    public static SeedResult Generate(DateTimeOffset now, int randomSeed = 20261102)
    {
        var rnd = new Random(randomSeed);
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var users = Enumerable.Range(1, UserCount).Select(i => new User
        {
            Id = Guid.CreateVersion7(),
            DisplayName = $"{FirstNames[i % FirstNames.Length]} Demo {i}",
            Email = $"seed-user-{i}@example.invalid", // no password: seed users cannot log in
            CreatedAt = now.AddDays(-rnd.Next(30, 400)),
            Status = UserStatus.Active,
        }).ToList();

        var communities = new List<Community>();
        var slugs = new HashSet<string>();

        Community Make(string name, CommunityCategory cat, City? city, string? hood, double? lat, double? lon,
            DateOnly? start, DateOnly? end, string description, string? topic = null, bool allowRandomPolicy = true,
            Visibility? vis = null, AccessMode? access = null, PricingMode? pricing = null, DateTimeOffset? expires = null,
            CommunityStatus status = CommunityStatus.Active)
        {
            var slug = CommunityService.Slugify(name);
            var n = 2;
            while (!slugs.Add(slug)) slug = $"{CommunityService.Slugify(name)}-{n++}";

            var roll = rnd.NextDouble();
            var visibility = vis ?? (!allowRandomPolicy || roll < 0.86 ? Visibility.Public : roll < 0.93 ? Visibility.Unlisted : Visibility.Private);
            var accessMode = access ?? (!allowRandomPolicy ? AccessMode.Open : rnd.Next(10) switch { < 6 => AccessMode.Open, < 8 => AccessMode.ApprovalRequired, 8 => AccessMode.InvitationOnly, _ => AccessMode.Verified });
            var pricingMode = pricing ?? (allowRandomPolicy && rnd.Next(10) == 0 ? PricingMode.Paid : PricingMode.Free);

            var owner = users[rnd.Next(users.Count)];
            var c = new Community
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                Slug = slug,
                Description = description,
                Category = cat,
                Topic = topic,
                LocationName = lat is null ? null : hood is null ? city?.Name : $"{hood}, {city?.Name}",
                Latitude = lat,
                Longitude = lon,
                ContextStartDate = start,
                ContextEndDate = end,
                CreatedAt = now.AddDays(-rnd.Next(1, 300)),
                ExpiresAt = expires,
                Visibility = visibility,
                AccessMode = accessMode,
                PricingMode = pricingMode,
                PriceAmount = pricingMode == PricingMode.Paid ? rnd.Next(3, 25) : null,
                PriceCurrency = pricingMode == PricingMode.Paid ? "EUR" : null,
                Status = status,
                CreatedByUserId = owner.Id,
            };
            communities.Add(c);
            return c;
        }

        (double lat, double lon) Jitter((string Hood, double Lat, double Lon) h)
            => (h.Lat + (rnd.NextDouble() - 0.5) * 0.012, h.Lon + (rnd.NextDouble() - 0.5) * 0.016);

        // ---- the examples from the product spec (fixed)
        var lisbon = Cities[0]; var madeira = Cities[6]; var algarve = Cities[7];
        Make("Digital Nomads Lisbon", CommunityCategory.DigitalNomads, lisbon, null, lisbon.Lat, lisbon.Lon, new(2026, 11, 1), new(2026, 11, 30), "Remote workers sharing coworking spots, meetups and tips in Lisbon.", allowRandomPolicy: false);
        Make("University of Lisbon Graduates", CommunityCategory.Alumni, lisbon, null, 38.7527, -9.1585, new(1974, 1, 1), new(1975, 12, 31), "Graduates of 1974–1975 reconnecting and sharing memories.", allowRandomPolicy: false, access: AccessMode.ApprovalRequired);
        Make("Web Summit Attendees", CommunityCategory.Conference, lisbon, "Parque das Nações", 38.7685, -9.0945, new(2026, 1, 1), new(2026, 12, 31), "People attending Web Summit in 2026: plan meetups and share notes.", allowRandomPolicy: false);
        Make("Algarve Summer Memories", CommunityCategory.Travel, algarve, null, 37.1028, -8.6730, new(2020, 6, 1), new(2020, 9, 30), "Photos and stories from the summer of 2020 on the Algarve coast.", allowRandomPolicy: false);
        Make("Madeira Digital Nomads", CommunityCategory.DigitalNomads, madeira, "Funchal", 32.6669, -16.9241, new(2026, 12, 1), new(2027, 2, 28), "Winter 2026 base in Madeira for remote workers.", allowRandomPolicy: false);
        Make("Indie Game Developers", CommunityCategory.Gaming, null, null, null, null, null, null, "A global community for indie game makers: feedback, jams and showcases.", "Indie games", allowRandomPolicy: false);
        Make("Photography Enthusiasts", CommunityCategory.Photography, null, null, null, null, null, null, "A global community for photographers of every level.", "Photography", allowRandomPolicy: false);

        // ---- located communities
        var guard = 0;
        while (communities.Count(c => c.HasLocation) < TargetLocated && guard++ < 10_000)
        {
            var city = Cities[rnd.Next(Cities.Length)];
            var hood = city.Hoods[rnd.Next(city.Hoods.Length)];
            var (lat, lon) = Jitter(hood);
            var kind = rnd.Next(9);
            switch (kind)
            {
                case 0: // digital nomads (seasonal window)
                {
                    var s = new DateOnly(2026, rnd.Next(1, 12), 1);
                    Make($"{hood.Hood} Nomad Crew", CommunityCategory.DigitalNomads, city, hood.Hood, lat, lon, s, s.AddMonths(rnd.Next(1, 4)).AddDays(-1), $"Remote workers around {hood.Hood}, {city.Name}: coworking, lunches, weekend plans.");
                    break;
                }
                case 1: // historical alumni
                {
                    var y = rnd.Next(1962, 2008);
                    Make($"{city.Name} University Alumni {y}", CommunityCategory.Alumni, city, hood.Hood, lat, lon, new(y, 9, 1), new(y + 1, 6, 30), $"Alumni who studied in {city.Name} during {y}–{y + 1}.");
                    break;
                }
                case 2: // conference (future / current)
                {
                    var topic = TechTopics[rnd.Next(TechTopics.Length)];
                    var s = today.AddDays(rnd.Next(-5, 330));
                    Make($"{city.Name} {topic} Summit {s.Year}", CommunityCategory.Conference, city, hood.Hood, lat, lon, s, s.AddDays(rnd.Next(1, 4)), $"Attendees of the {topic} Summit in {city.Name}.", topic, expires: now.AddDays(rnd.Next(40, 500)));
                    break;
                }
                case 3: // photography
                    Make($"{hood.Hood} Photo Walks", CommunityCategory.Photography, city, hood.Hood, lat, lon, new(rnd.Next(2012, 2024), 1, 1), null, $"Regular photo walks around {hood.Hood}.", "Photography");
                    break;
                case 4: // interest group
                {
                    var hobby = Hobbies[rnd.Next(Hobbies.Length)];
                    var cat = hobby switch { "Trail Running" or "Cycling" or "Climbing" or "Surf" => CommunityCategory.Sports, "Open Mic Music" => CommunityCategory.Music, "Sourdough Baking" or "Vegan Cooking" => CommunityCategory.Food, "Sketching" or "Pottery" => CommunityCategory.Arts, _ => CommunityCategory.Social };
                    Make($"{hood.Hood} {hobby}", cat, city, hood.Hood, lat, lon, null, null, $"{hobby} fans in and around {hood.Hood}, {city.Name}.", hobby);
                    break;
                }
                case 5: // current event
                {
                    var s = today.AddDays(-rnd.Next(0, 10));
                    Make($"{city.Name} Autumn Festival {rnd.Next(100, 999)}", CommunityCategory.Social, city, hood.Hood, lat, lon, s, s.AddDays(rnd.Next(5, 25)), $"Happening now in {hood.Hood}: meet fellow visitors.", expires: now.AddDays(rnd.Next(10, 40)));
                    break;
                }
                case 6: // future event
                {
                    var s = today.AddDays(rnd.Next(30, 320));
                    Make($"{hood.Hood} {Hobbies[rnd.Next(Hobbies.Length)]} Weekend {s.Year}", CommunityCategory.Social, city, hood.Hood, lat, lon, s, s.AddDays(2), $"Upcoming weekend event in {hood.Hood}.", expires: s.ToDateTime(TimeOnly.MinValue).AddDays(10) is var d ? new DateTimeOffset(d, TimeSpan.Zero) : null);
                    break;
                }
                case 7: // historical memories
                {
                    var y = rnd.Next(1985, 2022);
                    Make($"{hood.Hood} Memories {y}", CommunityCategory.History, city, hood.Hood, lat, lon, new(y, 1, 1), new(y, 12, 31), $"Stories and photos from {hood.Hood} in {y}.");
                    break;
                }
                default: // tech meetup
                {
                    var topic = TechTopics[rnd.Next(TechTopics.Length)];
                    Make($"{city.Name} {topic} Meetup ({hood.Hood})", CommunityCategory.Technology, city, hood.Hood, lat, lon, null, null, $"Monthly {topic} meetup near {hood.Hood}.", topic);
                    break;
                }
            }
        }

        // ---- global communities (no coordinates, ever)
        var globalNames = new[] { "Rustaceans Worldwide", "Nostr Builders", "Open Source Maintainers", "Remote Work Tips", "Language Learners", "Vintage Film Cameras", "Home Baristas", "Indie Hackers", "Chess Online Club", "Digital Nomad Visas", "Map Nerds", "Retro Gaming", "Sci-Fi Book Club", "Minimalist Travel", "Data Viz Practice" };
        for (var i = 0; communities.Count(c => !c.HasLocation) < TargetGlobal + 2 && i < 1000; i++)
        {
            var baseName = globalNames[i % globalNames.Length];
            var name = i < globalNames.Length ? baseName : $"{baseName} #{i / globalNames.Length + 1}";
            var cat = (CommunityCategory)rnd.Next(Enum.GetValues<CommunityCategory>().Length);
            Make(name, cat, null, null, null, null, null, null, $"A worldwide community: {baseName}.", baseName);
        }

        // ---- a few expired communities (never discoverable)
        for (var i = 0; i < 5; i++)
        {
            var city = Cities[rnd.Next(Cities.Length)];
            var hood = city.Hoods[rnd.Next(city.Hoods.Length)];
            var (lat, lon) = Jitter(hood);
            Make($"{hood.Hood} Pop-up {i + 1}", CommunityCategory.Social, city, hood.Hood, lat, lon, today.AddDays(-90), today.AddDays(-60), "A finished pop-up community.",
                allowRandomPolicy: false, expires: now.AddDays(-30), status: CommunityStatus.Expired);
        }

        // ---- members + conversations
        var members = new List<CommunityMember>();
        var conversations = new List<Conversation>();
        var convMembers = new List<ConversationMember>();
        foreach (var c in communities)
        {
            var conv = new Conversation { Id = Guid.CreateVersion7(), Type = ConversationType.Community, CommunityId = c.Id, CreatedAt = c.CreatedAt, Status = ConversationStatus.Active };
            conversations.Add(conv);

            var chosen = users.OrderBy(_ => rnd.Next()).Where(u => u.Id != c.CreatedByUserId).Take(rnd.Next(2, 25)).ToList();
            members.Add(new CommunityMember { CommunityId = c.Id, UserId = c.CreatedByUserId, Role = MemberRole.Owner, Status = MemberStatus.Active, JoinedAt = c.CreatedAt, UpdatedAt = c.CreatedAt });
            convMembers.Add(new ConversationMember { ConversationId = conv.Id, UserId = c.CreatedByUserId, JoinedAt = c.CreatedAt, Status = ConversationMemberStatus.Active });
            foreach (var u in chosen)
            {
                var joined = c.CreatedAt.AddDays(rnd.Next(0, 20));
                members.Add(new CommunityMember
                {
                    CommunityId = c.Id, UserId = u.Id, Role = MemberRole.Member, Status = MemberStatus.Active, JoinedAt = joined, UpdatedAt = joined,
                    PaidUntil = c.PricingMode == PricingMode.Paid ? now.AddDays(rnd.Next(10, 200)) : null,
                });
                convMembers.Add(new ConversationMember { ConversationId = conv.Id, UserId = u.Id, JoinedAt = joined, Status = ConversationMemberStatus.Active });
            }
            c.MemberCount = chosen.Count + 1;
        }

        return new SeedResult(users, communities, members, conversations, convMembers);
    }
}

public sealed class SeedService(
    ICommunityRepository communities,
    IUserRepository users,
    IMembershipRepository members,
    IConversationRepository conversations,
    IUnitOfWork uow,
    TimeProvider time)
{
    /// <summary>Seeds synthetic data when the database has no communities. Returns the number of communities added.</summary>
    public async Task<int> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        if (await communities.CountAsync(ct) > 0) return 0;
        var data = SeedDataGenerator.Generate(time.GetUtcNow());

        foreach (var u in data.Users) users.Add(u);
        await uow.SaveChangesAsync(ct);

        foreach (var c in data.Communities) communities.Add(c);
        await uow.SaveChangesAsync(ct);

        foreach (var conv in data.Conversations) conversations.Add(conv);
        await uow.SaveChangesAsync(ct);

        foreach (var m in data.Members) members.Add(m);
        foreach (var cm in data.ConversationMembers) conversations.AddMember(cm);
        await uow.SaveChangesAsync(ct);
        return data.Communities.Count;
    }
}
