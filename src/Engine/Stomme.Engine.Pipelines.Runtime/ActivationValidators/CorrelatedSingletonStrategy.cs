using System.ComponentModel.DataAnnotations;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;

namespace Stomme.Engine.Pipelines.Runtime.ActivationValidators;

/// <summary>
/// Only allow new pipeline instances if a running one of the same pipeline definition and correlation ID doesn't already exist.
/// </summary>
[Display(Name = "Correlated singleton", Description = "Only allow new pipeline instances if a running one of the same pipeline definition and correlation ID doesn't already exist.")]
public class CorrelatedSingletonStrategy(IPipelineInstanceStore pipelineInstanceStore) : IPipelineActivationStrategy
{
    /// <summary>
    /// Only allow a new instance if no running ones exist already. 
    /// </summary>
    public async ValueTask<bool> GetAllowActivationAsync(PipelineInstantiationStrategyContext context)
    {
        var filter = new PipelineInstanceFilter
        {
            DefinitionId = context.Pipeline.Identity.DefinitionId,
            CorrelationId = context.CorrelationId,
            PipelineStatus = PipelineStatus.Running
        };

        var count = await pipelineInstanceStore.CountAsync(filter, context.CancellationToken).ConfigureAwait(false);
        return count == 0;
    }
}