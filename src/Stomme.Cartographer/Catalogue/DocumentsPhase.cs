using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Runner;

namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// Phase 2 (the plan): every document of the map gets a card. Each document is one unit of work (ADR 0017),
/// or one unit per chunk when it is larger than one call (ADR 0014), the reader shown a fixed-shape block of
/// what the chunks before said (ADR 0019): the title, the kind, the language, a summary, the tags (topics,
/// components, people, dates), the references, the decisions it records (ADR 0006, checked later), and what it
/// says about its own standing (ADR 0020 rule 9). Files written for agents go on shelves of their own and stay
/// git's (ADR 0020 rule 8). Code merges the chunks, resolves the explicit references it can (ADR 0012), keeps
/// the cards in the state, and leaves the body to the atlas, which copies the source text.
/// </summary>
public sealed partial class DocumentsPhase(SurveyContext survey) : IPhase
{
    internal const string PhaseName = "documents";
    private const int ListCap = 40;
    private const int ReferenceCap = 200;
    private const int DecisionCap = 40;
    private const int SummaryChars = 2_000;

    public string Name => PhaseName;

    public Task RunAsync(CancellationToken cancellationToken) => RunAsync(survey, cancellationToken);

    private static async Task RunAsync(SurveyContext context, CancellationToken cancellationToken)
    {
        var map = context.Map ?? throw new InvalidOperationException("The documents phase needs the repository map, and orientation has not run into this state.");
        var budgets = context.Options.Budgets;
        var schemes = map.IdSchemes.Where(s => s.Confirmed).ToList();
        var ledgers = map.Ledgers.Where(l => l.Kind is "open-ledger" or "finished-ledger").ToList();
        var itemScheme = schemes.FirstOrDefault(s => s.RefersTo == "work-item" && ledgers.Any(l => string.Equals(l.Path, s.WhereDefined, StringComparison.OrdinalIgnoreCase)))
            ?? schemes.FirstOrDefault(s => s.RefersTo == "work-item");

        // 1. Which files are documents: the map's document-like files, less the ledgers (their rows are the items), the unreadable, and what orientation found not to be a document.
        var chosen = new List<(SourceFile File, MappedFile Mapped)>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mapped in map.Files.Where(f => f.Category is "Document" or "TextUnknown").OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            string? reason = null;
            if (mapped.Kind is "open-ledger" or "finished-ledger")
            {
                // A ledger is a document too, made by rule after the reading: its rows are the items, its prose stays.
                continue;
            }
            else if (mapped.Kind == "unreadable")
            {
                reason = "unreadable";
            }
            else if (OrientationPhase.IsNotADocument(context, mapped.Kind))
            {
                reason = mapped.Kind;
            }

            var file = reason is null ? context.File(mapped.Path) : null;
            if (reason is null && file is null)
            {
                reason = "gone from the input";
            }

            if (reason is not null)
            {
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                continue;
            }

            chosen.Add((file!, mapped));
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "documents",
            Subject = "the documents",
            Decision = $"{chosen.Count} document(s) to describe, one unit each (a large one in chunks, ADR 0014); not documents: {(skipped.Count == 0 ? "none" : string.Join(", ", skipped.OrderByDescending(s => s.Value).Select(s => $"{s.Value} {s.Key}")))}",
            By = DecidedBy.Rule,
            Figures = new Dictionary<string, double> { ["documents"] = chosen.Count, ["bytes"] = chosen.Sum(c => (double)c.File.Size) },
        });
        if (chosen.Count == 0)
        {
            return;
        }

        var digest = context.State.LoadRecord("digest", "map") ?? MapDigest.Build(map, context.Shown, budgets.DigestChars);
        var system = context.SystemPrompt("document-card", "document-card", new Dictionary<string, string>
        {
            ["digest"] = digest,
            ["kinds"] = OrientationPhase.KindVocabulary(context),
            ["not_documents"] = OrientationPhase.KindVocabulary(context, "not-a-document"),
            ["statuses"] = StatusesText(context),
        });
        var salt = context.Playbook.Salt(["rules", "document-card"], ["document-card"], ["kinds", "document-statuses", "agent-guides"], []);
        var agentGuides = AgentGuidePatterns(context.Playbook);

        // 2. One card per document, read in parallel up to the reader's concurrency; the chunks of one document in order.
        var cards = new ConcurrentDictionary<string, DocumentCard>(StringComparer.Ordinal);
        var done = 0;
        await Parallel.ForEachAsync(chosen,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Run.Options.Reader.Concurrency), CancellationToken = cancellationToken },
            async (entry, ct) =>
            {
                var agentPath = agentGuides.Any(p => p.IsMatch(entry.File.Path));
                var card = await ReadDocumentAsync(context, entry.File, entry.Mapped, system, salt, agentPath, ct).ConfigureAwait(false);
                cards[card.Path] = card;

                // Kept at once, so that a run that fails later still renders what it described; the final save adds the resolved references.
                DocumentCatalogue.SaveOne(context, card);
                var n = Interlocked.Increment(ref done);
                if (n % 25 == 0 || n == chosen.Count)
                {
                    context.Progress($"Documents: {n} of {chosen.Count} described");
                }

                context.Touch($"documents: {n} of {chosen.Count} described, the last {entry.File.Path}");
            }).ConfigureAwait(false);

        // 3. References resolved by code (ADR 0012; ADR 0020 rule 2): ids of the item scheme to items, paths to documents of the atlas. The links phase proposes and judges the rest.
        // The ledgers as documents (the verify session's check of 2026-11-04): their legend, notes and indexes are content
        // people need, so each ledger gets a card by rule, with the whole file as the body and no reader call.
        foreach (var ledger in ledgers)
        {
            var ledgerFile = context.File(ledger.Path);
            if (ledgerFile is null || ledgerFile.NotRead is not null || cards.ContainsKey(ledger.Path))
            {
                continue;
            }

            var ledgerCard = new DocumentCard
            {
                Path = ledger.Path,
                Title = DocumentTitle.Of(context.Decode(ledgerFile), ledger.Path),
                Kind = ledger.Kind,
                KindByOrientation = ledger.Kind,
                Summary = $"A ledger of work items ({ledger.Kind.Replace('-', ' ')}): its rows are the items in items/; the text around the rows, its legend, notes and indexes, is kept here with the whole file as the body.",
                Status = "current",
                By = DecidedBy.Rule,
                Flags = ["ledger", "by-rule"],
                Source = new DocumentSource { Path = ledger.Path, Sha256 = ledgerFile.Sha256, Size = ledgerFile.Size, Format = ledgerFile.Extension.TrimStart('.'), Chunks = 1, RunId = context.RunId },
            };
            cards[ledger.Path] = ledgerCard;
            DocumentCatalogue.SaveOne(context, ledgerCard);
        }

        // A document under a specs folder whose name carries an item id is a spec, whatever the reader said of its shape.
        foreach (var card in cards.Values)
        {
            if (!string.Equals(card.Kind, "spec", StringComparison.OrdinalIgnoreCase) && IsSpecByPath(card.Path, schemes))
            {
                card.Reasoning = (card.Reasoning is null ? "" : card.Reasoning + " ") + $"The reader read it as {card.Kind}; its place under a specs folder and the item id in its name make it a spec by rule.";
                card.Kind = "spec";
                card.Flags.Add("kind-by-path");
                DocumentCatalogue.SaveOne(context, card);
            }
        }

        ResolveReferences(context, cards, schemes, itemScheme);

        // 4. Documents of earlier runs whose file is gone: kept and marked, never deleted (ADR 0017).
        var gone = 0;
        var dropped = 0;
        foreach (var old in DocumentCatalogue.Load(context.State))
        {
            if (cards.ContainsKey(old.Path))
            {
                continue;
            }

            if (context.File(old.Path) is not null)
            {
                // The file is there and is no longer read as a document (bytes under a document's extension, a backup copy,
                // a file withheld or excluded, a kind that is not a document): the card described something that is not
                // one, so it goes; only a card whose file is gone is kept and marked.
                context.State.DeleteRecord(DocumentCatalogue.DocumentKind, old.Path);
                dropped++;
                continue;
            }

            if (!old.SourceGone)
            {
                old.SourceGone = true;
                if (!old.Flags.Contains("source-gone"))
                {
                    old.Flags.Add("source-gone");
                }

                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "document", Subject = old.Path, Decision = "the file is gone from the input or is no longer a document; its card is kept as source gone, never deleted", By = DecidedBy.Rule, Flags = ["source-gone"] });
            }

            cards[old.Path] = old;
            gone++;
        }

        if (dropped > 0)
        {
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "document", Subject = $"{dropped} card(s) of earlier runs", Decision = "dropped: their files are in the input and are not read as documents now (not text, a backup copy, withheld, excluded, or a kind that is not a document)", By = DecidedBy.Rule });
        }

        // 4b. Titles, by code only (the verdict's redo, change 17): the file's own title, never a raw first line, a section
        // heading or the reader's word; a heading copied onto three or more documents names none of them, so the path does.
        foreach (var card in cards.Values.Where(c => !c.SourceGone))
        {
            var file = context.File(card.Path);
            var decoded = file is null ? null : context.Decode(file);
            card.Title = DocumentTitle.Of(decoded, card.Path);
        }

        foreach (var copied in cards.Values.Where(c => !c.SourceGone && !string.IsNullOrWhiteSpace(c.Title)).GroupBy(c => c.Title!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3))
        {
            foreach (var card in copied)
            {
                card.Title = DocumentTitle.PathName(card.Path);
                if (!card.Flags.Contains("title-by-path"))
                {
                    card.Flags.Add("title-by-path");
                }
            }
        }

        // Names from paths that are still alike (code-review/SKILL in five example folders) take as much of the path as
        // tells them apart, up to the whole of it.
        for (var segments = 3; segments <= 12; segments++)
        {
            var alike = cards.Values.Where(c => !c.SourceGone && c.Flags.Contains("title-by-path") || (!c.SourceGone && c.Title == DocumentTitle.PathName(c.Path)))
                .GroupBy(c => c.Title!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
            if (alike.Count == 0)
            {
                break;
            }

            foreach (var card in alike.SelectMany(g => g))
            {
                card.Title = DocumentTitle.PathName(card.Path, segments);
                if (!card.Flags.Contains("title-by-path"))
                {
                    card.Flags.Add("title-by-path");
                }
            }
        }

        // 4c. A document under a folder of examples, tests or fixtures says so (the verdict's redo, change 13): it is content,
        // and it is not the repository's own word. A ledger a person said to read as the repository's work is the exception.
        foreach (var card in cards.Values)
        {
            var fixture = FixtureRule.FolderOf(card.Path) is not null && context.Answers.GetOption("ledger-fixture:" + card.Path) != "read";
            card.Flags.Remove("from-fixture");
            if (fixture)
            {
                card.Flags.Add("from-fixture");
            }
        }

        // 5. A kind orientation could not settle from the head (its question kind:<path>) is settled by the whole text (ADR 0007).
        var settled = 0;
        foreach (var asked in context.Book.Open().Where(q => q.Question.Key.StartsWith("kind:", StringComparison.Ordinal)).ToList())
        {
            var path = asked.Question.Key["kind:".Length..];
            if (cards.TryGetValue(path, out var card) && card.By == DecidedBy.Reader && card.Kind != "other" && !card.SourceGone && context.Book.Withdraw(asked.Question.Key))
            {
                settled++;
            }
        }

        if (settled > 0)
        {
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "questions", Subject = $"{settled} kind question(s)", Decision = "withdrawn: the whole text gave the kind orientation could not read from the head", By = DecidedBy.Rule });
        }

        // 6. The glossary (ADR 0017): the components and people the documents name most, bounded.
        AddToGlossary(context, cards.Values);

        // 7. The catalogue record and the log.
        var list = cards.Values.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        var byKind = list.GroupBy(c => c.Kind).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => (double)g.Count());
        var byStatus = list.GroupBy(c => c.Status).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "catalogue",
            Subject = "the documents",
            Decision = $"{list.Count} document(s): {string.Join(", ", byKind.Select(k => $"{k.Key} {k.Value:0}"))}; standing by their own words: {string.Join(", ", byStatus)}; {list.Count(c => c.ForAgents)} written for agents ({list.Count(c => c.Shelf == "process")} process, {list.Count(c => c.Shelf == "product")} product), reference-only; {list.Count(c => c.Flags.Contains("chunked"))} read in chunks; {list.Count(c => c.Flags.Contains("kind-revised"))} whose kind the whole text revised; {list.Count(c => c.Flags.Contains("not-a-document"))} found not to be documents by the whole text; {list.Count(c => c.Flags.Contains("discarded-by-gate"))} whose description the gate discarded; {list.Count(c => c.Flags.Contains("reader-failed") || c.Flags.Contains("judge-failed"))} whose reader or judge call failed (their units stay undone; the next run retries them); {gone} from sources now gone; {list.Sum(c => c.Decisions.Count)} decision(s) noted for the decisions phase",
            By = DecidedBy.Rule,
            Figures = byKind,
        });
        DocumentCatalogue.Save(context, list);
        context.Touch("documents done");
    }

    /// <summary>True for text no person wrote as a document: a line over 10,000 characters with fewer than three spaces in a hundred (an unwrapped paragraph has fifteen), or a long text that dense throughout.</summary>
    internal static bool LooksGenerated(DecodedDocument decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        if (decoded.Lines.Any(l => l.Length > 10_000 && l.Count(c => c == ' ') * 100 < l.Length * 3))
        {
            return true;
        }

        if (decoded.Text.Length < 20_000)
        {
            return false;
        }

        var breaks = decoded.Text.Count(c => c == ' ' || c == '\n');
        return breaks * 100 < decoded.Text.Length * 3;
    }

    /// <summary>The folder an agent guide belongs to: empty at the repository root, else the folder above its dot-folder or its file (src/App for src/App/.claude/skills/x/SKILL.md).</summary>
    internal static string AgentGuideFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('.') || i == segments.Length - 1)
            {
                return string.Join('/', segments.Take(i));
            }
        }

        return "";
    }

    /// <summary>The paths of files written for agents (ADR 0020 rule 8), from the playbook's agent-guides table.</summary>
    internal static List<Regex> AgentGuidePatterns(Playbook playbook)
    {
        ArgumentNullException.ThrowIfNull(playbook);
        return (playbook.Data("agent-guides")["patterns"] as JsonArray ?? [])
            .Select(p => FileTable.GlobToRegex(p!.GetValue<string>()))
            .ToList();
    }

    private static async Task<DocumentCard> ReadDocumentAsync(SurveyContext context, SourceFile file, MappedFile mapped, string system, string salt, bool agentPath, CancellationToken cancellationToken)
    {
        var budgets = context.Options.Budgets;
        var decoded = context.Decode(file);
        if (decoded is null)
        {
            var unreadable = new DocumentCard
            {
                Path = file.Path,
                Title = mapped.Title,
                Kind = mapped.Kind,
                KindByOrientation = mapped.Kind,
                Language = mapped.Language,
                Summary = "No decoder could read this file; it is catalogued only (ADR 0014).",
                Source = SourceOf(file, "none", 0, context.RunId),
                Flags = ["unreadable"],
                By = DecidedBy.Rule,
                Reasoning = "no decoder reads this format",
            };
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "document", Subject = file.Path, Decision = "no decoder could read it; catalogued only", By = DecidedBy.Rule, Flags = ["unreadable"] });
            return unreadable;
        }

        if (LooksGenerated(decoded))
        {
            // Minified code, a source map, an export: no reader reads it, and the judge's budget would not hold it (ADR 0014, 0016).
            var generated = new DocumentCard
            {
                Path = file.Path,
                Title = mapped.Title,
                Kind = "generated",
                KindByOrientation = mapped.Kind,
                Summary = $"Machine-generated or minified text ({decoded.Lines.Count} line(s), the longest {decoded.Lines.Max(l => l.Length):N0} characters); not read by the reader, catalogued only.",
                Source = SourceOf(file, decoded.Format, 0, context.RunId),
                Flags = ["not-a-document", "generated-by-rule"],
                By = DecidedBy.Rule,
                Reasoning = "a line longer than 10,000 characters, or fewer than three spaces in a hundred characters",
            };
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "document", Subject = file.Path, Decision = "generated or minified text by rule; not read, catalogued only", By = DecidedBy.Rule, Flags = ["not-a-document", "generated-by-rule"] });
            return generated;
        }

        var chunks = Chunker.Chunk(decoded, budgets.DocumentChunkTokens, budgets.ChunkOverlapLines, budgets.DocumentOverlapChars);
        var results = new List<DocumentChunkResult>();
        var resumed = 0;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = chunks.Count == 1 ? $"document/{file.Path}" : $"document/{file.Path}#{chunk.Index}";
            var hash = RunContext.HashOf("document-v1", file.Sha256, chunk.Index.ToString(CultureInfo.InvariantCulture), chunk.Text, salt, agentPath ? "agent-v2" : "");
            var stored = context.State.DoneUnit(PhaseName, key, hash);
            if (stored?.Result is not null && Deserialize<DocumentChunkResult>(stored.Result) is { } kept)
            {
                results.Add(kept);
                resumed++;
                continue;
            }

            context.Touch($"documents: {file.Path}{(chunks.Count == 1 ? "" : $", chunk {chunk.Index + 1} of {chunks.Count}")}");
            DocumentChunkResult result;
            try
            {
                result = await AskChunkAsync(context, file, mapped, decoded, chunk, results, agentPath, system, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ReaderException or JudgeUnavailableException)
            {
                // The reader could not answer for this chunk after its retries (ADR 0004), or the judge could not check it
                // (ADR 0013): the document keeps orientation's reading, its unit stays undone so that a later run retries
                // it, and the other documents go on.
                var who = ex is ReaderException ? "reader" : "judge";
                var failed = new DocumentCard
                {
                    Path = file.Path,
                    Title = decoded.Title ?? mapped.Title,
                    Kind = mapped.Kind,
                    KindByOrientation = mapped.Kind,
                    Language = mapped.Language,
                    Summary = $"The {who} could not handle this document in this run ({Cut(ex.Message, 300)}); orientation's reading of the head stands, the text is in the body below, and the next run retries it.",
                    Source = SourceOf(file, decoded.Format, chunks.Count, context.RunId),
                    Flags = [who + "-failed"],
                    By = DecidedBy.Rule,
                    Reasoning = $"the {who} failed on chunk {chunk.Index + 1} of {chunks.Count}",
                };
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "document", Subject = file.Path, Decision = $"the {who} failed on chunk {chunk.Index + 1} of {chunks.Count}: {Cut(ex.Message, 300)}; orientation's reading stands and the unit stays undone for a later run", By = DecidedBy.Rule, Flags = [who + "-failed"], Evidence = [new Evidence(file.Path, chunk.StartLine, null)] });
                return failed;
            }

            context.State.CompleteUnit(PhaseName, context.RunId, key, hash, result.Usd, result);
            results.Add(result);
        }

        var card = Merge(context, file, mapped, decoded, chunks.Count, results, agentPath);
        var allResumed = resumed == chunks.Count;
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "document",
            Subject = file.Path,
            Decision = (allResumed ? $"resumed from the state (read in run {results[0].RunId}): " : "") + Describe(card),
            By = allResumed ? DecidedBy.Rule : card.By,
            Model = results.Select(r => r.AnsweredBy).FirstOrDefault(m => !string.IsNullOrEmpty(m)),
            Evidence = [new Evidence(file.Path, card.EvidenceLine, card.EvidenceQuote)],
            Reasoning = card.Reasoning,
            Confidence = card.Confidence,
            Flags = card.Flags.ToList(),
            Call = results.Where(r => r.CallSeq > 0).Select(r => (int?)r.CallSeq).LastOrDefault(),
        });
        return card;
    }

    private static async Task<DocumentChunkResult> AskChunkAsync(
        SurveyContext context, SourceFile file, MappedFile mapped, DecodedDocument decoded, TextChunk chunk,
        IReadOnlyList<DocumentChunkResult> before, bool agentPath, string system, CancellationToken cancellationToken)
    {
        var instruction = DocumentSummary.Build(file, mapped, decoded, chunk, before, agentPath, context.Options.Budgets.ChunkSummaryChars);
        var parts = new List<ReaderPart>
        {
            new ReaderPart.Instruction(instruction),
            new ReaderPart.InputText(file.Path, chunk.StartLine - chunk.OverlapLines, chunk.EndLine, chunk.Text) { Position = chunk.Position },
        };
        var purpose = chunk.Count == 1 ? $"the document {file.Path}" : $"the document {file.Path}, chunk {chunk.Index + 1} of {chunk.Count}";
        var request = new ReaderRequest(PhaseName, purpose, context.Run.Options.Models.For(PhaseName), system, parts)
        {
            GlossaryText = context.Glossary.Render(),
        };
        var reply = await context.Reader.AskAsync(request, cancellationToken).ConfigureAwait(false);
        var result = new DocumentChunkResult
        {
            Index = chunk.Index,
            StartLine = chunk.StartLine,
            EndLine = chunk.EndLine,
            RunId = context.RunId,
            FlaggedParts = reply.FlaggedParts.Where(p => p != "glossary").ToList(),
        };
        if (reply.Reply is null)
        {
            result.Discarded = true;
            result.DiscardReason = reply.DiscardReason;
            return result;
        }

        var parsed = OrientationPhase.Parse<CardReply>(reply.Json) ?? new CardReply();
        // The reader numbers lines within the text as shown, which starts with the overlap from the chunk before.
        int? Absolute(int? line) => line is { } l && l >= 1 ? chunk.StartLine - chunk.OverlapLines + l - 1 : null;
        result.Title = Clean(parsed.Title, 300);
        result.Kind = Clean(parsed.Kind, 60);
        result.Language = Clean(parsed.Language, 12);
        result.Summary = Cut((parsed.Summary ?? "").Trim(), SummaryChars);
        result.Topics = Distinct(parsed.Topics, ListCap, 80);
        result.Components = Distinct(parsed.Components, ListCap, 120);
        result.People = Distinct(parsed.People, ListCap, 120);
        result.Dates = (parsed.Dates ?? []).Where(d => !string.IsNullOrWhiteSpace(d.Date)).Take(ListCap)
            .Select(d => new DocumentDate { What = Clean(d.What, 60) ?? "", Date = Clean(d.Date, 40) ?? "" }).ToList();
        result.References = (parsed.References ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Text)).Take(ReferenceCap)
            .Select(r => new DocumentReference { Kind = (Clean(r.Kind, 20) ?? "mention").ToLowerInvariant(), Text = Clean(r.Text, 300)!, Line = Absolute(r.Line) }).ToList();
        result.Decisions = (parsed.Decisions ?? []).Where(d => !string.IsNullOrWhiteSpace(d.Text)).Take(DecisionCap)
            .Select(d => new DocumentDecision { Text = Clean(d.Text, 400)!, Line = Absolute(d.Line), Quote = Clean(d.Quote, 200) }).ToList();
        result.Status = Clean(parsed.Status, 40);
        result.StatusAsWritten = Clean(parsed.StatusAsWritten, 200);
        result.SupersededBy = Clean(parsed.SupersededBy, 200);
        result.SelfRating = Clean(parsed.SelfRating, 200);
        result.Audience = Clean(parsed.Audience, 20)?.ToLowerInvariant();
        result.Shelf = Clean(parsed.Shelf, 20)?.ToLowerInvariant();
        result.EvidenceLine = Absolute(parsed.Evidence?.Line);
        result.EvidenceQuote = Clean(parsed.Evidence?.Quote, 200);
        result.Confidence = parsed.Confidence;
        result.Reasoning = Clean(parsed.Reasoning, 800);
        result.AnsweredBy = reply.Reply.AnsweredBy ?? reply.Reply.Model;
        result.CallSeq = reply.Reply.CallSeq;
        result.Usd = reply.Reply.Cost.Usd;
        return result;
    }

    /// <summary>The chunks' answers into one card, by code: the first chunk's reading leads, the lists are unions, the standing is the first stated.</summary>
    private static DocumentCard Merge(SurveyContext context, SourceFile file, MappedFile mapped, DecodedDocument decoded, int chunkCount, IReadOnlyList<DocumentChunkResult> results, bool agentPath)
    {
        var read = results.Where(r => !r.Discarded).ToList();
        var first = read.FirstOrDefault();
        var card = new DocumentCard
        {
            Path = file.Path,
            KindByOrientation = mapped.Kind,
            Source = SourceOf(file, decoded.Format, chunkCount, context.RunId),
        };
        if (first is null)
        {
            // Every chunk's output was discarded by the gate on the way out (ADR 0013): orientation's reading of the head stands, and the card says so.
            card.Title = decoded.Title ?? mapped.Title ?? Path.GetFileName(file.Path);
            card.Kind = mapped.Kind;
            card.Language = LanguageCode(mapped.Language);
            card.Summary = $"The reader's description was discarded by the injection gate ({results[0].DiscardReason}); orientation's reading of the head stands, and the text is in the body below.";
            card.Flags.Add("discarded-by-gate");
            card.By = DecidedBy.Rule;
            card.Reasoning = "the gate on the way out found the output followed the input's instructions, twice (ADR 0013)";
        }
        else
        {
            // The file's own title (front matter, the first level-one heading) before the reader's word, which on a long
            // document was a section heading such as "When to use" (backlog TR-26).
            card.Title = decoded.Title ?? first.Title ?? mapped.Title ?? Path.GetFileName(file.Path);
            var kind = OrientationPhase.NormaliseKind(first.Kind);
            if (!OrientationPhase.KindIsKnown(context, kind))
            {
                card.Flags.Add("kind-unknown");
                kind = "other";
            }

            card.Kind = kind;
            if (!string.Equals(kind, mapped.Kind, StringComparison.Ordinal))
            {
                card.Flags.Add("kind-revised");
            }

            if (OrientationPhase.IsNotADocument(context, kind))
            {
                card.Flags.Add("not-a-document");
            }

            card.Language = LanguageCode(read.Select(r => r.Language).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? mapped.Language);
            card.Summary = chunkCount == 1
                ? first.Summary ?? ""
                : Cut(string.Join(" ", read.Select(r => Cut(r.Summary ?? "", 400))), SummaryChars + 400) + $" (the summaries of {chunkCount} chunks, joined by the engine)";
            card.Topics = Union(read.Select(r => r.Topics), ListCap);
            card.Components = Union(read.Select(r => r.Components), ListCap);
            card.People = Union(read.Select(r => r.People), ListCap);
            var dates = new List<DocumentDate>();
            foreach (var date in read.SelectMany(r => r.Dates))
            {
                if (!dates.Any(d => string.Equals(d.Date, date.Date, StringComparison.Ordinal) && string.Equals(d.What, date.What, StringComparison.OrdinalIgnoreCase)) && dates.Count < ListCap)
                {
                    dates.Add(date);
                }
            }

            card.Dates = dates;
            var references = new List<DocumentReference>();
            foreach (var reference in read.SelectMany(r => r.References))
            {
                if (!references.Any(e => e.Kind == reference.Kind && string.Equals(e.Text, reference.Text, StringComparison.OrdinalIgnoreCase)) && references.Count < ReferenceCap)
                {
                    references.Add(reference);
                }
            }

            foreach (var reference in references)
            {
                reference.Line = LineByCode(decoded, reference.Text, reference.Line);
            }

            var confirmedSchemes = context.Map?.IdSchemes.Where(s => s.Confirmed).ToList() ?? [];
            foreach (var reference in references.Where(r => r.Kind == "id" && r.Line is { } l && l >= 1 && l <= decoded.Lines.Count && ItemsPhase.IsForeignInLine(decoded.Lines[l - 1], r.Text, confirmedSchemes)))
            {
                // The reader returned the id bare from a line that writes it in another repository's list.
                reference.Kind = "foreign-id";
            }

            var byCode = 0;
            foreach (var reference in ReferencesByCode(decoded, confirmedSchemes))
            {
                if (references.Count >= ReferenceCap * 3)
                {
                    break;
                }

                if (!references.Any(e => string.Equals(e.Text, reference.Text, StringComparison.OrdinalIgnoreCase)))
                {
                    references.Add(reference);
                    byCode++;
                }
            }

            if (byCode > 0)
            {
                card.Flags.Add("references-by-code");
            }

            card.References = references;
            var decisions = new List<DocumentDecision>();
            foreach (var decision in read.SelectMany(r => r.Decisions))
            {
                if (!decisions.Any(d => string.Equals(d.Text, decision.Text, StringComparison.OrdinalIgnoreCase)) && decisions.Count < DecisionCap)
                {
                    decisions.Add(decision);
                }
            }

            foreach (var decision in decisions)
            {
                decision.Line = LineByCode(decoded, decision.Quote ?? decision.Text, decision.Line);
            }

            card.Decisions = decisions;
            var stated = read.FirstOrDefault(r => NormaliseStatus(context, r.Status) != "unstated") ?? first;
            card.Status = NormaliseStatus(context, stated.Status);
            card.StatusAsWritten = stated.StatusAsWritten;
            card.SupersededBy = read.Select(r => r.SupersededBy).FirstOrDefault(s => s is not null);
            card.SelfRating = read.Select(r => r.SelfRating).FirstOrDefault(s => s is not null);
            card.Audience = read.Any(r => r.Audience == "agents") ? "agents" : read.Any(r => r.Audience == "both") ? "both" : "people";
            card.Shelf = read.Select(r => r.Shelf).FirstOrDefault(s => s is "process" or "product");
            card.EvidenceLine = first.EvidenceLine;
            card.EvidenceQuote = first.EvidenceQuote;
            var confidences = read.Where(r => r.Confidence is not null).Select(r => r.Confidence!.Value).ToList();
            card.Confidence = confidences.Count == 0 ? null : confidences.Min();
            card.Reasoning = (first.Reasoning ?? "") + (chunkCount > 1 ? $" (the first of {chunkCount} chunks; the rest merged by code)" : "");
            card.By = DecidedBy.Reader;
            if (read.Count < results.Count)
            {
                card.Flags.Add("discarded-by-gate");
            }
        }

        if (results.Any(r => r.FlaggedParts.Any(p => p != "glossary")))
        {
            card.Flags.Add("read-with-flagged-text");
        }

        if (chunkCount > 1)
        {
            card.Flags.Add("chunked");
        }

        // ADR 0020 rule 8: a file written for agents, by its path or by the reader's word, is reference-only on a shelf of its
        // own; git stays its owner. Without the reader's word, a file at the root is the repository's own process and one
        // inside a module or package folder ships with it.
        if (agentPath || card.Audience == "agents")
        {
            card.ForAgents = true;
            card.ReferenceOnly = true;
            card.Shelf ??= AgentGuideFolder(file.Path).Length == 0 ? "process" : "product";
            card.Flags.Add("for-agents");
            if (card.Audience == "people")
            {
                card.Audience = "agents";
                card.Flags.Add("audience-by-path");
            }
        }
        else
        {
            card.Shelf = null;
        }

        return card;
    }

    private static bool IsSpecByPath(string path, List<IdScheme> schemes)
    {
        var segments = path.Split('/');
        if (segments.Length < 2 || !segments[..^1].Any(s => s is "spec" or "specs" || s.EndsWith("-specs", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var name = segments[^1];
        var head = name.Split('-', '.', '_', ' ')[0];
        return IdNormaliser.Normalise(head, schemes) is not null || IdNormaliser.Normalise(head + "-" + name.Split('-', '.', '_', ' ').Skip(1).FirstOrDefault(), schemes) is not null;
    }

    private static void ResolveReferences(SurveyContext context, ConcurrentDictionary<string, DocumentCard> cards, List<IdScheme> schemes, IdScheme? itemScheme)
    {
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ItemCatalogue.Load(context.State))
        {
            byKey[item.Key] = item.Key;
            foreach (var alias in item.Aliases)
            {
                byKey.TryAdd(alias, item.Key);
            }
        }

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in cards.Keys)
        {
            paths.TryAdd(path, path);
        }

        var toItems = 0;
        var toDocuments = 0;
        var total = 0;
        foreach (var card in cards.Values)
        {
            var folder = card.Path.Contains('/', StringComparison.Ordinal) ? card.Path[..card.Path.LastIndexOf('/')] : "";
            foreach (var reference in card.References)
            {
                total++;
                if (reference.Kind == "id")
                {
                    var normalised = IdNormaliser.Normalise(reference.Text, schemes);
                    if (normalised is null)
                    {
                        continue;
                    }

                    if (byKey.TryGetValue(normalised.Key, out var key))
                    {
                        reference.Item = key;
                        toItems++;
                    }
                    else if (normalised.Suffix is not null && byKey.TryGetValue(normalised.Base, out var baseKey))
                    {
                        reference.Item = baseKey;
                        toItems++;
                    }
                }
                else if (reference.Kind is "path" or "anchor")
                {
                    var target = PathTarget(reference.Text, folder, paths);
                    if (target is not null && !string.Equals(target, card.Path, StringComparison.Ordinal))
                    {
                        reference.Document = target;
                        toDocuments++;
                    }
                }
            }
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "references",
            Subject = "the documents' references",
            Decision = $"{total} reference(s) in the cards: {toItems} id(s) resolved to items{(itemScheme is null ? " (no item scheme)" : $" of the {itemScheme.Name} scheme")}, {toDocuments} path(s) resolved to documents of the atlas; the rest stay text until the links phase (ADR 0012)",
            By = DecidedBy.Rule,
            Figures = new Dictionary<string, double> { ["references"] = total, ["toItems"] = toItems, ["toDocuments"] = toDocuments },
        });
    }

    /// <summary>A path reference resolved against the atlas: as written from the repository root, else relative to the document's own folder.</summary>
    internal static string? PathTarget(string text, string folder, IReadOnlyDictionary<string, string> paths)
    {
        var path = text.Trim().Trim('`', '"', '\'', '<', '>', '(', ')');
        var hash = path.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            path = path[..hash];
        }

        path = path.Replace('\\', '/').Trim().TrimEnd('.', ',', ';', ':');
        if (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        path = path.TrimStart('/');
        if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        if (paths.TryGetValue(path, out var direct))
        {
            return direct;
        }

        var combined = Combine(folder, path);
        return combined is not null && paths.TryGetValue(combined, out var relative) ? relative : null;
    }

    private static string? Combine(string folder, string path)
    {
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static void AddToGlossary(SurveyContext context, IEnumerable<DocumentCard> cards)
    {
        var list = cards.Where(c => !c.SourceGone).ToList();
        foreach (var group in list.SelectMany(c => c.Components.Select(n => (Name: n, c.Path))).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(20))
        {
            context.Glossary.Add("component", group.Key, $"named in {group.Count()} documents, such as {string.Join(", ", group.Take(3).Select(x => x.Path))}", "documents");
        }

        foreach (var group in list.SelectMany(c => c.People.Select(n => (Name: n, c.Path))).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(12))
        {
            context.Glossary.Add("person", group.Key, $"named in {group.Count()} documents, such as {string.Join(", ", group.Take(3).Select(x => x.Path))}", "documents");
        }
    }

    private static string Describe(DocumentCard card) =>
        $"kind {card.Kind}{(card.Flags.Contains("kind-revised") ? $" (orientation read the head as {card.KindByOrientation})" : "")}; standing {card.Status}{(card.StatusAsWritten is null ? "" : $" (\"{Cut(card.StatusAsWritten, 80)}\")")}; for {card.Audience}{(card.ForAgents ? $", on the {card.Shelf} shelf, reference-only" : "")}; {card.Topics.Count} topic(s), {card.Components.Count} component(s), {card.People.Count} person(s), {card.Dates.Count} date(s), {card.References.Count} reference(s), {card.Decisions.Count} decision(s) noted{(card.Source.Chunks > 1 ? $"; read in {card.Source.Chunks} chunks" : "")}";

    private static DocumentSource SourceOf(SourceFile file, string format, int chunks, string runId) =>
        new() { Path = file.Path, Sha256 = file.Sha256, Size = file.Size, Format = format, Chunks = chunks, RunId = runId };

    private static string StatusesText(SurveyContext context)
    {
        var text = new StringBuilder();
        foreach (var (status, node) in context.Playbook.Data("document-statuses")["statuses"] as JsonObject ?? [])
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {status}: {node?.GetValue<string>()}");
        }

        return text.ToString();
    }

    /// <summary>The reader's status word against the playbook's list; a word with the list's word at its start counts ("superseded by X"); anything else is unstated.</summary>
    internal static string NormaliseStatus(SurveyContext context, string? status)
    {
        var known = (context.Playbook.Data("document-statuses")["statuses"] as JsonObject ?? []).Select(s => s.Key).ToList();
        var word = (status ?? "").Trim().ToLowerInvariant();
        if (word.Length == 0)
        {
            return "unstated";
        }

        return known.FirstOrDefault(k => string.Equals(k, word, StringComparison.Ordinal))
            ?? known.FirstOrDefault(k => word.StartsWith(k, StringComparison.Ordinal))
            ?? "unstated";
    }

    private static List<string> Distinct(IEnumerable<string>? values, int cap, int maxChars)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values ?? [])
        {
            var value = Clean(raw, maxChars);
            if (value is null || !seen.Add(value))
            {
                continue;
            }

            result.Add(value);
            if (result.Count >= cap)
            {
                break;
            }
        }

        return result;
    }

    private static List<string> Union(IEnumerable<IEnumerable<string>> lists, int cap)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in lists.SelectMany(l => l))
        {
            if (!seen.Add(value))
            {
                continue;
            }

            result.Add(value);
            if (result.Count >= cap)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>One line of text: trimmed, line breaks and runs of spaces folded, cut at a length; null when empty.</summary>
    /// <summary>
    /// The line that holds <paramref name="text"/>: the reader's line when it does, else the nearest line within
    /// forty that does, else the first line of the document that does, else the reader's line as it was. The reader
    /// numbers lines within the text it was shown, and on a long chunked document its count drifts by hundreds.
    /// </summary>
    internal static int? LineByCode(DecodedDocument decoded, string? text, int? line)
    {
        var needle = (text ?? "").Trim().Trim('`', '"', '\'');
        if (needle.Length < 3)
        {
            return line;
        }

        var lines = decoded.Lines;
        if (line is { } l && l >= 1 && l <= lines.Count && lines[l - 1].Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        if (line is { } given)
        {
            for (var distance = 1; distance <= 40; distance++)
            {
                foreach (var candidate in new[] { given - 1 - distance, given - 1 + distance })
                {
                    if (candidate >= 0 && candidate < lines.Count && lines[candidate].Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate + 1;
                    }
                }
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return line;
    }

    /// <summary>
    /// The explicit references code finds in the text (ADR 0012): markdown links to files and ids of the confirmed
    /// schemes, each once with the line of its first occurrence. The reader lists what it reads as references; an index
    /// of a hundred records exceeds what it returns, and code misses none.
    /// </summary>
    internal static List<DocumentReference> ReferencesByCode(DecodedDocument decoded, IReadOnlyList<IdScheme> schemes)
    {
        var found = new List<DocumentReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < decoded.Lines.Count; i++)
        {
            var line = decoded.Lines[i];
            foreach (Match match in MarkdownLink().Matches(line))
            {
                var target = match.Groups["target"].Value.Trim();
                if (target.Length == 0 || target.StartsWith('#') || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hash = target.IndexOf('#', StringComparison.Ordinal);
                var path = hash >= 0 ? target[..hash] : target;
                if (path.Length > 0 && seen.Add("path:" + path))
                {
                    found.Add(new DocumentReference { Kind = hash >= 0 ? "anchor" : "path", Text = target, Line = i + 1 });
                }
            }

            foreach (var id in ItemsPhase.IdsByCode(line, schemes))
            {
                if (seen.Add("id:" + id))
                {
                    found.Add(new DocumentReference { Kind = "id", Text = id, Line = i + 1 });
                }
            }
        }

        return found;
    }

    [GeneratedRegex(@"\[[^\]]*\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLink();

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["swedish"] = "sv", ["german"] = "de", ["french"] = "fr", ["spanish"] = "es", ["italian"] = "it", ["dutch"] = "nl",
        ["portuguese"] = "pt", ["persian"] = "fa", ["farsi"] = "fa", ["arabic"] = "ar", ["russian"] = "ru", ["chinese"] = "zh", ["japanese"] = "ja",
        ["korean"] = "ko", ["turkish"] = "tr", ["polish"] = "pl", ["finnish"] = "fi", ["norwegian"] = "no", ["danish"] = "da", ["hindi"] = "hi",
    };

    /// <summary>A language as a short code (en, sv, pt-BR) or unknown: the reader's sentence or a language's name is not a value (backlog TR-26).</summary>
    internal static string? LanguageCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().Trim('.', ',', ';', '"', '\'', '`');
        if (LanguageTag().IsMatch(text))
        {
            var parts = text.Split('-', '_');
            return parts.Length == 1 ? parts[0].ToLowerInvariant() : parts[0].ToLowerInvariant() + "-" + parts[1].ToUpperInvariant();
        }

        if (LanguageNames.TryGetValue(text, out var code))
        {
            return code;
        }

        var first = text.Split([' ', ',', '/', '('], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (first.Length is 2 or 3 && first == first.ToLowerInvariant() && LanguageTag().IsMatch(first))
        {
            // "en (with Swedish terms)": a code written as a code, in lower case; "The document ..." is not one.
            return first;
        }

        return LanguageNames.TryGetValue(first, out code) ? code : "unknown";
    }

    [GeneratedRegex(@"^[A-Za-z]{2,3}([-_][A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguageTag();

    internal static string? Clean(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = new StringBuilder(value.Length);
        var space = true;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space)
                {
                    text.Append(' ');
                    space = true;
                }
            }
            else
            {
                text.Append(c);
                space = false;
            }
        }

        var line = text.ToString().Trim();
        return line.Length == 0 ? null : Cut(line, maxChars);
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

    /// <summary>What a document unit stores (ADR 0017): the chunk's card as read, lines made absolute, and the figures the log needs.</summary>
    internal sealed class DocumentChunkResult
    {
        public int Index { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string RunId { get; set; } = "";
        public bool Discarded { get; set; }
        public string? DiscardReason { get; set; }
        public string? Title { get; set; }
        public string? Kind { get; set; }
        public string? Language { get; set; }
        public string? Summary { get; set; }
        public List<string> Topics { get; set; } = [];
        public List<string> Components { get; set; } = [];
        public List<string> People { get; set; } = [];
        public List<DocumentDate> Dates { get; set; } = [];
        public List<DocumentReference> References { get; set; } = [];
        public List<DocumentDecision> Decisions { get; set; } = [];
        public string? Status { get; set; }
        public string? StatusAsWritten { get; set; }
        public string? SupersededBy { get; set; }
        public string? SelfRating { get; set; }
        public string? Audience { get; set; }
        public string? Shelf { get; set; }
        public int? EvidenceLine { get; set; }
        public string? EvidenceQuote { get; set; }
        public double? Confidence { get; set; }
        public string? Reasoning { get; set; }
        public string AnsweredBy { get; set; } = "";
        public int CallSeq { get; set; }
        public decimal Usd { get; set; }
        public List<string> FlaggedParts { get; set; } = [];
    }

    // The reader's reply shape (schemas/document-card.json), read with the forgiving snake_case options.
    internal sealed class CardReply
    {
        public string? Title { get; set; }
        public string? Kind { get; set; }
        public string? Language { get; set; }
        public string? Summary { get; set; }
        public List<string>? Topics { get; set; }
        public List<string>? Components { get; set; }
        public List<string>? People { get; set; }
        public List<ItemsPhase.DateReply>? Dates { get; set; }
        public List<ReferenceReply>? References { get; set; }
        public List<DecisionReply>? Decisions { get; set; }
        public string? Status { get; set; }
        public string? StatusAsWritten { get; set; }
        public string? SupersededBy { get; set; }
        public string? SelfRating { get; set; }
        public string? Audience { get; set; }
        public string? Shelf { get; set; }
        public ItemsPhase.LineEvidenceReply? Evidence { get; set; }
        public double? Confidence { get; set; }
        public string? Reasoning { get; set; }
    }

    internal sealed class ReferenceReply
    {
        public string? Kind { get; set; }
        public string? Text { get; set; }
        public int? Line { get; set; }
    }

    internal sealed class DecisionReply
    {
        public string? Text { get; set; }
        public int? Line { get; set; }
        public string? Quote { get; set; }
    }
}

/// <summary>
/// The fixed-shape block the reader sees before a document's text (ADR 0019): the file, orientation's reading
/// of its head, its place in the chunks, and, for a chunk after the first, what the chunks before said, in
/// bounded fields rebuilt by code from the units' results, never accumulated by a model.
/// </summary>
internal static class DocumentSummary
{
    private const int HeadingsKept = 12;

    public static string Build(SourceFile file, MappedFile mapped, DecodedDocument decoded, TextChunk chunk, IReadOnlyList<DocumentsPhase.DocumentChunkResult> before, bool agentPath, int maxChars)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Document: {file.Path} ({file.Size:N0} bytes, {decoded.Lines.Count} lines; format {decoded.Format}; {(file.Tracked ? "tracked by git" : "not tracked by git")}).");
        text.AppendLine(CultureInfo.InvariantCulture, $"Orientation read its head as kind {mapped.Kind}{(mapped.Confidence is null ? "" : $" ({mapped.Confidence.Value.ToString("0.00", CultureInfo.InvariantCulture)})")}{(string.IsNullOrWhiteSpace(mapped.Reasoning) ? "" : ": \"" + Cut(mapped.Reasoning, 200) + "\"")}. Title as decoded: {(decoded.Title is null ? "none" : "\"" + Cut(decoded.Title, 120) + "\"")}.");
        if (agentPath)
        {
            var folder = DocumentsPhase.AgentGuideFolder(file.Path);
            text.AppendLine(folder.Length == 0
                ? "Its path is one the engine lists among files written for AI agents and tools, at the repository root: unless the text says it ships to others, it is about working in this repository (the process shelf). Say which shelf it belongs on."
                : $"Its path is one the engine lists among files written for AI agents and tools, inside {folder} rather than at the repository root: unless the text says it is about working in this repository, it is guidance that ships with {folder} to its users (the product shelf). Say which shelf it belongs on.");
        }

        if (chunk.Count == 1)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"This is the whole file, lines 1 to {decoded.Lines.Count}.");
            return Cap(text.ToString(), maxChars);
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"This is chunk {chunk.Index + 1} of {chunk.Count}, lines {chunk.StartLine - chunk.OverlapLines} to {chunk.EndLine}{(chunk.OverlapLines > 0 ? $", the first {chunk.OverlapLines} of them (lines {chunk.StartLine - chunk.OverlapLines} to {chunk.StartLine - 1}) repeated from the chunk before" : "")}.");
        if (chunk.Index == 0)
        {
            text.AppendLine("Nothing came before this chunk. Describe what this chunk shows; the engine merges the chunks' answers by code.");
            return Cap(text.ToString(), maxChars);
        }

        var headings = decoded.Sections.Where(s => s.StartLine < chunk.StartLine).Select(s => s.Heading).ToList();
        if (headings.Count > 0)
        {
            var shown = headings.Count <= HeadingsKept ? headings : headings.Skip(headings.Count - HeadingsKept).ToList();
            text.AppendLine(CultureInfo.InvariantCulture, $"Headings before this chunk{(headings.Count > shown.Count ? $" (the last {shown.Count} of {headings.Count})" : "")}: {string.Join(" | ", shown.Select(h => Cut(h, 80)))}");
        }

        var read = before.Where(r => !r.Discarded).ToList();
        if (read.Count > 0)
        {
            var first = read[0];
            text.AppendLine(CultureInfo.InvariantCulture, $"What the engine kept from the {before.Count} chunk(s) before (fixed shape): kind {first.Kind ?? "unknown"}; standing {first.Status ?? "unstated"}; audience {first.Audience ?? "unknown"}; topics so far: {ListOf(read.SelectMany(r => r.Topics), 20)}; components so far: {ListOf(read.SelectMany(r => r.Components), 15)}; people so far: {ListOf(read.SelectMany(r => r.People), 10)}; {read.Sum(r => r.References.Count)} reference(s) and {read.Sum(r => r.Decisions.Count)} decision(s) noted so far.");
            var last = read[^1];
            text.AppendLine(CultureInfo.InvariantCulture, $"The chunk before was summarised as: \"{Cut(last.Summary ?? "", 400)}\"");
        }

        text.AppendLine("Describe this chunk's own text; the engine merges the chunks by code. Keep the kind, standing and audience as kept above unless this chunk shows otherwise, and do not repeat the earlier summary.");
        return Cap(text.ToString(), maxChars);
    }

    private static string ListOf(IEnumerable<string> values, int max)
    {
        var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            return "none";
        }

        var shown = distinct.Count <= max ? distinct : distinct.Skip(distinct.Count - max).ToList();
        return (distinct.Count > shown.Count ? $"(the last {shown.Count} of {distinct.Count}) " : "") + string.Join(", ", shown.Select(v => Cut(v, 60)));
    }

    private static string Cut(string value, int max)
    {
        var line = value.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return line.Length <= max ? line : line[..max] + "...";
    }

    private static string Cap(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - 22)] + "\n[cut by the engine]\n";
}
