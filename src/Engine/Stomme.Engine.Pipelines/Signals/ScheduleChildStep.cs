namespace Stomme.Engine.Pipelines.Signals;

/// <summary>
/// Signaled when the scheduling of a child step was requested.
/// </summary>
public class ScheduleChildStep
{
    /// <summary>
    /// Signaled when the scheduling of a child step was requested.
    /// </summary>
    /// <param name="step">The child step to schedule.</param>
    /// <param name="input">Input to pass to the child step.</param>
    public ScheduleChildStep(IStep step, IDictionary<string, object>? input = default)
    {
        Step = step;
        Input = input;
    }

    /// <summary>
    /// Signaled when the scheduling of a child step was requested.
    /// </summary>
    /// <param name="stepExecutionContext">The child step execution context to schedule.</param>
    /// <param name="input">The scheduling options.</param>
    public ScheduleChildStep(StepExecutionContext? stepExecutionContext, IDictionary<string, object>? input = default)
    {
        StepExecutionContext = stepExecutionContext;
        Input = input;
    }

    /// <summary>The child step to schedule.</summary>
    public IStep? Step { get; init; }

    /// <summary>
    /// Input to pass to the child step.
    /// </summary>
    public IDictionary<string, object>? Input { get; set;}

    /// <summary>
    /// The child step execution context to schedule.
    /// </summary>
    public StepExecutionContext? StepExecutionContext { get; init; }
}