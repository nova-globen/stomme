using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Features;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extensions to the specified <see cref="IModule"/>/
/// </summary>
public static class ModuleExtensions
{
    /// <summary>
    /// Adds the pipeline management feature to the specified module. 
    /// </summary>
    public static IModule UsePipelineManagement(this IModule module, Action<PipelineManagementFeature>? configure = null)
    {
        module.Configure<PipelineManagementFeature>(management =>
        {
            management.AddStep<NotFoundStep>();
            configure?.Invoke(management);
        });
        return module;
    }

    /// <summary>
    /// Adds the default pipeline management feature to the specified module. 
    /// </summary>
    public static PipelineManagementFeature UsePipelineDefinitions(this PipelineManagementFeature feature, Action<PipelineDefinitionsFeature>? configure = null)
    {
        feature.Module.Configure(configure);
        return feature;
    }

    /// <summary>
    /// Adds the pipeline instance feature to pipeline management module. 
    /// </summary>
    public static PipelineManagementFeature UsePipelineInstances(this PipelineManagementFeature feature, Action<PipelineInstancesFeature>? configure = null)
    {
        feature.Module.Configure(configure);
        return feature;
    }

    /// <summary>
    /// Adds all types implementing <see cref="IStep"/> to the system.
    /// </summary>
    public static IModule AddStepsFrom<TMarkerType>(this IModule module) => module.UsePipelineManagement(management => management.AddStepsFrom<TMarkerType>());

    /// <summary>
    /// Adds the specified step type to the system.
    /// </summary>
    public static IModule AddStep<T>(this IModule module) where T : IStep => module.UsePipelineManagement(management => management.AddStep<T>());

    /// <summary>
    /// Registers the specified step host type to the pipeline system.
    /// </summary>
    public static IModule AddStepHost<T>(this IModule module) where T : class => module.UsePipelineManagement(management => management.AddStepHost<T>());
    
    /// <summary>
    /// Removes the specified step type from the system.
    /// </summary>
    public static IModule RemoveStep<T>(this IModule module) where T : IStep => module.UsePipelineManagement(management => management.RemoveStep<T>());
    
    /// <summary>
    /// Adds the specified variable type to the system.
    /// </summary>
    public static IModule AddVariableType<T>(this IModule module, string category) => module.UsePipelineManagement(management => management.AddVariableType<T>(category));

    /// <summary>
    /// Adds a variable type and its alias to the specified module.
    /// </summary>
    public static IModule AddVariableTypeAndAlias<T>(this IModule module, string alias, string category)
    {
        return module
            .UsePipelineManagement(management => management.AddVariableType<T>(category))
            .AddTypeAlias<T>(alias);
    }

    /// <summary>
    /// Adds caching stores feature to the pipeline management feature.
    /// </summary>
    public static PipelineManagementFeature UseCache(this PipelineManagementFeature feature, Action<CachingPipelineDefinitionsFeature>? configure = null)
    {
        feature.Module.Configure(configure);
        return feature;
    }
}