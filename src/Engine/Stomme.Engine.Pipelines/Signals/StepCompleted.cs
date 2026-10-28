namespace Stomme.Engine.Pipelines.Signals;

/// <summary>
/// Signaled when a step has completed.
/// </summary>
/// <param name="Result">An optional result.</param>
public record StepCompleted(object? Result = null);