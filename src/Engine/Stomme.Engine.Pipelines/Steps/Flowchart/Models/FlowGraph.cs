using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Models;

/// <summary>
/// Represents a directed graph structure for managing pipeline connections.
/// Caches forward and backward connections to optimize graph traversal.
/// </summary>
public class FlowGraph(ICollection<Connection> connections, IStep? rootStep)
{
    private List<Connection>? _cachedForwardConnections;
    private readonly Dictionary<IStep, List<Connection>> _cachedInboundForwardConnections = new();
    private readonly Dictionary<IStep, List<Connection>> _cachedInboundConnections = new();
    private readonly Dictionary<IStep, List<Connection>> _cachedOutboundConnections = new();
    private readonly Dictionary<Connection, (bool IsBackwardConnection, bool IsValid)> _cachedIsBackwardConnection = new();
    private readonly Dictionary<IStep, bool> _cachedIsDanglingStep = new();
    private readonly Dictionary<IStep, List<IStep>> _cachedAncestors = new();

    /// <summary>
    /// Gets the list of forward connections, computing them if not already cached.
    /// </summary>
    private List<Connection> ForwardConnections => _cachedForwardConnections ??= rootStep == null ? new() : GetForwardConnections(connections, rootStep);

    /// <summary>
    /// Retrieves all inbound forward connections for a given step.
    /// </summary>
    public List<Connection> GetForwardInboundConnections(IStep step) => _cachedInboundForwardConnections.GetOrAdd(step, () => ForwardConnections.InboundConnections(step).ToList());

    /// <summary>
    /// Retrieves all outbound connections for a given step.
    /// </summary>
    public List<Connection> GetOutboundConnections(IStep step) => _cachedOutboundConnections.GetOrAdd(step, () => connections.OutboundConnections(step).ToList());
    
    /// <summary>
    /// Retrieves all inbound connections for a given step.
    /// </summary>
    public List<Connection> GetInboundConnections(IStep step) => _cachedInboundConnections.GetOrAdd(step, () => connections.InboundConnections(step).ToList());

    /// <summary>
    /// Determines if a given step is "dangling," meaning it does not exist as a target in any forward connection.
    /// </summary>
    public bool IsDanglingStep(IStep step) => _cachedIsDanglingStep.GetOrAdd(step, () => step != rootStep && ForwardConnections.All(c => c.Target.Step != step));

    /// <summary>
    /// Determines if a given connection is a backward connection (i.e., not part of the forward traversal) and whether it is valid.
    /// </summary>
    public bool IsBackwardConnection(Connection connection, out bool isValid)
    {
        // Check if result is already cached
        if (_cachedIsBackwardConnection.TryGetValue(connection, out var result))
        {
            isValid = result.IsValid;
            return result.IsBackwardConnection;
        }

        // Compute if the connection is backward
        bool isBackwardConnection = !GetForwardInboundConnections(connection.Target.Step).Contains(connection);

        // Compute if the backward connection is valid
        isValid = isBackwardConnection && IsValidBackwardConnection(ForwardConnections, rootStep, connection);

        // Cache the result
        _cachedIsBackwardConnection[connection] = (isBackwardConnection, isValid);

        return isBackwardConnection;
    }

    /// <summary>
    /// Retrieves all ancestor steps for a given step by traversing ForwardConnections in reverse.
    /// </summary>
    public List<IStep> GetAncestorSteps(IStep step)
    {
        return _cachedAncestors.GetOrAdd(step, () => ComputeAncestors(step));
    }

    /// <summary>
    /// Computes the list of ancestors by following Source steps in ForwardConnections.
    /// </summary>
    private List<IStep> ComputeAncestors(IStep step)
    {
        HashSet<IStep> ancestors = new();
        Queue<IStep> queue = new();

        // Find all connections where this step is the target
        foreach (var connection in ForwardConnections.Where(c => c.Target.Step == step))
        {
            if (ancestors.Add(connection.Source.Step))
                queue.Enqueue(connection.Source.Step);
        }

        // Traverse upwards through the graph
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var connection in ForwardConnections.Where(c => c.Target.Step == current))
            {
                if (ancestors.Add(connection.Source.Step))
                    queue.Enqueue(connection.Source.Step);
            }
        }

        return ancestors.ToList();
    }

    /// <summary>
    /// Computes the list of forward connections in the graph, excluding cyclic connections.
    /// </summary>
    private static List<Connection> GetForwardConnections(ICollection<Connection> connections, IStep root)
    {
        Dictionary<IStep, List<IStep>> adjList = new();

        foreach (var conn in connections)
        {
            if (!adjList.ContainsKey(conn.Source.Step))
                adjList[conn.Source.Step] = new();

            adjList[conn.Source.Step].Add(conn.Target.Step);
        }

        HashSet<IStep> visited = new();
        HashSet<(IStep, IStep)> visitedEdges = new();
        List<(IStep Source, IStep Target)> validEdges = new();
        Queue<IStep> queue = new();

        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var source = queue.Dequeue();
            visited.Add(source);

            if (!adjList.ContainsKey(source)) continue;

            foreach (var target in adjList[source])
            {
                var edge = (source, target);
                if (visitedEdges.Contains(edge))
                    continue;

                if (HasPathToStep(validEdges, target, source))
                    continue;

                visitedEdges.Add(edge);
                validEdges.Add((source, target));

                if (!visited.Contains(target))
                    queue.Enqueue(target);
            }
        }

        return validEdges
            .SelectMany(e => connections.Where(c => c.Source.Step == e.Source && c.Target.Step == e.Target))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Determines if there is an existing path from the source step to the target step.
    /// Helps in detecting cyclic connections.
    /// </summary>
    private static bool HasPathToStep(ICollection<(IStep Source, IStep Target)> edges, IStep source, IStep target)
    {
        if (source == target)
            return true;

        HashSet<IStep> visited = new();
        Stack<IStep> stack = new();
        stack.Push(source);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current == target)
                return true;

            if (visited.Contains(current))
                continue;

            visited.Add(current);

            foreach (var next in edges.Where(x => x.Source == current).Select(e => e.Target))
            {
                if (!visited.Contains(next))
                    stack.Push(next);
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a backward connection is valid by ensuring all paths from source to root pass through target.
    /// </summary>
    private static bool IsValidBackwardConnection(List<Connection> forwardConnections, IStep? root, Connection connection)
    {
        if (root == null) return false;

        var pathsToRoot = GetPathsToRoot(forwardConnections, root, connection.Source.Step);

        foreach (var path in pathsToRoot)
        {
            if (!path.Contains(connection.Target.Step))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Finds all paths from a given start step to the root using BFS.
    /// </summary>
    private static List<List<IStep>> GetPathsToRoot(List<Connection> forwardConnections, IStep root, IStep start)
    {
        List<List<IStep>> paths = new();
        Queue<List<IStep>> queue = new();
        queue.Enqueue(new()
            { start });

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var lastNode = path.Last();

            if (lastNode == root)
            {
                paths.Add([..path]);
                continue;
            }

            var previousNodes = forwardConnections
                .Where(c => c.Target.Step == lastNode)
                .Select(c => c.Source.Step);

            foreach (var prev in previousNodes)
            {
                if (!path.Contains(prev))
                {
                    var newPath = new List<IStep>(path) { prev };
                    queue.Enqueue(newPath);
                }
            }
        }

        return paths;
    }
}
