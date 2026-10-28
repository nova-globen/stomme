using System.Text.Json.Serialization;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Models;

/// <summary>
/// Represents an endpoint of a connection.
/// </summary>
public class Endpoint
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Endpoint"/> class.
    /// </summary>
    [JsonConstructor]
    public Endpoint()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Endpoint"/> class.
    /// </summary>
    /// <param name="step">The step that the endpoint is connected to.</param>
    /// <param name="port">The port that the endpoint is connected to.</param>
    public Endpoint(IStep step, string? port = null)
    {
        Step = step;
        Port = port;
    }
    
    /// <summary>
    /// The step that the endpoint is connected to.
    /// </summary>
    public IStep Step { get; set; } = null!;
    
    /// <summary>
    /// The port that the endpoint is connected to.
    /// </summary>
    public string? Port { get; set; }
}