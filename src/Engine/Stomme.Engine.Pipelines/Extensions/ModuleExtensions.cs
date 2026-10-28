using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.Features;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

public static class ModuleExtensions
{
    extension(IModule configuration)
    {
        public IModule UsePipelines(Action<PipelinesFeature>? configure = null)
        {
            configuration.Configure(configure);
            return configuration;
        }

        public IModule UseFlowchart(Action<FlowchartFeature>? configure = null)
        {
            configuration.Configure(configure);
            return configuration;
        }
    }
}