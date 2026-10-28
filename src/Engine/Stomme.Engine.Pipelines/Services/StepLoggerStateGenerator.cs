namespace Stomme.Engine.Pipelines.Services;

/// <summary>
/// Generates logger state for the specified <see cref="StepExecutionContext"/>.
/// </summary>
public class StepLoggerStateGenerator : ILoggerStateGenerator<StepExecutionContext>
{
    /// <summary>
    /// Generates logger state for the specified <see cref="StepExecutionContext"/>.
    /// </summary>
    /// <param name="stepExecutionContext">The <see cref="StepExecutionContext"/> to generate logger state for.</param>
    /// <returns>A <see cref="Dictionary{String, Object}" /> containing the state related to the <see cref="StepExecutionContext"/>.</returns>
    public Dictionary<string, object> GenerateLoggerState(StepExecutionContext stepExecutionContext)
    {
        return new()
        {
            ["StepInstanceId"] = stepExecutionContext.Id
        };
    }
}
