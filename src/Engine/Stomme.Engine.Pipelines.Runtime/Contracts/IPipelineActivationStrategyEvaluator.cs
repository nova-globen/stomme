namespace Stomme.Engine.Pipelines.Runtime;

public interface IPipelineActivationStrategyEvaluator
{
    public Task<bool> CanStartPipelineAsync(PipelineActivationStrategyEvaluationContext context);
}