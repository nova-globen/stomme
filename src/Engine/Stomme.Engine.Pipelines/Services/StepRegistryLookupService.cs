using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a service used to lookup step descriptors in the step registry.
/// </summary>
public class StepRegistryLookupService(IStepRegistry stepRegistry, IEnumerable<IStepProvider> providers) : IStepRegistryLookupService
{
    /// <inheritdoc />
    public Task<StepDescriptor?> FindAsync(string type)
    {
        return FindAsync(() => stepRegistry.Find(type));
    }

    /// <inheritdoc />
    public Task<StepDescriptor?> FindAsync(string type, int version)
    {
        return FindAsync(() => stepRegistry.Find(type, version));
    }

    /// <inheritdoc />
    public Task<StepDescriptor?> FindAsync(Func<StepDescriptor, bool> predicate)
    {
        return FindAsync(() => stepRegistry.Find(predicate));
    }

    /// <inheritdoc />
    public IEnumerable<StepDescriptor> FindMany(Func<StepDescriptor, bool> predicate)
    {
        return stepRegistry.FindMany(predicate);
    }
    
    private async Task<StepDescriptor?> FindAsync(Func<StepDescriptor?> findPredicate)
    {
        var descriptor = findPredicate.Invoke();
        if (descriptor is not null)
            return descriptor;

        await stepRegistry.RefreshDescriptorsAsync(providers).ConfigureAwait(false);
        return findPredicate.Invoke();
    }
}