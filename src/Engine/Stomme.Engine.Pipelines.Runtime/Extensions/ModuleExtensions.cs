using System.Reflection;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime.Features;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Adds extensions to setup the <see cref="PipelineRuntimeFeature"/> feature.
/// </summary>
public static class ModuleExtensions
{
    /// <summary>
    /// Enables the <see cref="PipelineRuntimeFeature"/> feature.
    /// </summary>
    public static IModule UsePipelineRuntime(this IModule module, Action<PipelineRuntimeFeature>? configure = default)
    {
        module.Configure(configure);
        return module;
    }

    /// <summary>
    /// Register the specified pipeline type.
    /// </summary>
    public static IModule AddPipeline<T>(this IModule module) where T : IPipeline
    {
        module.Configure<PipelineRuntimeFeature>().AddPipeline<T>();
        return module;
    }

    /// <summary>
    /// Register all pipelines contained in the assembly containing the specified marker type.
    /// </summary>
    public static IModule AddPipelinesFrom<TMarker>(this IModule module) => module.AddPipelinesFrom(typeof(TMarker).Assembly);
    
    /// <summary>
    /// Register all pipelines in the specified assembly.
    /// </summary>
    public static IModule AddPipelinesFrom(this IModule module, Assembly assembly)
    {
        module.Configure<PipelineRuntimeFeature>().AddPipelinesFrom(assembly);
        return module;
    }
    
    /// <summary>
    /// Configures the default pipeline runtime.
    /// </summary>
    /// <param name="feature">The pipeline runtime feature.</param>
    /// <param name="configure">A callback that configures the default pipeline runtime.</param>
    /// <returns>The pipeline runtime feature.</returns>
    public static PipelineRuntimeFeature UseDefaultRuntime(this PipelineRuntimeFeature feature, Action<DefaultPipelineRuntimeFeature>? configure = default)
    {
        feature.Module.Configure(configure);
        return feature;
    }
    
    /// <summary>
    /// Adds caching stores feature to the pipeline runtime feature.
    /// </summary>
    public static PipelineRuntimeFeature UseCache(this PipelineRuntimeFeature feature, Action<CachingPipelineRuntimeFeature>? configure = default)
    {
        feature.Module.Configure(configure);
        return feature;
    }
}