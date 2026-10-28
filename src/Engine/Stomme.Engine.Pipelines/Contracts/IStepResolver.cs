using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// A step resolver inspects a given step and returns its contained steps.
/// They are used to inspect a pipeline structure and build a graph of nodes from it for easy node traversal.
/// </summary>
public interface IStepResolver
{
    /// <summary>
    /// The priority of this resolver. Resolvers with higher priority are executed first.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Returns true if this resolver supports the specified step.
    /// </summary>
    bool GetSupportsStep(IStep step);

    /// <summary>
    /// Returns a list of contained step ports for the specified step.
    /// </summary>
    ValueTask<IEnumerable<StepPort>> GetStepPortsAsync(IStep step, CancellationToken cancellationToken = default);
}