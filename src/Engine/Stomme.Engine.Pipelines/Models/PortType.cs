namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// The type of a port.
/// </summary>
public enum PortType
{
    /// <summary>
    /// A port that is embedded in the step.
    /// </summary>
    Embedded,
    
    /// <summary>
    /// A flow port.
    /// </summary>
    Flow
}