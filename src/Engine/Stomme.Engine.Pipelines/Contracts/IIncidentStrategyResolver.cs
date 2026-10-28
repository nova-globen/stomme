namespace Stomme.Engine.Pipelines;

/// <summary>
/// Resolves an incident strategy for a given step execution context.
/// </summary>
public interface IIncidentStrategyResolver
{
    /// <summary>
    /// Resolves an incident strategy for a given step execution context.
    /// </summary>
    ValueTask<IIncidentStrategy> ResolveStrategyAsync(StepExecutionContext context, CancellationToken cancellationToken = default);
}