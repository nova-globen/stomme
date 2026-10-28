namespace Stomme.Engine.Pipelines.Runtime.Results;

public record TriggerPipelinesResult(ICollection<PipelineExecutionResult> TriggeredPipelines);