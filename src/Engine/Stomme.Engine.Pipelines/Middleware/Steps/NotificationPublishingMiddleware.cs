using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;

namespace Stomme.Engine.Pipelines.Middleware.Steps;

/// <summary>
/// A step execution middleware component that publishes step notifications:
/// <list type="bullet">
/// <item><see cref="StepExecuting"/> when a step starts executing</item>
/// <item><see cref="StepExecuted"/> when a step has executed</item>
/// <item><see cref="Notifications.StepCompleted"/> when a step transitions from Running to Completed status</item>
/// </list>
/// </summary>
public class NotificationPublishingMiddleware : IStepExecutionMiddleware
{
    private readonly StepMiddlewareDelegate _next;
    private readonly INotificationSender _notificationSender;

    /// <summary>
    /// Constructor.
    /// </summary>
    public NotificationPublishingMiddleware(StepMiddlewareDelegate next, INotificationSender notificationSender)
    {
        _next = next;
        _notificationSender = notificationSender;
    }

    /// <inheritdoc />
    public async ValueTask InvokeAsync(StepExecutionContext context)
    {
        // Store the status before execution
        var statusBeforeExecution = context.Status;
        
        // Send notification that step is executing
        await _notificationSender.SendAsync(new StepExecuting(context)).ConfigureAwait(false);
        
        // Execute the step
        await _next(context).ConfigureAwait(false);
        
        // Send notification that step has executed
        await _notificationSender.SendAsync(new StepExecuted(context)).ConfigureAwait(false);
        
        // Send notification that step has completed if status transitioned from Running to Completed
        if (statusBeforeExecution == StepStatus.Running && context.Status == StepStatus.Completed)
            await _notificationSender.SendAsync(new StepCompleted(context)).ConfigureAwait(false);
    }
}

/// <summary>
/// Extends <see cref="IStepExecutionPipelineBuilder"/> to install the <see cref="NotificationPublishingMiddleware"/> component.
/// </summary>
public static class NotificationPublishingMiddlewareExtensions
{
    /// <summary>
    /// Installs the <see cref="NotificationPublishingMiddleware"/> component.
    /// </summary>
    public static IStepExecutionPipelineBuilder UseNotifications(this IStepExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<NotificationPublishingMiddleware>();
}