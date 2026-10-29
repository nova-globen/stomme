using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Stomme.Mediator.Behaviors;

/// <summary>
/// The pipeline's outermost boundary (registered FIRST: exception → logging → performance → validation →
/// handler), so every failure — from a behavior or the handler — passes through here exactly once and is
/// logged at a single place instead of at every call site.
/// <para>
/// A <see cref="ValidationException"/> is deliberately NOT logged as an error and is re-thrown unchanged: it
/// is an expected outcome of the <see cref="ValidationBehavior{TRequest,TResponse}"/> that sits inside this
/// one, and the transport boundary (an MVC controller) is responsible for turning it into a 400. Swallowing
/// or reclassifying it here would hide a rejected command from the caller. Nor is a cancellation the request's own
/// token asked for. Every other exception is logged with its request name for diagnosis and then re-thrown — this
/// behavior observes, it never suppresses.
/// </para>
/// </summary>
public sealed class ExceptionBehavior<TRequest, TResponse>(
    ILogger<ExceptionBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        try
        {
            return await next();
        }
        catch (ValidationException)
        {
            // Expected: a command the ValidationBehavior rejected. Surface it verbatim for the caller to
            // translate into a 400; it is not an application fault.
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected too: the caller gave up - a browser that navigates away aborts its request and the request's token
            // with it - so the cancellation is the answer it asked for, not a fault. Re-thrown for the host, never logged
            // as an error: a dashboard query cancelled that way wrote one per early click. A cancellation
            // nobody asked for falls through below and is logged.
            throw;
        }
        catch (Exception ex) when (IsExpectedRefusal(ex))
        {
            // A refusal the domain or the framework meant - a conflict, a stale write, a denied key - is the answer,
            // not a fault. Logged without its stack, at Information, and re-thrown for the transport to map to a 4xx.
            logger.LogInformation(
                "[Mediator] {RequestName} was refused: {Refusal}: {Message}", typeof(TRequest).Name, ex.GetType().Name, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[Mediator] Unhandled exception while processing {RequestName}", typeof(TRequest).Name);
            throw;
        }
    }

    // The framework's own refusals live in assemblies this library does not reference, so they are recognised by name.
    private static readonly HashSet<string> FrameworkRefusals = new(StringComparer.Ordinal)
    {
        "Stomme.Security.Abstractions.AccessDeniedException",
        "Stomme.Persistence.Abstractions.ConcurrencyConflictException",
    };

    internal static bool IsExpectedRefusal(Exception exception) =>
        exception is IExpectedRefusal || (exception.GetType().FullName is { } name && FrameworkRefusals.Contains(name));
}
