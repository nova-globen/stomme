namespace Stomme.Engine.Pipelines.Runtime.Results;

/// <summary>
/// Result of delivering a pipeline inbox message.
/// </summary>
public record DeliverPipelineInboxMessageResult(ICollection<PipelineExecutionResult> PipelineExecutionResults);