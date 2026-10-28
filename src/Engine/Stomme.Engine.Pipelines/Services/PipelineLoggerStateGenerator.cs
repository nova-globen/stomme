namespace Stomme.Engine.Pipelines.Services;

/// <summary>
/// Generates logger state for the specified <see cref="PipelineExecutionContext"/>.
/// </summary>
public class PipelineLoggerStateGenerator : ILoggerStateGenerator<PipelineExecutionContext>
{
    /// <summary>
    /// Generates logger state for the specified <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="pipelineExecutionContext">The <see cref="PipelineExecutionContext"/> to generate logger state for.</param>
    /// <returns>A <see cref="Dictionary{String, Object}" /> containing the state related to the <see cref="PipelineExecutionContext"/>.</returns>
    public Dictionary<string, object> GenerateLoggerState(PipelineExecutionContext pipelineExecutionContext)
    {
        return new Dictionary<string, object>
        {
            ["PipelineInstanceId"] = pipelineExecutionContext.Id
        };
    }
}
