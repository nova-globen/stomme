using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// The scheduler contains work items to execute.
/// It continuously takes the next work item from the list until there are no more items left.
/// </summary>
public interface IStepScheduler
{
    /// <summary>
    /// Returns true if there are any work items in the scheduler.
    /// </summary>
    bool HasAny { get; }
    
    /// <summary>
    /// Schedules a work item.
    /// </summary>
    /// <param name="workItem">The work item to schedule.</param>
    void Schedule(StepWorkItem workItem);
    
    /// <summary>
    /// Takes the next work item from the scheduler.
    /// </summary>
    StepWorkItem Take();
    
    /// <summary>
    /// Returns a list of all work items in the scheduler.
    /// </summary>
    IEnumerable<StepWorkItem> List();
    
    /// <summary>
    /// Returns true if there are any work items matching the specified predicate.
    /// </summary>
    /// <param name="predicate">The predicate to match.</param>
    bool Any(Func<StepWorkItem, bool> predicate);
    
    /// <summary>
    /// Returns the first work item matching the specified predicate.
    /// </summary>
    /// <param name="predicate">The predicate to match.</param>
    StepWorkItem? Find(Func<StepWorkItem, bool> predicate);
    
    /// <summary>
    /// Clears all work items from the scheduler.
    /// </summary>
    void Clear();
}