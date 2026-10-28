using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Steps;

/// <summary>
/// A simple container that executes the specified step.
/// </summary>
[Step("Stomme", "Flow", "A simple container that executes the specified step.", DisplayName = "Container")]
[PublicAPI]
public class FlowNode : Step
{
    
    /// <inheritdoc />
    public FlowNode([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }
    
    /// <summary>
    /// The step to execute.
    /// </summary>
    [Port]
    public IStep? Body { get; set; }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context) => await context.ScheduleStepAsync(Body, OnBodyCompletedAsync).ConfigureAwait(false);

    private async ValueTask OnBodyCompletedAsync(StepCompletedContext context) => await context.TargetContext.CompleteStepAsync().ConfigureAwait(false);
}