using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Mediator;

public sealed class Mediator(IServiceProvider sp) : IMediator
{
    private static readonly ConcurrentDictionary<Type, Type> _handlerTypeCache = new();
    private static readonly ConcurrentDictionary<Type, Type> _behaviorTypeCache = new();
    private static readonly ConcurrentDictionary<Type, Type> _streamHandlerTypeCache = new();

    public async Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var responseType = typeof(TResponse);

        var handlerType = _handlerTypeCache.GetOrAdd(requestType,
            t => typeof(IRequestHandler<,>).MakeGenericType(t, responseType));

        var handler = sp.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No handler registered for {requestType.Name}. " +
                $"Register IRequestHandler<{requestType.Name}, {responseType.Name}> in DI.");

        var behaviorType = _behaviorTypeCache.GetOrAdd(requestType,
            t => typeof(IPipelineBehavior<,>).MakeGenericType(t, responseType));

        var behaviors = sp.GetServices(behaviorType)?
            .Cast<dynamic>()
            .Reverse()
            .ToArray() ?? [];

        RequestHandlerDelegate<TResponse> pipeline = ()
            => ((dynamic)handler).Handle((dynamic)request, ct);

        foreach (var behavior in behaviors)
        {
            var next = pipeline;
            pipeline = () => behavior.Handle((dynamic)request, next, ct);
        }

        return await pipeline();
    }

    public async Task Publish<TNotification>(
        TNotification notification, CancellationToken ct = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        var handlers = sp.GetServices<INotificationHandler<TNotification>>();

        foreach (var handler in handlers)
        {
            await handler.Handle(notification, ct);
        }
    }

    public async IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var responseType = typeof(TResponse);

        var handlerType = _streamHandlerTypeCache.GetOrAdd(requestType,
            t => typeof(IStreamRequestHandler<,>).MakeGenericType(t, responseType));

        var handler = sp.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No stream handler registered for {requestType.Name}.");

        // Use reflection to invoke Handle and get IAsyncEnumerable<TResponse>
        var handleMethod = handlerType.GetMethod("Handle")!;
        var result = handleMethod.Invoke(handler, [request, ct])!;
        var asyncEnumerable = (IAsyncEnumerable<TResponse>)result;

        await foreach (var item in asyncEnumerable.WithCancellation(ct))
        {
            yield return item;
        }
    }
}
