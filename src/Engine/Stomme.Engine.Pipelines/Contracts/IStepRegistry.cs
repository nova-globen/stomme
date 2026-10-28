using System.Diagnostics.CodeAnalysis;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Stores all step descriptors available to the system.
/// </summary>
public interface IStepRegistry : IStepProvider
{
    /// <summary>
    /// Adds a step descriptor to the registry.
    /// </summary>
    /// <param name="providerType">The type of the step provider.</param>
    /// <param name="descriptor">The step descriptor to add.</param>
    void Add(Type providerType, StepDescriptor descriptor);

    /// <summary>
    /// Removes a step descriptor from the registry.
    /// </summary>
    /// <param name="providerType">The type of the step provider.</param>
    /// <param name="descriptor">The step descriptor to remove.</param>
    void Remove(Type providerType, StepDescriptor descriptor);

    /// <summary>
    /// Returns all step descriptors in the registry.
    /// </summary>
    /// <returns>All step descriptors in the registry.</returns>
    IEnumerable<StepDescriptor> ListAll();

    /// <summary>
    /// Returns all step descriptors provided by the specified provider.
    /// </summary>
    /// <param name="providerType">The type of the step provider.</param>
    /// <returns>All step descriptors provided by the specified provider.</returns>
    IEnumerable<StepDescriptor> ListByProvider(Type providerType);

    /// <summary>
    /// Returns the step descriptor for the specified step type.
    /// </summary>
    /// <param name="type">The step type.</param>
    /// <returns>The step descriptor for the specified step type.</returns>
    StepDescriptor? Find(string type);

    /// <summary>
    /// Returns the step descriptor for the specified step type and version.
    /// </summary>
    /// <param name="type">The step type.</param>
    /// <param name="version">The step version.</param>
    /// <returns>The step descriptor for the specified step type and version.</returns>
    StepDescriptor? Find(string type, int version);

    /// <summary>
    /// Returns the step descriptor matching the specified predicate.
    /// </summary>
    /// <param name="predicate">The predicate to match.</param>
    /// <returns>The step descriptor matching the specified predicate.</returns>
    StepDescriptor? Find(Func<StepDescriptor, bool> predicate);

    /// <summary>
    /// Returns all step descriptors matching the specified predicate.
    /// </summary>
    /// <param name="predicate">The predicate to match.</param>
    /// <returns>All step descriptors matching the specified predicate.</returns>
    IEnumerable<StepDescriptor> FindMany(Func<StepDescriptor, bool> predicate);

    /// <summary>
    /// Registers a step descriptor.
    /// </summary>
    /// <param name="descriptor">The step descriptor to register.</param>
    void Register(StepDescriptor descriptor);

    /// <summary>
    /// Registers a step type.
    /// </summary>
    /// <param name="stepType">The step type to register.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task RegisterAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers multiple step types.
    /// </summary>
    /// <param name="stepTypes">The step types to register.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task RegisterAsync(IEnumerable<Type> stepTypes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the step descriptors in the registry by querying the specified step providers.
    /// </summary>
    /// <param name="stepProviders">The step providers used to retrieve the descriptors.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task RefreshDescriptorsAsync(IEnumerable<IStepProvider> stepProviders, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the step descriptors in the registry by querying the specified step provider.
    /// </summary>
    Task RefreshDescriptorsAsync(IStepProvider stepProvider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all step descriptors from the registry.
    /// </summary>
    void Clear();

    /// <summary>
    /// Clears all step descriptors provided by the specified provider.
    /// </summary>
    /// <param name="providerType">The type of the step provider.</param>
    void ClearProvider(Type providerType);
}