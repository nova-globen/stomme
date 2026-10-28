using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Walks a step tree starting at the root.
/// </summary>
public interface IStepVisitor
{
    /// <summary>
    /// Visits the specified step and returns a tree structure representing the step and its descendants.
    /// </summary>
    /// <param name="step">The step to visit.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tree structure representing the step and its descendants.</returns>
    Task<StepNode> VisitAsync(IStep step, CancellationToken cancellationToken = default);
    
}