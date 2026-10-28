using System.Diagnostics.CodeAnalysis;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides context information when a step has completed.
/// </summary>
public record StepCompletedContext(StepExecutionContext TargetContext, StepExecutionContext ChildContext, object? Result = default)
{
    /// <summary>
    /// Gets the pipeline execution context.
    /// </summary>
    public PipelineExecutionContext PipelineExecutionContext => TargetContext.PipelineExecutionContext;
    
    /// <summary>
    /// Resolves a required service using the service provider.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The resolved service.</returns>
    public T GetRequiredService<T>() where T : notnull => PipelineExecutionContext.GetRequiredService<T>();
    
    /// <summary>
    /// A cancellation token to use when invoking asynchronous operations.
    /// </summary>
    public CancellationToken CancellationToken => PipelineExecutionContext.CancellationToken;
    
    /// <summary>
    /// Complete the current step. This should only be called by steps that explicitly suppress automatic-completion.
    /// </summary>
    [RequiresUnreferencedCode("The step may be serialized and executed in a different context.")]
    public async ValueTask CompleteStepAsync(object? result = default)
    {
        await TargetContext.CompleteStepAsync(result).ConfigureAwait(false);
    }

    /// <summary>
    /// Complete the current step with the specified outcomes.
    /// </summary>
    [RequiresUnreferencedCode("The step may be serialized and executed in a different context.")]
    public ValueTask CompleteStepWithOutcomesAsync(params string[] outcomes) => CompleteStepAsync(new Outcomes(outcomes));
}