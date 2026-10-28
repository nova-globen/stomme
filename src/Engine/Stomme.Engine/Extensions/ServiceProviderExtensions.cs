using Stomme.Engine.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extension methods to the <see cref="IServiceProvider"/>.
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Configure the default step execution pipeline.
    /// </summary>
    public static IServiceProvider ConfigureDefaultStepExecutionPipeline(this IServiceProvider services, Action<IStepExecutionPipelineBuilder> setup)
    {
        var executionPipeline = services.GetRequiredService<IStepExecutionPipeline>();
        executionPipeline.Setup(setup);
        return services;
    }
    
}