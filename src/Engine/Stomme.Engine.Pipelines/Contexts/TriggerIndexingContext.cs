using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

public class TriggerIndexingContext(PipelineIndexingContext pipelineIndexingContext, ExpressionExecutionContext expressionExecutionContext, ITrigger trigger, CancellationToken cancellationToken)
{
    public PipelineIndexingContext PipelineIndexingContext { get; } = pipelineIndexingContext;
    public ExpressionExecutionContext ExpressionExecutionContext { get; } = expressionExecutionContext;
    public ITrigger Trigger { get; } = trigger;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public string TriggerName { get; set; } = trigger.Type;

    public T? Get<T>(Input<T>? input) => ExpressionExecutionContext.Get(input);
}