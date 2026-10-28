using Stomme.Engine.Mediator.Middleware.Notification.Contracts;

namespace Stomme.Engine.Mediator.Middleware.Notification;

/// <inheritdoc />
public class NotificationPipeline : INotificationPipeline
{
    private readonly IServiceProvider _serviceProvider;
    private NotificationMiddlewareDelegate? _executionPipeline;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationPipeline"/> class.
    /// </summary>
    public NotificationPipeline(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    /// <inheritdoc />
    public NotificationMiddlewareDelegate Pipeline => _executionPipeline ??= CreateDefaultPipeline();

    /// <inheritdoc />
    public NotificationMiddlewareDelegate Setup(Action<INotificationPipelineBuilder>? setup = default)
    {
        var builder = new NotificationPipelineBuilder(_serviceProvider);
        setup?.Invoke(builder);
        _executionPipeline = builder.Build();
        return _executionPipeline;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(NotificationContext context) => await Pipeline(context).ConfigureAwait(false);

    private NotificationMiddlewareDelegate CreateDefaultPipeline() => Setup(x => x.UseNotificationHandlers());
}