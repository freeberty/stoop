using Intersect.Abstractions;
using Intersect.Infrastructure.Persistence;
using Intersect.Nostr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Intersect.Infrastructure;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddPostgresPersistence(IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite())
            // The initial migration is hand-written SQL (no model snapshot), so skip the pending-model check.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<ICommunityRepository, EfCommunityRepository>();
        services.AddScoped<IMembershipRepository, EfMembershipRepository>();
        services.AddScoped<IConversationRepository, EfConversationRepository>();
        services.AddScoped<IMessageRepository, EfMessageRepository>();
        services.AddScoped<IAuditLog, EfAuditLog>();
        services.AddScoped<INostrChallengeStore, EfNostrChallengeStore>();
        return services;
    }

    /// <summary>Use the NBitcoin.Secp256k1 library for BIP-340 verification instead of the managed fallback.</summary>
    public static IServiceCollection AddNostrLibraryVerifier(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ISchnorrVerifier, NBitcoinSchnorrVerifier>());
        return services;
    }

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);
    }
}
