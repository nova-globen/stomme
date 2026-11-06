using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Runner;

namespace Stomme.Cartographer.Catalogue;

/// <summary>One judged sample's tally: how many were asked, and how the judge read them.</summary>
public sealed class SampleTally
{
    public int Asked { get; set; }
    public int Yes { get; set; }
    public int Unsure { get; set; }
    public int No { get; set; }
    public bool Judged { get; set; }
    public List<string> Doubts { get; set; } = [];
}

/// <summary>
/// What the verify phase found (the plan, phase 7): every source file in exactly one account, every ledger row
/// in exactly one item, every reference resolved or counted with its reason, the summary's counts reconciled
/// with the state, and a judged sample of items and links. Kept as a record in the state and rendered into
/// summary.md and state.md.
/// </summary>
public sealed class VerifyResult
{
    public string RunId { get; set; } = "";

    /// <summary>Files by account: what became of each (a ledger, a document, code, an image, ...), every file counted once.</summary>
    public Dictionary<string, int> FilesByAccount { get; set; } = [];

    /// <summary>Files in no account: the map does not know them (should be none).</summary>
    public List<string> FilesUnaccounted { get; set; } = [];

    /// <summary>Document-like files by the map with no document card, so the documents phase missed them.</summary>
    public List<string> DocumentsMissing { get; set; } = [];

    /// <summary>Document cards for files the map left out (a card where orientation said not a document), so a file counted twice.</summary>
    public List<string> DocumentsConflicting { get; set; } = [];

    /// <summary>Document cards whose source is gone from the input (kept, marked).</summary>
    public List<string> DocumentsWithoutSource { get; set; } = [];

    /// <summary>Document cards that would be written to one and the same file under documents/, so one would hide the other.</summary>
    public List<string> DocumentsSharingAFile { get; set; } = [];

    public int Rows { get; set; }
    public int RowsWithOneItem { get; set; }
    public List<string> RowsWithoutItem { get; set; } = [];
    public List<string> RowsWithTwoItems { get; set; } = [];
    public List<string> RowsInTwoLedgers { get; set; } = [];
    public List<string> ItemsWithoutRow { get; set; } = [];

    /// <summary>Rows whose cells' text is not all in their item's title and body (the item would hide part of the row).</summary>
    public List<string> RowsTextMissing { get; set; } = [];

    public int References { get; set; }
    public Dictionary<string, int> ReferencesResolved { get; set; } = [];
    public Dictionary<string, int> ReferencesUnresolved { get; set; } = [];

    /// <summary>One line per count reconciled with the state, and whether it holds.</summary>
    public List<string> Counts { get; set; } = [];

    /// <summary>Status words recorded by earlier runs that no row uses now (dropped from the summary).</summary>
    public List<string> StaleWords { get; set; } = [];

    public SampleTally Items { get; set; } = new();
    public SampleTally Links { get; set; } = new();

    /// <summary>Ledgers whose rows, as the reader returned them, are not the items written from them, one for one.</summary>
    public List<string> RowsNotWritten { get; set; } = [];

    /// <summary>Ledgers with rows the reader left out of this run that are kept because their lines still stand in the file, with the count.</summary>
    public List<string> RowsNotReturned { get; set; } = [];

    /// <summary>Lines of a ledger written as entries like its rows that no item stands on, each with its path, its line and its text (the final accounting).</summary>
    public List<string> EntriesWithoutItem { get; set; } = [];

    /// <summary>Sections of a ledger that hold list entries or table rows, by the file alone, and no row: each with its heading, the count and the lines.</summary>
    public List<string> SectionsWithoutRow { get; set; } = [];

    /// <summary>Ledgers, or chunks of one, the reader returned no row for though their lines hold entries: each with the count over the whole of it.</summary>
    public List<string> LedgersUnread { get; set; } = [];

    /// <summary>Items that hold more than one row: rows of one ledger taken into one item, and a row of an open ledger merged with one of a finished ledger.</summary>
    public List<string> RowsMerged { get; set; } = [];

    /// <summary>Rows of a finished ledger or section whose own words say they are not finished: kept as their words say, with a question.</summary>
    public List<string> OpenInFinished { get; set; } = [];

    /// <summary>Questions that offer options and whose default, as written for a consumer, is not one of them.</summary>
    public List<string> DefaultsNotOptions { get; set; } = [];

    /// <summary>Files not read because they look like they hold a secret, each with the rule and the line; never the value.</summary>
    public List<string> Withheld { get; set; } = [];

    /// <summary>Ledgers the guides name that are not in the input: their rows are not in the import.</summary>
    public List<string> LedgersMissing { get; set; } = [];

    /// <summary>Places outside the repository where it says its work is tracked; not read.</summary>
    public List<string> WorkElsewhere { get; set; } = [];

    /// <summary>What a person must look at: the anomalies above, named.</summary>
    public List<string> Findings { get; set; } = [];

    public bool Passed => Findings.Count == 0;
}

/// <summary>
/// The verify phase (the plan, phase 7): by code, every source file is put in exactly one account and every row
/// of every ledger is matched to exactly one item (the rows read again by <see cref="LedgerTable"/> over the whole
/// text), every reference is resolved or counted with its reason, and the counts the summary shows are
/// reconciled with the state; then the judge reads a small, fixed sample of items against their rows and of
/// links against their evidence. The findings go to the log, the state and summary.md.
/// </summary>
public sealed class VerifyPhase(SurveyContext survey) : IPhase
{
    public const string PhaseName = "verify";
    public const string ResultKind = "verify";
    public const string ResultKey = "result";

    public string Name => PhaseName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var context = survey;
        var map = context.Map ?? context.LoadMap() ?? throw new InvalidOperationException("The verify phase needs the repository map from orientation.");
        var schemes = map.IdSchemes.Where(s => s.Confirmed).ToList();
        var documents = DocumentCatalogue.Load(context.State);
        var items = ItemCatalogue.Load(context.State);
        var decisions = DecisionCatalogue.Load(context.State);
        var graph = GraphCatalogue.Load(context.State);
        var result = new VerifyResult { RunId = context.RunId };
        context.Touch("verify: the files, the rows, the references and the counts", force: true);

        var ledgerRows = AccountForFiles(context, map, documents, result);
        var rowSample = AccountForRows(context, map, schemes, items, result, ledgerRows);
        AccountForReferences(context, map, schemes, documents, items, graph, result);
        ReconcileCounts(context, map, documents, items, decisions, graph, result);

        var thresholds = context.Run.Options.Thresholds;
        var template = context.Playbook.JudgeSet("verify");
        var salt = context.Playbook.Salt([], [], [], ["verify"]);
        await JudgeItemsAsync(context, template, salt, thresholds, rowSample, result, cancellationToken).ConfigureAwait(false);
        await JudgeLinksAsync(context, template, salt, thresholds, documents, items, graph, result, cancellationToken).ConfigureAwait(false);

        foreach (var sameLine in items.Where(i => !i.Placeholder && !i.SourceGone && i.Sources.Count > 0 && i.Sources[0].Line is not null)
            .GroupBy(i => i.Sources[0].Path + ":" + i.Sources[0].Line, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            // One line of a ledger is one row (the re-check, B1): two items for it is a row doubled.
            result.RowsWithTwoItems.Add($"{sameLine.Key} ({string.Join(", ", sameLine.Select(i => i.Key))})");
        }

        foreach (var (ledgerPath, json) in context.State.Records(ItemsPhase.RowsReturnedKind))
        {
            // A row of the reader's is never taken into another without a word (the second re-check): the rows returned
            // against the items written from them, and the rows kept because the reader left them out, are a person's to see.
            if (JsonSerializer.Deserialize<ItemsPhase.RowsReturned>(json, RunContext.JsonOptions) is not { } counts)
            {
                continue;
            }

            if (counts.Returned != counts.Written)
            {
                result.RowsNotWritten.Add($"{ledgerPath}: the reader returned {counts.Returned} row(s) and {counts.Written} item(s) are written from them");
            }

            if (counts.Kept > 0)
            {
                result.RowsNotReturned.Add($"{ledgerPath}: {counts.Kept}");
            }
        }

        foreach (var (ledgerPath, json) in context.State.Records(ItemsPhase.AccountingKind).OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            // The final accounting of a ledger (the third re-check): an entry no item stands on, and rows taken into
            // one item, are a person's to see, each by its line.
            if (JsonSerializer.Deserialize<ItemsPhase.LedgerAccounting>(json, RunContext.JsonOptions) is not { } accounting)
            {
                continue;
            }

            result.EntriesWithoutItem.AddRange(accounting.WithoutItem.Select(e => $"{ledgerPath}:{e.Line} \"{(e.Text.Length > 70 ? e.Text[..70] + "..." : e.Text)}\""));
            result.RowsMerged.AddRange(accounting.Merged.Select(m => $"{ledgerPath}: {m}"));
            result.SectionsWithoutRow.AddRange(accounting.SectionsWithoutRow.Select(s => $"{ledgerPath} {(s.Heading.Length == 0 ? "before its first heading" : $"under \"{s.Heading}\"")} ({s.Entries} entr{(s.Entries == 1 ? "y" : "ies")}, lines {s.FirstLine} to {s.LastLine})"));
            result.LedgersUnread.AddRange(accounting.Unread.Select(u => $"{ledgerPath}{(u.Chunk == 0 ? ", the whole ledger" : $", chunk {u.Chunk} of {u.Chunks}")} ({u.Entries} entr{(u.Entries == 1 ? "y" : "ies")}, lines {u.FirstLine} to {u.LastLine}{(u.ReadAgain ? ", read twice" : "")})"));
        }

        foreach (var merged in items.Where(i => i.Flags.Contains("in-two-ledgers") && !i.SourceGone))
        {
            result.RowsMerged.Add($"{merged.Key} (rows of {string.Join(" and ", merged.Sources.Select(s => s.Path + (s.Line is { } l ? ":" + l : "")).Distinct(StringComparer.OrdinalIgnoreCase))})");
        }

        result.OpenInFinished.AddRange(items.Where(i => i.Flags.Contains("open-in-finished-ledger") && !i.Flags.Contains("status-by-ledger") && !i.SourceGone && i.Status is not ("done" or "cancelled")).Select(i => i.Key));

        result.DefaultsNotOptions.AddRange(DefaultsThatAreNoOption(context.Book.Written().Select(q => q.Question)));
        result.LedgersMissing.AddRange(map.MissingLedgers.Where(m => !m.Dismissed).Select(m => $"{m.Path} ({m.Kind}, named at {m.NamedIn})"));
        result.WorkElsewhere.AddRange(map.WorkElsewhere.Where(w => !w.Dismissed).Select(w => $"{(w.Where.Length > 160 ? w.Where[..160] + "..." : w.Where)} ({w.NamedIn})"));
        Findings(result);
        context.State.SaveRecord(ResultKind, ResultKey, JsonSerializer.Serialize(result, RunContext.JsonOptions), context.RunId);
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "verdict",
            Subject = "the bundle",
            Decision = result.Passed
                ? "every file is in one account, every ledger row is one item, the references are resolved or counted with a reason, the counts reconcile; nothing for a person to look at from this phase"
                : $"{result.Findings.Count} finding(s) for a person: {string.Join("; ", result.Findings)}",
            By = DecidedBy.Rule,
            Figures = new Dictionary<string, double>
            {
                ["files"] = result.FilesByAccount.Values.Sum(),
                ["rows"] = result.Rows,
                ["references"] = result.References,
                ["findings"] = result.Findings.Count,
            },
        });
    }

    public static VerifyResult? Load(Conductor.State.RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var json = state.LoadRecord(ResultKind, ResultKey);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VerifyResult>(json, RunContext.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ----- 1. Every source file in exactly one account -----

    private static List<(LedgerEntry Ledger, SourceFile File)> AccountForFiles(SurveyContext context, RepositoryMap map, List<DocumentCard> documents, VerifyResult result)
    {
        var cards = documents.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        var ledgers = new List<(LedgerEntry Ledger, SourceFile File)>();
        var setAside = new HashSet<string>(context.State.Records(ItemsPhase.SkippedLedgerKind).Select(r => r.Key), StringComparer.OrdinalIgnoreCase);
        // A map may name one ledger twice (the guides and the reader both named it on one sample): one entry per path.
        var ledgerPaths = map.Ledgers.Where(l => l.Kind is "open-ledger" or "finished-ledger" && !setAside.Contains(l.Path))
            .GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Account(string name) => result.FilesByAccount[name] = result.FilesByAccount.GetValueOrDefault(name) + 1;

        foreach (var file in context.Snapshot.Files)
        {
            seen.Add(file.Path);
            var mapped = map.File(file.Path);
            var card = cards.GetValueOrDefault(file.Path);
            if (mapped is null)
            {
                result.FilesUnaccounted.Add(file.Path);
                Account("not in the map");
                continue;
            }

            if (file.NotRead is not null)
            {
                Account("not read: " + file.NotReadKind);
                if (card is not null)
                {
                    result.DocumentsConflicting.Add(file.Path);
                }

                if (file.Secret is not null)
                {
                    result.Withheld.Add($"{file.Path} ({file.Secret})");
                }

                continue;
            }

            if (setAside.Contains(file.Path))
            {
                Account("ledger under an example, test or fixture folder, set aside by the items phase: a document" + (card is null ? "; no card" : ""));
                continue;
            }

            if (ledgerPaths.TryGetValue(file.Path, out var ledger) || mapped.Kind is "open-ledger" or "finished-ledger")
            {
                ledgers.Add((ledger ?? new LedgerEntry { Path = file.Path, Kind = mapped.Kind }, file));
                Account("ledger: its rows are the items, its prose a document by rule");
                if (card is null)
                {
                    result.DocumentsMissing.Add(file.Path);
                }

                continue;
            }

            var documentLike = mapped.Category is "Document" or "TextUnknown";
            if (documentLike && mapped.Kind == "unreadable")
            {
                Account("document-like, unreadable: no decoder could read it");
                if (card is not null)
                {
                    result.DocumentsConflicting.Add(file.Path);
                }

                continue;
            }

            if (documentLike && OrientationPhase.IsNotADocument(context, mapped.Kind))
            {
                Account($"text orientation found not to be a document ({mapped.Kind}): in the manifest only");
                if (card is not null)
                {
                    result.DocumentsConflicting.Add(file.Path);
                }

                continue;
            }

            if (documentLike)
            {
                if (card is null)
                {
                    result.DocumentsMissing.Add(file.Path);
                    Account("document expected, no card");
                }
                else
                {
                    Account("document");
                }

                continue;
            }

            if (card is not null)
            {
                // A card for a file the map does not call a document: counted as the document it became, and noted.
                result.DocumentsConflicting.Add(file.Path);
                Account("document");
                continue;
            }

            Account(file.Category switch
            {
                FileCategory.Code => "code: in the manifest; the code survey is not built in this version",
                FileCategory.Image or FileCategory.Design => "image or design: in the manifest; the vision pass is not built in this version",
                FileCategory.Data or FileCategory.Config => "data or config: in the manifest only",
                FileCategory.Binary or FileCategory.Archive => "binary or archive: no decoder reads it; in the manifest only",
                _ => "other: in the manifest only",
            });
        }

        foreach (var card in documents.Where(d => !seen.Contains(d.Path)))
        {
            result.DocumentsWithoutSource.Add(card.Path);
        }

        foreach (var group in documents.GroupBy(d => DocumentCatalogue.AtlasRelativePath(d.Path), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            result.DocumentsSharingAFile.Add($"{string.Join(" and ", group.Select(d => d.Path))} at documents/{group.Key}");
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "files",
            Subject = "every source file",
            Decision = $"{context.Snapshot.Files.Count} files, each in one account: {string.Join("; ", result.FilesByAccount.OrderByDescending(a => a.Value).Select(a => $"{a.Value} {a.Key}"))}; {result.FilesUnaccounted.Count} in no account, {result.DocumentsMissing.Count} document(s) expected with no card, {result.DocumentsConflicting.Count} card(s) for files the map left out, {result.DocumentsWithoutSource.Count} card(s) whose source is gone",
            By = DecidedBy.Rule,
            Reasoning = Examples(result.FilesUnaccounted, "no account") + Examples(result.DocumentsMissing, "no card") + Examples(result.DocumentsConflicting, "card for a left-out file"),
        });
        return ledgers;
    }

    // ----- 2. Every ledger row in exactly one item -----

    private static List<(string Key, string Row, WorkItem Item)> AccountForRows(SurveyContext context, RepositoryMap map, List<IdScheme> schemes, List<WorkItem> items,
        VerifyResult result, List<(LedgerEntry Ledger, SourceFile File)> ledgers)
    {
        var sample = new List<(string Key, string Row, WorkItem Item)>();
        var byKey = new Dictionary<string, List<WorkItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => !i.Placeholder))
        {
            foreach (var key in new[] { item.Key }.Concat(item.Aliases))
            {
                if (!byKey.TryGetValue(key, out var list))
                {
                    byKey[key] = list = [];
                }

                if (!list.Contains(item))
                {
                    list.Add(item);
                }
            }
        }

        var rowKeysByLedger = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ledger, file) in ledgers)
        {
            var decoded = context.Decode(file);
            if (decoded is null)
            {
                continue;
            }

            var rows = LedgerTable.Parse(decoded.Text, schemes);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            rowKeysByLedger[ledger.Path] = keys;
            foreach (var row in rows)
            {
                result.Rows++;
                var normalised = IdNormaliser.Normalise(row.Id, schemes);
                var key = normalised?.Key ?? row.Id;
                var where = $"{ledger.Path}:{row.Line} {row.Id}";
                if (!keys.Add(key))
                {
                    result.RowsWithTwoItems.Add(where + " (the id twice in the ledger)");
                }

                var matched = byKey.GetValueOrDefault(key) ?? (normalised?.Base is { } b && !string.Equals(b, key, StringComparison.OrdinalIgnoreCase) ? byKey.GetValueOrDefault(b) : null) ?? [];
                var fromThisLedger = matched.Where(i => i.Sources.Any(s => string.Equals(s.Path, ledger.Path, StringComparison.OrdinalIgnoreCase))).ToList();
                if (fromThisLedger.Count == 0)
                {
                    fromThisLedger = matched;
                }

                if (fromThisLedger.Count == 0)
                {
                    result.RowsWithoutItem.Add(where);
                }
                else if (fromThisLedger.Count > 1)
                {
                    result.RowsWithTwoItems.Add($"{where} (items {string.Join(", ", fromThisLedger.Select(i => i.Number))})");
                }
                else
                {
                    result.RowsWithOneItem++;
                    sample.Add((key, decoded.Lines.Count >= row.Line && row.Line > 0 ? decoded.Lines[row.Line - 1] : row.Title, fromThisLedger[0]));
                    var item = fromThisLedger[0];
                    var held = Squeeze(LedgerTable.Bare(item.Title + " " + item.Detail + " " + (item.StatusAsWritten ?? "") + " " + (item.Tier ?? "") + " " + (item.Size ?? "")));
                    var missingCells = row.Cells.Where(c => c.Role != "id" && c.Text.Length > 0 && !held.Contains(Squeeze(LedgerTable.Bare(c.Text)), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (missingCells.Count > 0)
                    {
                        result.RowsTextMissing.Add($"{where}: {string.Join(", ", missingCells.Select(c => (c.Header.Length > 0 ? c.Header : c.Role) + " cell (" + c.Text.Length + " chars)"))}");
                    }
                }
            }
        }

        foreach (var (path, keys) in rowKeysByLedger)
        {
            foreach (var (other, otherKeys) in rowKeysByLedger.Where(l => string.CompareOrdinal(l.Key, path) > 0))
            {
                foreach (var key in keys.Intersect(otherKeys, StringComparer.OrdinalIgnoreCase))
                {
                    result.RowsInTwoLedgers.Add($"{key} in {path} and {other}");
                }
            }
        }

        // Only a ledger whose table the parser could read says anything about its items: a ledger of list entries or
        // headings (two of the samples) yields no table row, and its items are not for this check.
        var ledgersWithRows = new HashSet<string>(rowKeysByLedger.Where(l => l.Value.Count > 0).Select(l => l.Key), StringComparer.OrdinalIgnoreCase);
        var allRowKeys = new HashSet<string>(rowKeysByLedger.Values.SelectMany(k => k), StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => !i.Placeholder && !i.Flags.Contains("from-prose") && i.Sources.Any(s => ledgersWithRows.Contains(s.Path))))
        {
            var keys = new[] { item.Key }.Concat(item.Aliases);
            if (!keys.Any(allRowKeys.Contains) && !(IdNormaliser.Normalise(item.Key, schemes)?.Base is { } b && allRowKeys.Contains(b)))
            {
                result.ItemsWithoutRow.Add($"{item.Key} (item {item.Number}, from {item.Sources[0].Path})");
            }
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "rows",
            Subject = "every ledger row",
            Decision = $"{result.Rows} row(s) with an id in {ledgers.Count} ledger(s), read again by code over the whole text: {result.RowsWithOneItem} are one item each, {result.RowsWithoutItem.Count} have no item, {result.RowsWithTwoItems.Count} have two or are written twice, {result.RowsInTwoLedgers.Count} id(s) sit in two ledgers; {result.ItemsWithoutRow.Count} item(s) from a ledger match no row of it; {result.RowsTextMissing.Count} row(s) whose cells are not all in the item's text",
            By = DecidedBy.Rule,
            Reasoning = Examples(result.RowsWithoutItem, "no item") + Examples(result.RowsWithTwoItems, "two items") + Examples(result.RowsInTwoLedgers, "two ledgers") + Examples(result.ItemsWithoutRow, "no row") + Examples(result.RowsTextMissing, "text missing"),
        });
        return sample;
    }

    // ----- 3. Every reference resolved, or counted with its reason -----

    private static void AccountForReferences(SurveyContext context, RepositoryMap map, List<IdScheme> schemes, List<DocumentCard> documents, List<WorkItem> items, Graph? graph, VerifyResult result)
    {
        var itemsByKey = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            itemsByKey[item.Key] = item;
            foreach (var alias in item.Aliases)
            {
                itemsByKey.TryAdd(alias, item);
            }
        }

        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            var name = document.Path.Contains('/', StringComparison.Ordinal) ? document.Path[(document.Path.LastIndexOf('/') + 1)..] : document.Path;
            var head = name.Split('-', '.', '_', ' ')[0];
            if (head.Length > 0)
            {
                heads.Add(LinksPhase.HeadKey(head));
                if (IdNormaliser.Normalise(head, schemes) is { } normalised)
                {
                    carried.Add(normalised.Key);
                }
            }
        }

        var documentPaths = new HashSet<string>(documents.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
        void Resolved(string how) => result.ReferencesResolved[how] = result.ReferencesResolved.GetValueOrDefault(how) + 1;
        void Unresolved(string why) => result.ReferencesUnresolved[why] = result.ReferencesUnresolved.GetValueOrDefault(why) + 1;

        void One(string kind, string text, string fromFolder)
        {
            result.References++;
            var cleaned = LinksPhase.Clean(text);
            switch (kind)
            {
                case "id" when ItemsPhase.IsQualifiedByAnotherName(text):
                case "foreign-id":
                    Unresolved("an id written as another repository's");
                    break;
                case "id":
                    if (LinksPhase.ResolveItem(cleaned, schemes, itemsByKey) is not null)
                    {
                        Resolved("id to an item");
                    }
                    else if (IdNormaliser.Normalise(cleaned, schemes) is { } normalised && carried.Contains(normalised.Key) || heads.Contains(LinksPhase.HeadKey(cleaned)))
                    {
                        Resolved("id to the document carrying it");
                    }
                    else
                    {
                        Unresolved("an id no row defines and no document carries");
                    }

                    break;
                case "path":
                case "anchor":
                    var hash = cleaned.IndexOf('#', StringComparison.Ordinal);
                    var bare = hash >= 0 ? cleaned[..hash] : cleaned;
                    if (bare.Length == 0)
                    {
                        Unresolved(kind == "anchor" ? "an anchor within the document itself" : "an empty path");
                    }
                    else if (bare.Contains("://", StringComparison.Ordinal))
                    {
                        Unresolved("a URL: outside the repository, no node by design");
                    }
                    else if (LinksPhase.PathCandidates(bare, fromFolder).FirstOrDefault(c => documentPaths.Contains(c) || context.File(c) is not null) is { } found)
                    {
                        Resolved(documentPaths.Contains(found) ? "path to a document" : "path to a file (code, design or data)");
                    }
                    else
                    {
                        Unresolved("a path naming no file in the repository");
                    }

                    break;
                case "url":
                    Unresolved("a URL: outside the repository, no node by design");
                    break;
                case "version":
                    Resolved("version to a tag");
                    break;
                case "mention":
                    // Counted from the graph's figures below: by title, by the judge, or without a title near enough.
                    result.References--;
                    break;
                default:
                    Unresolved($"a reference of kind {kind}, not resolved in this version");
                    break;
            }
        }

        foreach (var document in documents)
        {
            var folder = document.Path.Contains('/', StringComparison.Ordinal) ? document.Path[..document.Path.LastIndexOf('/')] : "";
            foreach (var reference in document.References)
            {
                One(reference.Kind, reference.Document ?? reference.Item ?? reference.Text, folder);
            }
        }

        foreach (var item in items)
        {
            foreach (var reference in item.References)
            {
                One(reference.Kind, reference.ResolvedKey ?? reference.Text, "");
            }
        }

        if (graph is not null)
        {
            var mentions = graph.Figures.GetValueOrDefault("mentions");
            var byRule = graph.Figures.GetValueOrDefault("mentions by rule");
            var byJudge = graph.Figures.GetValueOrDefault("mentions by judge");
            var without = graph.Figures.GetValueOrDefault("mentions without candidate");
            result.References += mentions;
            if (byRule > 0)
            {
                result.ReferencesResolved["mention to a title, by code"] = byRule;
            }

            if (byJudge > 0)
            {
                result.ReferencesResolved["mention to a title, by the judge"] = byJudge;
            }

            if (without > 0)
            {
                result.ReferencesUnresolved["a mention with no title near enough (most are code names)"] = without;
            }

            var judgedNo = mentions - byRule - byJudge - without;
            if (judgedNo > 0)
            {
                result.ReferencesUnresolved["a mention the judge found to be of no near title"] = judgedNo;
            }
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "references",
            Subject = "every reference",
            Decision = $"{result.References} reference(s) in the cards: resolved {result.ReferencesResolved.Values.Sum()} ({string.Join(", ", result.ReferencesResolved.OrderByDescending(r => r.Value).Select(r => $"{r.Value} {r.Key}"))}); not resolved {result.ReferencesUnresolved.Values.Sum()}, each with its reason ({string.Join(", ", result.ReferencesUnresolved.OrderByDescending(r => r.Value).Select(r => $"{r.Value} {r.Key}"))})",
            By = DecidedBy.Rule,
        });
    }

    // ----- 4. The counts the summary shows, reconciled with the state -----

    private static void ReconcileCounts(SurveyContext context, RepositoryMap map, List<DocumentCard> documents, List<WorkItem> items, List<DecisionCard> decisions, Graph? graph, VerifyResult result)
    {
        void Count(string what, long a, string against, long b)
        {
            var holds = a == b;
            result.Counts.Add($"{what} {a} against {against} {b}: {(holds ? "the same" : "DIFFER")}");
        }

        var catalogued = context.Log.Records.LastOrDefault(r => r.Phase == ItemsPhase.PhaseName && r.Kind == "catalogue");
        if (catalogued?.Figures is { } figures)
        {
            Count("items in the state", items.Count, "the items phase's last count", (long)Math.Round(figures.Values.Sum()));
        }

        var ledgers = map.Files.Count(f => f.Kind is "open-ledger" or "finished-ledger");
        var expected = map.Files.Count(f => f.Category is "Document" or "TextUnknown" && f.Kind != "unreadable" && !OrientationPhase.IsNotADocument(context, f.Kind));
        Count("documents in the state", documents.Count(d => !d.SourceGone), "the map's document-like files less the unreadable and the not-documents", expected);
        Count("the map's files", map.Files.Count, "the input's files", context.Snapshot.Files.Count);
        Count("decisions by standing", decisions.Count(d => d.Standing is "draft" or "record" or "recorded" or "not-a-decision" or "unsure" or "merged"), "decisions", decisions.Count);
        if (graph is not null)
        {
            Count("item nodes", graph.Nodes.Count(n => n.Kind == "item"), "items", items.Count);
            Count("document nodes", graph.Nodes.Count(n => n.Kind == "document"), "documents", documents.Count);
            Count("decision nodes", graph.Nodes.Count(n => n.Kind == "decision"), "decisions that are decisions", decisions.Count(d => d.Standing is not ("not-a-decision" or "merged")));
        }

        var words = ItemCatalogue.LoadWords(context.State);
        var used = new HashSet<string>(items.Where(i => !string.IsNullOrWhiteSpace(i.StatusAsWritten)).Select(i => StatusVocabulary.WordOf(i.StatusAsWritten!.Trim())), StringComparer.OrdinalIgnoreCase);
        foreach (var word in words.Keys.Where(w => !used.Contains(StatusVocabulary.WordOf(w))).OrderBy(w => w, StringComparer.OrdinalIgnoreCase))
        {
            result.StaleWords.Add(word);
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "counts",
            Subject = "the summary's counts",
            Decision = string.Join("; ", result.Counts) + (result.StaleWords.Count == 0 ? "" : $"; {result.StaleWords.Count} status word(s) recorded by earlier runs that no row uses now, left out of the summary: {string.Join(", ", result.StaleWords.Take(12))}"),
            By = DecidedBy.Rule,
        });
    }

    // ----- 5. The judged samples -----

    private static async Task JudgeItemsAsync(SurveyContext context, JsonObject template, string salt, Conductor.Options.JudgeThresholds thresholds,
        List<(string Key, string Row, WorkItem Item)> rows, VerifyResult result, CancellationToken cancellationToken)
    {
        var take = context.Options.Budgets.VerifySampleItems;
        if (take <= 0 || rows.Count == 0)
        {
            return;
        }

        var ordered = rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var sample = Spread(ordered, take);
        var state = new JsonObject();
        var rowsNode = new JsonObject();
        var itemsNode = new JsonObject();
        var questions = new JsonObject();
        for (var i = 0; i < sample.Count; i++)
        {
            var n = "i" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var (_, row, item) = sample[i];
            rowsNode[n] = new JsonObject { ["row"] = CutCells(row, 700) };
            itemsNode[n] = new JsonObject
            {
                ["title"] = item.Title,
                ["status"] = item.Status + (item.Condition is null ? "" : " (" + Cut(item.Condition, 120) + ")"),
                ["status_as_written"] = item.StatusAsWritten ?? "",
            };
            var filled = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["row"] = $"rows.{n}", ["item"] = $"items.{n}", ["link"] = "" });
            questions[$"{n}_title"] = filled["title_matches"]?.DeepClone();
            questions[$"{n}_status"] = filled["status_matches"]?.DeepClone();
        }

        state["rows"] = rowsNode;
        state["items"] = itemsNode;
        var figures = await AskAsync(context, "sample/items#0", "items against their rows", state, questions, salt, cancellationToken).ConfigureAwait(false);
        result.Items.Asked = sample.Count;
        if (figures is null)
        {
            return;
        }

        result.Items.Judged = true;
        for (var i = 0; i < sample.Count; i++)
        {
            var n = "i" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var (key, _, item) = sample[i];
            foreach (var (what, id) in new[] { ("title", $"{n}_title"), ("status", $"{n}_status") })
            {
                var noul = figures.GetValueOrDefault(id, 0.5);
                if (noul >= thresholds.Yes)
                {
                    result.Items.Yes++;
                }
                else if (noul <= thresholds.No)
                {
                    result.Items.No++;
                    result.Items.Doubts.Add($"{key} (item {item.Number}): the {what} does not match its row ({noul:0.00})");
                }
                else
                {
                    result.Items.Unsure++;
                    result.Items.Doubts.Add($"{key} (item {item.Number}): the {what} unsure ({noul:0.00})");
                }
            }
        }
    }

    private static async Task JudgeLinksAsync(SurveyContext context, JsonObject template, string salt, Conductor.Options.JudgeThresholds thresholds,
        List<DocumentCard> documents, List<WorkItem> items, Graph? graph, VerifyResult result, CancellationToken cancellationToken)
    {
        var take = context.Options.Budgets.VerifySampleLinks;
        if (take <= 0 || graph is null)
        {
            return;
        }

        var titles = graph.Nodes.Where(n => n.Kind is "item" or "document").ToDictionary(n => n.Id, n => n.Title, StringComparer.Ordinal);
        var candidates = new List<(GraphEdge Edge, string Text, string Target)>();
        foreach (var edge in graph.Edges.Where(e => e.Kind == "references" && e.From.StartsWith("document:", StringComparison.Ordinal) && titles.ContainsKey(e.To)).OrderBy(e => e.Evidence, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
        {
            if (LineOf(context, edge.Evidence) is { } text)
            {
                candidates.Add((edge, text, titles[edge.To]));
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var sample = Spread(candidates, take);
        var state = new JsonObject();
        var linksNode = new JsonObject();
        var questions = new JsonObject();
        for (var i = 0; i < sample.Count; i++)
        {
            var n = "l" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var (edge, text, target) = sample[i];
            linksNode[n] = new JsonObject
            {
                ["text"] = Cut(text, 700),
                ["in"] = edge.From["document:".Length..],
                ["target"] = target,
                ["target_kind"] = edge.To.StartsWith("item:", StringComparison.Ordinal) ? "work item " + edge.To["item:".Length..] : "document " + edge.To["document:".Length..],
            };
            var filled = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["row"] = "", ["item"] = "", ["link"] = $"links.{n}" });
            questions[$"{n}_holds"] = filled["link_holds"]?.DeepClone();
        }

        state["links"] = linksNode;
        var figures = await AskAsync(context, "sample/links#0", "links against their evidence", state, questions, salt, cancellationToken).ConfigureAwait(false);
        result.Links.Asked = sample.Count;
        if (figures is null)
        {
            return;
        }

        result.Links.Judged = true;
        for (var i = 0; i < sample.Count; i++)
        {
            var n = "l" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var (edge, _, target) = sample[i];
            var noul = figures.GetValueOrDefault($"{n}_holds", 0.5);
            if (noul >= thresholds.Yes)
            {
                result.Links.Yes++;
            }
            else if (noul <= thresholds.No)
            {
                result.Links.No++;
                result.Links.Doubts.Add($"{edge.From} to {edge.To} ({Cut(target, 50)}, by the {edge.By}): the line does not refer to it ({noul:0.00})");
            }
            else
            {
                result.Links.Unsure++;
                result.Links.Doubts.Add($"{edge.From} to {edge.To} ({Cut(target, 50)}, by the {edge.By}): unsure ({noul:0.00})");
            }
        }
    }

    /// <summary>One judge request as a unit of work: answered from the state when its input is unchanged, else asked; null when the judge could not answer.</summary>
    private static async Task<Dictionary<string, double>?> AskAsync(SurveyContext context, string key, string purpose, JsonObject state, JsonObject questions, string salt, CancellationToken cancellationToken)
    {
        if (questions.Count == 0)
        {
            return null;
        }

        var hash = RunContext.HashOf("verify-v1", state.ToJsonString(), salt);
        var done = context.State.DoneUnit(PhaseName, key, hash);
        if (done?.Result is { } result)
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, double>>(result, RunContext.JsonOptions);
            }
            catch (JsonException)
            {
                // Asked again below.
            }
        }

        context.Touch($"verify: the judge on {purpose}");
        try
        {
            var reply = await context.Judge.AskAsync(PhaseName, purpose, state, questions, cancellationToken).ConfigureAwait(false);
            var answers = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (id, _) in questions)
            {
                answers[id] = reply.Reply.Noul(id);
            }

            context.State.CompleteUnit(PhaseName, context.RunId, key, hash, reply.Cost.Usd, answers);
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "sample",
                Subject = purpose,
                Decision = $"{questions.Count} question(s) over {state.Count} field(s): {string.Join(", ", answers.Select(a => $"{a.Key} {a.Value:0.00}"))}",
                By = DecidedBy.Judge,
                Figures = answers,
            });
            return answers;
        }
        catch (JudgeUnavailableException ex)
        {
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "sample",
                Subject = purpose,
                Decision = $"the judge could not read the sample: {ex.Reason}; it waits for the next run",
                By = DecidedBy.Rule,
                Flags = ["judge-failed"],
            });
            return null;
        }
    }

    /// <summary>The keys of the questions that offer options and whose default is not one of them (docs/bundle-format.md: a default is an option's value, never a sentence).</summary>
    internal static List<string> DefaultsThatAreNoOption(IEnumerable<Stomme.Conductor.Policies.Question> questions) =>
        questions.Where(q => !q.DefaultIsAnOption).Select(q => $"{q.Key} (\"{(q.DefaultIfUnanswered.Length > 60 ? q.DefaultIfUnanswered[..60] + "..." : q.DefaultIfUnanswered)}\")").ToList();

    private static void Findings(VerifyResult result)
    {
        void Finding(List<string> list, string what, int shown = 5)
        {
            if (list.Count > 0)
            {
                result.Findings.Add($"{list.Count} {what}: {string.Join(", ", list.Take(shown))}{(list.Count > shown ? ", ..." : "")}");
            }
        }

        Finding(result.EntriesWithoutItem, "line(s) of a ledger written as entries like its rows that no item stands on (the reader returned no row for them, or one that is not found there); they are listed, not imported", 12);
        Finding(result.LedgersUnread, "ledger(s) or chunk(s) of a ledger the reader returned no row for, though their lines hold entries; none is imported, and a question asks what to do", 12);
        Finding(result.SectionsWithoutRow, "section(s) of a ledger that hold list entries or table rows and no row; they are listed, not imported", 12);
        Finding(result.RowsMerged, "item(s) that hold more than one row (rows of one ledger taken into one item, or a row of an open ledger merged with one of a finished ledger)", 12);
        Finding(result.OpenInFinished, "row(s) of a finished ledger or section whose own words say they are not finished: each keeps its own state and a question asks which is right", 12);
        Finding(result.RowsNotWritten, "ledger(s) where the rows the reader returned and the items written from them differ");
        Finding(result.RowsNotReturned, "ledger(s) with rows the reader did not return this run, kept as they were read because their lines still stand in the file");
        Finding(result.DefaultsNotOptions, "question(s) whose default is not one of their options");
        Finding(result.Withheld, "file(s) not read because they look like they hold a secret (allow one by its path if it holds none)");
        Finding(result.LedgersMissing, "ledger(s) the guides name that are not in the input, so their rows are not in this import");
        Finding(result.WorkElsewhere, "place(s) outside the repository where it says its work is tracked, which this import did not read");
        Finding(result.FilesUnaccounted, "file(s) in no account");
        Finding(result.DocumentsMissing, "document(s) expected with no card");
        Finding(result.DocumentsConflicting, "card(s) for files the map left out");
        Finding(result.DocumentsSharingAFile, "pair(s) of documents that would share one file");
        Finding(result.RowsWithoutItem, "ledger row(s) that became no item");
        Finding(result.RowsWithTwoItems, "ledger row(s) that became two items or are written twice");
        Finding(result.ItemsWithoutRow, "item(s) from a ledger matching no row of it");
        Finding(result.RowsTextMissing, "row(s) whose cells are not all in the item's text");
        foreach (var count in result.Counts.Where(c => c.EndsWith("DIFFER", StringComparison.Ordinal)))
        {
            result.Findings.Add(count);
        }

        Finding(result.Items.Doubts.Where(d => !d.Contains("unsure", StringComparison.Ordinal)).ToList(), "sampled item(s) the judge read against their rows");
        Finding(result.Links.Doubts.Where(d => !d.Contains("unsure", StringComparison.Ordinal)).ToList(), "sampled link(s) the judge read against their evidence");
    }

    /// <summary>
    /// The source line an edge's evidence names ("path:line \"cited\""), or null when it names none. The reader's line
    /// numbers drift by a few on long documents, so when the evidence quotes what was cited, the nearest line within
    /// six that holds the quote is taken.
    /// </summary>
    private static string? LineOf(SurveyContext context, string evidence)
    {
        var head = evidence.Split(' ')[0];
        var colon = head.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(head[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line <= 0)
        {
            return null;
        }

        var file = context.File(head[..colon]);
        if (file is null)
        {
            return null;
        }

        var decoded = context.Decode(file);
        if (decoded is null || decoded.Lines.Count < line)
        {
            return null;
        }

        var quoteAt = evidence.IndexOf('"', StringComparison.Ordinal);
        var quote = quoteAt >= 0 ? evidence[(quoteAt + 1)..].TrimEnd('"') : "";
        var found = DocumentsPhase.LineByCode(decoded, quote, line) ?? line;
        return decoded.Lines[found - 1];
    }

    /// <summary>Up to <paramref name="take"/> entries spread evenly over the list, so the same sample is read on every run.</summary>
    private static List<T> Spread<T>(List<T> list, int take)
    {
        if (list.Count <= take)
        {
            return list;
        }

        var result = new List<T>(take);
        for (var i = 0; i < take; i++)
        {
            result.Add(list[(int)((long)i * list.Count / take)]);
        }

        return result;
    }

    /// <summary>A table row with every cell cut, so a long description does not push the status cell past the cut (Stomme's rows run to 41,000 characters).</summary>
    private static string CutCells(string row, int max)
    {
        var flat = row.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (!flat.StartsWith('|') || flat.Length <= max)
        {
            return Cut(flat, max);
        }

        var cells = flat.Trim('|').Split('|').Select(c => c.Trim()).ToList();
        var each = Math.Max(60, max / Math.Max(1, cells.Count));
        return "| " + string.Join(" | ", cells.Select(c => Cut(c, each))) + " |";
    }

    private static string Squeeze(string text) => string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static string Examples(List<string> list, string what) =>
        list.Count == 0 ? "" : $"{what}: {string.Join(", ", list.Take(10))}{(list.Count > 10 ? ", ..." : "")}. ";

    private static string Cut(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
