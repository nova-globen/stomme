using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// A registry for step types that are missing from the pipeline definition.
/// </summary>
public interface IMissingStepTypesRegistry
{
    /// <summary>
    /// Adds a missing step type.
    /// </summary>
    /// <param name="missingStepType">The missing step type.</param>
    void Add(MissingStepType missingStepType);
    
    /// <summary>
    /// Removes a missing step type.
    /// </summary>
    /// <param name="typeName">The type name of the missing step type to remove.</param>
    /// <param name="version">The version of the missing step type to remove.</param>
    void Remove(string typeName, int version);

    /// <summary>
    /// Returns a list of missing step types.
    /// </summary>
    /// <returns>A list of missing step types.</returns>
    IEnumerable<MissingStepType> List();
}