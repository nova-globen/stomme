using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.Management.Handlers.Notifications;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Management.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Management.Features;

/// <summary>
/// Configures pipeline definition caching.
/// </summary>
public class CachingPipelineDefinitionsFeature : FeatureBase
{
    /// <inheritdoc />
    public CachingPipelineDefinitionsFeature(IModule module) : base(module)
    {
    }
    
    /// <inheritdoc />
    public override void Apply()
    {
        Services.AddSingleton<IPipelineDefinitionCacheManager, PipelineDefinitionCacheManager>();
        Services.Decorate<IPipelineDefinitionStore, CachingPipelineDefinitionStore>();
        Services.Decorate<IPipelineDefinitionService, CachingPipelineDefinitionService>();
        Services.AddNotificationHandler<EvictPipelineDefinitionServiceCache>();
    }
}