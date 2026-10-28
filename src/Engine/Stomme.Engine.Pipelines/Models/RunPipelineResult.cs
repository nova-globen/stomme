using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Contains information about a pipeline run, such as <see cref="PipelineState"/>.
/// </summary>
public record RunPipelineResult(PipelineExecutionContext PipelineExecutionContext, PipelineState PipelineState, Pipeline Pipeline, object? Result, Journal Journal);

/// <summary>
/// Contains information about a pipeline run, such as <see cref="PipelineState"/>.
/// </summary>
public record RunPipelineResult<TResult>(PipelineExecutionContext PipelineExecutionContext, PipelineState PipelineState, Pipeline Pipeline, TResult Result, Journal Journal);

public record Journal(ICollection<PipelineExecutionLogEntry> PipelineExecutionLogEntries, ICollection<StepExecutionContext> StepExecutionContexts)
{
    public static Journal Empty => new([], []);   
}