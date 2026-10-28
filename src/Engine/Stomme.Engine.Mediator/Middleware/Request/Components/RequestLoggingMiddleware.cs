using Stomme.Engine.Mediator.Middleware.Request.Contracts;

namespace Stomme.Engine.Mediator.Middleware.Request.Components;

/// <summary>
/// A middleware that logs the request.
/// </summary>
public class RequestLoggingMiddleware : IRequestMiddleware
{
    private readonly RequestMiddlewareDelegate _next;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="RequestLoggingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public RequestLoggingMiddleware(RequestMiddlewareDelegate next) => _next = next;

    /// <inheritdoc />
    public async ValueTask InvokeAsync(RequestContext context)
    {
        // Invoke next middleware.
        await _next(context).ConfigureAwait(false);
    }
}