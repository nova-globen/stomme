using System.Reflection;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Runtime.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// A <see cref="PipelineDefinitionValidating"/> handler that validates a pipeline path and return any errors.
/// </summary>
[UsedImplicitly]
public class ValidatePipelineRequestHandler(
    ITriggerIndexer triggerIndexer,
    IServiceProvider serviceProvider) : INotificationHandler<PipelineDefinitionValidating>
{
    public async Task HandleAsync(PipelineDefinitionValidating notification, CancellationToken cancellationToken)
    {
        Pipeline pipeline = notification.Pipeline;
        ICollection<PipelineValidationError> validationErrors = notification.ValidationErrors;
        foreach (StoredTrigger trigger in await triggerIndexer.GetTriggersAsync(pipeline, cancellationToken).ConfigureAwait(false))
        {
            if (trigger.Payload is null)
            {
                validationErrors.Add(new($"Trigger should have a payload",
                    trigger.StepId));
            }
            else
            {
                MethodInfo method = typeof(ValidatePipelineRequestHandler)
                    .GetMethod(nameof(ValidateInternalAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(trigger.Payload.GetType());

                object? result = method.Invoke(this, [trigger.Payload, pipeline, trigger, validationErrors, cancellationToken]);
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ValidateInternalAsync<TPayload>(TPayload instance,
        Pipeline pipeline,
        StoredTrigger trigger,
        ICollection<PipelineValidationError> validationErrors,
        CancellationToken cancellationToken)
    {
        ITriggerPayloadValidator<TPayload>? payloadValidator = serviceProvider.GetService<ITriggerPayloadValidator<TPayload>>();

        if (payloadValidator != null)
            await payloadValidator.ValidateAsync(instance, pipeline, trigger, validationErrors, cancellationToken).ConfigureAwait(false);
    }
}