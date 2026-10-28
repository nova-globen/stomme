using System.ComponentModel.DataAnnotations;

namespace Stomme.Engine.Pipelines.ActivationValidators;

/// <summary>
/// Always allow the creation of a new pipeline instance.
/// </summary>
[Display(Name = "Always", Description = "Always allow the creation of a new pipeline instance.")]
public class AllowAlwaysStrategy : IPipelineActivationStrategy
{
    /// <summary>
    /// Always allow creating a new instance. 
    /// </summary>
    public ValueTask<bool> GetAllowActivationAsync(PipelineInstantiationStrategyContext context) => new(true);
}