using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Options;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// Phase 1 (the plan): the rows of every ledger become work items. Each ledger is cut into chunks (ADR 0014)
/// and each chunk is one unit of work (ADR 0017): the reader turns the chunk's rows into the item-rows shape,
/// shown the fixed-shape summary of the chunks before it (ADR 0019). Code then merges the rows by normalised
/// id (ADR 0020 rule 1), maps every status word through the repository's vocabulary, the judge, the reader and
/// a person in that order of trust (ADR 0020 rule 12), numbers the items (ADR 0015), makes a placeholder for
/// every id the rows refer to that nothing defines, asks about work listed in prose (ADR 0020 rule 11), and
/// keeps the catalogue in the state with its provenance.
/// </summary>
public sealed partial class ItemsPhase(SurveyContext survey) : IPhase
{
    internal const string PhaseName = "items";

    /// <summary>The state record kind for a ledger set aside because it lies under an example, test or fixture folder (backlog TR-25); its key is the path.</summary>
    internal const string SkippedLedgerKind = "ledger-set-aside";

    public string Name => PhaseName;

    public Task RunAsync(CancellationToken cancellationToken) => RunAsync(survey, cancellationToken);

    private static async Task RunAsync(SurveyContext context, CancellationToken cancellationToken)
    {
        var map = context.Map ?? throw new InvalidOperationException("The items phase needs the repository map, and orientation has not run into this state.");
        var budgets = context.Options.Budgets;
        // A path is read as one ledger per run (the re-check, B1): a file the map lists as an open ledger and as a
        // finished one is one file with sections of both, read once, and each row's section says which it is.
        var ledgers = map.Ledgers.Where(l => l.Kind is "open-ledger" or "finished-ledger")
            .GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(l => l.Kind == "open-ledger") ?? g.First())
            .OrderBy(l => l.Kind == "open-ledger" ? 0 : 1).ThenBy(l => l.Path, StringComparer.Ordinal).ToList();
        ledgers = await SetAsideFixtureLedgersAsync(context, ledgers, cancellationToken).ConfigureAwait(false);
        if (ledgers.Count == 0)
        {
            DropItemsOfSetAsideLedgers(context, ItemCatalogue.Load(context.State));
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "ledgers", Subject = "the ledgers", Decision = "the map names no ledger of work items, so no items are made; a thin repository gets a code survey instead (ADR 0010)", By = DecidedBy.Rule });
            return;
        }

        var schemes = map.IdSchemes.Where(s => s.Confirmed).ToList();
        var itemScheme = schemes.FirstOrDefault(s => s.RefersTo == "work-item" && ledgers.Any(l => string.Equals(l.Path, s.WhereDefined, StringComparison.OrdinalIgnoreCase)))
            ?? schemes.FirstOrDefault(s => s.RefersTo == "work-item");
        var vocabulary = StatusVocabulary.Build(map, context.Answers, context.Playbook);
        var digest = context.State.LoadRecord("digest", "map") ?? MapDigest.Build(map, context.Shown, budgets.DigestChars);
        var statesText = StatusVocabulary.MappedStatesText(context.Playbook);
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "ledgers",
            Subject = "the ledgers",
            Decision = $"{ledgers.Count} ledger(s): {string.Join(", ", ledgers.Select(l => $"{l.Path} ({l.Kind})"))}; item ids follow {(itemScheme is null ? "no scheme, so numbers are assigned in source order (ADR 0015)" : $"the {itemScheme.Name} scheme, like {itemScheme.Example}")}; {vocabulary.Mappings.Count} status word(s) known from orientation",
            By = DecidedBy.Rule,
        });

        // 1. The rows of every chunk of every ledger: one unit each, in order, each shown the summary of the chunks before it.
        var chunks = new List<ChunkRows>();
        foreach (var ledger in ledgers)
        {
            var file = context.File(ledger.Path);
            var decoded = file is null ? null : context.Decode(file);
            if (file is null || decoded is null)
            {
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "ledgers", Subject = ledger.Path, Decision = "the ledger is not in the input or no decoder reads it; skipped", By = DecidedBy.Rule, Flags = ["missing"] });
                continue;
            }

            var pieces = Chunker.Chunk(decoded, budgets.ReaderChunkTokens, budgets.ChunkOverlapLines, budgets.ChunkOverlapChars);
            var rowsBefore = new List<RowSummary>();

            // A person who was told that the reader returned no row for this ledger, or for a chunk of it, may ask
            // for it to be read again (the question ledger-no-rows): a chunk that came back with nothing is then read
            // once more, with a word to the reader that the first reading returned nothing.
            var again = AnsweredNoRows(context, ledger.Path) == "read-again";
            foreach (var chunk in pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = $"rows/{ledger.Path}#{chunk.Index}";
                var hash = RunContext.HashOf("rows-v1", file.Sha256, chunk.Index.ToString(CultureInfo.InvariantCulture), chunk.Text, context.Playbook.Salt(["rules", "items-rows"], ["item-rows"], ["statuses"], []), vocabulary.Hash);
                var hashAgain = RunContext.HashOf(hash, "read-again");
                var stored = (again ? context.State.DoneUnit(PhaseName, key, hashAgain) : null) ?? context.State.DoneUnit(PhaseName, key, hash);
                RowsResult result;
                if (stored?.Result is not null && Deserialize<RowsResult>(stored.Result) is { } kept && !(again && stored.InputHash == hash && kept.Rows.Count == 0 && kept.ProseWork.Count == 0))
                {
                    result = kept;
                    context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "rows", Subject = $"{ledger.Path}, chunk {chunk.Index + 1} of {chunk.Count}", Decision = $"resumed from the state: {result.Rows.Count} row(s), read in run {stored.RunId}", By = DecidedBy.Rule });
                }
                else
                {
                    context.Touch($"items: {ledger.Path}, chunk {chunk.Index + 1} of {chunk.Count}");
                    result = await AskRowsAsync(context, ledger, decoded, chunk, rowsBefore, digest, vocabulary, statesText, again: false, cancellationToken).ConfigureAwait(false);
                    var readTwice = again && result.Rows.Count == 0 && result.ProseWork.Count == 0;
                    if (readTwice)
                    {
                        var first = result.Usd;
                        result = await AskRowsAsync(context, ledger, decoded, chunk, rowsBefore, digest, vocabulary, statesText, again: true, cancellationToken).ConfigureAwait(false);
                        result.Usd += first;
                        result.ReadAgain = true;
                    }

                    context.State.CompleteUnit(PhaseName, context.RunId, key, readTwice ? hashAgain : hash, result.Usd, result);
                }

                chunks.Add(new ChunkRows(ledger, file, chunk, result));
                rowsBefore.AddRange(result.Rows.Select(r => new RowSummary(r.Key, r.Title ?? "", r.StatusAsWritten)));
            }
        }

        // A row whose own lines have not changed keeps what was read of it (the re-check, B3): a re-read of its ledger,
        // because another row was edited, does not change its status, its kind or its relations.
        var placed = PlaceRows(context, chunks, ItemCatalogue.Load(context.State));
        KeepEarlierReadings(context, chunks, schemes, placed, RunContext.HashOf("readings-v1", context.Playbook.Salt(["rules", "items-rows"], ["item-rows"], ["statuses"], []), vocabulary.Hash));

        // A person's answer about a status word the map did not know applies before anything else is asked (ADR 0007).
        foreach (var word in chunks.SelectMany(c => c.Result.Rows).Select(r => r.StatusAsWritten).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => StatusVocabulary.WordOf(w!)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A person's answer beats every mapping, the rule's included, so a word with no open question can be corrected too.
            vocabulary.ApplyAnswer(word, context.Answers.Get("status:" + word));
        }

        // 2. The judge on rows whose status word no vocabulary knows (the plan: "is this row finished?" where the mapping is not plain).
        await CheckStatusesAsync(context, chunks, vocabulary, budgets.JudgeRowsPerRequest, cancellationToken).ConfigureAwait(false);
        await CheckShapesAsync(context, chunks, vocabulary, budgets.JudgeRowsPerRequest, cancellationToken).ConfigureAwait(false);
        KeepEarlierJudgements(context, chunks, placed);

        // 3. The catalogue, by code: merge, statuses, references, placeholders, prose lists, numbers, records.
        await BuildCatalogueAsync(context, chunks, ledgers, schemes, itemScheme, vocabulary, placed, cancellationToken).ConfigureAwait(false);
        context.Touch("items done");
    }

    private static async Task<RowsResult> AskRowsAsync(
        SurveyContext context, LedgerEntry ledger, DecodedDocument decoded, TextChunk chunk, IReadOnlyList<RowSummary> rowsBefore,
        string digest, StatusVocabulary vocabulary, string statesText, bool again, CancellationToken cancellationToken)
    {
        var summary = ChunkSummary.Build(ledger.Path, ledger.Kind, decoded, chunk, rowsBefore, context.Options.Budgets.ChunkSummaryChars);
        var system = context.SystemPrompt("items-rows", "item-rows", new Dictionary<string, string>
        {
            ["digest"] = digest,
            ["vocabulary"] = vocabulary.Text,
            ["statuses"] = statesText,
        });
        var parts = new List<ReaderPart>
        {
            new ReaderPart.Instruction(summary),
            new ReaderPart.InputText(ledger.Path, chunk.StartLine - chunk.OverlapLines, chunk.EndLine, chunk.Text) { Position = chunk.Position },
        };
        if (again)
        {
            parts.Insert(1, new ReaderPart.Instruction("An earlier reading of this text returned no row and no list of work in prose, and a person asked for it to be read again. Read it with care: every entry of work in it is a row. If it holds none, say why in the notes."));
        }

        var request = new ReaderRequest(PhaseName, $"the rows of {ledger.Path}, chunk {chunk.Index + 1} of {chunk.Count}", context.Run.Options.Models.For(PhaseName), system, parts)
        {
            GlossaryText = context.Glossary.Render(),
        };
        var reply = await context.Reader.AskAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Reply is null)
        {
            throw new ReaderException($"The rows of {ledger.Path} (chunk {chunk.Index + 1}) were discarded by the injection gate: {reply.DiscardReason}");
        }

        var parsed = OrientationPhase.Parse<RowsReply>(reply.Json) ?? new RowsReply();
        var rows = new List<RowReply>();
        var dropped = 0;
        foreach (var row in parsed.Rows ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Title) && string.IsNullOrWhiteSpace(row.Key))
            {
                dropped++;
                continue;
            }

            // A title is one line: a list entry wrapped over two lines in its file has one title, with single spaces, so the
            // generated heading of its file is one line and what follows it is the row's text and nothing else.
            row.Title = string.Join(' ', (string.IsNullOrWhiteSpace(row.Title) ? row.Key! : row.Title).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim('*').Trim();
            row.Detail = Cut(row.Detail ?? "", context.Options.Budgets.DetailChars);
            row.Ids ??= [];
            row.References ??= [];
            row.Dates ??= [];
            rows.Add(row);
        }

        var prose = parsed.ProseWork ?? [];

        // The glossary is judged with every call (ADR 0017); its own flag is in the gate's record and is not this chunk's.
        var flagged = reply.FlaggedParts.Where(p => p != "glossary").ToList();
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "rows",
            Subject = $"{ledger.Path}, chunk {chunk.Index + 1} of {chunk.Count}",
            Decision = $"{rows.Count} row(s) read{(dropped > 0 ? $", {dropped} with neither title nor id dropped" : "")}; {prose.Count} list(s) of work in prose{(parsed.Notes is { Count: > 0 } ? "; notes: " + string.Join(" ", parsed.Notes) : "")}",
            By = DecidedBy.Reader,
            Model = reply.Reply.AnsweredBy ?? reply.Reply.Model,
            Reasoning = parsed.Reasoning,
            Evidence = [new Evidence(ledger.Path, chunk.StartLine, null)],
            Call = reply.Reply.CallSeq,
            Flags = flagged.Count > 0 ? ["read-with-flagged-text"] : [],
        });
        return new RowsResult
        {
            Rows = rows,
            ProseWork = prose,
            Notes = parsed.Notes ?? [],
            AnsweredBy = reply.Reply.AnsweredBy ?? reply.Reply.Model,
            CallSeq = reply.Reply.CallSeq,
            Usd = reply.Reply.Cost.Usd,
            FlaggedParts = flagged,
            StartLine = chunk.StartLine,
            EndLine = chunk.EndLine,
        };
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase) { "bug", "feature", "task", "spike", "chore", "question", "epic", "decision", "documentation" };

    private static readonly HashSet<string> KnownRelations = new(StringComparer.OrdinalIgnoreCase) { "parent", "child", "blocker", "blocks", "related", "duplicate", "supersedes", "derived" };

    /// <summary>True for a row id that is only a number (1, 12, 9a, #3, 4.): its place in a list or a table, not an identity.</summary>
    internal static bool IsLocalNumber(string written) => LocalNumber().IsMatch(written);

    [GeneratedRegex(@"^#?\d{1,4}[a-zA-Z]?[.)]?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalNumber();

    /// <summary>The heading of the section a line sits in: the nearest heading at or above it, or null when the file has none there.</summary>
    internal static string? SectionOf(DecodedDocument? decoded, int line)
    {
        if (decoded is null)
        {
            return null;
        }

        Section? found = null;
        foreach (var section in decoded.Sections)
        {
            if (section.StartLine <= line && (found is null || section.StartLine >= found.StartLine))
            {
                found = section;
            }
        }

        return found?.Heading;
    }

    /// <summary>
    /// One unit per chunk that has rows with a kind or a relation from the reader: the judge reads the row's own words
    /// and says the kind (a choice) and, for each relation proposed, what the words say the link is (a choice, none
    /// among the options). The set items-shape has a salt of its own.
    /// </summary>
    private static async Task CheckShapesAsync(SurveyContext context, List<ChunkRows> chunks, StatusVocabulary vocabulary, int rowsPerRequest, CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            var shaped = chunk.Result.Rows.Where(r => !string.IsNullOrWhiteSpace(r.Type) || (r.Relations?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(EffectiveCondition(r, vocabulary))).ToList();
            if (shaped.Count == 0)
            {
                continue;
            }

            var key = $"shape-check/{chunk.Ledger.Path}#{chunk.Chunk.Index}";
            var hash = RunContext.HashOf("shape-check-v2", context.Playbook.Salt([], [], [], ["items-shape"]),
                string.Join("\u001e", shaped.Select(r => RowId(chunk, r) + "|" + r.Type + "|" + string.Join(",", (r.Relations ?? []).Select(x => x.Kind + ":" + x.Id + ":" + x.AsWritten)) + "|" + Cut(r.Title ?? "", 200) + "|" + Cut(r.Detail ?? "", 600) + "|" + (EffectiveCondition(r, vocabulary) ?? ""))));
            var stored = context.State.DoneUnit(PhaseName, key, hash);
            if (stored?.Result is not null && Deserialize<Dictionary<string, ShapeFigures>>(stored.Result) is { } kept)
            {
                chunk.Shapes = kept;
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "shape-check", Subject = $"{chunk.Ledger.Path}, chunk {chunk.Chunk.Index + 1}", Decision = $"resumed from the state: the judge's reading of the kind and relations of {kept.Count} row(s), from run {stored.RunId}", By = DecidedBy.Rule });
                continue;
            }

            context.Touch($"items: the judge on the kind and relations of {shaped.Count} row(s) of {chunk.Ledger.Path}");
            var figures = new Dictionary<string, ShapeFigures>(StringComparer.Ordinal);
            var usd = 0m;
            var template = context.Playbook.JudgeSet("items-shape");
            try
            {
                foreach (var batch in shaped.Chunk(Math.Max(1, rowsPerRequest)))
                {
                    var state = new JsonObject();
                    var rows = new JsonObject();
                    var questions = new JsonObject();
                    for (var i = 0; i < batch.Length; i++)
                    {
                        var field = "r" + (i + 1).ToString(CultureInfo.InvariantCulture);
                        var row = batch[i];
                        var relations = new JsonObject();
                        var list = row.Relations ?? [];
                        for (var k = 0; k < list.Count; k++)
                        {
                            var r = "k" + (k + 1).ToString(CultureInfo.InvariantCulture);
                            relations[r] = new JsonObject
                            {
                                ["item"] = row.Key,
                                ["id"] = list[k].Id,
                                ["as_written"] = Cut(list[k].AsWritten ?? "", 200),
                                ["reader_reading"] = list[k].Kind,
                            };
                            var filledRelation = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["field"] = $"rows.{field}", ["relation"] = $"rows.{field}.relations.{r}" });
                            questions[$"{field}_relation_{r}"] = filledRelation["relation"]?.DeepClone();
                        }

                        rows[field] = new JsonObject
                        {
                            ["key"] = row.Key,
                            ["title"] = row.Title,
                            ["detail"] = Cut(row.Detail ?? "", 600),
                            ["status_as_written"] = row.StatusAsWritten,
                            ["condition"] = EffectiveCondition(row, vocabulary),
                            ["as_written"] = row.TypeAsWritten,
                            ["reader_reading"] = row.Type,
                            ["relations"] = relations,
                        };
                        var filledRow = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["field"] = $"rows.{field}", ["relation"] = $"rows.{field}.relations" });
                        if (!string.IsNullOrWhiteSpace(row.Type))
                        {
                            questions[$"{field}_type"] = filledRow["type"]?.DeepClone();
                        }

                        if (!string.IsNullOrWhiteSpace(EffectiveCondition(row, vocabulary)))
                        {
                            questions[$"{field}_waits"] = filledRow["waits"]?.DeepClone();
                        }
                    }

                    state["rows"] = rows;
                    var result = await context.Judge.AskAsync(PhaseName, $"the kind and relations of {batch.Length} row(s) of {chunk.Ledger.Path}", state, questions, cancellationToken).ConfigureAwait(false);
                    usd += result.Cost.Usd;
                    for (var i = 0; i < batch.Length; i++)
                    {
                        var field = "r" + (i + 1).ToString(CultureInfo.InvariantCulture);
                        var shape = new ShapeFigures();
                        if (result.Reply.Answers.TryGetValue($"{field}_type", out var typeAnswer) && typeAnswer is ChoiceAnswer typed)
                        {
                            shape.Type = typed.Choice;
                            shape.TypeConfidence = typed.Probabilities.TryGetValue(typed.Choice, out var p) ? p : typed.Confidence ?? 0;
                            shape.TypeProbabilities = typed.Probabilities.ToDictionary(x => x.Key, x => Math.Round(x.Value, 3), StringComparer.Ordinal);
                        }

                        if (result.Reply.Answers.ContainsKey($"{field}_waits"))
                        {
                            shape.Waits = result.Reply.Noul($"{field}_waits");
                        }

                        var list = batch[i].Relations ?? [];
                        for (var k = 0; k < list.Count; k++)
                        {
                            var r = "k" + (k + 1).ToString(CultureInfo.InvariantCulture);
                            if (result.Reply.Answers.TryGetValue($"{field}_relation_{r}", out var answer) && answer is ChoiceAnswer chosen)
                            {
                                shape.Relations.Add(new RelationFigure { Id = list[k].Id ?? "", Kind = chosen.Choice, Confidence = chosen.Probabilities.TryGetValue(chosen.Choice, out var p) ? p : chosen.Confidence ?? 0 });
                            }
                        }

                        figures[RowId(chunk, batch[i])] = shape;
                    }
                }
            }
            catch (JudgeUnavailableException ex)
            {
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "shape-check", Subject = $"{chunk.Ledger.Path}, chunk {chunk.Chunk.Index + 1}", Decision = "the judge could not read the rows' kinds and relations: " + ex.Reason + "; the reader's reading stands, flagged, and the judge is asked again next run", By = DecidedBy.Rule, Flags = ["judge-failed"] });
                continue;
            }

            chunk.Shapes = figures;
            context.State.CompleteUnit(PhaseName, context.RunId, key, hash, usd, figures);
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "shape-check",
                Subject = $"{chunk.Ledger.Path}, chunk {chunk.Chunk.Index + 1}",
                Decision = $"the judge read the kind of {figures.Count(f => f.Value.Type is not null)} row(s) and {figures.Sum(f => f.Value.Relations.Count)} relation(s): {string.Join(", ", figures.Where(f => f.Value.Type is not null).GroupBy(f => f.Value.Type!).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}",
                By = DecidedBy.Judge,
                Model = context.Judge.Options.Model,
                Cost = new CallCost(0, 0, 0, 0, usd, false),
            });
        }
    }

    /// <summary>
    /// Whether the row's condition says the item waits: the judge's figure and who decided, the word list only when the
    /// judge gave no figure (it was unavailable), null when the row has no condition.
    /// </summary>
    private static (double? Waits, string? By) Waits(RowReply row, string? condition, ChunkRows chunk, JudgeThresholds thresholds)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return (null, null);
        }

        if (chunk.Shapes is not null && chunk.Shapes.TryGetValue(RowId(chunk, row), out var shape) && shape.Waits is { } figure)
        {
            return figure >= thresholds.Yes || figure <= thresholds.No ? (figure, DecidedBy.Judge) : (figure, null);
        }

        return IsWaitingCondition(condition) ? (1, DecidedBy.Rule) : (0, DecidedBy.Rule);
    }

    /// <summary>The condition the item will carry: the row's own, else the one its status word carries in the repository's vocabulary ("only with the owner").</summary>
    private static string? EffectiveCondition(RowReply row, StatusVocabulary vocabulary) =>
        !string.IsNullOrWhiteSpace(row.Condition)
            ? row.Condition
            : string.IsNullOrWhiteSpace(row.StatusAsWritten) ? null : vocabulary.Lookup(StatusVocabulary.WordOf(row.StatusAsWritten.Trim()))?.Condition;

    /// <summary>The state record kind of a row's reading: what the reader said of a row, kept while the row's own lines and its section do not change.</summary>
    internal const string ReadingKind = "row-reading";

    /// <summary>The state record kind of the judge's reading of a row (its status figures, its kind, its relations, its wait), kept as the reader's is.</summary>
    internal const string JudgementKind = "row-judgement";

    /// <summary>The state record kind of a table's title column, by the ledger and the table's header row: chosen once by the reader's titles and kept while the header stands.</summary>
    internal const string TitleColumnKind = "table-title-column";

    /// <summary>The state record kind of a ledger's counts in the last run: rows the reader returned, items written from them, rows kept because their lines still stand.</summary>
    internal const string RowsReturnedKind = "rows-returned";

    /// <summary>The state record kind of a ledger's final accounting, by code: every line written as an entry, against the items.</summary>
    internal const string AccountingKind = "ledger-accounting";

    /// <summary>
    /// The final accounting of one ledger (the third re-check): how many lines of it are written as entries like the
    /// rows found in it, how many of them an item stands on, which have none, and which rows were taken into one item.
    /// </summary>
    internal sealed class LedgerAccounting
    {
        public int Entries { get; set; }
        public int WithItem { get; set; }

        /// <summary>The entries no item stands on: the reader returned no row for them, or returned one that is not found on them.</summary>
        public List<AccountingEntry> WithoutItem { get; set; } = [];

        /// <summary>The items that hold more than one row of this ledger, each with its lines.</summary>
        public List<string> Merged { get; set; } = [];

        /// <summary>The sections that hold list entries or table rows, by the file alone, and no row at all.</summary>
        public List<AccountingSpan> SectionsWithoutRow { get; set; } = [];

        /// <summary>The chunks the reader returned no row for though their lines hold entries; one span with Chunk 0 when that is the whole ledger.</summary>
        public List<AccountingSpan> Unread { get; set; } = [];
    }

    /// <summary>A run of a ledger's lines that holds entries and no row: a section by its heading, or a chunk by its number.</summary>
    internal sealed class AccountingSpan
    {
        public string Heading { get; set; } = "";
        public int Chunk { get; set; }
        public int Chunks { get; set; }
        public int FirstLine { get; set; }
        public int LastLine { get; set; }
        public int Entries { get; set; }
        public bool ReadAgain { get; set; }
    }

    /// <summary>The key of the question about a ledger, or a chunk of one, that the reader returned no row for, before the ledger's path.</summary>
    internal const string NoRowsQuestion = "ledger-no-rows:";

    internal sealed class AccountingEntry
    {
        public int Line { get; set; }
        public string Text { get; set; } = "";
    }

    internal sealed class RowsReturned
    {
        public int Returned { get; set; }
        public int Written { get; set; }
        public int Kept { get; set; }
    }

    private sealed class RowJudgement
    {
        public StatusFigures? Status { get; set; }
        public ShapeFigures? Shape { get; set; }
    }

    /// <summary>Where the rows of a run begin in their files, by code (PlaceRows).</summary>
    private sealed class Placed
    {
        /// <summary>The lines of every ledger read, by path.</summary>
        public Dictionary<string, LedgerLines> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every line a row begins on, by path (0-based): the rows the reader returned, and the rows of earlier runs whose lines still stand.</summary>
        public Dictionary<string, HashSet<int>> Starts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per chunk, for each of its rows in order: the line it begins on (0-based), or -1 when it was not found.</summary>
        public Dictionary<ChunkRows, List<int>> RowStarts { get; } = [];

        /// <summary>The items of earlier runs whose raw lines still stand in their ledger on a line no row of this run was found on, by key.</summary>
        public Dictionary<string, (string Path, int Start)> KeptAt { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<ChunkRows, string?[]> ReadingKeys { get; } = [];
        public Dictionary<ChunkRows, bool[]> ReadingKept { get; } = [];
    }

    /// <summary>
    /// Finds every row the reader returned in its file, by code, before anything is made of it (the second re-check).
    /// The reader says which rows there are; where each one is, is the file's to say: a row is looked for forward from
    /// the row before it, on a line its title leads before a line that mentions it, and never on a line another row of
    /// the chunk was found on, so a short title does not land on an earlier row that names it and two rows never share
    /// a line. A row of an earlier run whose raw lines still stand in the ledger, on a line no row was found on, is a
    /// row too: the reader left it out, the file did not.
    /// </summary>
    private static Placed PlaceRows(SurveyContext context, List<ChunkRows> chunks, IReadOnlyList<WorkItem> earlier)
    {
        var placed = new Placed();
        foreach (var chunk in chunks)
        {
            var path = chunk.Ledger.Path;
            var starts = new List<int>();
            placed.RowStarts[chunk] = starts;
            var decoded = context.Decode(chunk.File);
            if (decoded is null)
            {
                starts.AddRange(Enumerable.Repeat(-1, chunk.Result.Rows.Count));
                continue;
            }

            if (!placed.Files.TryGetValue(path, out var file))
            {
                file = placed.Files[path] = new LedgerLines(decoded.Lines);
                placed.Starts[path] = [];
            }

            // The chunk's lines are the file's from the chunk's first line on; where they do not line up, the whole file is looked through.
            var chunkLines = chunk.Chunk.Text.Split('\n');
            var from = chunk.Chunk.StartLine - chunk.Chunk.OverlapLines - 1;
            var until = from + chunkLines.Length;
            var firstShown = Array.FindIndex(chunkLines, l => !string.IsNullOrWhiteSpace(l));
            if (from < 0 || until > file.Count + 1 || (firstShown >= 0 && (from + firstShown >= file.Count || LedgerLines.Plain(file.Line(from + firstShown)) != LedgerLines.Plain(chunkLines[firstShown]))))
            {
                from = 0;
                until = file.Count;
            }

            var used = new HashSet<int>();
            var after = from - 1;
            foreach (var row in chunk.Result.Rows)
            {
                var at = file.FindRow((row.Title ?? "").Trim(), after, from, until, used);
                starts.Add(at);
                if (at >= 0)
                {
                    used.Add(at);
                    placed.Starts[path].Add(at);
                    after = Math.Max(after, at);
                }
            }
        }

        foreach (var old in earlier.Where(i => !i.Placeholder && !i.SourceGone && !string.IsNullOrEmpty(i.RowRaw) && i.Sources.Count > 0))
        {
            var path = old.Sources[0].Path;
            if (!placed.Files.TryGetValue(path, out var file))
            {
                continue;
            }

            var wanted = (old.Sources[0].Line ?? 1) - 1;
            var taken = placed.KeptAt.Values.Where(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase)).Select(k => k.Start).ToHashSet();
            var stands = file.FindRaw(old.RowRaw!).Where(i => !placed.Starts[path].Contains(i) && !taken.Contains(i)).OrderBy(i => Math.Abs(i - wanted)).Select(i => (int?)i).FirstOrDefault();
            if (stands is { } index)
            {
                placed.KeptAt[old.Key] = (path, index);
            }
        }

        foreach (var (path, start) in placed.KeptAt.Values)
        {
            placed.Starts[path].Add(start);
        }

        return placed;
    }

    /// <summary>
    /// A reader that reads a ledger again words and judges its rows afresh, and an unchanged row would come back with
    /// another status or another kind (seen on a sample: one row edited, three others changed state). So what was read
    /// of a row is kept under the hash of its raw lines and its section, and a later reading of the same lines under
    /// the same section gives way to it. An edited row, a row that moved to another section, a changed prompt or a
    /// person's new answer about a status word is read anew, and so is everything when the phase is run again on request.
    /// </summary>
    private static void KeepEarlierReadings(SurveyContext context, List<ChunkRows> chunks, List<IdScheme> schemes, Placed placed, string salt)
    {
        var again = context.Run.Options.RerunPhases.Contains(PhaseName) || context.Run.Options.RerunPhases.Contains("*");
        var stored = context.State.Records(ReadingKind).ToDictionary(r => r.Key, r => r.Json, StringComparer.Ordinal);
        var prefix = salt[..Math.Min(12, salt.Length)] + "|";
        var live = new HashSet<string>(StringComparer.Ordinal);
        var kept = 0;
        var read = 0;
        foreach (var chunk in chunks)
        {
            var path = chunk.Ledger.Path;
            var keys = new string?[chunk.Result.Rows.Count];
            var keptHere = new bool[chunk.Result.Rows.Count];
            placed.ReadingKeys[chunk] = keys;
            placed.ReadingKept[chunk] = keptHere;
            if (!placed.Files.TryGetValue(path, out var file))
            {
                continue;
            }

            var decoded = context.Decode(chunk.File);
            var rowStarts = placed.RowStarts[chunk];
            for (var n = 0; n < chunk.Result.Rows.Count && n < rowStarts.Count; n++)
            {
                if (rowStarts[n] < 0)
                {
                    continue;
                }

                var row = chunk.Result.Rows[n];
                var block = file.Block(rowStarts[n], placed.Starts[path], schemes);
                var key = prefix + path + "|" + Sha256Of(string.Join(" > ", SectionPath(decoded, block.Line)) + "\n" + block.Raw);
                keys[n] = key;
                live.Add(key);
                if (!again && stored.TryGetValue(key, out var json) && Deserialize<RowReply>(json) is { } earlier)
                {
                    // The reading of an earlier run stands; only where the row sits now is this run's.
                    earlier.Evidence = row.Evidence;
                    chunk.Result.Rows[n] = earlier;
                    keptHere[n] = true;
                    kept++;
                }
                else
                {
                    var reading = JsonSerializer.Serialize(row, RunContext.JsonOptions);
                    context.State.SaveRecord(ReadingKind, key, reading, context.RunId);
                    stored[key] = reading;
                    read++;
                }
            }
        }

        // Readings of rows that are no longer there, under this prompt, go: a row that comes back is read anew.
        foreach (var key in stored.Keys.Where(k => !live.Contains(k) && placed.Files.Keys.Any(p => k.Contains("|" + p + "|", StringComparison.OrdinalIgnoreCase))).ToList())
        {
            context.State.DeleteRecord(ReadingKind, key);
        }

        if (kept > 0)
        {
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "rows",
                Subject = "the rows read before",
                Decision = $"{kept} row(s) whose own lines and section have not changed keep what an earlier run read of them (status, kind, relations), and what the judge read of them; {read} row(s) are new, edited or moved and carry this run's reading",
                By = DecidedBy.Rule,
                Figures = new Dictionary<string, double> { ["kept"] = kept, ["read anew"] = read },
            });
        }
    }

    /// <summary>
    /// A row that keeps the reader's reading keeps the judge's too (the second re-check): the judge is asked per chunk,
    /// so a chunk with one edited row is judged again whole, and the figures of its other rows would move a little
    /// each time. What the judge read of a row is kept under the same key as the reader's, and stands while that does.
    /// </summary>
    private static void KeepEarlierJudgements(SurveyContext context, List<ChunkRows> chunks, Placed placed)
    {
        var stored = context.State.Records(JudgementKind).ToDictionary(r => r.Key, r => r.Json, StringComparer.Ordinal);
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            if (!placed.ReadingKeys.TryGetValue(chunk, out var keys) || !placed.ReadingKept.TryGetValue(chunk, out var keptHere))
            {
                continue;
            }

            for (var n = 0; n < keys.Length && n < chunk.Result.Rows.Count; n++)
            {
                if (keys[n] is not { } key)
                {
                    continue;
                }

                live.Add(key);
                var id = RowId(chunk, chunk.Result.Rows[n]);
                if (keptHere[n] && stored.TryGetValue(key, out var json) && Deserialize<RowJudgement>(json) is { } earlier && (earlier.Status is not null || earlier.Shape is not null))
                {
                    if (earlier.Status is null)
                    {
                        chunk.Figures?.Remove(id);
                    }
                    else
                    {
                        (chunk.Figures ??= new Dictionary<string, StatusFigures>(StringComparer.Ordinal))[id] = earlier.Status;
                    }

                    if (earlier.Shape is null)
                    {
                        chunk.Shapes?.Remove(id);
                    }
                    else
                    {
                        (chunk.Shapes ??= new Dictionary<string, ShapeFigures>(StringComparer.Ordinal))[id] = earlier.Shape;
                    }

                    continue;
                }

                var now = new RowJudgement { Status = chunk.Figures?.GetValueOrDefault(id), Shape = chunk.Shapes?.GetValueOrDefault(id) };
                context.State.SaveRecord(JudgementKind, key, JsonSerializer.Serialize(now, RunContext.JsonOptions), context.RunId);
            }
        }

        foreach (var key in stored.Keys.Where(k => !live.Contains(k) && placed.Files.Keys.Any(p => k.Contains("|" + p + "|", StringComparison.OrdinalIgnoreCase))).ToList())
        {
            context.State.DeleteRecord(JudgementKind, key);
        }
    }

    /// <summary>What the file says of one row the reader returned (LocateRowsAsync).</summary>
    private sealed class Located
    {
        public string Path { get; init; } = "";

        /// <summary>The line of the file the row begins on (0-based), or -1 when its title is not found there.</summary>
        public int Position { get; set; } = -1;

        /// <summary>The line of the file the row begins on (1-based), by code; null when the row is not found.</summary>
        public int? Line { get; set; }

        public bool InOverlap { get; set; }
        public bool SeenBefore { get; set; }
        public LedgerRow? TableRow { get; set; }

        /// <summary>The id of a confirmed scheme the row's own line carries, normalised; null when it carries none.</summary>
        public string? Id { get; set; }

        /// <summary>True when the id stands in the bracket that ends the row's first line: it gives way to a row that opens with it.</summary>
        public bool IdAtEnd { get; set; }

        /// <summary>The id in the bracket that ends the row, when another row opens with it or holds it in its id cell: the bracket names that row, so it is a reference.</summary>
        public string? Mentions { get; set; }

        /// <summary>The heading that says what the row's section holds (finished or open work), when the file has both; null when no heading above the row says either.</summary>
        public string? SaidBy { get; set; }

        /// <summary>The title: by code from the row's own line, the reader's only when the row is not found.</summary>
        public string Title { get; set; } = "";

        /// <summary>The row's text by code, or null when the row is not found and the reader's rendering stands.</summary>
        public string? Text { get; set; }

        /// <summary>The row's first line as the file writes it, trimmed; null when the row is not found.</summary>
        public string? LineText { get; set; }

        /// <summary>The raw lines that belong to the row, up to the next row; null when the row is not found.</summary>
        public string? RowRaw { get; set; }

        public string? RowSha256 { get; set; }
        public string Key { get; set; } = "";
        public string LedgerKind { get; set; } = "open-ledger";
        public List<string> Sections { get; set; } = [];
        public bool InSeveralLedgers { get; set; }
    }

    /// <summary>
    /// The title column of every table that holds rows of this run, by what the reader's titles point at: each row
    /// votes for the cells its title is found in, and the column most rows agree on is the table's title column, in
    /// any language and whatever the columns are called. The choice is kept by the table's header row, so the keys do
    /// not turn on a later reading. A table with fewer than two votes for one column, or no clear lead, is left to
    /// the rule by code: the first cell that is no number of the row, no date and no status.
    /// </summary>
    private static Dictionary<string, int> TitleColumns(SurveyContext context, List<ChunkRows> chunks, Placed placed)
    {
        var stored = context.State.Records(TitleColumnKind).ToDictionary(r => r.Key, r => r.Json, StringComparer.Ordinal);
        var votes = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, (string Path, TableAround Table)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            var path = chunk.Ledger.Path;
            if (!placed.Files.TryGetValue(path, out var file) || !placed.RowStarts.TryGetValue(chunk, out var rowStarts))
            {
                continue;
            }

            for (var n = 0; n < chunk.Result.Rows.Count && n < rowStarts.Count; n++)
            {
                if (rowStarts[n] < 0 || file.TableAt(rowStarts[n]) is not { } table)
                {
                    continue;
                }

                var id = path + ":" + table.First.ToString(CultureInfo.InvariantCulture);
                tables[id] = (path, table);
                var title = LedgerLines.Needle(chunk.Result.Rows[n].Title);
                if (title.Length < 2)
                {
                    continue;
                }

                var counted = votes.TryGetValue(id, out var known) ? known : votes[id] = [];
                for (var c = 0; c < table.Cells.Count; c++)
                {
                    var cell = LedgerLines.Plain(table.Cells[c]);
                    if (cell.Length >= 2 && (cell.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(cell, StringComparison.OrdinalIgnoreCase)))
                    {
                        counted[c] = counted.GetValueOrDefault(c) + 1;
                    }
                }
            }
        }

        var chosen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, (path, table)) in tables)
        {
            var key = path + "|" + (table.HeaderLine.Length > 0 ? Sha256Of(LedgerLines.Plain(table.HeaderLine))[..16] : "no-header-" + table.Cells.Count.ToString(CultureInfo.InvariantCulture));
            if (stored.TryGetValue(key, out var json) && int.TryParse(json, NumberStyles.None, CultureInfo.InvariantCulture, out var kept))
            {
                chosen[id] = kept;
                continue;
            }

            var ordered = (votes.GetValueOrDefault(id) ?? []).OrderByDescending(v => v.Value).ThenBy(v => v.Key).ToList();
            if (ordered.Count > 0 && ordered[0].Value >= 2 && (ordered.Count == 1 || ordered[0].Value > ordered[1].Value))
            {
                chosen[id] = ordered[0].Key;
                context.State.SaveRecord(TitleColumnKind, key, ordered[0].Key.ToString(CultureInfo.InvariantCulture), context.RunId);
                stored[key] = ordered[0].Key.ToString(CultureInfo.InvariantCulture);
            }
        }

        return chosen;
    }

    /// <summary>
    /// Reads every row where it was found, by code: the title, the text, a hash of the raw lines that belong to it, the
    /// id when its own line carries one of a confirmed scheme, and the key. The lines of a ledger are split at every
    /// row that was found, so two rows of the reader's never share lines; a row that was not found keeps the reader's
    /// wording and says so. The same raw lines give the same key, title and text whatever the reader replies. A row
    /// whose section says finished, in a file with open and finished sections, is a finished ledger's row. An id that
    /// rows of several ledgers carry merges into one item only for one open and one finished ledger whose rows agree
    /// in title or text; otherwise every such row keeps an item of its own, keyed with its ledger's path whichever
    /// ledger is read first, and flagged.
    /// </summary>
    private static async Task<(Dictionary<RowReply, Located> Located, HashSet<string> AmbiguousIds)> LocateRowsAsync(
        SurveyContext context, List<ChunkRows> chunks, List<IdScheme> schemes, Dictionary<string, LedgerRow> rowByKey, HashSet<string> mixedPaths, Placed placed, CancellationToken cancellationToken)
    {
        var located = new Dictionary<RowReply, Located>(ReferenceEqualityComparer.Instance);

        // The table rows that carry an id of a scheme, by ledger and line: which row a line is, is the file's to say.
        var rowOnLine = new Dictionary<string, LedgerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ledgerAndId, tableRowOfLedger) in rowByKey)
        {
            rowOnLine.TryAdd(ledgerAndId[..ledgerAndId.IndexOf((char)10, StringComparison.Ordinal)] + ":" + tableRowOfLedger.Line.ToString(CultureInfo.InvariantCulture), tableRowOfLedger);
        }

        var titleColumns = TitleColumns(context, chunks, placed);
        foreach (var chunk in chunks)
        {
            var path = chunk.Ledger.Path;
            placed.Files.TryGetValue(path, out var file);
            var starts = placed.Starts.GetValueOrDefault(path) ?? [];
            var rowStarts = placed.RowStarts.GetValueOrDefault(chunk) ?? [];
            for (var n = 0; n < chunk.Result.Rows.Count; n++)
            {
                var row = chunk.Result.Rows[n];
                var start = file is not null && n < rowStarts.Count ? rowStarts[n] : -1;
                var info = new Located { Path = path, Title = (row.Title ?? "").Trim(), LedgerKind = chunk.Ledger.Kind, Position = start };
                located[row] = info;
                info.InOverlap = start >= 0 ? start + 1 < chunk.Chunk.StartLine : row.Evidence?.Line is { } quoted && quoted <= chunk.Chunk.OverlapLines;
                var readerId = IdNormaliser.Normalise(row.Key, schemes);

                // Which row of a table this is: the row on the line the title was found on, by code; the reader's own id
                // only finds a row whose title is not in the file as written.
                var tableRow = start >= 0
                    ? rowOnLine.TryGetValue(path + ":" + (start + 1).ToString(CultureInfo.InvariantCulture), out var onLine) && LedgerLines.Plain(onLine.Text) == LedgerLines.Plain(file!.Line(start)) ? onLine : null
                    : readerId is null ? null : rowByKey.GetValueOrDefault(path + (char)10 + readerId.Key);
                int? column = start >= 0 && file!.TableAt(start) is { } around && titleColumns.TryGetValue(path + ":" + around.First.ToString(CultureInfo.InvariantCulture), out var voted) ? voted : null;
                if (tableRow is not null && IdNormaliser.Normalise(tableRow.Id, schemes) is { } idOfRow)
                {
                    // A table row with an id of the scheme: the table read over the whole file gives its cells under their
                    // headers; its title is the lead of the table's title cell, by code.
                    var written = start >= 0 ? file!.Line(start) : tableRow.Text;
                    info.TableRow = tableRow;
                    info.Id = idOfRow.Key;
                    info.Line = start >= 0 ? start + 1 : tableRow.Line;
                    info.Text = tableRow.Body();
                    info.LineText = written.Trim();
                    info.RowRaw = written.TrimEnd();
                    info.RowSha256 = Sha256Of(info.RowRaw);

                    // The title cell: the column the table's rows agree on by the reader's titles; else the cell the table
                    // reader took, unless it holds only a date or a number; then the next cell that says something.
                    var cell = column is { } at && at < tableRow.Cells.Count && tableRow.Cells[at].Role != "id" && tableRow.Cells[at].Text.Length > 0
                        ? tableRow.Cells[at].Text
                        : tableRow.Cells.Where(c => c.Role != "id" && !LedgerLines.IsDateOrNumber(LedgerTable.Bare(c.Text)))
                            .OrderBy(c => c.Role == "title" ? 0 : c.Role == "status" ? 2 : 1).Select(c => c.Text).FirstOrDefault() ?? tableRow.Title;
                    var (cellTitle, _) = LedgerLines.TitleOf(cell.Length > 0 ? cell : tableRow.Title, [], cut: false);
                    info.Title = cellTitle.Length > 0 ? cellTitle : tableRow.Title;
                    continue;
                }

                if (start < 0)
                {
                    // Not found in the file as written: the reader's own word stands, and the row says so.
                    info.Id = readerId?.Key;
                    continue;
                }

                var block = file!.Block(start, starts, schemes, column);
                info.Line = block.Line;
                info.Title = block.Title.Length > 0 ? block.Title : info.Title;
                info.Text = block.Text.Length > 0 ? block.Text : null;
                info.LineText = file.Line(start).Trim();
                info.RowRaw = block.Raw;
                info.RowSha256 = block.RowSha256;

                // The row's own id is the one its line opens with, the one alone in its last bracket, or its id cell: by
                // code. An id the row only mentions is a reference, whatever the reader made of it.
                info.Id = block.Id;
                info.IdAtEnd = block.Id is not null && block.IdAtEnd;
            }
        }

        // An id in the bracket that ends a row gives way to any row that opens with that id or holds it in its id
        // cell, in any ledger, whether the reader returned that row or not (the third re-check): the bracket then
        // names the other row, and this row is keyed by its own title.
        var idsAtEnd = located.Values.Where(i => i.Id is not null && i.IdAtEnd).ToList();
        if (idsAtEnd.Count > 0)
        {
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ledgerAndId in rowByKey.Keys)
            {
                owned.Add(ledgerAndId[(ledgerAndId.IndexOf((char)10, StringComparison.Ordinal) + 1)..]);
            }

            foreach (var info in located.Values.Where(i => i.Id is not null && !i.IdAtEnd))
            {
                owned.Add(info.Id!);
            }

            foreach (var file in placed.Files.Values)
            {
                for (var i = 0; i < file.Count; i++)
                {
                    if (file.LeadIdAt(i, schemes) is { } lead)
                    {
                        owned.Add(lead);
                    }
                }
            }

            foreach (var info in idsAtEnd.Where(i => owned.Contains(i.Id!)))
            {
                info.Mentions = info.Id;
                info.Id = null;
                info.IdAtEnd = false;
                context.Log.Record(new DecisionRecord
                {
                    Phase = PhaseName,
                    Kind = "item",
                    Subject = $"{info.Path}:{info.Line}",
                    Decision = $"the row ends with ({info.Mentions}) and another row opens with that id or holds it in its id cell: the bracket names that row, so this row is keyed by its own title and refers to {info.Mentions}",
                    By = DecidedBy.Rule,
                    Evidence = [new Evidence(info.Path, info.Line, null)],
                    Flags = ["id-at-end-gives-way"],
                });
            }
        }

        // A file with open and finished sections: the nearest heading above the row that says which it holds decides,
        // read by the judge once per heading; a heading that says neither (a date, a topic) leaves it to the one above.
        if (mixedPaths.Count > 0)
        {
            foreach (var chunk in chunks.Where(c => mixedPaths.Contains(c.Ledger.Path)))
            {
                var decoded = context.Decode(chunk.File);
                foreach (var row in chunk.Result.Rows)
                {
                    var info = located[row];
                    info.Sections = info.Line is { } line ? SectionPath(decoded, line) : [];
                }
            }

            var (finished, open) = await HeadingsAsync(context, located.Values.SelectMany(i => i.Sections), cancellationToken).ConfigureAwait(false);
            foreach (var info in located.Values.Where(i => mixedPaths.Contains(i.Path)))
            {
                info.SaidBy = info.Sections.AsEnumerable().Reverse().FirstOrDefault(h => open.Contains(h) || finished.Contains(h));
                info.LedgerKind = info.SaidBy is { } heading && !open.Contains(heading) ? "finished-ledger" : "open-ledger";
            }
        }

        // The keys. A row without an id: its ledger's path and the slug of its title by code.
        foreach (var info in located.Values.Where(i => i.Id is null))
        {
            var slug = ItemCatalogue.Slug(info.Title);
            info.Key = $"{info.Path}#{(info.Title.Length > 0 && slug != "item" ? slug : "row-" + ContentHash(info.Title + "|" + (info.Text ?? "")))}";
        }

        // A row with an id: the id, unless rows of several ledgers carry it and are not one row that moved.
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in located.Values.Where(i => i.Id is not null).GroupBy(i => i.Id!, StringComparer.OrdinalIgnoreCase))
        {
            var ledgers = group.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            var oneRowMoved = ledgers.Count == 2
                && ledgers.All(l => l.Select(i => i.LedgerKind).Distinct(StringComparer.Ordinal).Count() == 1 && l.Select(i => i.Line ?? -1).Distinct().Count() == 1)
                && ledgers[0].First().LedgerKind != ledgers[1].First().LedgerKind
                && Agree(ledgers[0].First(), ledgers[1].First());
            if (ledgers.Count == 1 || oneRowMoved)
            {
                foreach (var info in group)
                {
                    info.Key = group.Key;
                }

                continue;
            }

            ambiguous.Add(group.Key);
            foreach (var info in group)
            {
                info.Key = $"{info.Path}#{group.Key}";
                info.InSeveralLedgers = true;
            }

            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "item",
                Subject = group.Key,
                Decision = $"the id is carried by rows of {ledgers.Count} ledgers ({string.Join(", ", ledgers.Select(l => $"{l.Key} ({l.First().LedgerKind})"))}) that are not one row moved from an open ledger to a finished one with the same title or text: every row keeps an item of its own, keyed with its ledger's path, and a reference to the id names none of them",
                By = DecidedBy.Rule,
                Evidence = group.Select(i => new Evidence(i.Path, i.Line, null)).ToList(),
                Flags = ["id-in-several-ledgers"],
            });
        }

        var notFound = located.Values.Count(i => i.Line is null);
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "rows-located",
            Subject = "the rows in their files",
            Decision = $"{located.Count - notFound} of {located.Count} row(s) found in their files by code and read there (title, text and key from the file's own lines); {notFound} not found as written, kept by the reader's wording and flagged; {ambiguous.Count} id(s) carried by several ledgers and kept apart",
            By = DecidedBy.Rule,
            Figures = new Dictionary<string, double> { ["rows"] = located.Count, ["not found"] = notFound, ["ids in several ledgers"] = ambiguous.Count },
        });
        return (located, ambiguous);
    }

    private static string Sha256Of(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// True when two rows that carry one id read as the same row: the same title by its letters, the same text, or
    /// titles that share most of their words. A title that only begins with the other's ("Build the login page" and
    /// "Build the login page follow-up") is another row named after the first, not the same row (the third re-check).
    /// </summary>
    private static bool Agree(Located one, Located other)
    {
        var a = ItemCatalogue.Slug(one.Title);
        var b = ItemCatalogue.Slug(other.Title);
        if (a.Length > 0 && a != "item" && a == b)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(one.Text) && LedgerLines.Plain(one.Text) == LedgerLines.Plain(other.Text ?? ""))
        {
            return true;
        }

        if (a.StartsWith(b + "-", StringComparison.Ordinal) || b.StartsWith(a + "-", StringComparison.Ordinal))
        {
            return false;
        }

        var wordsA = a.Split('-', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet(StringComparer.Ordinal);
        var wordsB = b.Split('-', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet(StringComparer.Ordinal);
        var union = wordsA.Union(wordsB).Count();
        return union > 0 && wordsA.Intersect(wordsB).Count() / (double)union >= 0.6;
    }

    /// <summary>The headings a line sits under, from the outermost to the nearest.</summary>
    internal static List<string> SectionPath(DecodedDocument? decoded, int line)
    {
        var path = new List<(int Level, string Heading)>();
        foreach (var section in (decoded?.Sections ?? []).Where(s => s.StartLine <= line).OrderBy(s => s.StartLine))
        {
            path.RemoveAll(p => p.Level >= section.Level);
            path.Add((section.Level, section.Heading));
        }

        return path.Select(p => p.Heading).ToList();
    }

    /// <summary>
    /// The headings that say the entries under them are finished work, and the headings that say they are still to
    /// do, in whatever language: the judge reads each heading once, with one question for each (the set
    /// items-sections, with a salt of its own). A heading that says neither (a date, a topic, a name) is in neither
    /// set, and one the judge could not read is in neither, so nothing is closed on a guess.
    /// </summary>
    private static async Task<(HashSet<string> Finished, HashSet<string> Open)> HeadingsAsync(SurveyContext context, IEnumerable<string> headings, CancellationToken cancellationToken)
    {
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var open = new HashSet<string>(StringComparer.Ordinal);
        var thresholds = context.Run.Options.Thresholds;
        var salt = context.Playbook.Salt([], [], [], ["items-sections"]);
        var figures = new Dictionary<string, double>(StringComparer.Ordinal);
        var asked = 0;
        foreach (var heading in headings.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = RunContext.HashOf("section-v2", heading, salt);
            var unit = "section/" + hash[..Math.Min(16, hash.Length)];
            SectionCheck check;
            if (context.State.DoneUnit(PhaseName, unit, hash)?.Result is { } stored && Deserialize<SectionCheck>(stored) is { } known)
            {
                check = known;
            }
            else
            {
                try
                {
                    var result = await context.Judge.AskAsync(PhaseName, "does a heading of a ledger say the entries under it are finished, or still to do", new JsonObject { ["heading"] = heading }, JudgeQuestions.Fill(context.Playbook.JudgeSet("items-sections")), cancellationToken).ConfigureAwait(false);
                    check = new SectionCheck { HoldsFinished = result.Reply.Noul("holds_finished"), HoldsOpen = result.Reply.Noul("holds_open") };
                    context.State.CompleteUnit(PhaseName, context.RunId, unit, hash, result.Cost.Usd, check);
                }
                catch (JudgeUnavailableException ex)
                {
                    context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "sections", Subject = Cut(heading, 80), Decision = "the judge gave no answer (" + ex.Reason + "); the heading says nothing of its entries in this run and is read again next run", By = DecidedBy.Rule, Flags = ["judge-unavailable"] });
                    continue;
                }
            }

            asked++;
            figures[Cut(heading, 50) + ", finished"] = Math.Round(check.HoldsFinished, 3);
            figures[Cut(heading, 50) + ", open"] = Math.Round(check.HoldsOpen, 3);

            // A heading that says both ("Done, with open follow-ups") closes nothing: open stands.
            if (check.HoldsOpen >= thresholds.Yes)
            {
                open.Add(heading);
            }
            else if (check.HoldsFinished >= thresholds.Yes)
            {
                finished.Add(heading);
            }
        }

        if (asked > 0)
        {
            static string Named(HashSet<string> set) => set.Count == 0 ? "none" : string.Join(", ", set.Select(h => "\"" + Cut(h, 40) + "\""));
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "sections",
                Subject = "the sections of a ledger with open and finished work",
                Decision = $"of {asked} heading(s), {finished.Count} say the entries under them are finished ({Named(finished)}) and {open.Count} say they are still to do ({Named(open)}); the nearest of these above a row decides which ledger's row it is, and a row under none is an open ledger's",
                By = DecidedBy.Judge,
                Model = context.Judge.Options.Model,
                Figures = figures,
            });
        }

        return (finished, open);
    }

    /// <summary>
    /// Whether the words a row of a finished ledger says of its own state say that its work is not finished. By code
    /// where code can tell: an empty box, or a word a person mapped to a state. Else the judge reads the words once
    /// (the set items-against-ledger, with a salt of its own), knowing the row stands among finished work: a word the
    /// engine's table maps for an open ledger (ACCEPTED as backlog) may tell how the work ended in a finished one. True: the words say unfinished. False: they tell how the work ended. Null: the judge was
    /// unsure or gave no answer, and the row is treated as one that says unfinished, so nothing is closed on a guess.
    /// </summary>
    private static async Task<(bool? Unfinished, string Why)> SaysUnfinishedAsync(SurveyContext context, WorkItem item, StatusVocabulary vocabulary, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.StatusAsWritten))
        {
            // No word and no box, and the reader read the row's text as open work: the judge has no words to read,
            // so the reader's reading stands as the row's own until a person says (the fourth re-check, D01).
            return (true, $"no status word; the reader read the row as {item.Status}");
        }

        var words = item.StatusAsWritten!.Trim();
        if (EmptyBox().IsMatch(words))
        {
            return (true, "an empty box, by rule");
        }

        var word = StatusVocabulary.WordOf(words);
        if (vocabulary.Lookup(word) is { } known && known.By == DecidedBy.Person && !StatusVocabulary.IsQualified(words, word))
        {
            return (known.Mapped is not ("done" or "cancelled"), $"the word {word} means {known.Mapped}, {Who(known.By)}");
        }

        var thresholds = context.Run.Options.Thresholds;
        var salt = context.Playbook.Salt([], [], [], ["items-against-ledger"]);
        var hash = RunContext.HashOf("against-ledger-v1", item.Title, words, salt);
        var unit = "against-ledger/" + hash[..Math.Min(16, hash.Length)];
        double figure;
        if (context.State.DoneUnit(PhaseName, unit, hash)?.Result is { } stored && Deserialize<AgainstLedgerCheck>(stored) is { } check)
        {
            figure = check.SaysUnfinished;
        }
        else
        {
            try
            {
                var result = await context.Judge.AskAsync(PhaseName, "do the words of a row among finished work say it is not finished", new JsonObject { ["title"] = Cut(item.Title, 200), ["status"] = words }, JudgeQuestions.Fill(context.Playbook.JudgeSet("items-against-ledger")), cancellationToken).ConfigureAwait(false);
                figure = result.Reply.Noul("says_unfinished");
                context.State.CompleteUnit(PhaseName, context.RunId, unit, hash, result.Cost.Usd, new AgainstLedgerCheck { SaysUnfinished = figure });
            }
            catch (JudgeUnavailableException ex)
            {
                return (null, "the judge gave no answer: " + ex.Reason);
            }
        }

        var read = $"the judge read them at {figure:0.00}";
        return figure >= thresholds.Yes ? (true, read) : figure <= thresholds.No ? (false, read) : (null, read + ", between its lines");
    }

    [GeneratedRegex(@"^\[\s?\]$", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyBox();

    private sealed class AgainstLedgerCheck
    {
        public double SaysUnfinished { get; set; }
    }

    /// <summary>
    /// What a person answered to the question about a ledger the reader returned no row for: the option they chose,
    /// whether it came alone or with their own words beside it (a consumer's dialog allows both); null when the
    /// question has no answer that names an option.
    /// </summary>
    private static string? AnsweredNoRows(SurveyContext context, string path)
    {
        var chosen = (context.Answers.GetOption(NoRowsQuestion + path) ?? context.Answers.Get(NoRowsQuestion + path) ?? "").Trim();
        return new[] { "read-again", "no-work", "leave" }.FirstOrDefault(o => chosen.StartsWith(o, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SectionCheck
    {
        public double HoldsFinished { get; set; }
        public double HoldsOpen { get; set; }
    }

    /// <summary>Six hex characters of SHA-256 over a row's own text: a key's suffix for a row that would otherwise share a key, the same on every run while the row is unchanged.</summary>
    private static string ContentHash(string rowText) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rowText)))[..6];

    /// <summary>Items of earlier runs whose ledger is set aside now (an examples folder) go: they were never the repository's own work (change 4 of the verdict).</summary>
    /// <summary>
    /// The ledgers whose rows are not items now: those set aside under a folder of examples, tests or fixtures, and
    /// those that are in the input and not read (a file that looks like it holds a secret, a file the person
    /// excluded, a backup copy). Items an earlier run made of them leave, with their text.
    /// </summary>
    private static HashSet<string> NotThisRepositorysRows(SurveyContext context)
    {
        var paths = new HashSet<string>(context.State.Records(SkippedLedgerKind).Select(r => r.Key), StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(context.Snapshot.Files.Where(f => f.NotRead is not null).Select(f => f.Path));
        return paths;
    }

    private static void DropItemsOfSetAsideLedgers(SurveyContext context, IReadOnlyList<WorkItem> earlier)
    {
        var setAside = NotThisRepositorysRows(context);
        if (setAside.Count == 0)
        {
            return;
        }

        var dropped = 0;
        foreach (var old in earlier.Where(i => setAside.Contains(i.Sources.FirstOrDefault()?.Path ?? "")))
        {
            context.State.DeleteRecord(ItemCatalogue.ItemKind, ItemCatalogue.RecordKey(old.Number));
            context.State.DeleteRecord(ItemCatalogue.NumberKind, old.Key);
            dropped++;
        }

        if (dropped > 0)
        {
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = $"{dropped} item(s) of set-aside ledgers", Decision = "dropped: their ledger is set aside (it lies under an examples, tests or fixtures folder) or is not read now (it looks like it holds a secret, a person excluded it, or it is a backup copy), so its rows are not items", By = DecidedBy.Rule, Flags = ["set-aside"] });
        }
    }

    /// <summary>The row's kind and relations as the item carries them: the judge's reading where it is sure, else the reader's, flagged; a relation the judge read as none is dropped.</summary>
    private static (string? Type, string? TypeAsWritten, string? TypeBy, double? TypeConfidence, List<ItemRelation> Relations, List<string> Flags) Shape(RowReply row, ChunkRows chunk, JudgeThresholds thresholds)
    {
        var flags = new List<string>();
        var judged = chunk.Shapes is not null && chunk.Shapes.TryGetValue(RowId(chunk, row), out var j) ? j : null;

        string? type = null;
        string? typeBy = null;
        double? typeConfidence = null;
        var readerType = string.IsNullOrWhiteSpace(row.Type) ? null : row.Type.Trim().ToLowerInvariant();
        if (judged?.Type is { } judgedType && judged.TypeConfidence >= thresholds.Yes)
        {
            type = judgedType == "other" ? null : judgedType;
            typeBy = DecidedBy.Judge;
            typeConfidence = judged.TypeConfidence;
            if (readerType is not null && !string.Equals(readerType, judgedType, StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("type-corrected-by-judge");
            }
        }
        else if (readerType is not null && KnownTypes.Contains(readerType))
        {
            type = readerType;
            typeBy = DecidedBy.Reader;
            typeConfidence = row.TypeConfidence;
            flags.Add(judged?.Type is null ? "type-by-reader" : "type-unsure");
        }
        else if (readerType is not null)
        {
            // A kind outside the list: kept as written, by the reader, for a person.
            type = readerType;
            typeBy = DecidedBy.Reader;
            typeConfidence = row.TypeConfidence;
            flags.Add("type-outside-list");
        }

        var relations = new List<ItemRelation>();
        foreach (var proposed in row.Relations ?? [])
        {
            if (string.IsNullOrWhiteSpace(proposed.Id) || string.IsNullOrWhiteSpace(proposed.Kind))
            {
                continue;
            }

            var readerKind = proposed.Kind.Trim().ToLowerInvariant();
            var figure = judged?.Relations.FirstOrDefault(r => string.Equals(r.Id, proposed.Id, StringComparison.OrdinalIgnoreCase));
            var relation = new ItemRelation { Kind = readerKind, Text = proposed.Id.Trim(), AsWritten = proposed.AsWritten, By = DecidedBy.Reader, Confidence = proposed.Confidence };
            if (figure is not null && figure.Confidence >= thresholds.Yes)
            {
                if (figure.Kind == "none")
                {
                    flags.Add("relation-dropped-by-judge");
                    continue;
                }

                relation.Kind = figure.Kind;
                relation.By = DecidedBy.Judge;
                relation.Confidence = figure.Confidence;
                if (!string.Equals(figure.Kind, readerKind, StringComparison.OrdinalIgnoreCase))
                {
                    relation.Flags.Add("kind-corrected-by-judge");
                }
            }
            else if (!KnownRelations.Contains(readerKind))
            {
                continue;
            }
            else
            {
                relation.Flags.Add(figure is null ? "relation-by-reader" : "relation-unsure");
            }

            // Parent and child mean containment and nothing else (change 11 of the verdict's redo): a parent the judge did
            // not confirm is kept as a plain relation, so no hierarchy is built on a reading of "split from" or "residue of".
            if (relation.Kind is "parent" or "child" && relation.By != DecidedBy.Judge)
            {
                relation.Kind = "related";
                relation.Flags.Add("parent-unsure");
            }

            relations.Add(relation);
        }

        return (type, string.IsNullOrWhiteSpace(row.TypeAsWritten) ? null : row.TypeAsWritten.Trim(), typeBy, typeConfidence, relations, flags);
    }

    /// <summary>One unit per chunk that has rows with a status word no vocabulary maps: the judge says finished or cancelled, batched (ADR 0013's thresholds).</summary>
    private static async Task CheckStatusesAsync(SurveyContext context, List<ChunkRows> chunks, StatusVocabulary vocabulary, int rowsPerRequest, CancellationToken cancellationToken)
    {
        var thresholds = context.Run.Options.Thresholds;
        foreach (var chunk in chunks)
        {
            var unknown = chunk.Result.Rows.Where(r => !string.IsNullOrWhiteSpace(r.StatusAsWritten) && NeedsTheJudge(r.StatusAsWritten!, vocabulary)).ToList();
            if (unknown.Count == 0)
            {
                continue;
            }

            var key = $"status-check/{chunk.Ledger.Path}#{chunk.Chunk.Index}";
            var hash = RunContext.HashOf("status-check-v2", context.Playbook.Salt([], [], [], ["items"]), string.Join("\u001e", unknown.Select(r => RowId(chunk, r) + "|" + r.StatusAsWritten + "|" + Cut(r.Detail ?? "", 600))));
            var stored = context.State.DoneUnit(PhaseName, key, hash);
            if (stored?.Result is not null && Deserialize<Dictionary<string, StatusFigures>>(stored.Result) is { } kept)
            {
                chunk.Figures = kept;
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "status-check", Subject = $"{chunk.Ledger.Path}, chunk {chunk.Chunk.Index + 1}", Decision = $"resumed from the state: the judge's figures for {kept.Count} row(s), from run {stored.RunId}", By = DecidedBy.Rule });
                continue;
            }

            context.Touch($"items: the judge on {unknown.Count} row(s) of {chunk.Ledger.Path}");
            var figures = new Dictionary<string, StatusFigures>(StringComparer.Ordinal);
            var usd = 0m;
            var template = context.Playbook.JudgeSet("items");
            foreach (var batch in unknown.Chunk(Math.Max(1, rowsPerRequest)))
            {
                var state = new JsonObject();
                var rows = new JsonObject();
                var questions = new JsonObject();
                for (var i = 0; i < batch.Length; i++)
                {
                    var field = "r" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    rows[field] = new JsonObject
                    {
                        ["key"] = batch[i].Key,
                        ["title"] = batch[i].Title,
                        ["status_as_written"] = batch[i].StatusAsWritten,
                        ["detail"] = Cut(batch[i].Detail ?? "", 600),
                    };
                    var filled = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["field"] = $"rows.{field}" });
                    foreach (var (id, question) in filled.ToList())
                    {
                        questions[$"{field}_{id}"] = question?.DeepClone();
                    }
                }

                state["rows"] = rows;
                var result = await context.Judge.AskAsync(PhaseName, $"the status of {batch.Length} row(s) of {chunk.Ledger.Path}", state, questions, cancellationToken).ConfigureAwait(false);
                usd += result.Cost.Usd;
                for (var i = 0; i < batch.Length; i++)
                {
                    var field = "r" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    figures[RowId(chunk, batch[i])] = new StatusFigures
                    {
                        Finished = result.Reply.Noul($"{field}_is_finished"),
                        Cancelled = result.Reply.Noul($"{field}_is_cancelled"),
                    };
                }
            }

            chunk.Figures = figures;
            context.State.CompleteUnit(PhaseName, context.RunId, key, hash, usd, figures);
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "status-check",
                Subject = $"{chunk.Ledger.Path}, chunk {chunk.Chunk.Index + 1}",
                Decision = $"the judge read {unknown.Count} row(s) whose status word no vocabulary maps: {figures.Count(f => f.Value.Finished >= thresholds.Yes)} finished, {figures.Count(f => f.Value.Cancelled >= thresholds.Yes)} cancelled, {figures.Count(f => f.Value.Finished > thresholds.No && f.Value.Finished < thresholds.Yes)} unsure",
                By = DecidedBy.Judge,
                Model = context.Judge.Options.Model,
                Figures = figures.ToDictionary(f => f.Key + " finished", f => Math.Round(f.Value.Finished, 3)),
            });
        }
    }

    private static async Task BuildCatalogueAsync(
        SurveyContext context, List<ChunkRows> chunks, List<LedgerEntry> ledgers, List<IdScheme> schemes, IdScheme? itemScheme,
        StatusVocabulary vocabulary, Placed placed, CancellationToken cancellationToken)
    {
        var thresholds = context.Run.Options.Thresholds;
        var items = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var words = new Dictionary<string, WordUse>(StringComparer.OrdinalIgnoreCase);
        var keyless = 0;
        var inTwoLedgers = 0;

        // The rows of every ledger over its whole text, by ledger and key (two ledgers may each carry an id for a row of
        // their own): the body of each item is its row's own text, with the
        // table's headers, which a chunk that starts inside the table does not carry.
        var rowByKey = new Dictionary<string, LedgerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in ledgers)
        {
            var file = context.File(ledger.Path);
            var decoded = file is null ? null : context.Decode(file);
            if (decoded is null)
            {
                continue;
            }

            foreach (var row in LedgerTable.Parse(decoded.Text, schemes))
            {
                if (IdNormaliser.Normalise(row.Id, schemes) is { } known)
                {
                    rowByKey.TryAdd(ledger.Path + (char)10 + known.Key, row);
                }
            }
        }

        var askedThisRun = new HashSet<string>(StringComparer.Ordinal);

        // The rows reconciled against the file by code (the verify session's check of 2026-11-04: the reader missed 22
        // rows of two chunks and nothing noticed): a table row the reader did not return is filled by rule from its cells.
        var filledByRule = 0;
        foreach (var chunk in chunks)
        {
            var table = LedgerTable.Parse(chunk.Chunk.Text, schemes);
            if (table.Count == 0)
            {
                var text = chunk.Chunk.Text;
                var lines = text.Split('\n');
                context.Log.Record(new DecisionRecord
                {
                    Phase = PhaseName,
                    Kind = "rows-check",
                    Subject = $"{chunk.Ledger.Path}, chunk {chunk.Chunk.Index + 1}",
                    Decision = $"code found no table row with an id in this chunk's text ({text.Length} characters, {lines.Length} lines, {lines.Count(l => l.TrimStart().StartsWith('|'))} starting with a pipe; it starts \"{Cut(text.TrimStart().Replace('\n', ' '), 80)}\"), so nothing is reconciled for it",
                    By = DecidedBy.Rule,
                    Flags = ["rows-unchecked"],
                });
                continue;
            }

            foreach (var row in table)
            {
                if (IdNormaliser.Normalise(row.Id, schemes) is { } known)
                {
                    rowByKey.TryAdd(chunk.Ledger.Path + (char)10 + known.Key, row);
                }
            }

            var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in chunk.Result.Rows)
            {
                if (IdNormaliser.Normalise(row.Key, schemes) is { } known)
                {
                    returned.Add(known.Key);
                }
            }

            var missing = table.Where(r => IdNormaliser.Normalise(r.Id, schemes) is { } id && !returned.Contains(id.Key)).ToList();
            foreach (var row in missing)
            {
                chunk.Result.Rows.Add(new RowReply
                {
                    Key = row.Id,
                    Title = row.Title,
                    StatusAsWritten = row.Status,
                    Detail = "",
                    Ids = [],
                    References = [],
                    Dates = [],
                    Evidence = new LineEvidenceReply { Line = row.Line },
                    FilledByRule = true,
                });
                filledByRule++;
            }

            if (missing.Count > 0)
            {
                context.Log.Record(new DecisionRecord
                {
                    Phase = PhaseName,
                    Kind = "rows-check",
                    Subject = $"{chunk.Ledger.Path}, chunk {chunk.Chunk.Index + 1}",
                    Decision = $"the table holds {table.Count} row(s) with an id and the reader returned {returned.Count}; {missing.Count} filled by rule from their cells: {string.Join(", ", missing.Take(30).Select(m => m.Id))}",
                    By = DecidedBy.Rule,
                    Flags = ["rows-filled-by-rule"],
                });
            }
        }

        // Where each row sits in its file and what the file says it is, by code, before any item is made.
        var mixedPaths = (context.Map?.Ledgers ?? []).Where(l => l.Kind is "open-ledger" or "finished-ledger")
            .GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(l => l.Kind).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (located, ambiguousIds) = await LocateRowsAsync(context, chunks, schemes, rowByKey, mixedPaths, placed, cancellationToken).ConfigureAwait(false);

        // What the reader returned against what is written, per ledger: a row of the reader's is never taken into another
        // without a word, so the two counts are kept and the verify phase compares them.
        var rowsReturned = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var rowsWritten = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rowsKept = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Rows into items, merged by normalised key.
        var rowKeysByLedger = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            var rowKeys = rowKeysByLedger.TryGetValue(chunk.Ledger.Path, out var seenRows) ? seenRows : rowKeysByLedger[chunk.Ledger.Path] = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in chunk.Result.Rows)
            {
                // The key is the file's, never the reader's wording (the re-check, B2 and B3): an id of a confirmed scheme
                // where the row's own line carries one, else the ledger's path and the slug of the title the line gives by
                // code, in the letters of whatever script it is written in. A number in a list or a table is a position,
                // and a label that is no id of a scheme ("Task 1", "UI-2") is part of the title: neither is ever a key.
                var info = located[row];
                if (!rowsReturned.TryGetValue(chunk.Ledger.Path, out var returnedHere))
                {
                    rowsReturned[chunk.Ledger.Path] = returnedHere = new HashSet<string>(StringComparer.Ordinal);
                }

                returnedHere.Add(info.Line is { } foundOn ? "line " + foundOn.ToString(CultureInfo.InvariantCulture)
                    : info.InOverlap ? "title " + info.Title
                    : "row " + chunk.Chunk.Index.ToString(CultureInfo.InvariantCulture) + ":" + chunk.Result.Rows.IndexOf(row).ToString(CultureInfo.InvariantCulture));
                var writtenKey = (row.Key ?? "").Trim().Trim('*', '`');
                var key = info.Key;
                if (info.Id is null)
                {
                    keyless++;
                }

                var (status, by, condition, flags) = ResolveStatus(row, chunk, info.LedgerKind, vocabulary, thresholds);
                var (waits, waitsBy) = Waits(row, condition, chunk, thresholds);
                if (waitsBy is null && !string.IsNullOrWhiteSpace(condition) && chunk.Shapes is not null && chunk.Shapes.TryGetValue(RowId(chunk, row), out var unsureShape) && unsureShape.Waits is { } unsureFigure && unsureFigure > thresholds.No && unsureFigure < thresholds.Yes)
                {
                    // The judge was unsure whether the condition is a wait: a person says (change 5 of the verdict).
                    var question = new Question($"condition:{key}", PhaseName,
                        $"The row {key} carries the condition \"{Cut(condition!, 160)}\" (status as written: {row.StatusAsWritten ?? "none"}). Does the item wait on someone or something before it can proceed, so that it is blocked?",
                        [new QuestionOption("waits", "it waits: blocked, with the condition as the reason"), new QuestionOption("does-not-wait", "the condition is a note; the item is not blocked")],
                        "does-not-wait")
                    {
                        Context = Cut(row.Detail ?? "", 400),
                        About = new QuestionAbout("condition", [chunk.Ledger.Path], [key], 1),
                        DefaultReason = $"the judge read the condition as a wait at {unsureFigure:0.00}, between its lines",
                    };
                    askedThisRun.Add(question.Key);
                    var answer = await context.AskAsync(question, key, "no answer; the item is not read as blocked", cancellationToken).ConfigureAwait(false);
                    if (answer?.Option == "waits")
                    {
                        waits = 1;
                        waitsBy = DecidedBy.Person;
                    }
                }

                if (status is "ready" && waits is { } w && w >= thresholds.Yes)
                {
                    // An item that waits on someone or something is not ready, whatever its word says (backlog TR-34).
                    status = "deferred";
                    by = waitsBy ?? DecidedBy.Rule;
                    flags.Add("status-by-condition");
                }

                var (type, typeAsWritten, typeBy, typeConfidence, relations, shapeFlags) = Shape(row, chunk, thresholds);
                flags.AddRange(shapeFlags);
                var statusConfidence = by == DecidedBy.Judge && chunk.Figures is not null && chunk.Figures.TryGetValue(RowId(chunk, row), out var statusFigures)
                    ? Math.Max(statusFigures.Finished, statusFigures.Cancelled)
                    : by == DecidedBy.Reader ? row.Confidence : null;

                if (row.FilledByRule)
                {
                    flags.Add("row-by-rule");
                }

                if (!string.IsNullOrWhiteSpace(row.StatusAsWritten) && IdNormaliser.Normalise(LedgerTable.Bare(row.StatusAsWritten.Trim()), schemes) is null)
                {
                    // One word, one question (ADR 0020 rule 12): "DONE 2026-10-21" and "DONE" are the word DONE; the phrase stays on the item.
                    // A status cell that holds an id (a row closed "BL-144", backlog TR-33) is no status word.
                    var phrase = row.StatusAsWritten.Trim();
                    var word = StatusVocabulary.WordOf(phrase);
                    var qualified = StatusVocabulary.IsQualified(phrase, word);
                    if (!words.TryGetValue(word, out var use))
                    {
                        var known = vocabulary.Lookup(word);
                        use = new WordUse { Word = word, Mapped = known?.Mapped ?? status, By = known?.By ?? by, Condition = known?.Condition ?? condition, Meaning = known?.Meaning, FromQualified = known is null && qualified };
                        words[word] = use;
                    }
                    else if (use.FromQualified && !qualified)
                    {
                        // A plain use of the word says more about the word than a qualified one ("CLOSED" over "CLOSED as declined").
                        use.Mapped = status;
                        use.By = by;
                        use.Condition = condition;
                        use.FromQualified = false;
                    }

                    if (use.Samples.Count < 3 && !use.Samples.Contains(phrase, StringComparer.OrdinalIgnoreCase))
                    {
                        use.Samples.Add(phrase);
                    }

                    use.Keys.Add(key);
                }

                // Where the row sits, by code (LocateRowsAsync): the line of the file its title begins on, found with the
                // markup removed and across wrapped lines; a table row not found by its title has its line from the table
                // read over the whole file; the reader's own count is the last resort, and the row is flagged then.
                var position = info.Position;
                var seenBefore = info.SeenBefore;
                var inOverlap = info.InOverlap;
                var lineByCode = info.Line;
                var line = lineByCode ?? (row.Evidence?.Line is { } l ? chunk.Chunk.StartLine - chunk.Chunk.OverlapLines + l - 1 : (int?)null);
                var rowText = info.LineText ?? (row.Title ?? "") + "|" + (row.StatusAsWritten ?? "") + "|" + (row.Detail ?? "");
                var section = line is { } at ? SectionOf(context.Decode(chunk.File), at) : null;
                var source = new ItemSource { Path = chunk.Ledger.Path, Line = line, Sha256 = chunk.File.Sha256, ChunkIndex = chunk.Chunk.Index, LedgerKind = info.LedgerKind, RunId = context.RunId };
                var references = (row.References ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Text)).Select(r => new ItemReference { Kind = (r.Kind ?? "id").Trim().ToLowerInvariant(), Text = r.Text!.Trim() }).ToList();
                // The row's own text is the body (nothing cut), from the file by code wherever the row's line is found; and
                // every id of a confirmed scheme in a table row is a reference by code.
                var tableRow = info.TableRow;
                var body = info.Text;
                if (lineByCode is null)
                {
                    flags.Add("row-not-located");
                }

                if (info.Mentions is { } named && !references.Any(r => r.Kind == "id" && string.Equals(IdNormaliser.Normalise(r.Text, schemes)?.Key ?? r.Text, named, StringComparison.OrdinalIgnoreCase)))
                {
                    // The id in the bracket that ends the row is another row's: a reference to it, by code.
                    references.Add(new ItemReference { Kind = "id", Text = named });
                }

                if (info.InSeveralLedgers)
                {
                    flags.Add("id-in-several-ledgers");
                }

                if (tableRow is not null)
                {
                    // The reader returns ids bare: one the row writes in another repository's list is marked so.
                    foreach (var reference in references.Where(r => r.Kind == "id" && IsForeignInLine(tableRow.Text, r.Text, schemes)))
                    {
                        reference.Kind = "foreign-id";
                    }

                    foreach (var id in IdsByCode(tableRow.Text, schemes).Where(i => !string.Equals(i, key, StringComparison.OrdinalIgnoreCase) && !references.Any(r => r.Kind == "id" && string.Equals(r.Text, i, StringComparison.OrdinalIgnoreCase))))
                    {
                        references.Add(new ItemReference { Kind = "id", Text = id });
                    }
                }

                // A row's identity is its ledger and its line. The same row again (the overlap repeats the chunk before, or
                // the reader returned it twice) is merged below. Another row of the ledger that comes to the same key (alike
                // titles, the same id written twice) keeps an item of its own: told apart by its section first, which a
                // person can read, then by its own text in the file (change 3 of the verdict and its redo).
                var identity = lineByCode is { } exact ? chunk.Ledger.Path + ":" + exact.ToString(CultureInfo.InvariantCulture) : null;
                items.TryGetValue(key, out var existing);
                if (identity is not null && rowKeys.TryGetValue(identity, out var knownKey))
                {
                    key = knownKey;
                    items.TryGetValue(key, out existing);
                }
                else if (existing is not null && existing.Sources.Any(s => s.Path == chunk.Ledger.Path)
                    && (identity is not null || !(inOverlap || (position < 0 && seenBefore))))
                {
                    var bySection = section is not null && !string.Equals(section, existing.Section, StringComparison.Ordinal) && ItemCatalogue.Slug(section).Length > 0
                        ? key + "~" + ItemCatalogue.Slug(section)
                        : null;
                    key = bySection is not null && !items.ContainsKey(bySection) ? bySection : key + "~" + ContentHash(rowText);
                    items.TryGetValue(key, out existing);
                    if (tableRow is not null && info.LineText is not null)
                    {
                        // The table's row under this id is the first one; this row's own line is its text.
                        body = info.LineText;
                    }
                }

                if (identity is not null)
                {
                    rowKeys[identity] = key;
                }

                if (existing is not null)
                {
                    if (existing.Sources.Any(s => s.Path == chunk.Ledger.Path))
                    {
                        // The chunk overlap repeated the row: keep the first reading, add what the second saw.
                        foreach (var reference in references.Where(r => !existing.References.Any(e => e.Kind == r.Kind && string.Equals(e.Text, r.Text, StringComparison.OrdinalIgnoreCase))))
                        {
                            existing.References.Add(reference);
                        }

                        continue;
                    }

                    // Two ledgers hold the key: a row moves to the finished ledger when it closes, so that row stands.
                    inTwoLedgers++;
                    rowsWritten[chunk.Ledger.Path] = rowsWritten.GetValueOrDefault(chunk.Ledger.Path) + 1;
                    var incomingFinished = chunk.Ledger.Kind == "finished-ledger";
                    var keptFinished = existing.Sources[0].LedgerKind == "finished-ledger";
                    if (incomingFinished && !keptFinished)
                    {
                        existing.Title = info.Title.Length > 0 ? info.Title : existing.Title;
                        existing.RowSha256 = info.RowSha256 ?? existing.RowSha256;
                        existing.RowRaw = info.RowRaw ?? existing.RowRaw;
                        existing.Section = section ?? existing.Section;
                        existing.StatusAsWritten = row.StatusAsWritten;
                        existing.Status = status;
                        existing.StatusBy = by;
                        existing.Condition = condition;
                        existing.Tier = row.Tier ?? existing.Tier;
                        existing.Size = row.Size ?? existing.Size;
                        existing.Detail = body ?? (string.IsNullOrWhiteSpace(row.Detail) ? existing.Detail : row.Detail);
                        existing.Summary = body is null ? null : row.Detail;
                        existing.StatusConfidence = statusConfidence;
                        existing.Waits = waits;
                        existing.WaitsBy = waitsBy;
                        if (type is not null)
                        {
                            existing.Type = type;
                            existing.TypeAsWritten = typeAsWritten;
                            existing.TypeBy = typeBy;
                            existing.TypeConfidence = typeConfidence;
                        }

                        foreach (var relation in relations.Where(r => !existing.Relations.Any(e => e.Kind == r.Kind && string.Equals(e.Text, r.Text, StringComparison.OrdinalIgnoreCase))))
                        {
                            existing.Relations.Add(relation);
                        }

                        existing.Dates = (row.Dates ?? []).Select(d => new ItemDate { What = d.What ?? "", Date = d.Date ?? "" }).ToList();
                        existing.EvidenceLine = line;
                        existing.EvidenceQuote = row.Evidence?.Quote;
                        existing.Sources.Insert(0, source);
                    }
                    else
                    {
                        existing.Sources.Add(source);
                    }

                    foreach (var reference in references.Where(r => !existing.References.Any(e => e.Kind == r.Kind && string.Equals(e.Text, r.Text, StringComparison.OrdinalIgnoreCase))))
                    {
                        existing.References.Add(reference);
                    }

                    if (!existing.Flags.Contains("in-two-ledgers"))
                    {
                        existing.Flags.Add("in-two-ledgers");
                    }

                    context.Log.Record(new DecisionRecord
                    {
                        Phase = PhaseName,
                        Kind = "item",
                        Subject = key,
                        Decision = $"appears in {existing.Sources[0].Path} and {existing.Sources[^1].Path}, one an open ledger and one a finished one, and the two rows agree in title or text: one item, the row of the finished ledger stands and both sources are kept",
                        By = DecidedBy.Rule,
                        Evidence = existing.Sources.Select(s => new Evidence(s.Path, s.Line, null)).ToList(),
                        Flags = ["in-two-ledgers"],
                    });
                    continue;
                }

                var item = new WorkItem
                {
                    Key = key,
                    Aliases = (row.Ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i) && !IsLocalNumber(i.Trim().Trim('*', '`')) && !ambiguousIds.Contains(IdNormaliser.Normalise(i, schemes)?.Key ?? i.Trim())
                            && !string.Equals(IdNormaliser.Normalise(i, schemes)?.Key ?? i.Trim(), info.Mentions, StringComparison.OrdinalIgnoreCase))
                        .Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Section = section,
                    RowSha256 = info.RowSha256,
                    RowRaw = info.RowRaw,
                    Title = info.Title.Length > 0 ? info.Title : key,
                    StatusAsWritten = row.StatusAsWritten?.Trim(),
                    Status = status,
                    StatusBy = by,
                    Condition = condition,
                    Tier = row.Tier?.Trim(),
                    Size = row.Size?.Trim(),
                    Dates = (row.Dates ?? []).Where(d => !string.IsNullOrWhiteSpace(d.Date)).Select(d => new ItemDate { What = d.What ?? "", Date = d.Date! }).ToList(),
                    Detail = string.IsNullOrWhiteSpace(body ?? row.Detail) ? (info.Title.Length > 0 ? info.Title : key).Trim() : (body ?? row.Detail)!,
                    Summary = body is null ? null : row.Detail,
                    Type = type,
                    TypeAsWritten = typeAsWritten,
                    TypeBy = typeBy,
                    TypeConfidence = typeConfidence,
                    StatusConfidence = statusConfidence,
                    Waits = waits,
                    WaitsBy = waitsBy,
                    Relations = relations,
                    References = references,
                    Sources = [source],
                    Flags = flags,
                    EvidenceLine = line,
                    EvidenceQuote = row.Evidence?.Quote,
                    Confidence = row.Confidence,
                    Reasoning = row.Reasoning,
                    By = DecidedBy.Reader,
                };
                // The row's own id is an alias a reference can land on only when it is an id of a confirmed scheme that one
                // row alone carries; a label of the reader's ("Task 1") is in the title, not among the ids.
                if (info.Id is not null && !info.InSeveralLedgers && IdNormaliser.Normalise(writtenKey, schemes) is not null && !item.Aliases.Contains(row.Key!.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    item.Aliases.Insert(0, row.Key!.Trim());
                }

                items[key] = item;
                order.Add(key);
                rowsWritten[chunk.Ledger.Path] = rowsWritten.GetValueOrDefault(chunk.Ledger.Path) + 1;
            }
        }

        // One word, one mapping (ADR 0020 rule 12): a plain use of a word takes the word's mapping, the majority over its
        // plain rows, so one row's stray answer ("CLOSED (2026-09-27)" read as cancelled) does not stand alone.
        var majority = new Dictionary<string, (string Status, string By)>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in order.Select(k => items[k]).Where(i => !i.Placeholder && !string.IsNullOrWhiteSpace(i.StatusAsWritten))
            .GroupBy(i => StatusVocabulary.WordOf(i.StatusAsWritten!.Trim()), StringComparer.OrdinalIgnoreCase))
        {
            var plain = group.Where(i => !StatusVocabulary.IsQualified(i.StatusAsWritten!.Trim(), group.Key)).ToList();
            if (plain.Count == 0)
            {
                continue;
            }

            var top = plain.GroupBy(i => i.Status, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First();
            majority[group.Key] = (top.Key, top.First().StatusBy);
        }

        var byMajority = 0;
        foreach (var item in order.Select(k => items[k]).Where(i => !i.Placeholder && !string.IsNullOrWhiteSpace(i.StatusAsWritten)))
        {
            var phrase = item.StatusAsWritten!.Trim();
            var word = StatusVocabulary.WordOf(phrase);
            if (!StatusVocabulary.IsQualified(phrase, word) && majority.TryGetValue(word, out var usual) && item.Status != usual.Status)
            {
                item.Status = usual.Status;
                item.StatusBy = usual.By;
                item.Flags.Add("status-by-word");
                byMajority++;
            }
        }

        // A row's place against its ledger (ADR 0020 rule 10, as the third re-check left it). A row of a finished
        // ledger, or under a heading that says finished, that says nothing of its own state is finished: the ledger
        // says so. So is one whose words tell how the work ended (ACCEPTED, "will never be verified"): the words of
        // a closed row describe its outcome, and they stay as the condition. A row there whose own word or box says
        // the work is not finished ("[ ]", OPEN, "in progress", "Drafts until reviewed") keeps the state its words
        // give it and a person is asked which is right: nothing is closed against the row's own words on a guess.
        // A finished word in the open ledger is flagged, not changed.
        var againstLedger = 0;
        var headingOfKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, heading) in located.Values.Where(i => i.SaidBy is not null).Select(i => (i.Key, i.SaidBy!)))
        {
            headingOfKey.TryAdd(key, heading);
        }

        foreach (var item in order.Select(k => items[k]).ToList())
        {
            var kind = item.Sources[0].LedgerKind;
            if (kind == "finished-ledger" && item.Status is not ("done" or "cancelled") && !item.Placeholder)
            {
                againstLedger++;
                item.Flags.Add("open-in-finished-ledger");
                var noWord = string.IsNullOrWhiteSpace(item.StatusAsWritten);
                var said = noWord ? "no status word, and the reader read its text as not finished" : $"\"{Cut(item.StatusAsWritten!, 80)}\"";
                var (unfinished, why) = await SaysUnfinishedAsync(context, item, vocabulary, cancellationToken).ConfigureAwait(false);
                if (unfinished == false)
                {
                    item.Condition ??= item.StatusAsWritten;
                    item.Status = "done";
                    item.StatusBy = DecidedBy.Rule;
                    item.Flags.Add("status-by-ledger");
                    context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = item.Key, Decision = $"stands among finished work and its own words ({said}) tell how the work ended, not that it is still to do ({why}): done by its ledger, the words kept as the condition", By = DecidedBy.Rule, Flags = ["open-in-finished-ledger", "status-by-ledger"] });
                    continue;
                }

                var path = item.Sources[0].Path;
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = item.Key, Decision = $"stands among finished work and says it is not finished ({said}; {why}): the row keeps its own state ({item.Status}) and a person is asked which is right", By = DecidedBy.Rule, Flags = ["open-in-finished-ledger"] });
                var where = headingOfKey.TryGetValue(item.Key, out var under) ? $"under the heading \"{Cut(under, 80)}\" of {path}, which says finished work" : $"in {path}, a ledger of finished work";
                var question = new Question($"open-in-finished:{item.Key}", PhaseName,
                    noWord
                        ? $"The row {item.Key} (\"{Cut(item.Title, 100)}\") stands {where}. It writes no status word, and the reader read its text as not finished ({item.Status}). Is it open or finished?"
                        : $"The row {item.Key} (\"{Cut(item.Title, 100)}\") stands {where}, and its own words say it is not finished (\"{Cut(item.StatusAsWritten!, 80)}\", read as {item.Status}). Is it open or finished?",
                    [new QuestionOption("open", $"it is open: the row keeps its own state ({item.Status})"), new QuestionOption("finished", noWord ? "it is finished: done" : "it is finished: done, with its own words kept as the condition")],
                    "open")
                {
                    Context = Cut(item.Detail, 400),
                    About = new QuestionAbout("open-in-finished", [path], [item.Key], 1),
                    DefaultReason = noWord
                        ? "the reader read the row as not finished, and nothing is closed against that on a guess"
                        : "a row's own word or box stands against its ledger until a person says otherwise; nothing is closed on a guess",
                };
                askedThisRun.Add(question.Key);
                var answer = await context.AskAsync(question, item.Key, $"no answer; the row keeps its own state ({item.Status})", cancellationToken).ConfigureAwait(false);
                if (answer?.Option == "finished")
                {
                    item.Condition ??= item.StatusAsWritten;
                    item.Status = "done";
                    item.StatusBy = DecidedBy.Person;
                    item.StatusConfidence = null;
                }
            }
            else if (kind == "open-ledger" && item.Status is "done" or "cancelled")
            {
                item.Flags.Add("finished-in-open-ledger");
                againstLedger++;
            }
        }

        // References of the item scheme: resolved to the row that defines them, to the base of a sub-label, or to a placeholder (ADR 0015, ADR 0020 rule 2).
        // An id no row defines that the ledgers' own text writes with another repository's name before it, even once, is that
        // repository's (backlog TR-33): the reader returns ids bare, so the text is looked at here.
        var ledgerTexts = ledgers.Select(l => context.File(l.Path)).Where(f => f is not null).Select(f => context.Decode(f!)?.Text ?? "").ToList();
        var foreign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var placeholders = new List<WorkItem>();
        foreach (var item in order.Select(k => items[k]).ToList())
        {
            foreach (var reference in item.References.Where(r => r.Kind == "id"))
            {
                if (IsQualifiedByAnotherName(reference.Text))
                {
                    // "infra BL-144": another repository's id; it stays text (the verify session's check of 2026-11-04).
                    continue;
                }

                var normalised = IdNormaliser.Normalise(reference.Text, schemes);
                if (normalised is null)
                {
                    continue;
                }

                if (ambiguousIds.Contains(normalised.Key) || ambiguousIds.Contains(normalised.Base))
                {
                    // Several ledgers each define this id for a row of their own: the reference names none of them for
                    // sure, so it stays text, and no placeholder is made for an id that is defined.
                    reference.Kind = "ambiguous-id";
                    continue;
                }

                if (items.ContainsKey(normalised.Key))
                {
                    reference.ResolvedKey = items[normalised.Key].Key;
                    continue;
                }

                if (normalised.Suffix is not null && items.ContainsKey(normalised.Base))
                {
                    reference.ResolvedKey = items[normalised.Base].Key;
                    continue;
                }

                if (itemScheme is null || normalised.Scheme != itemScheme.Name)
                {
                    continue;
                }

                var baseKey = normalised.Suffix is not null ? normalised.Base : normalised.Key;
                if (!items.ContainsKey(baseKey))
                {
                    if (!foreign.TryGetValue(baseKey, out var qualifier))
                    {
                        qualifier = QualifierInText(baseKey, ledgerTexts) ?? "";
                        foreign[baseKey] = qualifier;
                    }

                    if (qualifier.Length > 0)
                    {
                        // "infra BL-144" somewhere in the ledgers: another repository's id, so no placeholder; the reference stays text.
                        if (!item.Flags.Contains("foreign-id"))
                        {
                            item.Flags.Add("foreign-id");
                        }

                        continue;
                    }
                }

                if (!items.TryGetValue(baseKey, out var placeholder))
                {
                    placeholder = new WorkItem
                    {
                        Key = baseKey,
                        Aliases = [baseKey],
                        Title = $"{baseKey} (referenced, defined nowhere)",
                        Status = "unknown",
                        StatusBy = DecidedBy.Rule,
                        Detail = "",
                        Sources = [new ItemSource { Path = item.Sources[0].Path, Line = item.Sources[0].Line, Sha256 = item.Sources[0].Sha256, ChunkIndex = item.Sources[0].ChunkIndex, LedgerKind = item.Sources[0].LedgerKind, RunId = context.RunId }],
                        Placeholder = true,
                        Flags = ["placeholder"],
                        Reasoning = $"referenced by {item.Key}",
                        By = DecidedBy.Rule,
                    };
                    items[baseKey] = placeholder;
                    order.Add(baseKey);
                    placeholders.Add(placeholder);
                }
                else if (placeholder.Placeholder && placeholder.Reasoning is not null && !placeholder.Reasoning.Contains(item.Key, StringComparison.Ordinal))
                {
                    placeholder.Reasoning += ", " + item.Key;
                }

                reference.ResolvedKey = baseKey;
            }
        }

        // The relations' ids resolved to items (or to the base of a sub-label); one nothing defines stays text, flagged.
        var relationsResolved = 0;
        var relationsUnresolved = 0;
        foreach (var item in order.Select(k => items[k]).ToList())
        {
            foreach (var relation in item.Relations)
            {
                var normalised = IdNormaliser.Normalise(relation.Text, schemes);
                if (normalised is not null && items.TryGetValue(normalised.Key, out var target))
                {
                    relation.ResolvedKey = target.Key;
                    relationsResolved++;
                }
                else if (normalised?.Suffix is not null && items.TryGetValue(normalised.Base, out var baseItem))
                {
                    relation.ResolvedKey = baseItem.Key;
                    relationsResolved++;
                }
                else
                {
                    relation.Flags.Add("unresolved");
                    relationsUnresolved++;
                }
            }
        }

        if (relationsResolved + relationsUnresolved > 0)
        {
            var all = order.Select(k => items[k]).SelectMany(i => i.Relations).ToList();
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "relations",
                Subject = "the items' relations",
                Decision = $"{all.Count} relation(s) stated by the rows: {string.Join(", ", all.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}; {all.Count(r => r.By == DecidedBy.Judge)} read by the judge, {all.Count(r => r.By == DecidedBy.Reader)} by the reader only; {relationsResolved} resolved to items, {relationsUnresolved} to no item (kept as text)",
                By = DecidedBy.Rule,
            });
        }

        foreach (var (id, qualifier) in foreign.Where(f => f.Value.Length > 0).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "foreign-id",
                Subject = id,
                Decision = $"no row defines {id} and the ledgers write it as \"{qualifier} {id}\" at least once, so it is another repository's id: no placeholder, the references to it stay text",
                By = DecidedBy.Rule,
                Flags = ["foreign-id"],
            });
        }

        foreach (var use in words.Values)
        {
            use.Keys.Clear();
        }

        foreach (var item in items.Values.Where(i => !string.IsNullOrWhiteSpace(i.StatusAsWritten)))
        {
            if (words.TryGetValue(StatusVocabulary.WordOf(item.StatusAsWritten!.Trim()), out var use))
            {
                use.Keys.Add(item.Key);
            }
        }

        foreach (var unused in words.Where(w => w.Value.Rows == 0).Select(w => w.Key).ToList())
        {
            words.Remove(unused);
        }

        // Work in prose (ADR 0020 rule 11): a question per list; an answer of "items" makes one deferred item per bullet.
        var prose = new List<ProseWork>();
        var fromProse = 0;
        foreach (var chunk in chunks)
        {
            foreach (var work in chunk.Result.ProseWork)
            {
                if (string.IsNullOrWhiteSpace(work.Heading) || work.Bullets is not { Count: > 0 })
                {
                    continue;
                }

                var questionKey = $"prose-work:{chunk.Ledger.Path}#{ItemCatalogue.Slug(work.Heading)}";
                if (prose.Any(p => p.QuestionKey == questionKey))
                {
                    continue;
                }

                var question = new Question(questionKey, PhaseName,
                    $"The section \"{work.Heading.Trim()}\" of {chunk.Ledger.Path} lists work in prose, without rows ({work.Bullets.Count} bullet(s)). Should its bullets become items?",
                    [new QuestionOption("items", "one item per bullet, with status deferred"), new QuestionOption("notes", "no items; the summary names the list as work in prose"), new QuestionOption("ignore", "not work; say nothing")],
                    "notes")
                {
                    Context = string.Join("\n", work.Bullets.Select(b => "- " + Cut(b, 300))),
                    About = new QuestionAbout("prose-work", [chunk.Ledger.Path], [], work.Bullets.Count),
                    DefaultReason = "a list in prose is named, not turned into rows, until a person says so (ADR 0020 rule 11)",
                };
                askedThisRun.Add(question.Key);
                var answer = await context.AskAsync(question, $"{chunk.Ledger.Path}: {work.Heading.Trim()}", "no answer; the summary names the list, no items are made", cancellationToken).ConfigureAwait(false);
                var entry = new ProseWork { Path = chunk.Ledger.Path, Heading = work.Heading.Trim(), Bullets = work.Bullets.Select(b => Cut(b, 300)).ToList(), Reasoning = work.Reasoning, QuestionKey = questionKey, Answer = answer?.Option ?? (answer is null ? null : "notes") };
                prose.Add(entry);
                if (answer?.Option != "items")
                {
                    continue;
                }

                var headingLine = chunk.File is not null && context.Decode(chunk.File) is { } decoded
                    ? decoded.Sections.FirstOrDefault(s => string.Equals(s.Heading.Trim(), entry.Heading, StringComparison.OrdinalIgnoreCase))?.StartLine
                    : null;
                for (var i = 0; i < entry.Bullets.Count; i++)
                {
                    var bullet = entry.Bullets[i];
                    var key = $"{chunk.Ledger.Path}#{ItemCatalogue.Slug(entry.Heading)}#{i + 1}";
                    if (items.ContainsKey(key))
                    {
                        continue;
                    }

                    items[key] = new WorkItem
                    {
                        Key = key,
                        Title = Cut(FirstSentence(bullet), 120),
                        Status = "deferred",
                        StatusBy = DecidedBy.Person,
                        Detail = bullet,
                        Sources = [new ItemSource { Path = chunk.Ledger.Path, Line = headingLine, Sha256 = chunk.File!.Sha256, ChunkIndex = chunk.Chunk.Index, LedgerKind = chunk.Ledger.Kind, RunId = context.RunId }],
                        Flags = ["from-prose"],
                        Reasoning = $"a bullet of \"{entry.Heading}\", made an item by a person's answer to {questionKey}",
                        By = DecidedBy.Person,
                    };
                    order.Add(key);
                    fromProse++;
                }
            }
        }

        // Placeholders are asked about once per scheme (ADR 0015); "drop" removes them and leaves the references as text.
        if (placeholders.Count == 0 && itemScheme is not null)
        {
            context.Book.Withdraw($"placeholders:{itemScheme.Name}");
        }

        if (placeholders.Count > 0 && itemScheme is not null)
        {
            var listed = string.Join(", ", placeholders.Take(40).Select(p => p.Key)) + (placeholders.Count > 40 ? $", and {placeholders.Count - 40} more" : "");
            var question = new Question($"placeholders:{itemScheme.Name}", PhaseName,
                $"{placeholders.Count} id(s) of the {itemScheme.Name} scheme are referenced by rows but no row defines them: {listed}. They are kept as placeholder items with status unknown, so that references to them resolve. Keep them?",
                [new QuestionOption("keep", "keep the placeholders"), new QuestionOption("drop", "no placeholders; such references stay text")],
                "keep")
            {
                About = new QuestionAbout("placeholders", [], placeholders.Select(p => p.Key).ToList(), placeholders.Count),
                DefaultReason = "a placeholder keeps the references resolvable (ADR 0015)",
            };
            askedThisRun.Add(question.Key);
            var answer = await context.AskAsync(question, $"{placeholders.Count} placeholder(s)", "no answer; the placeholders are kept", cancellationToken).ConfigureAwait(false);
            if (answer?.Option == "drop")
            {
                foreach (var placeholder in placeholders)
                {
                    items.Remove(placeholder.Key);
                    order.Remove(placeholder.Key);
                }

                foreach (var reference in items.Values.SelectMany(i => i.References).Where(r => r.ResolvedKey is not null && placeholders.Any(p => p.Key == r.ResolvedKey)))
                {
                    reference.ResolvedKey = null;
                }

                placeholders.Clear();
            }
        }

        // Items of earlier runs that no row defines now are kept and marked, never deleted (ADR 0017). A placeholder
        // is the exception: it stands for a reference, not a row, so when the reference no longer asks for it (the id
        // is another repository's, or a row defines it now) the placeholder goes, and its number with it.
        var gone = 0;
        var placeholdersDropped = 0;
        var earlier = ItemCatalogue.Load(context.State);
        DropItemsOfSetAsideLedgers(context, earlier);
        var setAsidePaths = NotThisRepositorysRows(context);
        var readThisRun = new HashSet<string>(ledgers.Select(l => l.Path), StringComparer.OrdinalIgnoreCase);
        var rowOnLine = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var current in order.Select(k => items[k]).Where(i => !i.Placeholder && i.Sources.Count > 0 && i.Sources[0].Line is not null))
        {
            rowOnLine.TryAdd(current.Sources[0].Path + ":" + current.Sources[0].Line!.Value.ToString(CultureInfo.InvariantCulture), current);
        }

        var rowWithLines = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var current in order.Select(k => items[k]).Where(i => !i.Placeholder && i.Sources.Count > 0 && i.RowSha256 is not null))
        {
            rowWithLines.TryAdd(current.Sources[0].Path + "|" + current.RowSha256, current);
        }

        var numbersBefore = ItemCatalogue.LoadNumbers(context.State);
        var keptLines = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var rekeyed = 0;
        foreach (var old in earlier)
        {
            if (items.ContainsKey(old.Key) || setAsidePaths.Contains(old.Sources.FirstOrDefault()?.Path ?? ""))
            {
                continue;
            }

            // An item of prose keyed by its line in an earlier run, from a ledger this run read again: this run's reading
            // of that ledger supersedes it whatever its title became (the ghosts one sample showed, change 4 of the verdict).
            if (OldProseKey().IsMatch(old.Key) && old.Sources.Any(s => readThisRun.Contains(s.Path)))
            {
                context.State.DeleteRecord(ItemCatalogue.ItemKind, ItemCatalogue.RecordKey(old.Number));
                context.State.DeleteRecord(ItemCatalogue.NumberKind, old.Key);
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = old.Key, Decision = "an earlier run keyed this item of prose by its line; this run read its ledger again, so the earlier record goes", By = DecidedBy.Rule, Flags = ["key-migrated"] });
                continue;
            }

            if (old.Placeholder)
            {
                context.State.DeleteRecord(ItemCatalogue.ItemKind, ItemCatalogue.RecordKey(old.Number));
                placeholdersDropped++;
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = old.Key, Decision = "the placeholder of an earlier run is dropped: no reference asks for it now (the id is another repository's, or a row defines it)", By = DecidedBy.Rule, Flags = ["placeholder-dropped"] });
                continue;
            }

            // An item of prose keyed by an earlier form (with a line in it) whose twin exists by the key without one: the same item, so the old record goes.
            var earlierForm = OldProseKey().Match(old.Key);
            if (earlierForm.Success && items.ContainsKey(earlierForm.Groups["path"].Value + "#" + earlierForm.Groups["slug"].Value))
            {
                context.State.DeleteRecord(ItemCatalogue.ItemKind, ItemCatalogue.RecordKey(old.Number));
                context.State.DeleteRecord(ItemCatalogue.NumberKind, old.Key);
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = old.Key, Decision = "an earlier run keyed this item of prose by its line; the same item now has the key without one, so the earlier record goes", By = DecidedBy.Rule, Flags = ["key-migrated"] });
                continue;
            }

            // The same raw lines stand in the ledger under another key of this run (a title read otherwise, an id found
            // where it was not before): the same row re-keyed. The earlier record goes, and its number stays with the row.
            if (!old.SourceGone && old.RowSha256 is not null && old.Sources.Count > 0 && rowWithLines.TryGetValue(old.Sources[0].Path + "|" + old.RowSha256, out var sameLines))
            {
                context.State.DeleteRecord(ItemCatalogue.ItemKind, ItemCatalogue.RecordKey(old.Number));
                context.State.DeleteRecord(ItemCatalogue.NumberKind, old.Key);
                if (old.Number > 0 && !numbersBefore.ContainsKey(sameLines.Key) && !numbersBefore.Where(n => !string.Equals(n.Key, old.Key, StringComparison.OrdinalIgnoreCase)).Any(n => n.Value == old.Number))
                {
                    context.State.SaveRecord(ItemCatalogue.NumberKind, sameLines.Key, old.Number.ToString(CultureInfo.InvariantCulture), context.RunId);
                    numbersBefore[sameLines.Key] = old.Number;
                }

                rekeyed++;
                continue;
            }

            // A row is gone only when its raw lines have left its ledger, by code. The reader did not return this one in
            // this run and its lines still stand there: the item stays as it was read, on the line it stands on now.
            if (!old.SourceGone && placed.KeptAt.TryGetValue(old.Key, out var stands) && context.File(stands.Path) is { } ledgerNow)
            {
                old.Sources[0].Line = stands.Start + 1;
                old.Sources[0].Sha256 = ledgerNow.Sha256;
                old.Sources[0].RunId = context.RunId;
                if (!old.Flags.Contains("not-returned"))
                {
                    old.Flags.Add("not-returned");
                }

                items[old.Key] = old;
                order.Add(old.Key);
                rowsKept[stands.Path] = rowsKept.GetValueOrDefault(stands.Path) + 1;
                (keptLines.TryGetValue(stands.Path, out var keptHere) ? keptHere : keptLines[stands.Path] = []).Add(stands.Start);
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = old.Key, Decision = $"the reader did not return this row in this run and its lines still stand in {stands.Path} (line {stands.Start + 1}): the row is not gone, and the item stays as it was read", By = DecidedBy.Rule, Evidence = [new Evidence(stands.Path, stands.Start + 1, null)], Flags = ["not-returned"] });
                continue;
            }

            // The same row under the key an earlier engine gave it (a key from the reader's wording, or a row read twice):
            // its ledger has not changed and a row of this run sits on its line, so it is that row and no row is gone.
            // The earlier record goes, and its number stays with the row.
            var earlierSource = old.Sources.FirstOrDefault();
            if (!old.SourceGone && earlierSource?.Line is { } earlierLine && context.File(earlierSource.Path) is { } sameFile && sameFile.Sha256 == earlierSource.Sha256
                && rowOnLine.TryGetValue(earlierSource.Path + ":" + earlierLine.ToString(CultureInfo.InvariantCulture), out var sameRow))
            {
                context.State.DeleteRecord(ItemCatalogue.ItemKind, ItemCatalogue.RecordKey(old.Number));
                context.State.DeleteRecord(ItemCatalogue.NumberKind, old.Key);
                if (old.Number > 0 && !numbersBefore.ContainsKey(sameRow.Key) && !numbersBefore.Where(n => !string.Equals(n.Key, old.Key, StringComparison.OrdinalIgnoreCase)).Any(n => n.Value == old.Number))
                {
                    context.State.SaveRecord(ItemCatalogue.NumberKind, sameRow.Key, old.Number.ToString(CultureInfo.InvariantCulture), context.RunId);
                    numbersBefore[sameRow.Key] = old.Number;
                }

                rekeyed++;
                continue;
            }

            if (!old.SourceGone)
            {
                old.SourceGone = true;
                if (!old.Flags.Contains("source-gone"))
                {
                    old.Flags.Add("source-gone");
                }

                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = old.Key, Decision = $"no row defines it any more ({old.Sources.FirstOrDefault()?.Path ?? "its source"} changed or is gone); kept as source gone, never deleted", By = DecidedBy.Rule, Flags = ["source-gone"] });
            }

            items[old.Key] = old;
            order.Add(old.Key);
            gone++;
        }

        // The final accounting of every ledger read, by code (the third re-check): each line written as an entry like
        // the rows found, in a table, a list, under a heading or line by line, is set against the items. An entry no
        // item stands on is listed, never made an item and never dropped without a word; rows taken into one item are
        // listed and the item is flagged. So a shape nobody has thought of shows as a finding instead of a lost row.
        foreach (var (stale, _) in context.State.Records(AccountingKind))
        {
            context.State.DeleteRecord(AccountingKind, stale);
        }

        foreach (var (path, file) in placed.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var onItems = new HashSet<int>(keptLines.GetValueOrDefault(path) ?? []);
            var linesOfKey = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (identity, key) in rowKeysByLedger.GetValueOrDefault(path) ?? [])
            {
                if (!int.TryParse(identity[(identity.LastIndexOf(':') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var oneBased) || !items.ContainsKey(key))
                {
                    continue;
                }

                onItems.Add(oneBased - 1);
                (linesOfKey.TryGetValue(key, out var held) ? held : linesOfKey[key] = []).Add(oneBased);
            }

            var entries = file.Entries(new HashSet<int>((placed.Starts.GetValueOrDefault(path) ?? []).Union(onItems)));
            var accounting = new LedgerAccounting
            {
                Entries = entries.Count,
                WithItem = entries.Count(onItems.Contains),
                WithoutItem = entries.Where(e => !onItems.Contains(e)).Select(e => new AccountingEntry { Line = e + 1, Text = Cut(file.Line(e).Trim(), 120) }).ToList(),
            };
            foreach (var (key, lines) in linesOfKey.Where(k => k.Value.Count > 1).OrderBy(k => k.Value.Min))
            {
                accounting.Merged.Add($"{key} (lines {string.Join(", ", lines)})");
                if (!items[key].Flags.Contains("rows-merged"))
                {
                    items[key].Flags.Add("rows-merged");
                }
            }

            // From what the file holds, whatever rows were found (the fourth re-check): the entries by code, and the
            // sections the reader named as work in prose, which have their own question and need no second word.
            var census = file.Census();
            var ofLedger = chunks.Where(c => string.Equals(c.Ledger.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            var proseHeadings = ofLedger.SelectMany(c => c.Result.ProseWork).Select(w => LedgerLines.Plain(w.Heading ?? "")).Where(h => h.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool NamedAsProse(int section) => section >= 0 && proseHeadings.Contains(LedgerLines.Plain(file.HeadingText(section)));
            var sectionsWithRows = onItems.Select(file.SectionOf).ToHashSet();
            bool HoldsARow(int section) => sectionsWithRows.Contains(section) || (section >= 0 && (onItems.Contains(section) || file.SectionsAbove(section).Any(onItems.Contains)));

            // A chunk the reader returned no row for, though its own lines hold entries no item stands on: counted
            // over the whole chunk. A person said no-work of the ledger: nothing more is said of it.
            var unreadLines = new HashSet<int>();
            foreach (var chunk in ofLedger.Where(c => c.Result.Rows.Count == 0))
            {
                var own = census.Where(e => e + 1 >= chunk.Chunk.StartLine && e + 1 <= chunk.Chunk.EndLine && !onItems.Contains(e) && !NamedAsProse(file.SectionOf(e))).ToList();
                if (own.Count == 0)
                {
                    continue;
                }

                unreadLines.UnionWith(own);
                accounting.Unread.Add(new AccountingSpan { Chunk = chunk.Chunk.Index + 1, Chunks = chunk.Chunk.Count, FirstLine = own[0] + 1, LastLine = own[^1] + 1, Entries = own.Count, ReadAgain = chunk.Result.ReadAgain });
            }

            // An entry of such a chunk is counted there, once, and not listed line by line beside it.
            accounting.WithoutItem.RemoveAll(e => unreadLines.Contains(e.Line - 1));
            if (accounting.Unread.Count > 0 && accounting.Unread.Count == ofLedger.Count)
            {
                // Every chunk came back with nothing: the whole ledger, in one line.
                accounting.Unread = [new AccountingSpan { Chunk = 0, Chunks = ofLedger.Count, FirstLine = accounting.Unread.Min(u => u.FirstLine), LastLine = accounting.Unread.Max(u => u.LastLine), Entries = accounting.Unread.Sum(u => u.Entries), ReadAgain = accounting.Unread.All(u => u.ReadAgain) }];
            }

            // A section that holds list entries or table rows and no row: one line, with its heading, the count and
            // the lines. One that lies in a chunk named above is counted there.
            foreach (var section in census.Where(e => !unreadLines.Contains(e)).GroupBy(file.SectionOf).OrderBy(g => g.Key))
            {
                if (HoldsARow(section.Key) || NamedAsProse(section.Key) || section.All(e => !(LedgerLines.IsTableLine(file.Line(e)) || LedgerLines.MarkerOf(file.Line(e)) is not null)))
                {
                    continue;
                }

                accounting.SectionsWithoutRow.Add(new AccountingSpan { Heading = section.Key < 0 ? "" : Cut(file.HeadingText(section.Key), 80), FirstLine = section.Min() + 1, LastLine = section.Max() + 1, Entries = section.Count() });
            }

            if (accounting.Unread.Count > 0)
            {
                var whole = accounting.Unread[0].Chunk == 0;
                var total = accounting.Unread.Sum(u => u.Entries);
                var spans = whole ? "the whole ledger" : string.Join(", ", accounting.Unread.Select(u => $"chunk {u.Chunk} of {u.Chunks} (lines {u.FirstLine} to {u.LastLine})"));
                var question = new Question(NoRowsQuestion + path, PhaseName,
                    $"The reader returned no row for {(whole ? "the ledger " + path : spans + " of the ledger " + path)}, though {(whole ? "it holds" : "those lines hold")} {total} line(s) written as entries{(accounting.Unread.Any(u => u.ReadAgain) ? ", and a second reading returned none either" : "")}. No item is made of them. What should the import do?",
                    [
                        new QuestionOption("leave", "leave it: no items from those lines, and the finding stays"),
                        new QuestionOption("read-again", "read those lines again in the next run, telling the reader that the first reading returned nothing"),
                        new QuestionOption("no-work", "they list no work: no items, and no finding"),
                    ],
                    "leave")
                {
                    Context = string.Join("\n", census.Where(unreadLines.Contains).Take(12).Select(e => Cut(file.Line(e).Trim(), 120))),
                    About = new QuestionAbout("ledger-no-rows", [path], [], total),
                    DefaultReason = "no item is made by code of a line the reader returned no row for; a person says whether it is work",
                };
                askedThisRun.Add(question.Key);
                var answer = await context.AskAsync(question, path, "no answer; no items from those lines, and the finding stays", cancellationToken).ConfigureAwait(false);
                if (answer?.Option == "no-work" || AnsweredNoRows(context, path) == "no-work")
                {
                    accounting.Unread.Clear();
                }
            }

            context.State.SaveRecord(AccountingKind, path, JsonSerializer.Serialize(accounting, RunContext.JsonOptions), context.RunId);
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "ledger-accounting",
                Subject = path,
                Decision = $"{accounting.Entries} line(s) are written as entries like the rows found; an item stands on {accounting.WithItem}; {accounting.WithoutItem.Count} have none"
                    + (accounting.WithoutItem.Count == 0 ? "" : " (" + string.Join("; ", accounting.WithoutItem.Take(40).Select(e => $"line {e.Line}: \"{Cut(e.Text, 60)}\"")) + (accounting.WithoutItem.Count > 40 ? "; ..." : "") + "): listed for a person, not made items")
                    + $"; {accounting.Merged.Count} item(s) hold more than one row" + (accounting.Merged.Count == 0 ? "" : " (" + string.Join("; ", accounting.Merged.Take(20)) + ")")
                    + $"; {accounting.SectionsWithoutRow.Count} section(s) hold entries and no row" + (accounting.SectionsWithoutRow.Count == 0 ? "" : " (" + string.Join("; ", accounting.SectionsWithoutRow.Take(20).Select(s => $"\"{s.Heading}\": {s.Entries}, lines {s.FirstLine} to {s.LastLine}")) + ")")
                    + (accounting.Unread.Count == 0 ? "" : "; the reader returned no row for " + string.Join("; ", accounting.Unread.Select(u => (u.Chunk == 0 ? "the whole ledger" : $"chunk {u.Chunk} of {u.Chunks}") + $", {u.Entries} entr(ies) on lines {u.FirstLine} to {u.LastLine}"))),
                By = DecidedBy.Rule,
                Evidence = accounting.WithoutItem.Take(40).Select(e => new Evidence(path, e.Line, e.Text)).ToList(),
                Flags = accounting.WithoutItem.Count + accounting.Merged.Count + accounting.SectionsWithoutRow.Count + accounting.Unread.Count > 0 ? ["entries-unaccounted"] : [],
                Figures = new Dictionary<string, double> { ["entries"] = accounting.Entries, ["with an item"] = accounting.WithItem, ["without"] = accounting.WithoutItem.Count, ["merged"] = accounting.Merged.Count },
            });
        }

        // The counts the verify phase compares, per ledger read in this run.
        foreach (var (stale, _) in context.State.Records(RowsReturnedKind))
        {
            context.State.DeleteRecord(RowsReturnedKind, stale);
        }

        foreach (var path in rowsReturned.Keys.Union(rowsKept.Keys, StringComparer.OrdinalIgnoreCase))
        {
            context.State.SaveRecord(RowsReturnedKind, path, JsonSerializer.Serialize(new RowsReturned
            {
                Returned = rowsReturned.GetValueOrDefault(path)?.Count ?? 0,
                Written = rowsWritten.GetValueOrDefault(path),
                Kept = rowsKept.GetValueOrDefault(path),
            }, RunContext.JsonOptions), context.RunId);
        }

        if (rekeyed > 0)
        {
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item", Subject = $"{rekeyed} item(s) of earlier runs", Decision = "each is the row on its own line of an unchanged ledger, which this run keys from the file's own text: the earlier record goes, no row is gone, and the number stays with the row", By = DecidedBy.Rule, Flags = ["rekeyed"] });
        }

        // Numbers (ADR 0015): the scheme's number where it has one, else an assignment kept across runs.
        var assigned = ItemCatalogue.LoadNumbers(context.State);
        var newlyAssigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<int>();
        foreach (var item in order.Select(k => items[k]))
        {
            if (item.SourceGone && item.Number > 0)
            {
                used.Add(item.Number);
                continue;
            }

            var normalised = IdNormaliser.Normalise(item.Key, schemes);
            if (normalised?.Number is { } number && normalised.Suffix is null && itemScheme is not null && normalised.Scheme == itemScheme.Name && !used.Contains(number))
            {
                item.Number = number;
                used.Add(number);
            }
            else
            {
                item.Number = 0;
            }
        }

        var next = Math.Max(used.Count == 0 ? 0 : used.Max(), assigned.Count == 0 ? 0 : assigned.Values.Max());
        foreach (var item in order.Select(k => items[k]).Where(i => i.Number == 0))
        {
            if (assigned.TryGetValue(item.Key, out var kept) && !used.Contains(kept))
            {
                item.Number = kept;
            }
            else
            {
                do
                {
                    next++;
                }
                while (used.Contains(next));
                item.Number = next;
                newlyAssigned[item.Key] = next;
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "item-number", Subject = item.Key, Decision = $"number {next} assigned in source order: the scheme gives this item no number of its own (ADR 0015)", By = DecidedBy.Rule });
            }

            used.Add(item.Number);
        }

        // A word a person confirmed settles its open question (ADR 0007); the rest are asked, once each per repository (ADR 0020 rule 12).
        foreach (var confirmed in words.Values.Where(w => w.By == DecidedBy.Person))
        {
            context.Book.Withdraw($"status:{confirmed.Word}");
        }

        // A plain word the engine's own table maps (DONE, CLOSED, FIXED) asks no question: the mapping is the word's meaning, and
        // questions.md stays short; a word the reader or the judge mapped, or one used only with qualifiers, is asked once.
        // A word whose every row stands among finished work asks nothing: the ledger closed the row and the word
        // stays as the condition (BL-27's PERMANENTLY UNVERIFIED), or the row keeps its own state and its own
        // question asks which is right.
        bool ClosedByTheLedger(WordUse w) => w.Keys.Count > 0 && w.Keys.All(k => items.TryGetValue(k, out var i) && (i.Flags.Contains("status-by-ledger") || i.Flags.Contains("open-in-finished-ledger")));
        foreach (var use in words.Values.Where(w => w.By != DecidedBy.Person && !(w.By == DecidedBy.Rule && !w.FromQualified) && !ClosedByTheLedger(w)).OrderByDescending(w => w.Rows).ThenBy(w => w.Word, StringComparer.OrdinalIgnoreCase))
        {
            var question = new Question($"status:{use.Word}", PhaseName,
                $"The ledgers use the status word \"{use.Word}\" on {use.Rows} row(s); the engine maps it to {use.Mapped} ({Who(use.By)}). Which mapped state does it mean? For deferred, add the condition after a comma (\"deferred, only with the owner\"). A row whose status says more than the word (a reason, a condition) is judged on its own words either way.",
                StatusVocabulary.Options(context.Playbook), use.Mapped)
            {
                Context = (use.Meaning is null ? "" : use.Meaning + "\n") + "As written: " + string.Join("; ", use.Samples.Select(s => "\"" + Cut(s, 100) + "\"")),
                About = new QuestionAbout("status-word", ledgers.Select(l => l.Path).ToList(), use.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(), use.Rows),
                DefaultReason = $"the engine's mapping, {Who(use.By)}",
            };
            askedThisRun.Add(question.Key);
            await context.AskAsync(question, use.Word, $"no answer; {use.Mapped} ({Who(use.By)}) stands", cancellationToken).ConfigureAwait(false);
        }

        // A question of this phase an earlier run asked and this run did not (a word now mapped by rule, a phrase no
        // longer a word, a prose list the reader now reads as its subsections) is settled or stale: withdrawn (ADR 0007),
        // so questions.md says what the bundle did (the verify session's check of 2026-11-04).
        var withdrawn = 0;
        foreach (var asked in context.Book.Open().Where(q => q.Question.Phase == PhaseName && !askedThisRun.Contains(q.Question.Key) && !q.Question.Key.StartsWith("ledger-fixture:", StringComparison.Ordinal)).ToList())
        {
            if (context.Book.Withdraw(asked.Question.Key))
            {
                withdrawn++;
            }
        }

        if (withdrawn > 0)
        {
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "questions", Subject = "the open questions of this phase", Decision = $"{withdrawn} question(s) of an earlier run withdrawn: this run settled them by rule or no longer asks them", By = DecidedBy.Rule });
        }

        var list = order.Select(k => items[k]).OrderBy(i => i.Number).ToList();
        var byStatus = list.GroupBy(i => i.Status).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => (double)g.Count());
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "catalogue",
            Subject = "the items",
            Decision = $"{list.Count} item(s) from {ledgers.Count} ledger(s): {string.Join(", ", byStatus.Select(s => $"{s.Key} {s.Value:0}"))}; {placeholders.Count} placeholder(s) for ids nothing defines; {inTwoLedgers} in two ledgers; {againstLedger} whose status sits against its ledger; {keyless} without an id; {fromProse} from prose lists by a person's answer; {gone} from sources now gone; {newlyAssigned.Count} number(s) assigned this run; {words.Count} status word(s) in use",
            By = DecidedBy.Rule,
            Figures = byStatus,
        });
        ItemCatalogue.Save(context, list, newlyAssigned, prose, words.ToDictionary(w => w.Key, w => new StatusMapping(w.Value.Word, w.Value.Mapped, w.Value.By, w.Value.Condition, w.Value.Meaning), StringComparer.OrdinalIgnoreCase));
    }

    private static (string Status, string By, string? Condition, List<string> Flags) ResolveStatus(RowReply row, ChunkRows chunk, string ledgerKind, StatusVocabulary vocabulary, JudgeThresholds thresholds)
    {
        var flags = new List<string>();
        var phrase = string.IsNullOrWhiteSpace(row.StatusAsWritten) ? null : row.StatusAsWritten.Trim();
        var word = phrase is null ? null : StatusVocabulary.WordOf(phrase);
        var known = vocabulary.Lookup(word);
        if (phrase is not null && known is not null && !StatusVocabulary.IsQualified(phrase, word!))
        {
            return (known.Mapped, known.By, row.Condition ?? known.Condition, flags);
        }

        var readerState = vocabulary.IsState(row.Status) ? row.Status!.Trim().ToLowerInvariant() : null;
        if (word is not null && chunk.Figures is not null && chunk.Figures.TryGetValue(RowId(chunk, row), out var figures))
        {
            if (figures.Cancelled >= thresholds.Yes)
            {
                flags.Add("status-by-judge");
                return ("cancelled", DecidedBy.Judge, row.Condition, flags);
            }

            if (figures.Finished >= thresholds.Yes)
            {
                flags.Add("status-by-judge");
                return ("done", DecidedBy.Judge, row.Condition, flags);
            }

            if (figures.Finished <= thresholds.No && figures.Cancelled <= thresholds.No)
            {
                // Open work: the reader's reading says which open state, else the plain one.
                if (readerState is not null && readerState is not ("done" or "cancelled"))
                {
                    flags.Add("status-by-reader");
                    return (readerState, DecidedBy.Reader, row.Condition, flags);
                }

                flags.Add("status-by-judge");
                return ("backlog", DecidedBy.Judge, row.Condition, flags);
            }

            flags.Add("status-unsure");
            return readerState is not null
                ? (readerState, DecidedBy.Reader, row.Condition, flags)
                : known is not null
                    ? (known.Mapped, known.By, row.Condition ?? known.Condition, flags)
                    : (ledgerKind == "finished-ledger" ? "done" : "backlog", DecidedBy.Rule, row.Condition, flags);
        }

        if (known is not null)
        {
            flags.Add("status-by-word");
            return (known.Mapped, known.By, row.Condition ?? known.Condition, flags);
        }

        if (readerState is not null)
        {
            flags.Add("status-by-reader");
            return (readerState, DecidedBy.Reader, row.Condition, flags);
        }

        flags.Add("status-by-ledger");
        return (ledgerKind == "finished-ledger" ? "done" : "backlog", DecidedBy.Rule, row.Condition, flags);
    }

    /// <summary>
    /// A row's identity within its chunk: its place in the chunk's rows, then its key or title for a person reading the
    /// log. A bare id is not an identity: two rows of one chunk may carry the same number (a list and a table that both
    /// count from 1), and a judge's figures keyed by it landed on the wrong row (change 15 of the verdict's redo).
    /// </summary>
    private static string RowId(ChunkRows chunk, RowReply row) =>
        chunk.Result.Rows.IndexOf(row).ToString(CultureInfo.InvariantCulture) + ":" + (string.IsNullOrWhiteSpace(row.Key) ? "title:" + (row.Title ?? "") : row.Key.Trim());

    private static readonly Regex QualifiedIdPattern = new(@"^(?<word>[A-Za-z][\w.'-]*)\s+(?<id>[A-Za-z]{0,8}-?\d{1,6}[A-Za-z]?|#\d+)$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Connectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "see", "ref", "refs", "cf", "item", "items", "issue", "issues", "ticket", "row", "rows", "spec", "adr", "decision", "the", "a", "an",
        "and", "or", "of", "in", "for", "to", "with", "per", "via", "from", "by", "id", "no", "number", "under", "after", "before", "like", "as",
    };

    /// <summary>"infra BL-144" or "infra's D35": an id that another name qualifies belongs elsewhere, so it gets no placeholder and no link; "see BL-12" is still BL-12.</summary>
    [GeneratedRegex(@"^(?<path>.+)#\d+:(?<slug>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex OldProseKey();

    /// <summary>True when a condition says the item waits on someone or something: wait, until, after, blocked, pending, needs, depends, once.</summary>
    internal static bool IsWaitingCondition(string? condition) =>
        !string.IsNullOrWhiteSpace(condition) && WaitingWords().IsMatch(condition);

    [GeneratedRegex(@"\b(wait|waits|waiting|until|after|blocked|blocks|pending|needs|need|depends|once|requires|awaits)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WaitingWords();

    /// <summary>
    /// The ids of the confirmed schemes written in <paramref name="text"/>, each once, in order. A token of no
    /// confirmed scheme is left out, and so is an id that follows one in the same clause ("infra's rulings C2, C18,
    /// D23": C is no scheme here, so D23 is that repository's too), or that follows a numbering word with only ids
    /// and separators between ("identity phases D2+D3" numbers phases, not decisions). A clause ends at a period,
    /// a semicolon, a colon, a bracket or a table cell's edge.
    /// </summary>
    internal static List<string> IdsByCode(string text, IReadOnlyList<IdScheme> schemes)
    {
        var found = new List<string>();
        var foreignClause = false;
        var numberedChain = false;
        var clauseStart = 0;
        var lastEnd = 0;
        foreach (Match match in IdTokenPattern.Matches(text))
        {
            var between = text[lastEnd..match.Index];
            var boundary = between.LastIndexOfAny(ClauseEnds);
            if (boundary >= 0)
            {
                foreignClause = false;
                numberedChain = false;
                clauseStart = lastEnd + boundary + 1;
                between = between[(boundary + 1)..];
            }

            var joiner = between.Trim();
            var joinedToPrevious = lastEnd > clauseStart && (joiner.Length == 0 || joiner.All(c => c is ',' or '/' or '+' or '&' or ' ') || string.Equals(joiner, "and", StringComparison.OrdinalIgnoreCase) || string.Equals(joiner, ", and", StringComparison.OrdinalIgnoreCase) || string.Equals(joiner, "or", StringComparison.OrdinalIgnoreCase));
            if (!joinedToPrevious)
            {
                numberedChain = NumberingWord().IsMatch(between);
            }

            lastEnd = match.Index + match.Length;
            var normalised = IdNormaliser.Normalise(match.Value, schemes);
            if (normalised is null)
            {
                if (!match.Value.All(char.IsDigit))
                {
                    foreignClause = true;
                }

                continue;
            }

            if (foreignClause || numberedChain)
            {
                continue;
            }

            if (!found.Contains(normalised.Key, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(normalised.Key);
            }
        }

        return found;
    }

    private static readonly char[] ClauseEnds = ['.', ';', ':', '(', ')', '[', ']', '|'];

    private static readonly Regex IdTokenPattern = new(@"(?<![\w-])[A-Za-z§]{1,8}\.?[A-Za-z]{0,3}-\d{1,6}[A-Za-z]?(?![\w-])|(?<![\w-])[A-Z]{1,3}\d{1,5}(?![\w-])", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\b(phase|phases|step|steps|wave|waves|part|parts|level|levels|stage|stages|tier|tiers|round|rounds|week|weeks|day|days)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberingWord();

    /// <summary>True when <paramref name="id"/> is written in <paramref name="line"/> but not as a reference of this repository (another repository's list, a numbered phase).</summary>
    internal static bool IsForeignInLine(string line, string id, IReadOnlyList<IdScheme> schemes)
    {
        if (IdNormaliser.Normalise(id, schemes) is not { } normalised || !line.Contains(id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IdsByCode(line, schemes).Contains(normalised.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The word the ledgers write before <paramref name="id"/> when it is another repository's name ("infra", "infra's"),
    /// else null. A name is a word that is used as one: it stands in the possessive before an id somewhere ("infra's
    /// BL-134"), or it stands before three or more different ids. A verb before one or two ids ("closes BL-14") is
    /// not a name, and the ids it precedes may be the other repository's own numbers, which collide with this one's,
    /// so whether they are defined here says nothing.
    /// </summary>
    internal static string? QualifierInText(string id, IReadOnlyList<string> texts)
    {
        var pattern = new Regex(@"(?<![\w-])(?<word>[A-Za-z][\w.'-]*)\s+" + Regex.Escape(id) + @"(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            foreach (Match match in pattern.Matches(text))
            {
                var word = match.Groups["word"].Value;
                if (!tried.Add(word) || !IsQualifiedByAnotherName(word + " " + id))
                {
                    continue;
                }

                var stem = word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ? word[..^2] : word;
                var before = new Regex(@"(?<![\w-])" + Regex.Escape(stem) + @"(?<possessive>'s)?\s+(?<id>[A-Za-z]{0,8}-?\d{1,6}[A-Za-z]?|#\d+)(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var possessive = false;
                foreach (var t in texts)
                {
                    foreach (Match m in before.Matches(t))
                    {
                        ids.Add(m.Groups["id"].Value);
                        possessive |= m.Groups["possessive"].Success;
                    }
                }

                if (possessive || ids.Count >= 3)
                {
                    return stem;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A ledger under a folder named example, test, fixture or the like (backlog TR-25) is not the repository's own
    /// work until a person says so: its rows are set aside, a question asks, and an answer of "read" takes them in.
    /// </summary>
    private static async Task<List<LedgerEntry>> SetAsideFixtureLedgersAsync(SurveyContext context, List<LedgerEntry> ledgers, CancellationToken cancellationToken)
    {
        var kept = new List<LedgerEntry>();
        foreach (var ledger in ledgers)
        {
            var folder = FixtureRule.FolderOf(ledger.Path);
            if (folder is null)
            {
                kept.Add(ledger);
                continue;
            }

            var question = new Question($"ledger-fixture:{ledger.Path}", PhaseName,
                $"The ledger {ledger.Path} lies under a folder named \"{folder}\", which usually holds examples, tests or fixtures rather than the repository's own work. Should its rows become items of this repository?",
                [new QuestionOption("set-aside", "no; the file stays a document, its rows are not items"), new QuestionOption("read", "yes; read its rows as items")],
                "set-aside")
            {
                About = new QuestionAbout("ledger", [ledger.Path], [], null),
                DefaultReason = "a folder of examples, tests or fixtures usually holds no work of the repository's own",
            };
            var answer = await context.AskAsync(question, ledger.Path, "no answer; the ledger is set aside and its rows are not items", cancellationToken).ConfigureAwait(false);
            if (answer?.Option == "read")
            {
                context.State.DeleteRecord(SkippedLedgerKind, ledger.Path);
                kept.Add(ledger);
                continue;
            }

            context.State.SaveRecord(SkippedLedgerKind, ledger.Path, "{\"folder\":\"" + folder + "\"}", context.RunId);
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "ledgers",
                Subject = ledger.Path,
                Decision = $"set aside: it lies under \"{folder}\", a folder of examples, tests or fixtures; its rows are not items of this repository unless a person answers the question; the file stays a document",
                By = DecidedBy.Rule,
                Flags = ["from-fixture"],
            });
        }

        return kept;
    }

    internal static bool IsQualifiedByAnotherName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = QualifiedIdPattern.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var word = match.Groups["word"].Value;
        return word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || !Connectors.Contains(word);
    }

    /// <summary>A status phrase goes to the judge when its word is in no vocabulary, or when it says more than the word plus a date or a hash (ADR 0020 rule 12: "CLOSED as declined" is not CLOSED).</summary>
    private static bool NeedsTheJudge(string phrase, StatusVocabulary vocabulary)
    {
        var word = StatusVocabulary.WordOf(phrase);
        return vocabulary.Lookup(word) is null || StatusVocabulary.IsQualified(phrase, word);
    }

    private static string Who(string by) => by switch
    {
        DecidedBy.Person => "confirmed by a person",
        DecidedBy.Judge => "the judge's reading of the rows",
        DecidedBy.Reader => "the reader's reading of the guides",
        _ => "the ledger's kind, by rule",
    };

    private static string FirstSentence(string text)
    {
        var cleaned = text.Trim().TrimStart('-', '*', ' ').Trim();
        var end = cleaned.IndexOfAny(['.', ';', ':']);
        return end > 20 ? cleaned[..end] : cleaned;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, RunContext.JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>One chunk of a ledger with what the reader and the judge said about it.</summary>
    private sealed class ChunkRows(LedgerEntry ledger, SourceFile file, TextChunk chunk, RowsResult result)
    {
        public LedgerEntry Ledger { get; } = ledger;
        public SourceFile File { get; } = file;
        public TextChunk Chunk { get; } = chunk;
        public RowsResult Result { get; } = result;
        public Dictionary<string, StatusFigures>? Figures { get; set; }
        public Dictionary<string, ShapeFigures>? Shapes { get; set; }
    }

    private sealed class WordUse
    {
        public string Word { get; set; } = "";
        public string Mapped { get; set; } = "";
        public string By { get; set; } = "";
        public string? Condition { get; set; }
        public string? Meaning { get; set; }

        /// <summary>The keys of the rows that use the word, each once; the overlap of two chunks shows a row twice.</summary>
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Rows => Keys.Count;

        /// <summary>True while the mapping came from a qualified phrase only ("CLOSED as declined"), not a plain use of the word.</summary>
        public bool FromQualified { get; set; }

        /// <summary>Up to three phrases as the ledgers write the word, for the question's context.</summary>
        public List<string> Samples { get; } = [];
    }

    /// <summary>What a rows unit stores (ADR 0017): the rows as read and the figures the log needs.</summary>
    internal sealed class RowsResult
    {
        /// <summary>True when the chunk returned nothing and was read a second time on a person's word.</summary>
        public bool ReadAgain { get; set; }

        public List<RowReply> Rows { get; set; } = [];
        public List<ProseWorkReply> ProseWork { get; set; } = [];
        public List<string> Notes { get; set; } = [];
        public string AnsweredBy { get; set; } = "";
        public int CallSeq { get; set; }
        public decimal Usd { get; set; }
        public List<string> FlaggedParts { get; set; } = [];
        public int StartLine { get; set; }
        public int EndLine { get; set; }
    }

    internal sealed class StatusFigures
    {
        public double Finished { get; set; }
        public double Cancelled { get; set; }
    }

    /// <summary>The judge's reading of a row's kind and of each relation the reader proposed (the set items-shape).</summary>
    internal sealed class ShapeFigures
    {
        /// <summary>How surely the row's condition says the item waits on someone or something; null when the row has no condition or the judge gave none.</summary>
        public double? Waits { get; set; }

        public string? Type { get; set; }
        public double TypeConfidence { get; set; }
        public Dictionary<string, double> TypeProbabilities { get; set; } = [];
        public List<RelationFigure> Relations { get; set; } = [];
    }

    internal sealed class RelationFigure
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public double Confidence { get; set; }
    }

    // The reader's reply shapes (schemas/item-rows.json), read with the forgiving snake_case options.
    internal sealed class RowsReply
    {
        public List<RowReply>? Rows { get; set; }
        public List<ProseWorkReply>? ProseWork { get; set; }
        public List<string>? Notes { get; set; }
        public string? Reasoning { get; set; }
    }

    internal sealed class RowReply
    {
        public string? Key { get; set; }
        public List<string>? Ids { get; set; }
        public string? Title { get; set; }
        public string? StatusAsWritten { get; set; }
        public string? Status { get; set; }
        public string? Condition { get; set; }
        public string? Tier { get; set; }
        public string? Size { get; set; }
        public List<DateReply>? Dates { get; set; }
        public string? Detail { get; set; }
        public List<ReferenceReply>? References { get; set; }
        public LineEvidenceReply? Evidence { get; set; }
        public double? Confidence { get; set; }
        public string? Type { get; set; }
        public string? TypeAsWritten { get; set; }
        public double? TypeConfidence { get; set; }
        public List<RelationReply>? Relations { get; set; }

        /// <summary>True for a row the reader missed and code filled from the table (never in the reader's reply).</summary>
        public bool FilledByRule { get; set; }
        public string? Reasoning { get; set; }
    }

    internal sealed class RelationReply
    {
        public string? Kind { get; set; }
        public string? Id { get; set; }
        public string? AsWritten { get; set; }
        public double? Confidence { get; set; }
    }

    internal sealed class DateReply
    {
        public string? What { get; set; }
        public string? Date { get; set; }
    }

    internal sealed class ReferenceReply
    {
        public string? Kind { get; set; }
        public string? Text { get; set; }
    }

    internal sealed class LineEvidenceReply
    {
        public int? Line { get; set; }
        public string? Quote { get; set; }
    }

    internal sealed class ProseWorkReply
    {
        public string? Heading { get; set; }
        public List<string>? Bullets { get; set; }
        public string? Reasoning { get; set; }
    }
}
