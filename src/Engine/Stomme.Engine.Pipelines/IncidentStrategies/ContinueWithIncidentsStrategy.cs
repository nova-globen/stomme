using System.ComponentModel;
using JetBrains.Annotations;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Pipelines.IncidentStrategies;

/// <summary>
/// An incident strategy that allows the pipeline to continue running, but with incidents.
/// </summary>
[PublicAPI]
[Description("An incident strategy that allows the pipeline to continue running, but with incidents.")]
public class ContinueWithIncidentsStrategy : IIncidentStrategy
{
    /// <inheritdoc />
    public void HandleIncident(StepExecutionContext context)
    {
        // Do nothing.
    }
}