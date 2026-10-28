using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides utilities that assigns unique identities to step graph nodes. 
/// </summary>
public interface IIdentityGraphService
{
    /// <summary>
    /// Assign unique identities tot the specified <see cref="Pipeline"/> and its steps.
    /// </summary>
    Task AssignIdentitiesAsync(Pipeline pipeline, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Assign unique identities tot the specified <see cref="IStep"/> graph.
    /// </summary>
    Task AssignIdentitiesAsync(IStep root, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Assign unique identities tot the specified <see cref="StepNode"/> graph.
    /// </summary>
    Task AssignIdentitiesAsync(StepNode root);
    
    /// <summary>
    /// Assign unique identities tot the specified flattened list of <see cref="StepNode"/>s.
    /// </summary>
    Task AssignIdentitiesAsync(ICollection<StepNode> flattenedList);    
    
    /// <summary>
    /// Assign unique identities to input and output properties of the specified <see cref="IStep"/> graph.
    /// </summary>
    Task AssignInputOutputsAsync(IStep step);
    
    /// <summary>
    /// Assign unique identities to variables of the specified <see cref="IVariableContainer"/>.
    /// </summary>
    void AssignVariables(IVariableContainer step);
}