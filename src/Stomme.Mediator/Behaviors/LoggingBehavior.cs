using Microsoft.Extensions.Logging;

namespace Stomme.Mediator.Behaviors;

/// <summary>
/// Logs the start and completion of every request that flows through the mediator. Registered SECOND
/// (outer-to-inner: exception → <b>logging</b> → performance → validation → handler), so it brackets the
/// whole pipeline — the log pair spans validation, the timing behavior and the handler, which is what makes
/// "handled {RequestName}" a truthful record that the work actually completed rather than merely started.
/// </summary>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        var requestName = typeof(TRequest).Name;
        logger.LogInformation("[Mediator] Handling {RequestName}", requestName);

        var response = await next();

        logger.LogInformation("[Mediator] Handled {RequestName}", requestName);
        return response;
    }
}
