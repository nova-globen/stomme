namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Provides functionality for evaluating log persistence settings for step properties
/// during the execution of a pipeline.
/// </summary>
public interface IStepPropertyLogPersistenceEvaluator
{
    /// <summary>
    /// Evaluates the log persistence settings for step properties within the context of a pipeline's execution.
    /// </summary>
    Task<StepLogPersistenceModeMap> EvaluateLogPersistenceModesAsync(StepExecutionContext context);

    /// <summary>
    /// Retrieves a dictionary of persistable output values generated during the execution of a step.
    /// </summary>
    /// <returns>A dictionary where the keys represent output property names and the values represent their persistable data.</returns>
    Task<Dictionary<string, object>> GetPersistableOutputAsync(StepExecutionContext context);
}