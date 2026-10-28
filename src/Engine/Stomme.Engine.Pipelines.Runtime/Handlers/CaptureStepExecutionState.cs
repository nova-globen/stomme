using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Notifications;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Captures the execution state of a step when it completes.
/// </summary>
[UsedImplicitly]
public class CaptureStepExecutionState : INotificationHandler<StepCompleted>
{
    public async Task HandleAsync(StepCompleted notification, CancellationToken cancellationToken)
    {
        var context = notification.StepExecutionContext;
        await context.CaptureStepExecutionRecordAsync().ConfigureAwait(false);
    }
}