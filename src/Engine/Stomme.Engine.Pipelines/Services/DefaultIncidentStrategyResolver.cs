using Stomme.Engine.Pipelines.IncidentStrategies;
using Stomme.Engine.Pipelines.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class DefaultIncidentStrategyResolver : IIncidentStrategyResolver
{
    private readonly IOptions<IncidentOptions> _options;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultIncidentStrategyResolver"/> class.
    /// </summary>
    public DefaultIncidentStrategyResolver(IOptions<IncidentOptions> options, IServiceProvider serviceProvider)
    {
        _options = options;
        _serviceProvider = serviceProvider;
    }
    
    /// <inheritdoc />
    public ValueTask<IIncidentStrategy> ResolveStrategyAsync(StepExecutionContext context, CancellationToken cancellationToken = default)
    {
        var strategyType = ResolveStrategyType(context);
        var strategy = (IIncidentStrategy)ActivatorUtilities.GetServiceOrCreateInstance(_serviceProvider, strategyType);
        
        return new(strategy);
    }

    private Type ResolveStrategyType(StepExecutionContext context)
    {
        // First check if the pipeline is configured with an incident strategy.
        // If no strategy was configured there, use the application-level configured strategy.
        
        var strategyType = context.PipelineExecutionContext.Pipeline.Options.IncidentStrategyType;
        
        if(strategyType == null)
            strategyType = _options.Value.DefaultIncidentStrategy;

        return strategyType ?? typeof(FaultStrategy);
    }
}