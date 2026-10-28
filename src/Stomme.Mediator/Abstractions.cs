namespace Stomme.Mediator;

// ---------------------------------------------------------------------------
// Core request/response
// ---------------------------------------------------------------------------
public interface IRequest<TResponse>;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken ct);
}

// ---------------------------------------------------------------------------
// CQRS-explicit interfaces
// ---------------------------------------------------------------------------
public interface ICommand<TResponse> : IRequest<TResponse>;
public interface IQuery<TResponse> : IRequest<TResponse>;

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>;

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>;

// ---------------------------------------------------------------------------
// Void commands
// ---------------------------------------------------------------------------
public readonly struct Unit
{
    public static readonly Unit Value;
    public static readonly Task<Unit> Task = System.Threading.Tasks.Task.FromResult(Value);
}

public interface ICommand : ICommand<Unit>;

// ---------------------------------------------------------------------------
// Notifications / Events
// ---------------------------------------------------------------------------
public interface INotification;

public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken ct);
}

// ---------------------------------------------------------------------------
// Pipeline Behaviors
// ---------------------------------------------------------------------------
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct);
}

// ---------------------------------------------------------------------------
// Streaming
// ---------------------------------------------------------------------------
public interface IStreamRequest<out TResponse>;

public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken ct);
}

// ---------------------------------------------------------------------------
// Mediator interface
// ---------------------------------------------------------------------------
public interface IMediator
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
    Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification;
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request,
        CancellationToken ct = default);
}
