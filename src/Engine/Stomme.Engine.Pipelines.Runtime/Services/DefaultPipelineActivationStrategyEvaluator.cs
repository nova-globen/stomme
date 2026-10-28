namespace Stomme.Engine.Pipelines.Runtime;

public class DefaultPipelineActivationStrategyEvaluator(IEnumerable<IPipelineActivationStrategy> strategies) : IPipelineActivationStrategyEvaluator
{
    public async Task<bool> CanStartPipelineAsync(PipelineActivationStrategyEvaluationContext context)
    {
        var pipeline = context.Pipeline;
        var strategyType = pipeline.Options.ActivationStrategyType;

        if (strategyType == null)
            return true;
        
        var strategy = strategies.FirstOrDefault(x => x.GetType() == strategyType);

        if (strategy == null)
            return true;
        
        var correlationId = context.CorrelationId;
        var cancellationToken = context.CancellationToken;
        var strategyContext = new PipelineInstantiationStrategyContext(pipeline, correlationId, cancellationToken);
        return await strategy.GetAllowActivationAsync(strategyContext).ConfigureAwait(false);
    }
}