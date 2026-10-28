using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Persistence;

/// <summary>
/// Polls the <see cref="OutboxProcessor"/> to drain the outbox. Healthy iterations wait the configured
/// <see cref="OutboxOptions.PollInterval"/>; an iteration that throws (e.g. the store is unreachable)
/// backs off exponentially (<see cref="OutboxOptions.BackoffFor"/>, never below the poll interval) so a
/// down dependency is not hammered, resetting to the poll interval on the next success. Per-message
/// retry backoff is applied separately by the processor via <see cref="OutboxMessage.NextAttemptOnUtc"/>.
/// </summary>
internal sealed class OutboxBackgroundService(
    OutboxProcessor processor, IOptions<OutboxOptions> options, ILogger<OutboxBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = options.Value.PollInterval;
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = pollInterval;
            try
            {
                await processor.ProcessPendingAsync(stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var backoff = options.Value.BackoffFor(consecutiveFailures);
                delay = backoff > pollInterval ? backoff : pollInterval;
                logger.LogError(
                    ex, "Outbox processing iteration failed ({Attempt} in a row); backing off {Delay} before retry.",
                    consecutiveFailures, delay);
            }

            if (!await SafeDelayAsync(delay, stoppingToken))
            {
                break;
            }
        }
    }

    private static async Task<bool> SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
