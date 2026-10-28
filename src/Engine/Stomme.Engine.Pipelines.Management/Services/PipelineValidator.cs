using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Notifications;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class PipelineValidator(INotificationSender notificationSender) : IPipelineValidator
{
    /// <inheritdoc />
    public async Task<IEnumerable<PipelineValidationError>> ValidateAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        var validationErrors = new List<PipelineValidationError>();
        var notification = new PipelineDefinitionValidating(pipeline, validationErrors);
        await notificationSender.SendAsync(notification, cancellationToken).ConfigureAwait(false);
        return validationErrors;
    }
}