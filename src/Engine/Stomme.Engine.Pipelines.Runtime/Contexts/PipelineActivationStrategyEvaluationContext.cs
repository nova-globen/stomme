using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Runtime;

public class PipelineActivationStrategyEvaluationContext
{
    public Pipeline Pipeline { get; set; } = default!;
    public string? CorrelationId { get; set; }
    public CancellationToken CancellationToken { get; set; }
}