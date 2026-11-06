using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Runner;

namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// The links phase (ADR 0012): the typed graph of documents, items, decisions, designs, code files and tags.
/// Explicit references (ids, paths, anchors, versions) are resolved by code; a spec that carries an item's id
/// specifies it; decisions link to their evidence, their items and the ADR that records them; "superseded by" is
/// resolved through the id schemes; a mention is matched to a title by code, and judged against the nearest
/// titles when code cannot settle it; shared distinctive tags make related_to edges.
/// </summary>
public sealed partial class LinksPhase(SurveyContext survey) : IPhase
{
    public const string PhaseName = "links";

    private const string MentionUnit = "mentions/";

    public string Name => PhaseName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var context = survey;
        var map = context.Map ?? context.LoadMap() ?? throw new InvalidOperationException("The links phase needs the repository map from orientation.");
        var schemes = map.IdSchemes.Where(s => s.Confirmed).ToList();
        var documents = DocumentCatalogue.Load(context.State);
        var items = ItemCatalogue.Load(context.State);
        var decisions = DecisionCatalogue.Load(context.State).Where(d => d.Standing is not ("not-a-decision" or "merged")).ToList();
        var thresholds = context.Run.Options.Thresholds;
        var salt = context.Playbook.Salt([], [], [], ["links"]);
        context.Touch("links: building the graph", force: true);

        var graph = new Graph { RunId = context.RunId };
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new HashSet<string>(StringComparer.Ordinal);
        void Node(string id, string kind, string key, string title)
        {
            if (!nodes.ContainsKey(id))
            {
                var node = new GraphNode { Id = id, Kind = kind, Key = key, Title = title };
                nodes[id] = node;
                graph.Nodes.Add(node);
            }
        }

        void Edge(string from, string to, string kind, string by, double confidence, string evidence)
        {
            if (from == to || !edges.Add(from + "\u001f" + to + "\u001f" + kind))
            {
                return;
            }

            graph.Edges.Add(new GraphEdge { From = from, To = to, Kind = kind, By = by, Confidence = Math.Round(confidence, 3), Evidence = evidence });
        }

        void Unresolved(string kind) => graph.Unresolved[kind] = graph.Unresolved.GetValueOrDefault(kind) + 1;

        // The nodes everything else hangs on.
        var documentsByPath = new Dictionary<string, DocumentCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            documentsByPath[document.Path] = document;
            Node(GraphCatalogue.DocumentId(document.Path), "document", document.Path, document.Title ?? document.Path);
        }

        var itemsByKey = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            itemsByKey[item.Key] = item;
            foreach (var alias in item.Aliases)
            {
                itemsByKey.TryAdd(alias, item);
            }

            Node(GraphCatalogue.ItemId(item.Key), "item", item.Key, item.Title);
        }

        foreach (var decision in decisions)
        {
            Node(GraphCatalogue.DecisionId(decision.Key), "decision", decision.Key, decision.Title ?? Cut(decision.Text, 80));
        }

        // The id each document carries in its name (an ADR's D061, a spec's BL-12), for ids of other schemes and for "superseded by".
        var byCarriedId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byHead = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var name = document.Path.Contains('/', StringComparison.Ordinal) ? document.Path[(document.Path.LastIndexOf('/') + 1)..] : document.Path;
            var head = name.Split('-', '.', '_', ' ')[0];
            if (head.Length > 0)
            {
                byHead.TryAdd(HeadKey(head), document.Path);
            }

            if (IdNormaliser.Normalise(head, schemes) is { } normalised)
            {
                byCarriedId.TryAdd(normalised.Key, document.Path);
            }
        }

        string? DocumentCarrying(string text)
        {
            var cleaned = Clean(text);
            if (cleaned.Length == 0)
            {
                return null;
            }

            if (IdNormaliser.Normalise(cleaned, schemes) is { } normalised && byCarriedId.TryGetValue(normalised.Key, out var carried))
            {
                return carried;
            }

            return byHead.GetValueOrDefault(HeadKey(cleaned));
        }

        // An id reference: the text as a whole, else each id in it ("D61-D66", "D31/D60"); a list with an id of no scheme of
        // this repository ("C29, D35": C is not a scheme here) is another repository's, and links nothing.
        void LinkIds(string from, string text, string ownPath, string evidence)
        {
            var cited = " \"" + Cut(Clean(text), 60) + "\"";
            if (ResolveItem(text, schemes, itemsByKey) is { } whole)
            {
                Edge(from, GraphCatalogue.ItemId(whole.Key), "references", DecidedBy.Rule, 1, evidence + cited);
                return;
            }

            if (DocumentCarrying(text) is { } carryingWhole && !string.Equals(carryingWhole, ownPath, StringComparison.OrdinalIgnoreCase))
            {
                Edge(from, GraphCatalogue.DocumentId(carryingWhole), "references", DecidedBy.Rule, 0.95, evidence + cited);
                return;
            }

            var tokens = IdToken().Matches(Clean(text)).Select(m => m.Value).Where(v => !v.All(char.IsDigit)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tokens.Count == 0)
            {
                Unresolved("id");
                return;
            }

            if (tokens.Any(t => IdNormaliser.Normalise(t, schemes) is null && DocumentCarrying(t) is null))
            {
                Unresolved("id of another scheme or repository");
                return;
            }

            var linked = false;
            foreach (var token in tokens)
            {
                if (ResolveItem(token, schemes, itemsByKey) is { } target)
                {
                    Edge(from, GraphCatalogue.ItemId(target.Key), "references", DecidedBy.Rule, 1, evidence + cited);
                    linked = true;
                }
                else if (DocumentCarrying(token) is { } carrying && !string.Equals(carrying, ownPath, StringComparison.OrdinalIgnoreCase))
                {
                    Edge(from, GraphCatalogue.DocumentId(carrying), "references", DecidedBy.Rule, 0.95, evidence + cited);
                    linked = true;
                }
            }

            if (!linked)
            {
                Unresolved("id");
            }
        }

        // 1. The documents' explicit references, resolved by code (ADR 0012).
        var mentions = new List<(DocumentCard Document, DocumentReference Reference)>();
        foreach (var document in documents)
        {
            var from = GraphCatalogue.DocumentId(document.Path);
            foreach (var reference in document.References)
            {
                var evidence = document.Path + (reference.Line is { } line ? ":" + line.ToString(CultureInfo.InvariantCulture) : "");
                switch (reference.Kind)
                {
                    case "id" when ItemsPhase.IsQualifiedByAnotherName(reference.Text):
                    case "foreign-id":
                        Unresolved("id of another repository");
                        break;
                    case "id":
                        LinkIds(from, reference.Item ?? reference.Text, document.Path, evidence);
                        break;
                    case "path":
                    case "anchor":
                        LinkPath(context, from, reference.Document ?? reference.Text, document.Path, evidence, documentsByPath, Node, Edge, Unresolved, reference.Kind);
                        break;
                    case "version":
                        var version = Clean(reference.Text);
                        if (version.Length > 0)
                        {
                            Node(GraphCatalogue.TagId("version", version), "tag", "version:" + version.ToLowerInvariant(), version);
                            Edge(from, GraphCatalogue.TagId("version", version), "references", DecidedBy.Rule, 1, evidence);
                        }

                        break;
                    case "mention":
                        mentions.Add((document, reference));
                        break;
                    default:
                        Unresolved(reference.Kind);
                        break;
                }
            }

            // Tags from the reader (ADR 0012): topics, components, people.
            foreach (var (kind, values) in new[] { ("topic", document.Topics), ("component", document.Components), ("person", document.People) })
            {
                foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(40))
                {
                    var id = GraphCatalogue.TagId(kind, value);
                    Node(id, "tag", kind + ":" + value.Trim().ToLowerInvariant(), value.Trim());
                    Edge(from, id, "references", DecidedBy.Reader, document.Confidence ?? 0.7, document.Path);
                }
            }
        }

        // 2. The items' references.
        foreach (var item in items)
        {
            var from = GraphCatalogue.ItemId(item.Key);
            foreach (var reference in item.References)
            {
                switch (reference.Kind)
                {
                    case "id" when ItemsPhase.IsQualifiedByAnotherName(reference.Text):
                    case "foreign-id":
                        Unresolved("id of another repository");
                        break;
                    case "id":
                        LinkIds(from, reference.ResolvedKey ?? reference.Text, "", item.Sources.FirstOrDefault()?.Path ?? item.Key);
                        break;
                    case "path":
                    case "anchor":
                        LinkPath(context, from, reference.Text, "", item.Sources.FirstOrDefault()?.Path ?? item.Key, documentsByPath, Node, Edge, Unresolved, reference.Kind);
                        break;
                    case "version":
                        var version = Clean(reference.Text);
                        if (version.Length > 0)
                        {
                            Node(GraphCatalogue.TagId("version", version), "tag", "version:" + version.ToLowerInvariant(), version);
                            Edge(from, GraphCatalogue.TagId("version", version), "references", DecidedBy.Rule, 1, item.Key);
                        }

                        break;
                    default:
                        Unresolved(reference.Kind);
                        break;
                }
            }
        }

        // 2b. The relations the rows state (the plan's typed links): parent and child as part_of, blocker and blocks as
        // blocks (from the one that blocks to the one that waits), related as related_to, duplicate and supersedes as
        // themselves; by whoever read them, with their figure.
        foreach (var item in items)
        {
            var self = GraphCatalogue.ItemId(item.Key);
            foreach (var relation in item.Relations.Where(r => r.ResolvedKey is not null && itemsByKey.ContainsKey(r.ResolvedKey)))
            {
                var other = GraphCatalogue.ItemId(itemsByKey[relation.ResolvedKey!].Key);
                var evidence = (item.Sources.FirstOrDefault()?.Path ?? item.Key) + (relation.AsWritten is null ? "" : " \"" + Cut(relation.AsWritten, 60) + "\"");
                var confidence = relation.Confidence ?? 0.7;
                switch (relation.Kind)
                {
                    case "parent":
                        Edge(self, other, "part_of", relation.By, confidence, evidence);
                        break;
                    case "child":
                        Edge(other, self, "part_of", relation.By, confidence, evidence);
                        break;
                    case "blocker":
                        Edge(other, self, "blocks", relation.By, confidence, evidence);
                        break;
                    case "blocks":
                        Edge(self, other, "blocks", relation.By, confidence, evidence);
                        break;
                    case "related":
                        Edge(self, other, "related_to", relation.By, confidence, evidence);
                        break;
                    case "derived":
                        Edge(self, other, "derived_from", relation.By, confidence, evidence);
                        break;
                    case "duplicate":
                        Edge(self, other, "duplicates", relation.By, confidence, evidence);
                        break;
                    case "supersedes":
                        Edge(self, other, "supersedes", relation.By, confidence, evidence);
                        break;
                    default:
                        Unresolved("relation of kind " + relation.Kind);
                        break;
                }
            }
        }

        // 3. A spec that carries an item's id in its path or title specifies it; any other document doing so references it.
        foreach (var document in documents)
        {
            var carried = IdsIn(document.Path + " " + (document.Title ?? ""), schemes, itemsByKey);
            foreach (var item in carried)
            {
                var kind = string.Equals(document.Kind, "spec", StringComparison.OrdinalIgnoreCase) ? "specifies" : "references";
                Edge(GraphCatalogue.DocumentId(document.Path), GraphCatalogue.ItemId(item.Key), kind, DecidedBy.Rule, 1, document.Path + " (its name)");
            }
        }

        // 4. Decisions: their evidence, the items they name, and the ADR that records them (ADR 0006).
        foreach (var decision in decisions)
        {
            var id = GraphCatalogue.DecisionId(decision.Key);
            foreach (var source in decision.Sources.Where(s => s.Kind == "document" && documentsByPath.ContainsKey(s.Path)))
            {
                Edge(GraphCatalogue.DocumentId(source.Path), id, "evidences", DecidedBy.Rule, 1, source.Path + (source.Line is { } line ? ":" + line.ToString(CultureInfo.InvariantCulture) : ""));
            }

            foreach (var key in decision.Items)
            {
                if (itemsByKey.TryGetValue(key, out var item))
                {
                    Edge(id, GraphCatalogue.ItemId(item.Key), "decides", DecidedBy.Rule, 1, decision.Key);
                }
            }

            if (decision.RecordedIn is { } adr && documentsByPath.ContainsKey(adr))
            {
                Edge(id, GraphCatalogue.DocumentId(adr), "part_of", decision.By, decision.Recorded ?? 1, adr);
            }
        }

        // 5. "Superseded by" through the id schemes and the documents' names.
        var supersedes = 0;
        foreach (var document in documents.Where(d => !string.IsNullOrWhiteSpace(d.SupersededBy)))
        {
            var resolved = false;
            foreach (Match match in IdToken().Matches(document.SupersededBy!))
            {
                string? newer = null;
                if (IdNormaliser.Normalise(match.Value, schemes) is { } normalised)
                {
                    byCarriedId.TryGetValue(normalised.Key, out newer);
                }

                newer ??= byHead.GetValueOrDefault(HeadKey(match.Value));
                if (newer is not null && !string.Equals(newer, document.Path, StringComparison.OrdinalIgnoreCase))
                {
                    Edge(GraphCatalogue.DocumentId(newer), GraphCatalogue.DocumentId(document.Path), "supersedes", DecidedBy.Rule, 0.9, document.Path + " says \"" + Cut(document.SupersededBy!, 60) + "\"");
                    resolved = true;
                    supersedes++;
                }
            }

            if (!resolved && documents.FirstOrDefault(d => string.Equals(d.Path, Clean(document.SupersededBy!), StringComparison.OrdinalIgnoreCase)) is { } byPath)
            {
                Edge(GraphCatalogue.DocumentId(byPath.Path), GraphCatalogue.DocumentId(document.Path), "supersedes", DecidedBy.Rule, 0.9, document.Path);
                resolved = true;
                supersedes++;
            }

            if (!resolved)
            {
                Unresolved("superseded_by");
            }
        }

        // 6. Mentions: a title match by code; otherwise the nearest titles, judged (ADR 0012).
        var titles = new List<(string Id, string Title, HashSet<string> Words)>();
        foreach (var document in documents.Where(d => !string.IsNullOrWhiteSpace(d.Title)))
        {
            titles.Add((GraphCatalogue.DocumentId(document.Path), document.Title!, DecisionCatalogue.Words(document.Title!)));
        }

        foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.Title)))
        {
            titles.Add((GraphCatalogue.ItemId(item.Key), item.Title, DecisionCatalogue.Words(item.Title)));
        }

        var byTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, title, _) in titles)
        {
            byTitle.TryAdd(DecisionCatalogue.Normalise(title), id);
        }

        var toJudge = new List<(DocumentCard Document, DocumentReference Reference, List<(string Id, string Title)> Candidates)>();
        var mentionsByRule = 0;
        var mentionsWithoutCandidate = 0;
        foreach (var (document, reference) in mentions)
        {
            var text = Clean(reference.Text);
            if (text.Length < 3)
            {
                mentionsWithoutCandidate++;
                continue;
            }

            var evidence = document.Path + (reference.Line is { } line ? ":" + line.ToString(CultureInfo.InvariantCulture) : "");
            if (byTitle.TryGetValue(DecisionCatalogue.Normalise(text), out var exact))
            {
                Edge(GraphCatalogue.DocumentId(document.Path), exact, "references", DecidedBy.Rule, 0.95, evidence + " \"" + Cut(text, 60) + "\"");
                mentionsByRule++;
                continue;
            }

            var words = DecisionCatalogue.Words(text);
            var candidates = words.Count == 0
                ? []
                : titles.Where(t => t.Id != GraphCatalogue.DocumentId(document.Path))
                    .Select(t => (t.Id, t.Title, Likeness: DecisionCatalogue.Similarity(words, t.Words)))
                    .Where(t => t.Likeness >= 0.5)
                    .OrderByDescending(t => t.Likeness)
                    .Take(3)
                    .Select(t => (t.Id, t.Title))
                    .ToList();
            if (candidates.Count == 0)
            {
                mentionsWithoutCandidate++;
                continue;
            }

            toJudge.Add((document, reference, candidates));
        }

        var mentionsByJudge = 0;
        var mentionsJudged = 0;
        var mentionsFailed = 0;
        var template = context.Playbook.JudgeSet("links");
        foreach (var batch in toJudge.GroupBy(m => m.Document.Path).SelectMany(g => g.Chunk(10).Select((chunk, n) => (Path: g.Key, Index: n, Chunk: chunk))))
        {
            var key = MentionUnit + batch.Path + "#" + batch.Index.ToString(CultureInfo.InvariantCulture);
            var hash = RunContext.HashOf("mentions-v1", string.Join("\n", batch.Chunk.Select(m => m.Reference.Text + "|" + string.Join(",", m.Candidates.Select(c => c.Id)))), salt);
            Dictionary<string, double>? figures = null;
            var done = context.State.DoneUnit(PhaseName, key, hash);
            if (done?.Result is { } result)
            {
                figures = ParseFigures(result);
            }

            if (figures is null)
            {
                context.Touch($"links: the judge on {batch.Chunk.Length} mention(s) of {batch.Path}");
                try
                {
                    var (answers, usd) = await JudgeMentionsAsync(context, template, batch.Chunk, cancellationToken).ConfigureAwait(false);
                    figures = answers;
                    context.State.CompleteUnit(PhaseName, context.RunId, key, hash, usd, answers);
                    mentionsJudged += batch.Chunk.Length;
                }
                catch (JudgeUnavailableException ex)
                {
                    mentionsFailed += batch.Chunk.Length;
                    context.Log.Record(new DecisionRecord
                    {
                        Phase = PhaseName,
                        Kind = "mentions",
                        Subject = batch.Path,
                        Decision = $"the judge could not read {batch.Chunk.Length} mention(s): {ex.Reason}; they wait for the next run",
                        By = DecidedBy.Rule,
                        Flags = ["judge-failed"],
                    });
                    continue;
                }
            }

            for (var i = 0; i < batch.Chunk.Length; i++)
            {
                var (document, reference, candidates) = batch.Chunk[i];
                for (var k = 0; k < candidates.Count; k++)
                {
                    var noul = figures.GetValueOrDefault($"m{i + 1}_c{k + 1}");
                    if (noul >= thresholds.Yes)
                    {
                        var evidence = document.Path + (reference.Line is { } line ? ":" + line.ToString(CultureInfo.InvariantCulture) : "") + " \"" + Cut(Clean(reference.Text), 60) + "\"";
                        Edge(GraphCatalogue.DocumentId(document.Path), candidates[k].Id, "references", DecidedBy.Judge, noul, evidence);
                        mentionsByJudge++;
                    }
                }
            }
        }

        // 7. Designs: image files the documents name are design nodes (ADR 0012); the vision pass is not built.
        // (Made inside LinkPath as the paths resolve.)

        // 8. Related documents: two documents sharing two or more distinctive tags (held by at most 20 documents).
        var tagHolders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Where(e => e.Kind == "references" && e.To.StartsWith("tag:", StringComparison.Ordinal) && e.From.StartsWith("document:", StringComparison.Ordinal)))
        {
            if (!tagHolders.TryGetValue(edge.To, out var holders))
            {
                holders = [];
                tagHolders[edge.To] = holders;
            }

            holders.Add(edge.From);
        }

        var shared = new Dictionary<(string, string), int>();
        foreach (var holders in tagHolders.Values.Where(h => h.Count is >= 2 and <= 20))
        {
            for (var a = 0; a < holders.Count; a++)
            {
                for (var b = a + 1; b < holders.Count; b++)
                {
                    var pair = string.CompareOrdinal(holders[a], holders[b]) < 0 ? (holders[a], holders[b]) : (holders[b], holders[a]);
                    shared[pair] = shared.GetValueOrDefault(pair) + 1;
                }
            }
        }

        var related = 0;
        foreach (var ((a, b), count) in shared.Where(s => s.Value >= 2))
        {
            Edge(a, b, "related_to", DecidedBy.Rule, Math.Min(0.9, 0.3 + 0.2 * count), $"{count} distinctive tags shared");
            related++;
        }

        graph.Figures["mentions"] = mentions.Count;
        graph.Figures["mentions by rule"] = mentionsByRule;
        graph.Figures["mentions by judge"] = mentionsByJudge;
        graph.Figures["mentions judged this run"] = mentionsJudged;
        graph.Figures["mentions without candidate"] = mentionsWithoutCandidate;
        graph.Figures["mentions judge failed"] = mentionsFailed;
        graph.Figures["supersedes"] = supersedes;
        graph.Figures["related"] = related;
        GraphCatalogue.Save(context, graph);

        var byKind = graph.Edges.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "graph",
            Subject = "the graph",
            Decision = $"{graph.Nodes.Count} nodes ({string.Join(", ", graph.Nodes.GroupBy(n => n.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}) and {graph.Edges.Count} edges "
                + $"({string.Join(", ", byKind.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}); by the rule {graph.Edges.Count(e => e.By == DecidedBy.Rule)}, by the reader {graph.Edges.Count(e => e.By == DecidedBy.Reader)}, by the judge {graph.Edges.Count(e => e.By == DecidedBy.Judge)}; "
                + $"mentions: {mentionsByRule} matched a title, {mentionsByJudge} settled by the judge, {mentionsWithoutCandidate} with no candidate; unresolved: {string.Join(", ", graph.Unresolved.OrderByDescending(u => u.Value).Select(u => $"{u.Key} {u.Value}"))}",
            By = DecidedBy.Rule,
            Figures = byKind.ToDictionary(k => k.Key, k => (double)k.Value),
        });
        context.Progress($"Links: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
    }

    // ----- resolution -----

    private delegate void NodeAdder(string id, string kind, string key, string title);

    private delegate void EdgeAdder(string from, string to, string kind, string by, double confidence, string evidence);

    private static void LinkPath(SurveyContext context, string from, string text, string fromFolder, string evidence, Dictionary<string, DocumentCard> documents,
        NodeAdder node, EdgeAdder edge, Action<string> unresolved, string referenceKind)
    {
        var cleaned = Clean(text);
        var anchorAt = cleaned.IndexOf('#', StringComparison.Ordinal);
        var anchor = anchorAt >= 0 ? cleaned[(anchorAt + 1)..] : null;
        if (anchorAt >= 0)
        {
            cleaned = cleaned[..anchorAt];
        }

        if (cleaned.Length == 0 || cleaned.Contains("://", StringComparison.Ordinal))
        {
            unresolved(referenceKind);
            return;
        }

        var folder = fromFolder.Contains('/', StringComparison.Ordinal) ? fromFolder[..fromFolder.LastIndexOf('/')] : "";
        foreach (var candidate in Candidates(cleaned, folder))
        {
            if (documents.TryGetValue(candidate, out var document))
            {
                edge(from, GraphCatalogue.DocumentId(document.Path), "references", DecidedBy.Rule, 1, evidence + (anchor is null ? "" : " #" + anchor));
                return;
            }

            if (context.File(candidate) is { } file)
            {
                if (file.Category == FileCategory.Image || file.Category == FileCategory.Design)
                {
                    node(GraphCatalogue.DesignId(file.Path), "design", file.Path, file.Path);
                    edge(GraphCatalogue.DesignId(file.Path), from, "depicts", DecidedBy.Rule, 0.8, evidence);
                }
                else
                {
                    node(GraphCatalogue.CodeId(file.Path), "code", file.Path, file.Path);
                    edge(from, GraphCatalogue.CodeId(file.Path), "references", DecidedBy.Rule, 1, evidence);
                }

                return;
            }
        }

        unresolved(referenceKind);
    }

    /// <summary>The paths a reference may mean: as written from the root, from the document's folder, and with the dot segments folded.</summary>
    /// <summary>The paths a reference may mean, for the verify phase's reading of the same references.</summary>
    internal static IEnumerable<string> PathCandidates(string text, string folder) => Candidates(text, folder);

    private static IEnumerable<string> Candidates(string text, string folder)
    {
        var raw = text.Replace('\\', '/').TrimStart('/');
        if (raw.StartsWith("./", StringComparison.Ordinal))
        {
            raw = raw[2..];
        }

        yield return raw;
        if (folder.Length > 0)
        {
            yield return Fold(folder + "/" + raw);
        }

        yield return Fold(raw);
    }

    internal static string Fold(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        return string.Join("/", parts);
    }

    internal static WorkItem? ResolveItem(string text, List<IdScheme> schemes, Dictionary<string, WorkItem> items)
    {
        var cleaned = Clean(text);
        if (items.TryGetValue(cleaned, out var direct))
        {
            return direct;
        }

        return IdNormaliser.Normalise(cleaned, schemes) is { } normalised && items.TryGetValue(normalised.Key, out var item) ? item : null;
    }

    private static List<WorkItem> IdsIn(string text, List<IdScheme> schemes, Dictionary<string, WorkItem> items)
    {
        var found = new List<WorkItem>();
        foreach (Match match in IdToken().Matches(text))
        {
            if (ResolveItem(match.Value, schemes, items) is { } item && !found.Contains(item))
            {
                found.Add(item);
            }
        }

        return found;
    }

    // ----- the judge on mentions -----

    private static async Task<(Dictionary<string, double> Answers, decimal Usd)> JudgeMentionsAsync(
        SurveyContext context, JsonObject template, (DocumentCard Document, DocumentReference Reference, List<(string Id, string Title)> Candidates)[] batch, CancellationToken cancellationToken)
    {
        var state = new JsonObject();
        var mentionsNode = new JsonObject();
        var candidatesNode = new JsonObject();
        var questions = new JsonObject();
        for (var i = 0; i < batch.Length; i++)
        {
            var m = "m" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var (document, reference, candidates) = batch[i];
            mentionsNode[m] = new JsonObject
            {
                ["text"] = Cut(Clean(reference.Text), 200),
                ["in"] = document.Path + (reference.Line is { } line ? $" line {line}" : ""),
                ["about"] = Cut(document.Summary, 300),
            };
            var list = new JsonObject();
            for (var k = 0; k < candidates.Count; k++)
            {
                var c = "c" + (k + 1).ToString(CultureInfo.InvariantCulture);
                list[c] = new JsonObject { ["title"] = candidates[k].Title, ["kind"] = candidates[k].Id.StartsWith("item:", StringComparison.Ordinal) ? "work item" : "document" };
                var filled = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["mention"] = $"mentions.{m}", ["candidate"] = $"candidates.{m}.{c}" });
                questions[$"{m}_{c}"] = filled["is_about"]?.DeepClone();
            }

            candidatesNode[m] = list;
        }

        state["mentions"] = mentionsNode;
        state["candidates"] = candidatesNode;
        var result = await context.Judge.AskAsync(PhaseName, $"{batch.Length} mention(s) of {batch[0].Document.Path}", state, questions, cancellationToken).ConfigureAwait(false);
        var answers = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, _) in questions)
        {
            answers[id] = result.Reply.Noul(id);
        }

        return (answers, result.Cost.Usd);
    }

    private static Dictionary<string, double>? ParseFigures(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json, RunContext.JsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal static string Clean(string text) => text.Trim().Trim('`', '"', '\'', '(', ')', '<', '>', ',', ';').TrimEnd('.', ':').Trim();

    /// <summary>A file-name head or a token as a key: letters upper-cased, leading zeros dropped, so 0002 and 0002-new.md and D61 and D061 meet.</summary>
    internal static string HeadKey(string token)
    {
        var match = HeadShape().Match(token.Trim());
        return match.Success
            ? match.Groups["letters"].Value.ToUpperInvariant() + "-" + match.Groups["number"].Value.TrimStart('0').PadLeft(1, '0')
            : token.Trim().ToLowerInvariant();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    [GeneratedRegex(@"\b[A-Za-z]{1,8}-\d{1,6}[A-Za-z]?\b|\b[A-Z]{1,3}\d{1,5}\b|\b\d{3,5}\b")]
    private static partial Regex IdToken();

    [GeneratedRegex(@"^(?<letters>[A-Za-z]{0,8})-?0*(?<number>\d{1,6})$")]
    private static partial Regex HeadShape();
}
