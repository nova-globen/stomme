using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Contracts;

/// <summary>
/// Validator that validate a given trigger payload.
/// </summary>
public interface ITriggerPayloadValidator<TPayload>
{
    /// <summary>
    /// Validate a trigger payload. If trigger is not valid, error will be add in this list.
    /// </summary>
    Task ValidateAsync(
        TPayload payload,
        Pipeline pipeline,
        StoredTrigger trigger,
        ICollection<PipelineValidationError> validationErrors,
        CancellationToken cancellationToken);
}