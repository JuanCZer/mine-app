namespace Notes.Api.IdentityAdministration;

public sealed class IdentityRateLimitCleanupService(
    IIdentityRateLimiter limiter,
    ILogger<IdentityRateLimitCleanupService> logger) : BackgroundService
{
    private const int BatchSize = 250;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await limiter.DeleteExpiredBatchAsync(BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IdentityRateLimitUnavailableException)
            {
                logger.LogWarning("Identity rate-limit cleanup was unavailable.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
