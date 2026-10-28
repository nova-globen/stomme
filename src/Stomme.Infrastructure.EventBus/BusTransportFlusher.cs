using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// Background service that drains the <see cref="BusTransportBuffer"/> by replaying each buffered
/// message. Each <see cref="BusBufferedMessage.Replay"/> delegate closes over the original
/// <see cref="RebusEventBus"/> and message, so no additional service resolution is needed at flush
/// time. Stops flushing the moment a replay fails (transport still down) and waits for the next
/// tick to try again. Successfully replayed messages are removed before the next is attempted.
/// </summary>
internal sealed class BusTransportFlusher(
    BusTransportBuffer buffer,
    ILogger<BusTransportFlusher> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (buffer.Count > 0)
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        logger.LogInformation("Attempting to flush {Count} buffered bus message(s).", buffer.Count);
        var flushed = 0;

        while (buffer.TryPeek(out var msg))
        {
            try
            {
                await msg.Replay(ct).ConfigureAwait(false);
                buffer.TryDequeue(out _);
                flushed++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "Bus transport still unavailable during flush; {Flushed} message(s) flushed, {Remaining} remain buffered.",
                    flushed, buffer.Count);
                return;
            }
        }

        if (flushed > 0)
        {
            logger.LogInformation("Flushed {Count} buffered bus message(s) to the transport.", flushed);
        }
    }
}
