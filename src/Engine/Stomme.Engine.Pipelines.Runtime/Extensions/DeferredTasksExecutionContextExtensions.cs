using Stomme.Engine.Extensions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Provides a set of extension methods for working with deferred tasks within a pipeline execution context.
/// </summary>
public static class DeferredTasksExecutionContextExtensions
{
    private static readonly object DeferredTasksKey = new();

    /// <summary>
    /// Defers the execution of a task within the pipeline execution context.
    /// Deferred tasks are executed right after bookmarks have been persisted.
    /// </summary>
    public static void DeferTask(this PipelineExecutionContext context, Func<Task> task)
    {
        var deferredTasks = context.GetDeferredTasksInternal();
        deferredTasks.Add(task);
    }
    
    /// <summary>
    /// Defers the execution of a task within the pipeline execution context.
    /// Deferred tasks are executed right after bookmarks have been persisted.
    /// </summary>
    public static void DeferTask(this StepExecutionContext context, Action task)
    {
        context.DeferTask(() =>
        {
            task();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Defers the execution of a task within the pipeline execution context.
    /// Deferred tasks are executed right after bookmarks have been persisted.
    /// </summary>
    public static void DeferTask(this StepExecutionContext context, Func<Task> task)
    {
        context.PipelineExecutionContext.DeferTask(task);
    }

    /// <summary>
    /// Executes all deferred tasks within the pipeline execution context.
    /// </summary>
    public static async Task ExecuteDeferredTasksAsync(this PipelineExecutionContext context)
    {
        var deferredTasks = context.GetDeferredTasksInternal();
        var tasks = deferredTasks.Select(x => x()).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static ICollection<Func<Task>> GetDeferredTasksInternal(this PipelineExecutionContext context)
    {
        return context.TransientProperties.GetOrAdd(DeferredTasksKey, () => new List<Func<Task>>());
    }
}