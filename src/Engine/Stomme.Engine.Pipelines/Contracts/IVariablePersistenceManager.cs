namespace Stomme.Engine.Pipelines;

/// <summary>
/// Helps managing the persistence of variables.
/// </summary>
public interface IVariablePersistenceManager
{
    /// <summary>
    /// Loads the variables into the specified <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="excludeTags"></param>
    /// <returns></returns>
    Task LoadVariablesAsync(PipelineExecutionContext context, IEnumerable<string>? excludeTags = default);

    /// <summary>
    /// Persists all persistable variables from the specified <see cref="PipelineExecutionContext"/>. 
    /// </summary>
    Task SaveVariablesAsync(PipelineExecutionContext context);

    /// <summary>
    /// Deletes the specified variables from the <see cref="StepExecutionContext"/>.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="includeTags"></param>
    /// <returns></returns>
    Task DeleteVariablesAsync(StepExecutionContext context, IEnumerable<string>? includeTags = default);
    
    /// <summary>
    /// Deletes the specified variables from the <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="includeTags"></param>
    /// <returns></returns>
    Task DeleteVariablesAsync(PipelineExecutionContext context, IEnumerable<string>? includeTags = default);
}