using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// A FIFO queue based step scheduler.
/// </summary>
[PublicAPI]
public class QueueBasedStepScheduler : IStepScheduler
{
    private readonly Queue<StepWorkItem> _queue = new();

    /// <inheritdoc />
    public bool HasAny => _queue.Any();

    /// <inheritdoc />
    public void Schedule(StepWorkItem workItem) => _queue.Enqueue(workItem);

    /// <inheritdoc />
    public StepWorkItem Take() => _queue.Dequeue();

    /// <inheritdoc />
    public IEnumerable<StepWorkItem> List() => _queue;

    /// <inheritdoc />
    public bool Any(Func<StepWorkItem, bool> predicate) => _queue.Any(predicate);

    /// <inheritdoc />
    public StepWorkItem? Find(Func<StepWorkItem, bool> predicate) => _queue.FirstOrDefault(predicate);

    /// <inheritdoc />
    public void Clear() => _queue.Clear();
}