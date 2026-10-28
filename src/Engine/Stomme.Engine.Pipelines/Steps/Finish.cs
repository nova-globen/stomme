using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Mark the pipeline as finished.
/// </summary>
[Step("Stomme", "Primitives", "Mark the pipeline as finished.")]
[PublicAPI]
public class Finish : CodeStep, ITerminalNode
{
    /// <inheritdoc />
    public Finish([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        context.PipelineExecutionContext.ClearCompletionCallbacks();
        context.PipelineExecutionContext.Scheduler.Clear();
        context.PipelineExecutionContext.Bookmarks.Clear();
        context.PipelineExecutionContext.TransitionTo(PipelineSubStatus.Finished);
    }
}