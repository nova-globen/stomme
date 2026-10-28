namespace Stomme.Engine.Pipelines;

/// <summary>
/// Implement this method if your step needs to provide bookmark data that will be used when it is marked as a trigger. 
/// </summary>
public interface ITrigger : IStep
{
    /// <summary>
    /// Implementors should return a list of objects where each object represents a bookmark datum. For each datum, a trigger is created.
    /// </summary>
    ValueTask<IEnumerable<object>> GetTriggerPayloadsAsync(TriggerIndexingContext context);
}