using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Attributes;
using Stomme.Engine.Features.Services;

namespace Stomme.Engine.Pipelines.Runtime.Features;

/// <summary>
/// Installs the default runtime services.
/// </summary>
[DependsOn(typeof(PipelineRuntimeFeature))]
public class DefaultPipelineRuntimeFeature(IModule module) : FeatureBase(module);