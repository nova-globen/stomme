namespace Stomme.Engine.Pipelines.Management.Services;

/// <summary>
/// Populates the <see cref="IStepRegistry"/> with descriptors provided by <see cref="IStepProvider"/>s. 
/// </summary>
public class StepRegistryPopulator(IEnumerable<IStepProvider> providers, IStepRegistry registry) : IStepRegistryPopulator
{
    /// <inheritdoc />
    public async Task PopulateRegistryAsync(CancellationToken cancellationToken)
    {
        await registry.RefreshDescriptorsAsync(providers, cancellationToken).ConfigureAwait(false);
    }
}