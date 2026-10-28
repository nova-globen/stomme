using Stomme.Engine.Expressions.Contracts;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Updates the pipeline store from <see cref="IPipelinesProvider"/> implementations, creates triggers, updates the <see cref="IStepRegistry"/> and <see cref="IExpressionDescriptorRegistry"/>.
/// </summary>
public interface IRegistriesPopulator
{
    /// <summary>
    /// Updates the pipeline store from <see cref="IPipelinesProvider"/> implementations, creates triggers, updates the <see cref="IStepRegistry"/> and <see cref="IExpressionDescriptorRegistry"/>.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task PopulateAsync(CancellationToken cancellationToken = default);
}