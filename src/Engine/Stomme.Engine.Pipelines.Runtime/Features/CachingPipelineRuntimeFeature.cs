using Stomme.Engine.Caching.Features;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Attributes;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.Runtime.Handlers;
using Stomme.Engine.Pipelines.Runtime.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Runtime.Features;

/// <summary>
/// Installs and configures pipeline runtime caching features.
/// </summary>
[DependsOn(typeof(MemoryCacheFeature))]
public class CachingPipelineRuntimeFeature : FeatureBase
{
    /// <inheritdoc />
    public CachingPipelineRuntimeFeature(IModule module) : base(module)
    {
    }

    /// <inheritdoc />
    public override void Apply()
    {
        Services
            // Decorators.
            .Decorate<ITriggerStore, CachingTriggerStore>()

            // Handlers.
            .AddNotificationHandler<InvalidateTriggersCache>()
            .AddNotificationHandler<InvalidatePipelinesCache>();
    }
}