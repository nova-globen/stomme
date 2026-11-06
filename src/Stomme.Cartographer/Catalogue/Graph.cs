using System.Text.Json;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Catalogue;

/// <summary>A node of the typed graph (ADR 0012): a document, an item, a decision, a design, a code file or a tag.</summary>
public sealed class GraphNode
{
    /// <summary>kind:key, unique.</summary>
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Key { get; set; } = "";

    public string Title { get; set; } = "";
}

/// <summary>An edge with its provenance (ADR 0012): the kind, who decided it, the confidence and the evidence.</summary>
public sealed class GraphEdge
{
    public string From { get; set; } = "";

    public string To { get; set; } = "";

    /// <summary>references, specifies, decides, depicts, evidences, supersedes, part_of or related_to.</summary>
    public string Kind { get; set; } = "";

    public string By { get; set; } = "rule";

    public double Confidence { get; set; } = 1;

    /// <summary>Where the edge comes from: a path and line, a commit, or the words that made it.</summary>
    public string Evidence { get; set; } = "";
}

/// <summary>The graph of the bundle, kept whole in the state and written as graph.json with its exports.</summary>
public sealed class Graph
{
    public string RunId { get; set; } = "";

    public List<GraphNode> Nodes { get; set; } = [];

    public List<GraphEdge> Edges { get; set; } = [];

    /// <summary>References that resolved to nothing, by their kind.</summary>
    public Dictionary<string, int> Unresolved { get; set; } = [];

    /// <summary>Figures the phase kept for the summary: mentions judged, candidates and so on.</summary>
    public Dictionary<string, int> Figures { get; set; } = [];
}

public static class GraphCatalogue
{
    public const string GraphKind = "graph";

    public static string DocumentId(string path) => "document:" + path;

    public static string ItemId(string key) => "item:" + key;

    public static string DecisionId(string key) => "decision:" + key;

    public static string DesignId(string path) => "design:" + path;

    public static string CodeId(string path) => "code:" + path;

    public static string TagId(string kind, string value) => "tag:" + kind + ":" + value.Trim().ToLowerInvariant();

    public static Graph? Load(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var json = state.LoadRecord(GraphKind, "graph");
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Graph>(json, RunContext.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(SurveyContext context, Graph graph)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);
        context.State.SaveRecord(GraphKind, "graph", JsonSerializer.Serialize(graph, RunContext.JsonOptions), context.RunId);
    }
}
