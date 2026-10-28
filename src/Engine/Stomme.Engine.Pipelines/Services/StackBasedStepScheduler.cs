using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// A LIFO stack based step scheduler.
/// </summary>
[PublicAPI]
public class StackBasedStepScheduler : IStepScheduler
{
    private readonly Stack<StepWorkItem> _stack = new();

    /// <inheritdoc />
    public bool HasAny => _stack.Any();

    /// <inheritdoc />
    public void Schedule(StepWorkItem step) => _stack.Push(step);

    /// <inheritdoc />
    public StepWorkItem Take() => _stack.Pop();

    /// <inheritdoc />
    public IEnumerable<StepWorkItem> List() => _stack.ToList();

    /// <inheritdoc />
    public bool Any(Func<StepWorkItem, bool> predicate) => _stack.Any(predicate);

    /// <inheritdoc />
    public StepWorkItem? Find(Func<StepWorkItem, bool> predicate) => _stack.FirstOrDefault(predicate);

    /// <inheritdoc />
    public void Clear() => _stack.Clear();
}