using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Middleware.Command;
using Stomme.Engine.Mediator.Middleware.Command.Contracts;
using Stomme.Engine.Mediator.Middleware.Notification;
using Stomme.Engine.Mediator.Middleware.Notification.Contracts;
using Stomme.Engine.Mediator.Middleware.Request;
using Stomme.Engine.Mediator.Middleware.Request.Contracts;
using Stomme.Engine.Mediator.Models;
using Stomme.Engine.Mediator.Options;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Mediator.Services;

/// <inheritdoc />
public class DefaultMediator : IMediator
{
    private readonly IRequestPipeline _requestPipeline;
    private readonly ICommandPipeline _commandPipeline;
    private readonly INotificationPipeline _notificationPipeline;
    private readonly IServiceProvider _serviceProvider;
    private readonly IEventPublishingStrategy _defaultPublishingStrategy;
    private readonly ICommandStrategy _defaultCommandStrategy;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultMediator"/> class.
    /// </summary>
    /// <param name="requestPipeline">The request pipeline.</param>
    /// <param name="commandPipeline">The command pipeline.</param>
    /// <param name="notificationPipeline">The notification pipeline.</param>
    /// <param name="options">The mediator options.</param>
    /// <param name="serviceProvider">The service provider.</param>
    public DefaultMediator(
        IRequestPipeline requestPipeline,
        ICommandPipeline commandPipeline,
        INotificationPipeline notificationPipeline,
        IOptions<MediatorOptions> options,
        IServiceProvider serviceProvider)
    {
        _requestPipeline = requestPipeline;
        _commandPipeline = commandPipeline;
        _notificationPipeline = notificationPipeline;
        _serviceProvider = serviceProvider;
        _defaultPublishingStrategy = options.Value.DefaultPublishingStrategy;
        _defaultCommandStrategy = options.Value.DefaultCommandStrategy;
    }

    /// <inheritdoc />
    public async Task<T> SendAsync<T>(IRequest<T> request, CancellationToken cancellationToken = default)
    {
        var responseType = typeof(T);
        var context = new RequestContext(request, responseType, _serviceProvider, cancellationToken);
        await _requestPipeline.ExecuteAsync(context).ConfigureAwait(false);

        return (T)context.Response;
    }

    public async Task<T> SendAsync<T>(ICommand<T> command, ICommandStrategy? strategy, IDictionary<object, object> headers, CancellationToken cancellationToken = default)
    {
        var resultType = typeof(T);
        strategy ??= _defaultCommandStrategy;
        var context = new CommandContext(command, strategy, resultType, headers, _serviceProvider, cancellationToken);
        await _commandPipeline.InvokeAsync(context).ConfigureAwait(false);

        return (T)context.CommandResult!;
    }

    /// <inheritdoc />
    public async Task SendAsync(ICommand command, CancellationToken cancellationToken = default) => await SendAsync(command, _defaultCommandStrategy, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task SendAsync(ICommand command, ICommandStrategy? strategy = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(command, strategy, new Dictionary<object, object>(), cancellationToken);
    }

    public async Task SendAsync(ICommand command, ICommandStrategy? strategy, IDictionary<object, object> headers, CancellationToken cancellationToken = default)
    {
        var resultType = typeof(Unit);
        strategy ??= _defaultCommandStrategy;
        var context = new CommandContext(command, strategy, resultType, headers, _serviceProvider, cancellationToken);
        await _commandPipeline.InvokeAsync(context).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<T> SendAsync<T>(ICommand<T> command, CancellationToken cancellationToken = default)
    {
        return SendAsync(command, new Dictionary<object, object>(), cancellationToken);
    }

    public Task<T> SendAsync<T>(ICommand<T> command, IDictionary<object, object> headers, CancellationToken cancellationToken = default)
    {
        return SendAsync(command, _defaultCommandStrategy, headers, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T> SendAsync<T>(ICommand<T> command, ICommandStrategy? strategy, CancellationToken cancellationToken = default)
    {
        return SendAsync(command, strategy, new Dictionary<object, object>(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAsync(INotification notification, CancellationToken cancellationToken = default)
    {
        await SendAsync(notification, _defaultPublishingStrategy, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendAsync(INotification notification, IEventPublishingStrategy? strategy = null, CancellationToken cancellationToken = default)
    {
        strategy ??= _defaultPublishingStrategy;
        var context = new NotificationContext(notification, strategy, _serviceProvider, cancellationToken);
        await _notificationPipeline.ExecuteAsync(context).ConfigureAwait(false);
    }
}