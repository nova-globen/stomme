using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Invokes steps.
/// </summary>
public interface IStepInvoker
{
    /// <summary>
    /// Invokes the specified step.
    /// </summary>
    /// <param name="pipelineExecutionContext">The pipeline execution context.</param>
    /// <param name="step">The step to invoke.</param>
    /// <param name="options"></param>
    Task<StepExecutionContext> InvokeAsync(PipelineExecutionContext pipelineExecutionContext, IStep step, StepInvocationOptions? options = null);

    /// <summary>
    /// Invokes the specified step execution context.
    /// </summary>
    Task InvokeAsync(StepExecutionContext stepExecutionContext);
}