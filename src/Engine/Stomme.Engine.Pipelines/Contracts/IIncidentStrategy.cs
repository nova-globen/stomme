namespace Stomme.Engine.Pipelines;

/// <summary>
/// A strategy for handling pipeline incidents
/// </summary>
public interface IIncidentStrategy
{
    /// <summary>
    /// Handles an incident.
    /// </summary>
    /// <param name="context">The step execution context where the incident occurred.</param>
    void HandleIncident(StepExecutionContext context);
}