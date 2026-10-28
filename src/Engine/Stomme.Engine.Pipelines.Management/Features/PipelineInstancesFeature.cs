using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.Management.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Management.Features;

/// <summary>
/// Enables storage of pipeline instances.
/// </summary>
public class PipelineInstancesFeature(IModule module) : FeatureBase(module)
{
    /// <summary>
    /// The factory to create new instances of <see cref="IPipelineInstanceStore"/>.
    /// </summary>
    public Func<IServiceProvider, IPipelineInstanceStore> PipelineInstanceStore { get; set; } = sp => sp.GetRequiredService<MemoryPipelineInstanceStore>();

    /// <inheritdoc />
    public override void Apply()
    {
        Services.AddScoped(PipelineInstanceStore);
    }
}