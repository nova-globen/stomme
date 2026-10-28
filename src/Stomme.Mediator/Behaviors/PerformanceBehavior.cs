using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Stomme.Mediator.Behaviors;

/// <summary>
/// Times each request and logs the elapsed milliseconds, warning past a threshold. Registered THIRD
/// (outer-to-inner: exception → logging → <b>performance</b> → validation → handler), so its measurement
/// covers validation plus the handler — the wall-clock cost of actually serving the request — while itself
/// sitting inside the logging brackets. The stopwatch is stopped in a <c>finally</c> so a throwing handler
/// is still timed.
/// </summary>
public sealed class PerformanceBehavior<TRequest, TResponse>(
    ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    // Requests slower than this are surfaced at Warning so a slow handler is noticed without trawling
    // the Information stream. Deliberately generous — this is a demonstration threshold, not an SLO.
    private const long SlowRequestThresholdMs = 500;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await next();
        }
        finally
        {
            stopwatch.Stop();
            var elapsed = stopwatch.ElapsedMilliseconds;
            var requestName = typeof(TRequest).Name;

            if (elapsed > SlowRequestThresholdMs)
            {
                logger.LogWarning(
                    "[Mediator] {RequestName} took {ElapsedMilliseconds} ms (slow)", requestName, elapsed);
            }
            else
            {
                logger.LogInformation(
                    "[Mediator] {RequestName} completed in {ElapsedMilliseconds} ms", requestName, elapsed);
            }
        }
    }
}
