using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a service for looking up step descriptors.
/// </summary>
public interface IStepRegistryLookupService
{
    /// <summary>
    /// Returns the step descriptor for the specified step type.
    /// </summary>
    /// <param name="type">The step type.</param>
    /// <returns>The step descriptor for the specified step type.</returns>
    Task<StepDescriptor?> FindAsync(string type);

    /// <summary>
    /// Returns the step descriptor for the specified step type and version.
    /// </summary>
    /// <param name="type">The step type.</param>
    /// <param name="version">The step version.</param>
    /// <returns>The step descriptor for the specified step type and version.</returns>
    Task<StepDescriptor?> FindAsync(string type, int version);

    /// <summary>
    /// Returns the step descriptor matching the specified predicate.
    /// </summary>
    /// <param name="predicate">The predicate to match.</param>
    /// <returns>The step descriptor matching the specified predicate.</returns>
    Task<StepDescriptor?> FindAsync(Func<StepDescriptor, bool> predicate);
    
    /// <summary>
    /// Returns all step descriptors matching the specified predicate.
    /// </summary>
    /// <param name="predicate">The predicate to match.</param>
    /// <returns>All step descriptors matching the specified predicate.</returns>
    IEnumerable<StepDescriptor> FindMany(Func<StepDescriptor, bool> predicate);
}