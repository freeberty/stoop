using Stoop.Abstractions;
using Stoop.InMemory;
using Stoop.Nostr;
using Stoop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stoop;

public static class DependencyInjection
{
    /// <summary>Registers application services. A persistence provider must be registered separately.</summary>
    public static IServiceCollection AddStoopApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISchnorrVerifier, ManagedSchnorrVerifier>();
        services.TryAddSingleton<ITokenService, JwtTokenService>();
        services.TryAddSingleton<IPasswordHasher>(_ => new Pbkdf2PasswordHasher());
        services.TryAddSingleton<MessageRateLimiter>();
        services.TryAddSingleton<IRealtimeNotifier, NoOpRealtimeNotifier>();

        services.AddScoped<AuthService>();
        services.AddScoped<CommunityService>();
        services.AddScoped<DiscoveryService>();
        services.AddScoped<MembershipService>();
        services.AddScoped<ChatService>();
        services.AddScoped<LifecycleService>();
        services.AddScoped<SeedService>();
        return services;
    }

    /// <summary>Dev/test persistence: everything in memory, shared by all requests.</summary>
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryStore>();
        services.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<InMemoryStore>());
        services.AddSingleton<IUserRepository>(sp => sp.GetRequiredService<InMemoryStore>());
        services.AddSingleton<ICommunityRepository>(sp => sp.GetRequiredService<InMemoryStore>());
        services.AddSingleton<IMembershipRepository>(sp => sp.GetRequiredService<InMemoryStore>());
        services.AddSingleton<IConversationRepository>(sp => sp.GetRequiredService<InMemoryStore>());
        services.AddSingleton<IMessageRepository>(sp => sp.GetRequiredService<InMemoryStore>());
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<InMemoryStore>());
        services.AddSingleton<INostrChallengeStore>(sp => sp.GetRequiredService<InMemoryStore>());
        return services;
    }
}
