using System.ComponentModel.DataAnnotations;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;

namespace Stomme.Engine.Pipelines.Runtime.ActivationValidators;

/// <summary>
/// Only allow new pipeline instances if a running one of the same pipeline definition doesn't already exist.
/// </summary>
[Display(Name = "Singleton", Description = "Only allow new pipeline instances if a running one of the same pipeline definition doesn't already exist.")]
public class SingletonStrategy(IPipelineInstanceStore pipelineInstanceStore) : IPipelineActivationStrategy
{
    /// <summary>
    /// Only allow a new instance if no running ones exist already. 
    /// </summary>
    public async ValueTask<bool> GetAllowActivationAsync(PipelineInstantiationStrategyContext context)
    {
        var filter = new PipelineInstanceFilter
        {
            DefinitionId = context.Pipeline.Identity.DefinitionId,
            PipelineStatus = PipelineStatus.Running
        };

        var count = await pipelineInstanceStore.CountAsync(filter).ConfigureAwait(false);
        return count == 0;
    }
}