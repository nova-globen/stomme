namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a behavior that can be attached to a step.
/// </summary>
public interface IBehavior : ISignalHandler
{
    /// <summary>
    /// The owner of this behavior.
    /// </summary>
    IStep Owner { get; }
    
    /// <summary>
    /// Invoked when the step executes.
    /// </summary>
    ValueTask ExecuteAsync(StepExecutionContext context);
}