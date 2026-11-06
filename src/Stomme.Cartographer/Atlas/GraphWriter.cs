using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Runner;

namespace Stomme.Cartographer.Atlas;

/// <summary>Writes the graph (ADR 0012): graph.json, an index.sqlite for queries, and the GraphML and Mermaid exports.</summary>
public static class GraphWriter
{
    public static void WriteAll(SurveyContext context, Graph graph)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);
        var root = context.Paths.Root;
        File.WriteAllText(Path.Combine(root, "graph.json"), JsonSerializer.Serialize(graph, new JsonSerializerOptions(RunContext.JsonOptions) { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "graph.graphml"), GraphMl(graph), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "graph.mmd"), Mermaid(graph), new UTF8Encoding(false));
        WriteIndex(Path.Combine(root, "index.sqlite"), graph);
    }

    private static void WriteIndex(string path, Graph graph)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE nodes (id TEXT PRIMARY KEY, kind TEXT NOT NULL, key TEXT NOT NULL, title TEXT NOT NULL);
                CREATE TABLE edges (from_id TEXT NOT NULL, to_id TEXT NOT NULL, kind TEXT NOT NULL, by TEXT NOT NULL, confidence REAL NOT NULL, evidence TEXT NOT NULL);
                CREATE INDEX edges_from ON edges (from_id); CREATE INDEX edges_to ON edges (to_id); CREATE INDEX edges_kind ON edges (kind);
                """;
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO nodes (id, kind, key, title) VALUES ($id, $kind, $key, $title)";
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var kind = insert.Parameters.Add("$kind", SqliteType.Text);
            var key = insert.Parameters.Add("$key", SqliteType.Text);
            var title = insert.Parameters.Add("$title", SqliteType.Text);
            foreach (var node in graph.Nodes)
            {
                id.Value = node.Id;
                kind.Value = node.Kind;
                key.Value = node.Key;
                title.Value = node.Title;
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO edges (from_id, to_id, kind, by, confidence, evidence) VALUES ($from, $to, $kind, $by, $confidence, $evidence)";
            var from = insert.Parameters.Add("$from", SqliteType.Text);
            var to = insert.Parameters.Add("$to", SqliteType.Text);
            var kind = insert.Parameters.Add("$kind", SqliteType.Text);
            var by = insert.Parameters.Add("$by", SqliteType.Text);
            var confidence = insert.Parameters.Add("$confidence", SqliteType.Real);
            var evidence = insert.Parameters.Add("$evidence", SqliteType.Text);
            foreach (var edge in graph.Edges)
            {
                from.Value = edge.From;
                to.Value = edge.To;
                kind.Value = edge.Kind;
                by.Value = edge.By;
                confidence.Value = edge.Confidence;
                evidence.Value = edge.Evidence;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static string GraphMl(Graph graph)
    {
        var text = new StringBuilder();
        text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        text.Append("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">\n");
        text.Append("  <key id=\"kind\" for=\"all\" attr.name=\"kind\" attr.type=\"string\"/>\n");
        text.Append("  <key id=\"title\" for=\"node\" attr.name=\"title\" attr.type=\"string\"/>\n");
        text.Append("  <key id=\"by\" for=\"edge\" attr.name=\"by\" attr.type=\"string\"/>\n");
        text.Append("  <key id=\"confidence\" for=\"edge\" attr.name=\"confidence\" attr.type=\"double\"/>\n");
        text.Append("  <key id=\"evidence\" for=\"edge\" attr.name=\"evidence\" attr.type=\"string\"/>\n");
        text.Append("  <graph id=\"atlas\" edgedefault=\"directed\">\n");
        foreach (var node in graph.Nodes)
        {
            text.Append("    <node id=\"").Append(Xml(node.Id)).Append("\"><data key=\"kind\">").Append(Xml(node.Kind)).Append("</data><data key=\"title\">").Append(Xml(node.Title)).Append("</data></node>\n");
        }

        var n = 0;
        foreach (var edge in graph.Edges)
        {
            text.Append("    <edge id=\"e").Append((++n).ToString(CultureInfo.InvariantCulture)).Append("\" source=\"").Append(Xml(edge.From)).Append("\" target=\"").Append(Xml(edge.To)).Append("\">")
                .Append("<data key=\"kind\">").Append(Xml(edge.Kind)).Append("</data><data key=\"by\">").Append(Xml(edge.By)).Append("</data><data key=\"confidence\">")
                .Append(edge.Confidence.ToString("0.###", CultureInfo.InvariantCulture)).Append("</data><data key=\"evidence\">").Append(Xml(edge.Evidence)).Append("</data></edge>\n");
        }

        text.Append("  </graph>\n</graphml>\n");
        return text.ToString();
    }

    /// <summary>A Mermaid flowchart of the items, the documents and the drafted decisions with their edges; the tags, the code files and the related_to edges stay in graph.json.</summary>
    private static string Mermaid(Graph graph)
    {
        var text = new StringBuilder();
        text.Append("%% The atlas graph (ADR 0012): items, documents and drafted decisions. Tags, code files and related_to edges are in graph.json.\n");
        text.Append("flowchart LR\n");
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes.Where(n => n.Kind is "item" or "document" or "decision" or "design"))
        {
            var id = "n" + (ids.Count + 1).ToString(CultureInfo.InvariantCulture);
            ids[node.Id] = id;
            shown.Add(node.Id);
            var shape = node.Kind switch { "item" => ("[", "]"), "decision" => ("{{", "}}"), "design" => ("((", "))"), _ => ("(", ")") };
            text.Append("  ").Append(id).Append(shape.Item1).Append('"').Append(Label(node.Title)).Append('"').Append(shape.Item2).Append('\n');
        }

        var count = 0;
        foreach (var edge in graph.Edges.Where(e => e.Kind != "related_to" && shown.Contains(e.From) && shown.Contains(e.To)))
        {
            text.Append("  ").Append(ids[edge.From]).Append(" -->|").Append(edge.Kind).Append("| ").Append(ids[edge.To]).Append('\n');
            if (++count >= 5000)
            {
                text.Append("  %% cut at 5000 edges; the whole graph is in graph.json\n");
                break;
            }
        }

        return text.ToString();
    }

    private static string Label(string title)
    {
        var cut = title.Length <= 60 ? title : title[..60] + "...";
        return cut.Replace("\"", "'", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string Xml(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}
