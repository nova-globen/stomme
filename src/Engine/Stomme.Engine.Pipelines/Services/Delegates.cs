namespace Stomme.Engine.Pipelines;

/// <summary>
/// A delegate that executes a step.
/// </summary>
public delegate ValueTask ExecuteStepDelegate(StepExecutionContext context);

/// <summary>
/// A delegate that executes a step when its child steps have completed.
/// </summary>
public delegate ValueTask StepCompletionCallback(StepCompletedContext context);