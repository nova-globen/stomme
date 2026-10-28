using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Attributes;
using Stomme.Engine.Features.Services;

namespace Stomme.Engine.Features;

/// <summary>
/// A wrapper for invoking application-specific configuration, ensuring it is invoked lastly.
/// </summary>
[DependsOn(typeof(EngineFeature))]
public class AppFeature(IModule module) : FeatureBase(module)
{
    /// <summary>
    /// The configurator to invoke.
    /// </summary>
    public Action<IModule>? Configurator { get; set; }

    /// <inheritdoc />
    public override void Configure()
    {
        Configurator?.Invoke(Module);
    }
}