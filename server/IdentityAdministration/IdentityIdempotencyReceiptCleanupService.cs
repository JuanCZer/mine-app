namespace Notes.Api.IdentityAdministration;

public sealed class IdentityIdempotencyReceiptCleanupService(
    IIdentityAdministrationStore store,
    ILogger<IdentityIdempotencyReceiptCleanupService> logger) : BackgroundService
{
    private const int BatchSize = 250;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await store.TombstoneExpiredReceiptsAsync(BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogWarning("Identity idempotency receipt cleanup was unavailable.");
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
