using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a provider of step descriptors, which can be used from step pickers.
/// </summary>
public interface IStepProvider
{
    /// <summary>
    /// Returns a list of <see cref="StepDescriptor"/> objects.
    /// </summary>
    ValueTask<IEnumerable<StepDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default);
}