using System.ComponentModel.DataAnnotations;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;

namespace Stomme.Engine.Pipelines.Runtime.ActivationValidators;

/// <summary>
/// Only allow new pipeline instances if a running one with the same correlation ID doesn't already exist.
/// </summary>
[Display(Name = "Correlation", Description = "Only allow new pipeline instances of any pipeline definition if a running one with the same correlation ID doesn't already exist.")]
public class CorrelationStrategy(IPipelineInstanceStore pipelineInstanceStore) : IPipelineActivationStrategy
{
    /// <summary>
    /// Only allow a new instance if no running ones exist already. 
    /// </summary>
    public async ValueTask<bool> GetAllowActivationAsync(PipelineInstantiationStrategyContext context)
    {
        var filter = new PipelineInstanceFilter
        {
            CorrelationId = context.CorrelationId,
            PipelineStatus = PipelineStatus.Running
        };

        var count = await pipelineInstanceStore.CountAsync(filter).ConfigureAwait(false);
        return count == 0;
    }
}