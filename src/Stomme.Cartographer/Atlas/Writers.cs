using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

namespace Stomme.Cartographer.Atlas;

/// <summary>Writes repository-map.json and repository-map.md (the orientation phase's result, shown to the owner).</summary>
public static class RepositoryMapWriter
{
    public static void Write(SurveyContext context, RepositoryMap map)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(map);
        File.WriteAllText(context.Paths.MapJson, JsonSerializer.Serialize(map, RunContext.JsonOptions), new UTF8Encoding(false));

        var text = new StringBuilder();
        text.AppendLine("# Repository map");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Input: `{context.Snapshot.Root}` at {context.Snapshot.Git?.Commit[..Math.Min(12, context.Snapshot.Git.Commit.Length)] ?? "(not git)"}; {map.Files.Count} files. Every decision below is in `log.md` with its evidence and reasoning.");
        text.AppendLine();
        text.AppendLine("## Nature");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"**{map.Nature.Kind}** (confidence {map.Nature.Confidence:0.00}): {map.Nature.Description}");
        text.AppendLine();
        text.AppendLine("Reasoning: " + map.Nature.Reasoning);
        foreach (var evidence in map.Nature.Evidence)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Evidence: {evidence.Path}{(evidence.Line is null ? "" : ":" + evidence.Line)} \"{evidence.Quote}\"");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Natural languages: {Join(map.NaturalLanguages)}. Programming languages: {Join(map.ProgrammingLanguages)}. Guides read: {Join(map.Guides)}. Document roots: {Join(map.DocumentRoots)}.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Documents thin: **{(map.DocumentsThin ? "yes" : "no")}**. {map.ThinReasoning}{(map.ReaderThinView is null ? "" : " The reader's own view: " + map.ReaderThinView)}");
        text.AppendLine();

        text.AppendLine("## Id schemes (ADR 0015)");
        text.AppendLine();
        if (map.IdSchemes.Count == 0)
        {
            text.AppendLine("None found; numbers will be assigned in source order.");
        }
        else
        {
            text.AppendLine("| Scheme | Example | Refers to | Regex | Matches | Distinct ids | In documents | Confirmed | Reasoning |");
            text.AppendLine("|---|---|---|---|---:|---:|---:|---|---|");
            foreach (var s in map.IdSchemes)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(s.Name)} | `{Cell(s.Example)}` | {Cell(s.RefersTo)} | `{Cell(s.Regex)}` | {s.Matches} | {s.DistinctIds} | {s.MatchesInDocuments} | {(s.Confirmed ? "yes" : "no")} | {Cell(s.Reasoning)}{(s.Note is null ? "" : " [" + Cell(s.Note) + "]")} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Status and tier vocabulary");
        text.AppendLine();
        if (map.StatusVocabulary.Count == 0 && map.TierVocabulary.Count == 0)
        {
            text.AppendLine("None found in the guides.");
        }

        foreach (var w in map.StatusVocabulary)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Status `{w.Word}`: {w.Meaning} (mapped to {w.Mapped ?? "nothing"})");
        }

        foreach (var w in map.TierVocabulary)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Tier `{w.Word}`: {w.Meaning}");
        }

        text.AppendLine();
        text.AppendLine("## Ledgers and the spec index");
        text.AppendLine();
        if (map.SpecIndex is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Specs are indexed at `{map.SpecIndex.Path}{(map.SpecIndex.Anchor is null ? "" : "#" + map.SpecIndex.Anchor)}`: {map.SpecIndex.Reasoning}");
            text.AppendLine();
        }

        if (map.Ledgers.Count == 0)
        {
            text.AppendLine("No ledger of work items was found.");
        }
        else
        {
            text.AppendLine("| File | Kind | Named by | Judge: is a ledger | Judge: finished | Reasoning |");
            text.AppendLine("|---|---|---|---:|---:|---|");
            foreach (var l in map.Ledgers)
            {
                var isLedger = l.JudgeFigures is not null && l.JudgeFigures.TryGetValue("is_ledger", out var a) ? a.ToString("0.00", CultureInfo.InvariantCulture) : "";
                var finished = l.JudgeFigures is not null && l.JudgeFigures.TryGetValue("is_finished", out var b) ? b.ToString("0.00", CultureInfo.InvariantCulture) : "";
                text.AppendLine(CultureInfo.InvariantCulture, $"| `{l.Path}` | {l.Kind} | {Cell(l.NamedBy ?? "")} | {isLedger} | {finished} | {Cell(l.Reasoning)} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Counts");
        text.AppendLine();
        text.AppendLine("By kind: " + string.Join(", ", map.CountsByKind.OrderByDescending(c => c.Value).Select(c => $"{c.Key} {c.Value}")));
        text.AppendLine();
        text.AppendLine("By category: " + string.Join(", ", map.CountsByCategory.OrderByDescending(c => c.Value).Select(c => $"{c.Key} {c.Value}")));
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Pieces of text flagged by the injection gate: {map.FlaggedChunks} (each is in the log).");
        text.AppendLine();

        text.AppendLine("## Documents, by kind");
        text.AppendLine();
        var documents = map.Files.Where(f => f.By != DecidedBy.Rule || f.Category is "Document" or "TextUnknown").ToList();
        foreach (var group in documents.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"### {group.Key} ({group.Count()})");
            text.AppendLine();
            text.AppendLine("| File | Title | Lang | Conf. | Evidence | Reasoning | Flags |");
            text.AppendLine("|---|---|---|---:|---|---|---|");
            foreach (var f in group.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                var evidence = f.EvidenceLine is null && f.EvidenceQuote is null ? "" : $"{(f.EvidenceLine is null ? "" : "line " + f.EvidenceLine + ": ")}{Cell(f.EvidenceQuote ?? "")}";
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{f.Path}` | {Cell(f.Title ?? "")} | {f.Language} | {(f.Confidence is null ? "" : f.Confidence.Value.ToString("0.00", CultureInfo.InvariantCulture))} | {evidence} | {Cell(f.Reasoning ?? "")} | {string.Join(", ", f.Flags)} |");
            }

            text.AppendLine();
        }

        text.AppendLine("## Everything else, by rule");
        text.AppendLine();
        foreach (var group in map.Files.Where(f => f.By == DecidedBy.Rule && f.Category is not ("Document" or "TextUnknown")).GroupBy(f => f.Kind).OrderByDescending(g => g.Count()))
        {
            var byLanguage = group.GroupBy(f => f.Language ?? "").OrderByDescending(g => g.Count()).Select(g => g.Key.Length == 0 ? $"{g.Count()}" : $"{g.Key} {g.Count()}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key}: {group.Count()} files ({string.Join(", ", byLanguage)})");
        }

        if (map.Notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## The reader's notes");
            text.AppendLine();
            foreach (var note in map.Notes)
            {
                text.AppendLine("- " + note);
            }
        }

        File.WriteAllText(context.Paths.MapMarkdown, text.ToString(), new UTF8Encoding(false));
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "(none)" : string.Join(", ", values);

    internal static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ');
}

/// <summary>Writes summary.md: the totals, what was left out and why, the open points, the spend (ADR 0002, 0008).</summary>
public static class SummaryWriter
{
    public static void Write(SurveyContext context, BackupResult? backup, RunOutcome outcome, string message)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spending = context.Run.Options.Spending;
        var text = new StringBuilder();
        var cost = context.Log.CostOfRun();
        text.AppendLine("# Summary");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Run {context.RunId} on `{context.Snapshot.Root}` (commit {context.Snapshot.Git?.Commit ?? "none"}, branch {context.Snapshot.Git?.Branch ?? "none"}, tree {(context.Snapshot.Git is null ? "not git" : context.Snapshot.Git.Clean ? "clean" : "with uncommitted changes")}), playbook {context.Playbook.Name} {context.Playbook.Version}.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Outcome: **{outcome}**: {message}.");
        text.AppendLine();
        text.AppendLine(ManifestWriter.IsComplete(context, outcome)
            ? $"Bundle: **complete** (format {ManifestWriter.BundleFormatVersion}, `manifest.json` says `complete: true`): every phase with code ran to its end and every file of the bundle is written."
            : $"Bundle: **incomplete** (format {ManifestWriter.BundleFormatVersion}, `manifest.json` says `complete: false`): a consumer should not read it as the repository's whole. Not run: {string.Join(", ", context.Playbook.Phases.Where(p => ManifestWriter.BuiltPhases.Contains(p) && !context.PhasesDone.Contains(p))) switch { "" => "none", var s => s }}.");
        text.AppendLine();
        text.AppendLine("## Spend");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- This run: {PriceTable.Dollars(cost.Usd)} over {context.Log.CountOfRun("call")} calls ({context.Log.Records.Count(r => r.Run == context.RunId && r.Kind == "call" && r.Cached)} answered from the cache): {cost.InputTokens:N0} input tokens ({cost.CachedInputTokens:N0} served from the provider's cache), {cost.OutputTokens:N0} output tokens ({(cost.ReasoningTokens > 0 ? cost.ReasoningTokens.ToString("N0", CultureInfo.InvariantCulture) + " reasoning" : "the provider reports no separate reasoning count; thinking is inside the output count")}).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- This run by provider: reader {PriceTable.Dollars(context.Spend.RunUsdOf(SpendKind.Reader))}, judge {PriceTable.Dollars(context.Spend.RunUsdOf(SpendKind.Judge))}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- All runs in the spend file: {PriceTable.Dollars(context.Spend.AllRunsUsd)}; the reader {PriceTable.Dollars(context.Spend.AllRunsUsdOf(SpendKind.Reader))} of its {PriceTable.Dollars(spending.CeilingUsd)} ceiling ({spending.Scope}), the judge {PriceTable.Dollars(context.Spend.AllRunsUsdOf(SpendKind.Judge))} of its {PriceTable.Dollars(spending.JudgeCeilingUsd)} limit (ADR 0024). Prices read on {spending.Prices.ReadOn:yyyy-MM-dd}.");
        if (cost.Estimated)
        {
            text.AppendLine("- Some calls answered without usage figures and were counted at the input's estimated size.");
        }

        text.AppendLine();
        text.AppendLine("## Phases");
        text.AppendLine();
        text.AppendLine("A phase resumed from the state cost this run nothing; the units behind it, and what they cost across runs, are in the state's column.");
        text.AppendLine();
        var units = context.State.UnitCounts();
        foreach (var phase in context.Playbook.Phases)
        {
            var state = context.PhasesDone.Contains(phase) ? "done" : ManifestWriter.BuiltPhases.Contains(phase) ? "not run" : "not built in this version";
            var phaseCost = context.Log.CostOfRun(phase);
            var (unitsDone, unitsUsd) = units.TryGetValue(phase, out var u) ? u : (0, 0m);
            text.AppendLine(CultureInfo.InvariantCulture, $"- {phase}: {state}; this run {PriceTable.Dollars(phaseCost.Usd)} over {context.Log.CountOfRun("call", phase)} calls; in the state {unitsDone} units of work done ({PriceTable.Dollars(unitsUsd)} across runs)");
        }

        text.AppendLine();
        text.AppendLine("## The input");
        text.AppendLine();
        var files = context.Snapshot.Files;
        text.AppendLine(CultureInfo.InvariantCulture, $"- {files.Count} files listed by {context.Snapshot.ListedBy}: {string.Join(", ", files.GroupBy(f => f.Category).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}.");
        foreach (var note in context.Snapshot.Notes)
        {
            text.AppendLine("- " + note);
        }

        if (backup is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Backup: `{backup.ArchivePath}`: {backup.Note}");
        }
        else
        {
            var earlier = Directory.Exists(context.Paths.Backup)
                ? Directory.GetFiles(context.Paths.Backup, "*.zip").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList()
                : [];
            text.AppendLine(earlier.Count == 0
                ? "- Backup: none taken this run, and none in `backup/`."
                : $"- Backup: none taken this run (ADR 0009's zip was switched off); earlier zips in `backup/`: {string.Join(", ", earlier)}.");
        }

        text.AppendLine();

        if (context.Map is { } map)
        {
            text.AppendLine("## What orientation found");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"- Nature: **{map.Nature.Kind}** ({map.Nature.Confidence:0.00}): {map.Nature.Description}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Id schemes: {(map.IdSchemes.Count == 0 ? "none" : string.Join("; ", map.IdSchemes.Select(s => $"{s.Name} like `{s.Example}` ({(s.Confirmed ? $"confirmed, {s.DistinctIds} ids" : "dropped")})")))}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Ledgers: {(map.Ledgers.Count == 0 ? "none" : string.Join("; ", map.Ledgers.Select(l => $"`{l.Path}` ({l.Kind}{(l.Confirmed is true ? ", confirmed by the judge" : l.Confirmed is false ? ", not confirmed" : "")})")))}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Documents by kind: {string.Join(", ", map.CountsByKind.Where(c => c.Key is not ("code" or "data" or "config" or "image" or "binary" or "design-file" or "archive")).OrderByDescending(c => c.Value).Select(c => $"{c.Key} {c.Value}"))}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Documents thin: {(map.DocumentsThin ? "yes" : "no")} ({map.ThinReasoning})");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Pieces of text flagged by the injection gate during orientation: {map.FlaggedChunks} (this run's flags are under Flags and questions)");
            text.AppendLine("- The whole map: `repository-map.md`.");
            text.AppendLine();
        }

        var items = ItemCatalogue.Load(context.State);
        if (items.Count > 0)
        {
            text.AppendLine("## Items");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"- {items.Count} items in `items/`, one file each: {string.Join(", ", items.GroupBy(i => i.Status).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- {items.Count(i => i.Placeholder)} placeholder(s) for ids the rows refer to that nothing defines (ADR 0015); {items.Count(i => i.Flags.Contains("in-two-ledgers"))} in two ledgers; {items.Count(i => i.Flags.Contains("open-in-finished-ledger") || i.Flags.Contains("finished-in-open-ledger"))} whose words said open in the finished ledger, closed by the ledger and flagged, or finished in the open one, flagged; {items.Count(i => i.Flags.Contains("from-prose"))} made from prose lists by a person's answer; {items.Count(i => i.SourceGone)} from sources now gone (kept, marked).");
            var typed = items.Where(i => i.Type is not null).ToList();
            var relations = items.SelectMany(i => i.Relations).ToList();
            text.AppendLine(CultureInfo.InvariantCulture, $"- Kinds, from the rows' own words: {(typed.Count == 0 ? "none stated" : string.Join(", ", typed.GroupBy(i => i.Type!).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")))}; {items.Count - typed.Count} row(s) say nothing about their kind. {typed.Count(i => i.TypeBy == DecidedBy.Judge)} read by the judge, {typed.Count(i => i.TypeBy == DecidedBy.Reader)} by the reader only. Relations stated by the rows: {(relations.Count == 0 ? "none" : string.Join(", ", relations.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")))}; {relations.Count(r => r.ResolvedKey is not null)} resolved to items, {relations.Count(r => r.By == DecidedBy.Judge)} read by the judge. Each item's file says who decided its kind, parent and blockers, and how surely.");
            var words = ItemCatalogue.LoadWords(context.State);
            if (words.Count > 0)
            {
                // One entry per word, not per phrase (ADR 0020 rule 12): "DONE 2026-10-21" is a use of DONE. The
                // phrase-by-phrase mappings stay in the log; the summary names what a person must confirm.
                var rows = items.Where(i => !string.IsNullOrWhiteSpace(i.StatusAsWritten))
                    .GroupBy(i => StatusVocabulary.WordOf(i.StatusAsWritten!.Trim()), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var groups = words.GroupBy(w => StatusVocabulary.WordOf(w.Key), StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => rows.GetValueOrDefault(g.Key)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
                var parts = new List<string>();
                var stale = groups.Where(g => rows.GetValueOrDefault(g.Key) == 0).Select(g => g.Key).ToList();
                foreach (var group in groups.Where(g => rows.GetValueOrDefault(g.Key) > 0))
                {
                    var plain = group.FirstOrDefault(w => string.Equals(w.Key, group.Key, StringComparison.OrdinalIgnoreCase)).Value
                        ?? group.Select(w => w.Value).GroupBy(v => v.Mapped, StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v.Count()).First().First();
                    var who = plain.By == DecidedBy.Person ? "confirmed by a person" : "by the " + plain.By + ", to confirm";
                    var differing = group.Where(w => !string.Equals(w.Value.Mapped, plain.Mapped, StringComparison.OrdinalIgnoreCase))
                        .Take(3).Select(w => $"\"{Shorten(w.Key, 60)}\" to {w.Value.Mapped}").ToList();
                    parts.Add($"{group.Key} to {plain.Mapped}{(plain.Condition is null ? "" : " (" + Shorten(plain.Condition, 80) + ")")}, {rows.GetValueOrDefault(group.Key)} row(s), {who}{(differing.Count == 0 ? "" : ", except " + string.Join(", ", differing))}");
                }

                text.AppendLine(CultureInfo.InvariantCulture, $"- Status words ({parts.Count}): {string.Join("; ", parts)}. The phrase-by-phrase mappings are in `log.md`.{(stale.Count == 0 ? "" : $" {stale.Count} word(s) recorded by an earlier run that no row uses now are left out: {string.Join(", ", stale.Take(12))}{(stale.Count > 12 ? ", ..." : "")}.")}");

                static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "...";
            }

            var prose = ItemCatalogue.LoadProse(context.State);
            if (prose.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- Work in prose, not rows (ADR 0020 rule 11): {string.Join("; ", prose.Select(p => $"\"{p.Heading}\" in {p.Path} ({p.Bullets.Count} bullet(s); {(p.Answer is null ? "asked in questions.md" : "answered: " + p.Answer)})"))}.");
            }

            text.AppendLine();
        }

        var documents = DocumentCatalogue.Load(context.State);
        if (documents.Count > 0)
        {
            text.AppendLine("## Documents");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"- {documents.Count} documents in `documents/`, one file each at its source path, by kind: {string.Join(", ", documents.GroupBy(d => d.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- What they say about their own standing (ADR 0020 rule 9): {string.Join(", ", documents.GroupBy(d => d.Status).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}.");
            var agents = documents.Where(d => d.ForAgents).ToList();
            text.AppendLine(CultureInfo.InvariantCulture, $"- Written for agents (ADR 0020 rule 8): {agents.Count} ({agents.Count(d => d.Shelf == "process")} on the process shelf, {agents.Count(d => d.Shelf == "product")} on the product shelf), reference-only; git stays their owner.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- {documents.Count(d => d.Flags.Contains("chunked"))} read in chunks (ADR 0014); {documents.Count(d => d.Flags.Contains("kind-revised"))} whose whole text revised orientation's kind; {documents.Count(d => d.Flags.Contains("not-a-document"))} found not to be documents by the whole text (kept, flagged); {documents.Count(d => d.Flags.Contains("discarded-by-gate"))} whose description the injection gate discarded; {documents.Count(d => d.Flags.Contains("reader-failed") || d.Flags.Contains("judge-failed"))} whose reader or judge call failed (orientation's reading stands; the next run retries them); {documents.Count(d => d.SourceGone)} from sources now gone (kept, marked).");
            var references = documents.SelectMany(d => d.References).ToList();
            text.AppendLine(CultureInfo.InvariantCulture, $"- Languages: {string.Join(", ", documents.GroupBy(d => d.Language ?? "unknown").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}. References: {references.Count}, of which {references.Count(r => r.Item is not null)} resolved to items and {references.Count(r => r.Document is not null)} to documents by code; the rest wait for the links phase (ADR 0012). Decisions noted for the decisions phase (ADR 0006): {documents.Sum(d => d.Decisions.Count)} in {documents.Count(d => d.Decisions.Count > 0)} documents.");
            text.AppendLine();
        }

        var decisions = DecisionCatalogue.Load(context.State);
        if (decisions.Count > 0)
        {
            text.AppendLine("## Decisions");
            text.AppendLine();
            var drafts = decisions.Where(d => d.Number is not null).OrderBy(d => d.Number).ToList();
            var fromDocuments = decisions.Count(d => d.Sources.Any(s => s.Kind == "document"));
            var fromCommits = decisions.Count(d => d.Sources.Any(s => s.Kind != "document"));
            text.AppendLine(CultureInfo.InvariantCulture, $"- {decisions.Count} decisions found (ADR 0006): {fromDocuments} noted in documents, {fromCommits} in commit messages, the same decision noted in several places merged by its words; the list is `decisions/index.md`.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Already recorded in an ADR of the repository: {decisions.Count(d => d.Standing == "recorded")}, linked to it, no draft.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- The judge read the rest: {decisions.Count(d => d.Standing is "draft" or "record" or "merged")} record a decision that was made, {decisions.Count(d => d.Standing == "not-a-decision")} do not (a plan, a wish or a fact), {decisions.Count(d => d.Standing == "unsure")} unsure; {decisions.Count(d => d.Standing == "merged")} said the same decision as an earlier draft in other words and were merged into it by the judge's reading.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Drafts in `decisions/`: {drafts.Count} ({(drafts.Count == 0 ? "none" : $"ADR-draft-{drafts[0].Number:000} to ADR-draft-{drafts[^1].Number:000}")}), status proposed, drafted by the survey from the evidence, for a person to accept or discard; {decisions.Count(d => d.Standing == "record")} routine decisions kept as records for the graph; {decisions.Count(d => d.Flags.Contains("draft-failed"))} whose draft failed.");
            var history = context.State.LoadRecord(DecisionCatalogue.HistoryKind, "history");
            if (history is not null)
            {
                try
                {
                    var node = JsonNode.Parse(history) as JsonObject;
                    var gap = node?["gap"]?.GetValue<bool>() ?? false;
                    var read = node?["read"]?.GetValue<bool>() ?? false;
                    var commits = node?["commits"]?.GetValue<int>() ?? 0;
                    var line = read
                        ? string.Create(CultureInfo.InvariantCulture, $"- The older history (ADR 0021): read, {commits} commits; the bundle carries only the hash, date and message of what it took from it.")
                        : gap ? "- The older history (ADR 0021): a gap was seen and asked about in `questions.md`; nothing before the root commit is claimed." : "- The history: no gap seen before the root commit.";
                    text.AppendLine(line);
                }
                catch (JsonException)
                {
                    // An older record shape; the line is left out.
                }
            }

            text.AppendLine();
        }

        if (GraphCatalogue.Load(context.State) is { } graph)
        {
            text.AppendLine("## Links and the graph");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"- {graph.Nodes.Count} nodes ({string.Join(", ", graph.Nodes.GroupBy(n => n.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}) and {graph.Edges.Count} edges ({string.Join(", ", graph.Edges.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}), in `graph.json`, `index.sqlite`, `graph.graphml` and `graph.mmd` (ADR 0012).");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Edges by the rule {graph.Edges.Count(e => e.By == DecidedBy.Rule)}, by the reader (tags) {graph.Edges.Count(e => e.By == DecidedBy.Reader)}, by the judge {graph.Edges.Count(e => e.By == DecidedBy.Judge)}.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Mentions: {graph.Figures.GetValueOrDefault("mentions")}, of which {graph.Figures.GetValueOrDefault("mentions by rule")} matched a title, {graph.Figures.GetValueOrDefault("mentions by judge")} were settled by the judge and {graph.Figures.GetValueOrDefault("mentions without candidate")} had no title near enough (most are code names; the code survey will resolve them).");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Unresolved references: {(graph.Unresolved.Count == 0 ? "none" : string.Join(", ", graph.Unresolved.OrderByDescending(u => u.Value).Select(u => $"{u.Key} {u.Value}")))}; they keep their text in the cards.");
            text.AppendLine();
        }

        if (VerifyPhase.Load(context.State) is { } verified)
        {
            text.AppendLine("## Verification");
            text.AppendLine();
            text.AppendLine(verified.Passed
                ? "- **Passed**: every source file is in one account, every ledger row is one item, every reference is resolved or counted with its reason, and the counts reconcile with the state."
                : $"- **{verified.Findings.Count} finding(s)** for a person: {string.Join("; ", verified.Findings)}.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Files, each in one account: {string.Join("; ", verified.FilesByAccount.OrderByDescending(a => a.Value).Select(a => $"{a.Value} {a.Key}"))}.{(verified.DocumentsWithoutSource.Count == 0 ? "" : $" {verified.DocumentsWithoutSource.Count} document card(s) whose source is gone from the input, kept and marked.")}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Ledger rows, read again by code over the whole text: {verified.Rows}, of which {verified.RowsWithOneItem} are one item each; {verified.RowsWithoutItem.Count} became no item, {verified.RowsWithTwoItems.Count} became two or are written twice, {verified.RowsInTwoLedgers.Count} id(s) sit in two ledgers, {verified.ItemsWithoutRow.Count} item(s) from a ledger match no row of it, {verified.RowsTextMissing.Count} row(s) whose cells are not all in the item's text (an item's body is its row's own text, by code, nothing cut).");
            text.AppendLine(CultureInfo.InvariantCulture, $"- References: {verified.References}; resolved {verified.ReferencesResolved.Values.Sum()} ({string.Join(", ", verified.ReferencesResolved.OrderByDescending(r => r.Value).Select(r => $"{r.Value} {r.Key}"))}); not resolved {verified.ReferencesUnresolved.Values.Sum()}, each with its reason: {string.Join(", ", verified.ReferencesUnresolved.OrderByDescending(r => r.Value).Select(r => $"{r.Value} {r.Key}"))}.");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Counts against the state: {string.Join("; ", verified.Counts)}.{(verified.StaleWords.Count == 0 ? "" : $" Status words no row uses now: {string.Join(", ", verified.StaleWords.Take(12))}.")}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Sampled items against their rows: {Sample(verified.Items, "title and status")}. Sampled links against the line that is their evidence: {Sample(verified.Links, "link")}.");
            text.AppendLine();

            static string Sample(SampleTally tally, string what) => tally.Asked == 0
                ? "none asked"
                : !tally.Judged
                    ? $"{tally.Asked} asked, the judge could not answer (the next run asks again)"
                    : $"{tally.Asked} read, {tally.Yes} {what} answers yes, {tally.Unsure} unsure, {tally.No} no{(tally.Doubts.Count == 0 ? "" : " (" + string.Join("; ", tally.Doubts.Take(6)) + (tally.Doubts.Count > 6 ? "; ..." : "") + ")")}";
        }

        text.AppendLine("## Left out, and why");
        text.AppendLine();
        var leftOut = false;
        var images = files.Count(f => f.Category == FileCategory.Image);
        var designs = files.Count(f => f.Category == FileCategory.Design);
        if (images + designs > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {images} image file(s) and {designs} design file(s): the vision pass (ADR 0014) is not built in this version; catalogued in the manifest only.");
            leftOut = true;
        }

        if (context.Map is { } mapped)
        {
            var ledgerFiles = mapped.Files.Count(f => f.Kind is "open-ledger" or "finished-ledger");
            if (ledgerFiles > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {ledgerFiles} ledger file(s): their rows are the items; the files themselves are not copied as documents.");
                leftOut = true;
            }

            var notDocuments = mapped.Files.Where(f => f.Category is "Document" or "TextUnknown" && f.Kind != "unreadable" && OrientationPhase.IsNotADocument(context, f.Kind)).GroupBy(f => f.Kind).OrderByDescending(g => g.Count()).ToList();
            if (notDocuments.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {notDocuments.Sum(g => g.Count())} text file(s) orientation found not to be documents ({string.Join(", ", notDocuments.Select(g => $"{g.Key} {g.Count()}"))}): catalogued in the manifest only.");
                leftOut = true;
            }
        }
        foreach (var group in files.Where(f => f.NotRead is not null).GroupBy(f => f.NotReadKind!).OrderByDescending(g => g.Count()))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {group.Count()} file(s) not read, {group.Key}: {string.Join(", ", group.Take(12).Select(f => "`" + f.Path + "`"))}{(group.Count() > 12 ? ", ..." : "")}. Their content went to no provider and is not in the bundle; `manifest.json` lists each with its reason.");
            leftOut = true;
        }

        foreach (var group in files.Where(f => f.Category is FileCategory.Binary or FileCategory.Archive).GroupBy(f => f.Extension.Length == 0 ? "(no extension)" : f.Extension).OrderByDescending(g => g.Count()))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {group.Count()} `{group.Key}` file(s): no decoder reads them; catalogued in the manifest only.");
            leftOut = true;
        }

        var unreadable = context.Map?.Files.Where(f => f.Kind == "unreadable").ToList() ?? [];
        if (unreadable.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {unreadable.Count} document-like file(s) no decoder could read: {string.Join(", ", unreadable.Take(10).Select(f => "`" + f.Path + "`"))}{(unreadable.Count > 10 ? ", ..." : "")}.");
            leftOut = true;
        }

        foreach (var phase in context.Playbook.Phases.Where(p => !context.PhasesDone.Contains(p)))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- The {phase} phase did not run{(!ManifestWriter.BuiltPhases.Contains(phase) ? " (not built in this version; the bundle is complete without it)" : outcome == RunOutcome.StoppedAfterPhase ? " (the run was asked to stop earlier)" : "")}.");
            leftOut = true;
        }

        if (!leftOut)
        {
            text.AppendLine("Nothing.");
        }

        text.AppendLine();
        text.AppendLine("## Flags and questions");
        text.AppendLine();
        var runRecords = context.Log.Records.Where(r => r.Run == context.RunId).ToList();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Injection gate, on the way in: {runRecords.Count(r => r.Kind == "gate-in" && r.Flags.Contains("injection-flagged"))} piece(s) flagged (still read, marked as data).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Injection gate, on the way out: {runRecords.Count(r => r.Kind == "gate-out" && r.Flags.Contains("discarded"))} output(s) discarded, {runRecords.Count(r => r.Kind == "gate-out" && r.Flags.Contains("gate-out-unsure"))} kept with the judge unsure.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Questions for a person: {context.Book.Unanswered} open (in `questions.md`), {context.Book.Asked.Count(q => q.Answer is not null)} answered from `answers.md`.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Records written to `log.md` this run: {runRecords.Count(r => r.Kind != "call")} (every decision the engine made, with its evidence; the calls are counted under Spend).");
        File.WriteAllText(context.Paths.Summary, text.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>The fixed markers around what the survey adds to a body, so a consumer can strip or replace it (docs/bundle-format.md).</summary>
public static class SurveyMarkers
{
    public const string LinksStart = "<!-- atlas:links -->";
    public const string LinksEnd = "<!-- /atlas:links -->";

    /// <summary>On its own line where the row's own text ends and what the survey wrote begins (docs/bundle-format.md).</summary>
    public const string Generated = "<!-- atlas:generated -->";
}

/// <summary>
/// Writes the JSON catalogues a consumer reads instead of the front matter (docs/bundle-format.md, version 2):
/// items.json, documents.json and decisions.json, with the fields Fravashi's ingest asked for, derived by code
/// from the catalogue: level and kind from the item's type, resolution and duplicateOf, conditionKind, priority
/// from a P-scale tier, releasedIn, personBlockers, and a confidence per decided field.
/// </summary>
public static class CatalogueJsonWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void WriteItems(SurveyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = ItemCatalogue.Load(context.State).OrderBy(i => i.Number).ToList();
        var byKey = items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

        // Containment is stated from either end: a row names its parent, or a row lists its sub-items. Both give the
        // sub-item its parent here, as both give the graph its part_of edge; the item's own statement wins.
        var parentByChild = new Dictionary<string, (string Parent, ItemRelation Relation)>(StringComparer.OrdinalIgnoreCase);
        foreach (var holder in items)
        {
            foreach (var child in holder.Relations.Where(r => r.Kind == "child" && r.ResolvedKey is not null && byKey.ContainsKey(r.ResolvedKey) && !string.Equals(r.ResolvedKey, holder.Key, StringComparison.OrdinalIgnoreCase)))
            {
                parentByChild.TryAdd(child.ResolvedKey!, (holder.Key, child));
            }
        }

        var list = new JsonArray();
        foreach (var item in items)
        {
            var open = item.Status is not ("done" or "cancelled" or "unknown");
            var blockedBy = item.Relations.Where(r => r.Kind == "blocker" && r.ResolvedKey is not null && byKey.ContainsKey(r.ResolvedKey)).ToList();
            var duplicateOf = item.Relations.FirstOrDefault(r => r.Kind == "duplicate" && r.ResolvedKey is not null)?.ResolvedKey;
            // Blocked only when the judge (or a person) read the condition as a wait, never from blockers (change 5 of the verdict).
            var waiting = item.Waits is { } w && w >= 0.7 && item.WaitsBy is DecidedBy.Judge or DecidedBy.Person;
            string? conditionKind = !open ? null : waiting ? "blocked" : item.Status == "deferred" ? "deferred" : null;
            string? resolution = item.Status == "done" ? "completed" : item.Status == "cancelled" ? (duplicateOf is not null ? "duplicate" : "wont_do") : null;
            var (level, kind) = LevelAndKind(item.Type);
            var confidence = new JsonObject();
            if (item.StatusConfidence is not null || item.StatusBy is "reader" or "judge")
            {
                confidence["status"] = Figure(item.StatusBy, item.StatusConfidence);
            }

            if (item.Type is not null)
            {
                confidence[kind is not null ? "kind" : "level"] = Figure(item.TypeBy ?? DecidedBy.Reader, item.TypeConfidence);
            }

            var statedParent = item.Parent is not null && byKey.ContainsKey(item.Parent) ? item.Parent : null;
            var listedBy = statedParent is null && parentByChild.TryGetValue(item.Key, out var listing) ? listing : ((string Parent, ItemRelation Relation)?)null;
            if (statedParent is not null)
            {
                var parent = item.Relations.First(r => r.Kind == "parent" && r.ResolvedKey is not null);
                confidence["parent"] = Figure(parent.By, parent.Confidence);
            }
            else if (listedBy is not null)
            {
                confidence["parent"] = Figure(listedBy.Value.Relation.By, listedBy.Value.Relation.Confidence);
            }

            if (blockedBy.Count > 0)
            {
                confidence["blockedBy"] = Figure(blockedBy.All(b => b.By == DecidedBy.Judge) ? DecidedBy.Judge : DecidedBy.Reader, blockedBy.Min(b => b.Confidence ?? 0.7));
            }

            if (open && item.Waits is not null)
            {
                confidence["conditionKind"] = Figure(item.WaitsBy ?? DecidedBy.Rule, item.Waits);
            }

            var source = item.Sources.FirstOrDefault();
            list.Add(new JsonObject
            {
                ["key"] = item.Key,
                ["number"] = item.Number,
                ["title"] = Cut(item.Title, 120),
                ["body"] = "items/" + ItemWriter.FileName(item),
                ["bodySha256"] = ItemCatalogue.BodySha256(item),
                ["status"] = item.Status,
                ["statusAsWritten"] = item.StatusAsWritten,
                ["resolution"] = resolution,
                ["duplicateOf"] = duplicateOf,
                ["level"] = level,
                ["kind"] = kind,
                ["type"] = item.Type,
                ["parent"] = statedParent ?? listedBy?.Parent,
                ["blockedBy"] = new JsonArray(blockedBy.Select(b => (JsonNode)new JsonObject
                {
                    ["key"] = byKey[b.ResolvedKey!].Key,
                    ["evidence"] = (source?.Path ?? item.Key) + (source?.Line is { } line ? ":" + line.ToString(CultureInfo.InvariantCulture) : "") + (b.AsWritten is null ? "" : " \"" + Cut(b.AsWritten, 60) + "\""),
                    ["by"] = b.By,
                    ["confidence"] = b.Confidence,
                }).ToArray()),
                ["derivedFrom"] = new JsonArray(item.Relations.Where(r => r.Kind == "derived" && r.ResolvedKey is not null && byKey.ContainsKey(r.ResolvedKey)).Select(d => (JsonNode)new JsonObject
                {
                    ["key"] = byKey[d.ResolvedKey!].Key,
                    ["evidence"] = (source?.Path ?? item.Key) + (source?.Line is { } derivedLine ? ":" + derivedLine.ToString(CultureInfo.InvariantCulture) : "") + (d.AsWritten is null ? "" : " \"" + Cut(d.AsWritten, 60) + "\""),
                    ["by"] = d.By,
                    ["confidence"] = d.Confidence,
                }).ToArray()),
                ["section"] = item.Section,
                ["rowSha256"] = item.RowSha256,
                ["personBlockers"] = new JsonArray(),
                ["condition"] = open ? item.Condition : null,
                ["conditionKind"] = conditionKind,
                ["tier"] = item.Tier,
                ["priority"] = open ? Priority(item.Tier) : null,
                ["acceptance"] = new JsonArray(),
                ["releasedIn"] = item.Status == "done" ? ReleasedIn(item.StatusAsWritten) : null,
                ["sources"] = new JsonArray(item.Sources.Select(s => (JsonNode)new JsonObject { ["path"] = s.Path, ["line"] = s.Line, ["sha256"] = s.Sha256 }).ToArray()),
                ["sourceGone"] = item.SourceGone,
                ["placeholder"] = item.Placeholder,
                ["flags"] = new JsonArray(item.Flags.Select(f => (JsonNode)f).ToArray()),
                ["confidence"] = confidence,
            });
        }

        File.WriteAllText(Path.Combine(context.Paths.Root, "items.json"), new JsonObject { ["atlasVersion"] = ManifestWriter.AtlasVersion, ["items"] = list }.ToJsonString(Options), new UTF8Encoding(false));
    }

    public static void WriteDocuments(SurveyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var list = new JsonArray();
        foreach (var card in DocumentCatalogue.Load(context.State).OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            list.Add(new JsonObject
            {
                ["path"] = card.Path,
                ["body"] = "documents/" + DocumentCatalogue.AtlasRelativePath(card.Path),
                ["sha256"] = card.Source.Sha256,
                ["title"] = string.IsNullOrWhiteSpace(card.Title) ? Path.GetFileName(card.Path) : card.Title,
                ["kind"] = card.Kind,
                ["notADocument"] = card.Flags.Contains("not-a-document") || NotADocumentKinds.Contains(card.Kind),
                ["fixture"] = card.Flags.Contains("from-fixture"),
                ["status"] = card.Status,
                ["supersededBy"] = card.SupersededBy,
                ["language"] = card.Language,
                ["summary"] = card.Summary,
                ["forAgents"] = card.ForAgents,
                ["referenceOnly"] = card.ReferenceOnly,
                ["sourceGone"] = card.SourceGone,
                ["flags"] = new JsonArray(card.Flags.Select(f => (JsonNode)f).ToArray()),
                ["confidence"] = new JsonObject { ["kind"] = Figure(card.By, card.Confidence) },
            });
        }

        File.WriteAllText(Path.Combine(context.Paths.Root, "documents.json"), new JsonObject { ["atlasVersion"] = ManifestWriter.AtlasVersion, ["documents"] = list }.ToJsonString(Options), new UTF8Encoding(false));
    }

    public static void WriteDecisions(SurveyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var list = new JsonArray();
        foreach (var card in DecisionCatalogue.Load(context.State).OrderBy(d => d.Number ?? int.MaxValue).ThenBy(d => d.Key, StringComparer.Ordinal))
        {
            list.Add(new JsonObject
            {
                ["key"] = card.Key,
                ["standing"] = card.Standing,
                ["title"] = card.Title ?? Cut(card.Text, 120),
                ["body"] = card.Number is null ? null : "decisions/" + DecisionCatalogue.FileName(card),
                ["date"] = card.Date,
                ["made"] = card.Made,
                ["significant"] = card.Significant,
                ["recordedIn"] = card.RecordedIn,
                ["items"] = new JsonArray(card.Items.Select(i => (JsonNode)i).ToArray()),
                ["sources"] = new JsonArray(card.Sources.Select(s => (JsonNode)new JsonObject { ["path"] = s.Path, ["line"] = s.Line, ["kind"] = s.Kind }).ToArray()),
                ["flags"] = new JsonArray(card.Flags.Select(f => (JsonNode)f).ToArray()),
            });
        }

        File.WriteAllText(Path.Combine(context.Paths.Root, "decisions.json"), new JsonObject { ["atlasVersion"] = ManifestWriter.AtlasVersion, ["decisions"] = list }.ToJsonString(Options), new UTF8Encoding(false));
    }

    /// <summary>The kinds orientation gives a text file that is not a document; a card with one of them, or the flag, is skipped by a consumer.</summary>
    internal static readonly HashSet<string> NotADocumentKinds = new(StringComparer.OrdinalIgnoreCase) { "code", "config", "data", "generated", "empty", "unreadable" };

    /// <summary>The consumer's level (tier, feature, increment, task) and kind (standard, bug, spike) from the engine's type; null means task and standard.</summary>
    internal static (string? Level, string? Kind) LevelAndKind(string? type) => type switch
    {
        "bug" => (null, "bug"),
        "spike" => (null, "spike"),
        "epic" => ("feature", null),
        "feature" => ("feature", null),
        _ => (null, null),
    };

    /// <summary>A priority from a P-scale tier (P0 critical, P1 high, P2 medium, P3 and below low); any other scale is null, for a person.</summary>
    internal static string? Priority(string? tier)
    {
        var match = PScale().Match(tier ?? "");
        if (!match.Success)
        {
            return null;
        }

        return int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) switch { 0 => "critical", 1 => "high", 2 => "medium", _ => "low" };
    }

    /// <summary>The version a finished row names ("DONE 2026-10-29 for 10.75.0"), or null.</summary>
    internal static string? ReleasedIn(string? statusAsWritten)
    {
        var match = VersionInStatus().Match(statusAsWritten ?? "");
        return match.Success ? match.Groups["v"].Value : null;
    }

    /// <summary>The people an open item waits on, from its condition ("only with the owner", "waiting on the other team").</summary>
    internal static List<string> PersonBlockers(string? condition, bool open)
    {
        var people = new List<string>();
        if (!open || string.IsNullOrWhiteSpace(condition))
        {
            return people;
        }

        foreach (Match match in PersonInCondition().Matches(condition.Replace("`", "", StringComparison.Ordinal)))
        {
            var person = match.Groups["who"].Value.Trim().TrimEnd('.', ',', ';', ':');
            if (person.Length > 0 && !people.Contains(person, StringComparer.OrdinalIgnoreCase))
            {
                people.Add(person);
            }
        }

        return people;
    }

    private static JsonObject Figure(string by, double? value) => new() { ["by"] = by, ["value"] = value };

    private static string Cut(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..(max - 3)].TrimEnd() + "...";
    }

    private static readonly Regex PScaleRegex = new(@"^\s*P(?<n>\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex PScale() => PScaleRegex;

    private static readonly Regex VersionInStatusRegex = new(@"\b(?:for|in|with|as)\s+v?(?<v>\d+\.\d+\.\d+(?:-[\w.]+)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex VersionInStatus() => VersionInStatusRegex;

    private static readonly Regex PersonInConditionRegex = new(@"\b(?:only with|with|waiting on|waits on|waiting for|waits for|needs|on)\s+(?<who>the owner|[A-Za-z][\w:.-]*(?:'s)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex PersonInCondition() => PersonInConditionRegex;
}

/// <summary>Writes status-words.json: every status word found, including those decided by rule, with its state, who mapped it, whether a person confirmed it, the rows using it and its open question's key (docs/bundle-format.md).</summary>
public static class StatusWordsWriter
{
    public static void Write(SurveyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = ItemCatalogue.Load(context.State);
        var words = ItemCatalogue.LoadWords(context.State);
        var openQuestions = new HashSet<string>(context.Book.Open().Select(q => q.Question.Key), StringComparer.Ordinal);
        var list = new JsonArray();
        foreach (var (word, mapping) in words.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = items.Where(i => !string.IsNullOrWhiteSpace(i.StatusAsWritten) && string.Equals(StatusVocabulary.WordOf(i.StatusAsWritten!.Trim()), word, StringComparison.OrdinalIgnoreCase)).ToList();
            var questionKey = "status:" + word;
            list.Add(new JsonObject
            {
                ["word"] = word,
                ["mapped"] = mapping.Mapped,
                ["condition"] = mapping.Condition,
                ["meaning"] = mapping.Meaning,
                ["by"] = mapping.By,
                ["confirmed"] = mapping.By == DecidedBy.Person,
                ["rows"] = rows.Count,
                ["asWritten"] = new JsonArray(rows.Select(r => r.StatusAsWritten!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).Select(s => (JsonNode)s).ToArray()),
                ["itemKeys"] = new JsonArray(rows.Select(r => (JsonNode)r.Key).ToArray()),
                ["questionKey"] = openQuestions.Contains(questionKey) ? questionKey : null,
            });
        }

        var root = new JsonObject
        {
            ["atlasVersion"] = ManifestWriter.AtlasVersion,
            ["states"] = new JsonArray(StatusVocabulary.MappedStates(context.Playbook).Select(s => (JsonNode)s).ToArray()),
            ["words"] = list,
        };
        File.WriteAllText(Path.Combine(context.Paths.Root, "status-words.json"), root.ToJsonString(RunContext.JsonOptions), new UTF8Encoding(false));

        // The file of format 1 this one replaced: a folder written then would otherwise keep both.
        var replaced = Path.Combine(context.Paths.Root, "status-mappings.json");
        if (File.Exists(replaced))
        {
            File.Delete(replaced);
        }
    }
}

/// <summary>Kept for a run folder written at format 1; status-words.json is the file since format 2.</summary>
internal static class StatusMappingsWriter
{
    public static void Write(SurveyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = ItemCatalogue.Load(context.State);
        var words = ItemCatalogue.LoadWords(context.State);
        var list = new JsonArray();
        foreach (var (word, mapping) in words.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = items.Where(i => !string.IsNullOrWhiteSpace(i.StatusAsWritten) && string.Equals(StatusVocabulary.WordOf(i.StatusAsWritten!.Trim()), word, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(new JsonObject
            {
                ["word"] = word,
                ["mapped"] = mapping.Mapped,
                ["by"] = mapping.By,
                ["condition"] = mapping.Condition,
                ["meaning"] = mapping.Meaning,
                ["rows"] = rows.Count,
                ["as_written"] = new JsonArray(rows.Select(r => r.StatusAsWritten!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).Select(s => (JsonNode)s).ToArray()),
                ["items"] = new JsonArray(rows.Select(r => (JsonNode)r.Key).ToArray()),
            });
        }

        var root = new JsonObject
        {
            ["formatVersion"] = ManifestWriter.BundleFormatVersion,
            ["states"] = new JsonArray(StatusVocabulary.MappedStates(context.Playbook).Select(s => (JsonNode)s).ToArray()),
            ["mappings"] = list,
        };
        File.WriteAllText(Path.Combine(context.Paths.Root, "status-mappings.json"), root.ToJsonString(RunContext.JsonOptions), new UTF8Encoding(false));
    }
}

/// <summary>Writes manifest.json (ADR 0005): the run, the input and a hash per file, the playbook, the models, the totals, and the complete mark (docs/bundle-format.md).</summary>
public static class ManifestWriter
{
    /// <summary>The bundle format's version, as docs/bundle-format.md describes it; a consumer refuses a version it does not know.</summary>
    public const string BundleFormatVersion = "2";

    /// <summary>The same, as the number the ingest reads (atlasVersion); changed on any breaking change.</summary>
    public const int AtlasVersion = 2;

    /// <summary>The phases this version has code for; a playbook phase outside this set is listed as not built, and a complete bundle needs every built phase run.</summary>
    public static readonly IReadOnlySet<string> BuiltPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orientation", "items", "documents", "decisions", "links", "verify" };

    /// <summary>The engine's version and the commit it was built from (the build stamps the commit into the informational version), so a re-check can tell a stale bundle.</summary>
    public static JsonObject EngineVersion()
    {
        var informational = typeof(ManifestWriter).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return new JsonObject
        {
            ["version"] = plus >= 0 ? informational[..plus] : informational,
            ["commit"] = plus >= 0 ? informational[(plus + 1)..] : null,
        };
    }

    /// <summary>What stayed on the machine (ADR 0028): files that look like they hold a secret, files the person excluded, backup copies, commit messages, and the files a person allowed although a rule matched.</summary>
    private static JsonObject Withheld(SurveyContext context)
    {
        var secrets = new JsonArray();
        var excluded = new JsonArray();
        var backups = new JsonArray();
        var allowed = new JsonArray();
        foreach (var file in context.Snapshot.Files)
        {
            if (file.ExcludedBy is not null)
            {
                excluded.Add(new JsonObject { ["path"] = file.Path, ["pattern"] = file.ExcludedBy });
            }
            else if (file.Secret is not null)
            {
                (file.NotRead is null ? allowed : secrets).Add(new JsonObject { ["path"] = file.Path, ["rule"] = file.Secret.Rule, ["line"] = file.Secret.Line });
            }
            else if (file.NotRead is not null)
            {
                backups.Add(file.Path);
            }
        }

        var commits = new JsonArray();
        foreach (var (hash, rule) in context.Snapshot.Git?.WithheldCommits.OrderBy(c => c.Key, StringComparer.Ordinal) ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            commits.Add(new JsonObject { ["commit"] = hash, ["rule"] = rule });
        }

        return new JsonObject { ["secrets"] = secrets, ["excluded"] = excluded, ["backupCopies"] = backups, ["allowed"] = allowed, ["commits"] = commits };
    }

    /// <summary>True when every phase with code ran and the run completed: the bundle is whole.</summary>
    public static bool IsComplete(SurveyContext context, RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(context);
        return outcome == RunOutcome.Completed && context.Playbook.Phases.Where(p => BuiltPhases.Contains(p)).All(context.PhasesDone.Contains);
    }

    public static void Write(SurveyContext context, BackupResult? backup, RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cost = context.Log.CostOfRun();
        var models = context.Log.Records.Where(r => r.Kind == "call" && r.Model is not null).Select(r => r.Model!).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
        var documentPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var card in DocumentCatalogue.Load(context.State))
        {
            documentPaths[card.Path] = "documents/" + DocumentCatalogue.AtlasRelativePath(card.Path);
        }

        var files = new JsonArray();
        foreach (var file in context.Snapshot.Files)
        {
            var mapped = context.Map?.File(file.Path);
            files.Add(new JsonObject
            {
                ["path"] = file.Path,
                ["size"] = file.Size,
                ["sha256"] = file.Sha256,
                ["category"] = file.Category.ToString(),
                ["kind"] = mapped?.Kind,
                ["language"] = file.Language ?? mapped?.Language,
                ["tracked"] = file.Tracked,
                ["document"] = documentPaths.TryGetValue(file.Path, out var document) ? document : null,
                ["notRead"] = file.NotRead,
                ["secretRule"] = file.Secret?.Rule,
            });
        }

        var configured = new JsonObject();
        foreach (var (phase, model) in context.Run.Options.Models.Configured())
        {
            configured[phase] = model;
        }

        configured["judge"] = context.Run.Options.Judge.Model;

        var built = context.Playbook.Phases.Where(p => BuiltPhases.Contains(p)).ToList();
        var notBuilt = context.Playbook.Phases.Where(p => !BuiltPhases.Contains(p)).ToList();
        var notRun = built.Where(p => !context.PhasesDone.Contains(p)).ToList();
        var complete = outcome == RunOutcome.Completed && notRun.Count == 0;
        var incomplete = new JsonArray();
        if (!complete)
        {
            incomplete.Add((JsonNode)(outcome switch
            {
                RunOutcome.StoppedAfterPhase => "the run was asked to stop before the last phase with code",
                RunOutcome.StoppedAtCeiling => "the run stopped at a spending ceiling",
                RunOutcome.StoppedForQuestion => "the run stopped for a question a person must answer",
                RunOutcome.Failed => "the run failed",
                _ => "not every phase with code ran",
            }));
            if (notRun.Count > 0)
            {
                incomplete.Add((JsonNode)("not run: " + string.Join(", ", notRun)));
            }
        }

        var verified = VerifyPhase.Load(context.State);
        var manifest = new JsonObject
        {
            ["atlasVersion"] = AtlasVersion,
            ["formatVersion"] = BundleFormatVersion,
            ["complete"] = complete,
            ["incomplete"] = incomplete,
            ["findings"] = verified?.Findings.Count ?? 0,
            ["openQuestions"] = context.Book.Unanswered,
            ["run"] = context.RunId,
            ["outcome"] = outcome.ToString(),
            ["written"] = DateTimeOffset.Now,
            ["engine"] = EngineVersion(),
            ["input"] = new JsonObject
            {
                ["path"] = context.Snapshot.Root,
                ["commit"] = context.Snapshot.Git?.Commit,
                ["branch"] = context.Snapshot.Git?.Branch,
                ["clean"] = context.Snapshot.Git?.Clean,
                ["listedBy"] = context.Snapshot.ListedBy,
                ["filesHash"] = context.Snapshot.FilesHash,
            },
            ["playbook"] = new JsonObject { ["name"] = context.Playbook.Name, ["version"] = context.Playbook.Version, ["hash"] = context.Playbook.Hash },
            ["models"] = new JsonObject
            {
                ["configured"] = configured,
                ["answered"] = new JsonArray(models.Select(m => (JsonNode)m).ToArray()),
            },
            ["phases"] = new JsonObject
            {
                ["done"] = new JsonArray(context.PhasesDone.Select(p => (JsonNode)p).ToArray()),
                ["notBuilt"] = new JsonArray(notBuilt.Select(p => (JsonNode)p).ToArray()),
                ["notRun"] = new JsonArray(notRun.Select(p => (JsonNode)p).ToArray()),
            },
            ["withheld"] = Withheld(context),
            ["catalogues"] = new JsonObject { ["items"] = "items.json", ["documents"] = "documents.json", ["decisions"] = "decisions.json", ["graph"] = "graph.json", ["questions"] = "questions.json", ["statusWords"] = "status-words.json" },
            ["totals"] = new JsonObject
            {
                ["calls"] = context.Log.CountOfRun("call"),
                ["inputTokens"] = cost.InputTokens,
                ["cachedInputTokens"] = cost.CachedInputTokens,
                ["outputTokens"] = cost.OutputTokens,
                ["reasoningTokens"] = cost.ReasoningTokens,
                ["usd"] = cost.Usd,
                ["readerUsd"] = context.Spend.RunUsdOf(SpendKind.Reader),
                ["judgeUsd"] = context.Spend.RunUsdOf(SpendKind.Judge),
                ["allRunsUsd"] = context.Spend.AllRunsUsd,
                ["allRunsReaderUsd"] = context.Spend.AllRunsUsdOf(SpendKind.Reader),
                ["allRunsJudgeUsd"] = context.Spend.AllRunsUsdOf(SpendKind.Judge),
                ["decisions"] = context.Log.Records.Count(r => r.Run == context.RunId && r.Kind != "call"),
                ["openQuestions"] = context.Book.Unanswered,
            },
            ["backup"] = backup is null ? null : new JsonObject { ["zip"] = backup.ArchivePath, ["files"] = backup.Entries, ["bytes"] = backup.Bytes },
            ["files"] = files,
        };
        // Written last, through a temporary file, so a stopped run never leaves a manifest that says complete.
        var temporary = context.Paths.Manifest + ".tmp";
        File.WriteAllText(temporary, manifest.ToJsonString(RunContext.JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, context.Paths.Manifest, overwrite: true);
    }
}
