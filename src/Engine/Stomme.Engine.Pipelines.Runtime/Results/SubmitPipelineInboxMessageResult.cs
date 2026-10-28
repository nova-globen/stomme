using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Results;

/// <summary>
/// Contains pipeline execution results
/// </summary>
/// <param name="Message">The message that was submitted.</param>
/// <param name="PipelineExecutionResults">Contains pipeline execution results</param>
public record SubmitPipelineInboxMessageResult(PipelineInboxMessage Message, ICollection<PipelineExecutionResult> PipelineExecutionResults);