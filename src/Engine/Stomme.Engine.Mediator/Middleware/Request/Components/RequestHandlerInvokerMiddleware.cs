using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Middleware;
using Stomme.Engine.Mediator.Middleware.Request.Contracts;

namespace Stomme.Engine.Mediator.Middleware.Request.Components;

/// <summary>
/// A middleware component that invokes request handlers.
/// </summary>
public class RequestHandlerInvokerMiddleware(
    RequestMiddlewareDelegate next,
    IEnumerable<IRequestHandler> requestHandlers) : IRequestMiddleware
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(RequestContext context)
    {

        // Find all handlers for the specified request.
        var request = context.Request;
        var requestType = request.GetType();
        var responseType = context.ResponseType;
        var handlerType = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
        var handlers = requestHandlers.Where(x => handlerType.IsInstanceOfType(x)).ToArray();
        
        if (handlers.Length == 0)
            throw new InvalidOperationException($"There is no handler to handle the {requestType.FullName} request");

        if (handlers.Length > 1)
            throw new InvalidOperationException($"Multiple handlers were found to handle the {requestType.FullName} request");

        var handler = handlers.First();
        var handleMethod = handlerType.GetMethod("HandleAsync")!;
        var cancellationToken = context.CancellationToken;
        // Invoke the handler, await it, and unbox its runtime-typed response.
        var task = (Task)handleMethod.Invoke(handler, [request, cancellationToken])!;
        context.Response = (await MiddlewareHelpers.AwaitAndUnboxAsync(task, responseType).ConfigureAwait(false))!;

        // Invoke next middleware.
        await next(context).ConfigureAwait(false);
    }
}