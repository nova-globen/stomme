using System.Text.Json.Serialization;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Models;

/// <summary>
/// Represents a scope for tracking step and connection visits within a flowchart execution.
/// </summary>
public class FlowScope
{
    [JsonConstructor]
    public FlowScope()
    {
    }

    [JsonInclude]
    private Dictionary<string, long> StepsVisitCount { get; init; } = new();

    [JsonInclude]
    private Dictionary<string, long> ConnectionVisitCount { get; init; } = new();

    [JsonInclude]
    private Dictionary<string, bool> ConnectionLastVisitFollowed { get; init; } = new();

    /// <summary>
    /// Registers a visit to the specified step, incrementing its visit count.
    /// </summary>
    /// <param name="step">The step being visited.</param>
    public void RegisterStepVisit(IStep step)
    {
        string stepId = step.Id;
        StepsVisitCount.TryAdd(stepId, 0);
        StepsVisitCount[stepId]++;
    }

    /// <summary>
    /// Gets the number of times the specified step has been visited.
    /// </summary>
    /// <param name="step">The step to check.</param>
    /// <returns>The visit count of the step.</returns>
    private long GetStepVisitCount(IStep step) => StepsVisitCount.GetValueOrDefault(step.Id, 0);

    /// <summary>
    /// Registers a visit to the specified connection and records whether it was followed.
    /// </summary>
    /// <param name="connection">The connection being visited.</param>
    /// <param name="followed">Indicates whether the connection was followed.</param>
    public void RegisterConnectionVisit(Connection connection, bool followed)
    {
        var connectionId = connection.ToString();
        ConnectionVisitCount.TryAdd(connectionId, 0);
        ConnectionVisitCount[connectionId]++;
        ConnectionLastVisitFollowed[connectionId] = followed;
    }

    /// <summary>
    /// Gets the number of times the specified connection has been visited.
    /// </summary>
    /// <param name="connection">The connection to check.</param>
    /// <returns>The visit count of the connection.</returns>
    private long GetConnectionVisitCount(Connection connection) => ConnectionVisitCount.GetValueOrDefault(connection.ToString(), 0);

    /// <summary>
    /// Determines whether the last visit to the specified connection was followed.
    /// </summary>
    /// <param name="connection">The connection to check.</param>
    /// <returns>True if the connection was followed on the last visit, otherwise false.</returns>
    public bool GetConnectionLastVisitFollowed(Connection connection) => ConnectionLastVisitFollowed.GetValueOrDefault(connection.ToString(), false);

    /// <summary>
    /// Determines whether all inbound connections to the specified step have been visited.
    /// </summary>
    /// <param name="flowGraph">The flow graph containing connections.</param>
    /// <param name="step">The step to check.</param>
    /// <returns>True if all inbound connections have been visited, otherwise false.</returns>
    public bool AllInboundConnectionsVisited(FlowGraph flowGraph, IStep step)
    {
        var forwardInboundConnections = flowGraph.GetForwardInboundConnections(step);
        var outboundStepVisitCount = GetStepVisitCount(step);
        var minConnectionVisitCount = forwardInboundConnections.Min(GetConnectionVisitCount);
        return minConnectionVisitCount > outboundStepVisitCount;
    }

    /// <summary>
    /// Determines whether any inbound connection to the specified step has been followed.
    /// </summary>
    /// <param name="flowGraph">The flow graph containing connections.</param>
    /// <param name="step">The step to check.</param>
    /// <returns>True if any inbound connection has been followed, otherwise false.</returns>
    public bool AnyInboundConnectionsFollowed(FlowGraph flowGraph, IStep step)
    {
        var forwardInboundConnections = flowGraph.GetForwardInboundConnections(step);
        var outboundStepVisitCount = GetStepVisitCount(step);
        var maxConnectionVisitCount = forwardInboundConnections.Max(GetConnectionVisitCount);
        return maxConnectionVisitCount > outboundStepVisitCount 
            && forwardInboundConnections.Any(c => GetConnectionVisitCount(c) == maxConnectionVisitCount && GetConnectionLastVisitFollowed(c));
    }

    /// <summary>
    /// Determines whether all inbound connection to the specified step has been followed.
    /// </summary>
    /// <param name="flowGraph">The flow graph containing connections.</param>
    /// <param name="step">The step to check.</param>
    /// <returns>True if all inbound connection has been followed, otherwise false.</returns>
    public bool AllInboundConnectionsFollowed(FlowGraph flowGraph, IStep step)
    {
        var forwardInboundConnections = flowGraph.GetForwardInboundConnections(step);
        var outboundStepVisitCount = GetStepVisitCount(step);
        var maxConnectionVisitCount = forwardInboundConnections.Max(GetConnectionVisitCount);
        return maxConnectionVisitCount > outboundStepVisitCount
            && forwardInboundConnections.All(c => GetConnectionVisitCount(c) == maxConnectionVisitCount && GetConnectionLastVisitFollowed(c));
    }

    /// <summary>
    /// Determines whether a connection should be ignored based on visit counts.
    /// </summary>
    /// <param name="connection">The connection to check.</param>
    /// <param name="step">The step associated with the connection.</param>
    /// <returns>True if the connection should be ignored, otherwise false.</returns>
    public bool ShouldIgnoreConnection(Connection connection, IStep step)
    {
        var connectionVisitCount = GetConnectionVisitCount(connection);
        var stepVisitCount = GetStepVisitCount(step);
        return connectionVisitCount <= stepVisitCount;
    }
}
