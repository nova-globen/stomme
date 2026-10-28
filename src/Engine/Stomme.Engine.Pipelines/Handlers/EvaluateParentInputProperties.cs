using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Notifications;

namespace Stomme.Engine.Pipelines.Handlers;

public class EvaluateParentInputProperties : INotificationHandler<InvokingStepCallback>
{
    public async Task HandleAsync(InvokingStepCallback notification, CancellationToken cancellationToken)
    {
        // Before invoking the parent step, make sure its properties are evaluated.
        if (!notification.Parent.GetHasEvaluatedProperties()) 
            await notification.Parent.EvaluateInputPropertiesAsync().ConfigureAwait(false);
    }
}