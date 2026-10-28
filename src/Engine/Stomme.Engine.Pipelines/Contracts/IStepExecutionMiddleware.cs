namespace Stomme.Engine.Pipelines;

/// <summary>
/// The interface for step execution middleware components.
/// </summary>
public interface IStepExecutionMiddleware
{
    /// <summary>
    /// The method that is called to execute the middleware.
    /// </summary>
    ValueTask InvokeAsync(StepExecutionContext context);
}

