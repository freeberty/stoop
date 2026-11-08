using Intersect.Services;

namespace Intersect.Api.Infrastructure;

/// <summary>Expires communities, lapses subscriptions and purges old auth challenges once a minute.</summary>
public sealed class LifecycleWorker(IServiceScopeFactory scopes, ILogger<LifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var (communities, subscriptions) = await scope.ServiceProvider.GetRequiredService<LifecycleService>().RunOnceAsync(stoppingToken);
                if (communities + subscriptions > 0)
                    logger.LogInformation("Lifecycle: expired {Communities} communities, lapsed {Subscriptions} subscriptions", communities, subscriptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lifecycle run failed");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
