namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Populates the <see cref="IStepRegistry"/> with steps.
/// </summary>
public interface IStepRegistryPopulator
{
    /// <summary>
    /// Populates the <see cref="IStepRegistry"/> with steps.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task PopulateRegistryAsync(CancellationToken cancellationToken = default);
}