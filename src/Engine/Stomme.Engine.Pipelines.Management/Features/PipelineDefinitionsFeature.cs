using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.Management.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Management.Features;

/// <summary>
/// Configures pipeline definition storage.
/// </summary>
public class PipelineDefinitionsFeature(IModule module) : FeatureBase(module)
{
    /// <summary>
    /// The factory to create new instances of <see cref="IPipelineDefinitionStore"/>.
    /// </summary>
    public Func<IServiceProvider, IPipelineDefinitionStore> PipelineDefinitionStore { get; set; } = sp => sp.GetRequiredService<MemoryPipelineDefinitionStore>();

    /// <inheritdoc />
    public override void Apply()
    {
        Services
            .AddScoped(PipelineDefinitionStore);
    }
}