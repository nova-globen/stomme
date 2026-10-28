namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents a single directed edge in the pipeline reference graph.
/// The edge points from a consumer pipeline (Source) to the pipeline it references (Target).
/// </summary>
/// <param name="Source">The pipeline definition ID of the consumer (the pipeline that contains the reference).</param>
/// <param name="Target">The pipeline definition ID being referenced (the dependency).</param>
public record PipelineReferenceEdge(string Source, string Target);

/// <summary>
/// Represents a complete graph of pipeline references, built by recursively traversing all consumers
/// of a given pipeline definition.
/// </summary>
public class PipelineReferenceGraph
{
    /// <summary>
    /// Creates a new instance of the <see cref="PipelineReferenceGraph"/> class.
    /// </summary>
    /// <param name="rootDefinitionIds">The IDs of the root pipeline definitions from which the graph was built.</param>
    /// <param name="edges">The collection of edges representing all reference relationships in the graph.</param>
    public PipelineReferenceGraph(IReadOnlyCollection<string> rootDefinitionIds, IReadOnlyCollection<PipelineReferenceEdge> edges)
    {
        RootDefinitionIds = rootDefinitionIds;
        Edges = edges;
        
        // Pre-compute useful lookups
        AllDefinitionIds = ComputeAllDefinitionIds(edges, rootDefinitionIds);
        ConsumerDefinitionIds = AllDefinitionIds.Except(rootDefinitionIds).ToHashSet();
        
        // Outbound: Source → Targets (what does this pipeline depend on?)
        OutboundEdges = edges.ToLookup(e => e.Source, e => e.Target);
        
        // Inbound: Target → Sources (what pipelines consume this one?)
        InboundEdges = edges.ToLookup(e => e.Target, e => e.Source);
    }

    /// <summary>
    /// The IDs of the root pipeline definitions from which the graph was built.
    /// </summary>
    public IReadOnlyCollection<string> RootDefinitionIds { get; }

    /// <summary>
    /// The collection of edges representing all reference relationships in the graph.
    /// Each edge represents a single Source → Target relationship.
    /// </summary>
    public IReadOnlyCollection<PipelineReferenceEdge> Edges { get; }

    /// <summary>
    /// All pipeline definition IDs in the graph, including the roots and all consumers.
    /// </summary>
    public IReadOnlySet<string> AllDefinitionIds { get; }

    /// <summary>
    /// All pipeline definition IDs that consume (directly or indirectly) the root pipeline definitions.
    /// Does not include the roots themselves.
    /// </summary>
    public IReadOnlySet<string> ConsumerDefinitionIds { get; }

    /// <summary>
    /// A lookup that maps each pipeline definition ID to the IDs of pipelines it depends on (references).
    /// Use this to find: "What pipelines does X reference?"
    /// </summary>
    public ILookup<string, string> OutboundEdges { get; }

    /// <summary>
    /// A lookup that maps each pipeline definition ID to the IDs of pipelines that reference it.
    /// Use this to find: "What pipelines consume X?"
    /// </summary>
    public ILookup<string, string> InboundEdges { get; }

    /// <summary>
    /// Gets all pipeline definition IDs that the specified pipeline depends on (references).
    /// </summary>
    public IEnumerable<string> GetDependencies(string definitionId) => OutboundEdges[definitionId];

    /// <summary>
    /// Gets all pipeline definition IDs that reference (consume) the specified pipeline.
    /// </summary>
    public IEnumerable<string> GetConsumers(string definitionId) => InboundEdges[definitionId];

    private static HashSet<string> ComputeAllDefinitionIds(IReadOnlyCollection<PipelineReferenceEdge> edges, IReadOnlyCollection<string> rootDefinitionIds)
    {
        var ids = new HashSet<string>(rootDefinitionIds);
        
        foreach (var edge in edges)
        {
            ids.Add(edge.Source);
            ids.Add(edge.Target);
        }
        
        return ids;
    }
}

