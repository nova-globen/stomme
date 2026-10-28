using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Notifications;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

public class EvaluateParentLogPersistenceModes(IStepPropertyLogPersistenceEvaluator persistenceEvaluator) : INotificationHandler<InvokingStepCallback>
{
    public async Task HandleAsync(InvokingStepCallback notification, CancellationToken cancellationToken)
    {
        // Before invoking the parent step, make sure its persistence log properties are evaluated.
        if (!notification.Parent.HasLogPersistenceModeMap())
        {
            var persistenceLogMap = await persistenceEvaluator.EvaluateLogPersistenceModesAsync(notification.Parent).ConfigureAwait(false);
            notification.Parent.SetLogPersistenceModeMap(persistenceLogMap);
        }
    }
}