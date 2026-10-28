using Stomme.Engine.Pipelines.Options;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class StepInvoker(
    IStepExecutionPipeline executionPipeline,
    ILoggerStateGenerator<StepExecutionContext> loggerStateGenerator,
    ILogger<StepInvoker> logger)
    : IStepInvoker
{

    /// <inheritdoc />
    public async Task<StepExecutionContext> InvokeAsync(PipelineExecutionContext pipelineExecutionContext, IStep step, StepInvocationOptions? options = null)
    {
        // Setup a step execution context, potentially reusing an existing one if requested.
        var existingStepExecutionContext = options?.ExistingStepExecutionContext;

        // Perform a lookup to make sure the step execution context is part of the pipeline execution context.
        var stepExecutionContext = existingStepExecutionContext != null
            ? pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == existingStepExecutionContext.Id)
            : null;

        if (stepExecutionContext == null)
        {
            // Create a new step execution context.
            stepExecutionContext = await pipelineExecutionContext.CreateStepExecutionContextAsync(step, options).ConfigureAwait(false);
            stepExecutionContext.Taint();

            // Add the step context to the pipeline context.
            pipelineExecutionContext.AddStepExecutionContext(stepExecutionContext);
        }

        // Execute the step execution pipeline.
        await InvokeAsync(stepExecutionContext).ConfigureAwait(false);
        
        return stepExecutionContext;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(StepExecutionContext stepExecutionContext)
    {
        var loggerState = loggerStateGenerator.GenerateLoggerState(stepExecutionContext);
        using var loggingScope = logger.BeginScope(loggerState);

        // Execute the step execution pipeline.
        await executionPipeline.ExecuteAsync(stepExecutionContext).ConfigureAwait(false);
    }
}