using System.Text.Json.Serialization;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Holds information about a step incident.
/// </summary>
public class StepIncident
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StepIncident"/> class.
    /// </summary>
    [JsonConstructor]
    public StepIncident()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StepIncident"/> class.
    /// </summary>
    /// <param name="stepId">The ID of the step that caused the incident.</param>
    /// <param name="stepNodeId">The Node ID of the step that caused the incident.</param>
    /// <param name="stepType">The type of the step that caused the incident.</param>
    /// <param name="message">The message of the incident.</param>
    /// <param name="exception">The exception that caused the incident.</param>
    /// <param name="timestamp">The timestamp of the incident.</param>
    public StepIncident(string stepId, string stepNodeId, string stepType, string message, ExceptionState? exception, DateTimeOffset timestamp)
    {
        StepId = stepId;
        StepNodeId = stepNodeId;
        StepType = stepType;
        Message = message;
        Exception = exception;
        Timestamp = timestamp;
    }

    /// <summary>The ID of the step that caused the incident.</summary>
    public string StepId { get; init; } = default!;
    /// <summary>The Node ID of the step that caused the incident.</summary>
    public string StepNodeId { get; init; } = default!;

    /// <summary>The type of the step that caused the incident.</summary>
    public string StepType { get; init; } = default!;

    /// <summary>The message of the incident.</summary>
    public string Message { get; init; } = default!;

    /// <summary>The exception that caused the incident.</summary>
    public ExceptionState? Exception { get; init; }

    /// <summary>
    /// The timestamp of the incident.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }
}