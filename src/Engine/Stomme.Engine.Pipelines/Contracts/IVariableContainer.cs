using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a container for <see cref="Variable"/>s.
/// </summary>
public interface IVariableContainer : IStep
{
    /// <summary>
    /// A collection of variables within the scope of the variable container.
    /// </summary>
    ICollection<Variable> Variables { get; }
}