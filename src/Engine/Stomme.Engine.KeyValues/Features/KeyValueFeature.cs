using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.KeyValues.Features;

/// <summary>
/// Installs and configures instance management features.
/// </summary>
public class KeyValueFeature : FeatureBase
{
    /// <inheritdoc />
    public KeyValueFeature(IModule module) : base(module)
    {
    }

    /// <summary>
    /// A factory that instantiates an <see cref="IKeyValueStore"/>.
    /// </summary>
    public Func<IServiceProvider, IKeyValueStore> KeyValueStore { get; set; } = sp => ActivatorUtilities.CreateInstance<MemoryKeyValueStore>(sp);


    /// <inheritdoc />
    public override void Apply()
    {
        Services.AddScoped(KeyValueStore);
    }
}