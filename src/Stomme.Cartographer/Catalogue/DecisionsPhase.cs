using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Options;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// The decisions phase (ADR 0006): the decisions the documents phase noted and the commit messages that read like
/// one are the candidates; code merges the same decision noted in several places and settles what an ADR of the
/// repository already records; the judge reads the rest (made, not a plan; significant; already recorded); the
/// reader drafts a record for each significant one from its evidence. The older history, when given, is a second
/// source of commits (ADR 0021), and a gap in the history is asked about.
/// </summary>
public sealed partial class DecisionsPhase(SurveyContext survey) : IPhase
{
    public const string PhaseName = "decisions";

    private const string JudgeUnit = "judge/";
    private const string DraftUnit = "draft/";
    private const string MergeUnit = "merge/";

    /// <summary>Document kinds whose dates are project history (backlog TR-28): a licence's or a legal text's dates are not.</summary>
    private static readonly HashSet<string> HistoryKinds = new(StringComparer.OrdinalIgnoreCase) { "adr", "report", "change-log", "plan", "evidence", "handover" };

    public string Name => PhaseName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var context = survey;
        var map = context.Map ?? context.LoadMap() ?? throw new InvalidOperationException("The decisions phase needs the repository map from orientation.");
        var documents = DocumentCatalogue.Load(context.State);
        var items = ItemCatalogue.Load(context.State);
        var schemes = map.IdSchemes.Where(s => s.Confirmed).ToList();
        var words = DecisionWords(context.Playbook);
        var thresholds = context.Run.Options.Thresholds;
        var salt = context.Playbook.Salt(["rules", "decision-draft"], ["decision-draft"], ["decision-words"], ["decisions"]);
        context.Touch("decisions: gathering the candidates", force: true);

        // 1. The candidates: what the documents phase noted, and the commit messages that read like a decision.
        var candidates = new List<Candidate>();
        foreach (var card in documents)
        {
            var date = DateOf(card);
            foreach (var noted in card.Decisions.Where(d => !string.IsNullOrWhiteSpace(d.Text)))
            {
                candidates.Add(new Candidate(noted.Text.Trim(), new DecisionSource
                {
                    Kind = "document",
                    Path = card.Path,
                    Line = noted.Line,
                    Quote = noted.Quote,
                    Title = card.Title,
                    Date = date,
                    DocumentKind = card.Kind,
                }));
            }
        }

        var fromDocuments = candidates.Count;
        var commits = context.Snapshot.Git?.Log() ?? [];
        var fromCommits = AddCommitCandidates(candidates, commits, "commit", words);
        var history = await ReadHistoryAsync(context, commits, documents, cancellationToken).ConfigureAwait(false);
        var fromHistory = AddCommitCandidates(candidates, history.Commits, "history-commit", words);

        // 2. Merged by code: the same decision noted in several places is one decision with several sources.
        var groups = Merge(candidates);
        var adrs = documents.Where(d => string.Equals(d.Kind, "adr", StringComparison.OrdinalIgnoreCase) && !d.Flags.Contains("from-fixture"))
            .Select(d => new AdrIndex(d, DecisionCatalogue.Words(string.Join(" ", new[] { d.Title ?? "", d.Summary }.Concat(d.Decisions.Select(x => x.Text))))))
            .ToList();
        var cards = new Dictionary<string, DecisionCard>(StringComparer.Ordinal);
        var previous = DecisionCatalogue.Load(context.State).ToDictionary(c => c.Key, StringComparer.Ordinal);
        var adrByHead = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var adr in adrs)
        {
            var name = adr.Card.Path.Contains('/', StringComparison.Ordinal) ? adr.Card.Path[(adr.Card.Path.LastIndexOf('/') + 1)..] : adr.Card.Path;
            var head = name.Split('-', '.', '_', ' ')[0];
            if (head.Length > 0)
            {
                adrByHead.TryAdd(HeadKey(head), adr.Card.Path);
            }
        }

        var fixtureDocuments = documents.Where(d => d.Flags.Contains("from-fixture")).Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var fromFixtures = 0;
        var recordedByRule = 0;
        var citesAdr = 0;
        var citedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var card = ToCard(group, context.RunId);
            var adr = group.Members.FirstOrDefault(m => string.Equals(m.Source.DocumentKind, "adr", StringComparison.OrdinalIgnoreCase) && !fixtureDocuments.Contains(m.Source.Path ?? ""));
            if (group.Members.All(m => m.Source.Kind == "document" && fixtureDocuments.Contains(m.Source.Path ?? "")))
            {
                // Only an example, a test or a fixture holds it (the verdict's redo, change 13): it is kept on record with its
                // source, never as the repository's recorded decision and never as a draft, and no judge reads it.
                card.Standing = "record";
                card.By = DecidedBy.Rule;
                card.Flags.Add("from-fixture");
                fromFixtures++;
            }
            else if (adr is not null)
            {
                // ADR 0006: a decision an ADR of the repository records is that ADR's; no draft, a record linked to it.
                card.Standing = "recorded";
                card.RecordedIn = adr.Source.Path;
                card.Recorded = 1;
                card.By = DecidedBy.Rule;
                recordedByRule++;
            }
            else if (CitedAdr(group, adrByHead) is { } cited)
            {
                // A candidate that cites an ADR of the repository by its id is judged against it (the planner, 2026-11-04):
                // a correction of that decision attaches to the ADR as a correction candidate; a decision of its own that only
                // refers to it takes the ordinary path.
                card.Flags.Add("cites-adr");
                citedBy[card.Key] = cited;
                citesAdr++;
            }

            cards[card.Key] = card;
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "candidates",
            Subject = "the decisions found",
            Decision = $"{candidates.Count} candidates: {fromDocuments} noted in {documents.Count(d => d.Decisions.Count > 0)} document(s), {fromCommits} of {commits.Count} commit message(s)"
                + (history.Commits.Count > 0 ? $", {fromHistory} of {history.Commits.Count} message(s) of the older history" : "")
                + $"; merged into {groups.Count} decision(s) by their words; {recordedByRule} already recorded in an ADR of the repository (ADR 0006), linked to it and not judged; {fromFixtures} held only by documents under a folder of examples, tests or fixtures, kept on record and neither recorded nor drafted; {citesAdr} cite an ADR by its id and are judged as a correction of it or a decision of their own",
            By = DecidedBy.Rule,
            Figures = new Dictionary<string, double>
            {
                ["candidates"] = candidates.Count,
                ["from documents"] = fromDocuments,
                ["from commits"] = fromCommits,
                ["from history"] = fromHistory,
                ["merged"] = groups.Count,
                ["recorded by rule"] = recordedByRule,
            },
        });

        // 3. The judge on the rest: made, significant, already recorded in the nearest ADRs.
        var toJudge = new List<(Group Group, DecisionCard Card, List<AdrIndex> Near, string Hash)>();
        foreach (var group in groups)
        {
            var card = cards[group.Key];
            if (card.Standing == "recorded" || card.Flags.Contains("from-fixture"))
            {
                continue;
            }

            var near = Nearest(group, adrs, 3);
            if (citedBy.TryGetValue(card.Key, out var citedPath) && adrs.FirstOrDefault(a => string.Equals(a.Card.Path, citedPath, StringComparison.OrdinalIgnoreCase)) is { } citedAdr)
            {
                // The cited ADR comes first among the records, so the judge's correction question is about it.
                near = new[] { citedAdr }.Concat(near.Where(n => !string.Equals(n.Card.Path, citedPath, StringComparison.OrdinalIgnoreCase))).Take(3).ToList();
            }

            var hash = RunContext.HashOf("judge-v2", card.Text, string.Join("\n", card.Sources.Select(s => s.Kind + ":" + s.Path + ":" + s.Line)), string.Join("\n", near.Select(n => n.Card.Path)), salt);
            var done = context.State.DoneUnit(PhaseName, JudgeUnit + card.Key, hash);
            if (done?.Result is { } result && Deserialize<JudgeFigures>(result) is { } figures)
            {
                Apply(card, figures, thresholds, context.Options.DraftRoutineDecisions);
                continue;
            }

            toJudge.Add((group, card, near, hash));
        }

        var perCall = Math.Max(1, context.Options.Budgets.DecisionsPerJudgeCall);
        var judged = 0;
        var judgeFailed = 0;
        var template = context.Playbook.JudgeSet("decisions");
        foreach (var batch in toJudge.Chunk(perCall))
        {
            context.Touch($"decisions: the judge on {batch.Length} candidate(s), {judged} of {toJudge.Count} done");
            try
            {
                var results = await JudgeBatchAsync(context, template, batch, cancellationToken).ConfigureAwait(false);
                foreach (var (entry, figures, usd) in results)
                {
                    Apply(entry.Card, figures, thresholds, context.Options.DraftRoutineDecisions);
                    context.State.CompleteUnit(PhaseName, context.RunId, JudgeUnit + entry.Card.Key, entry.Hash, usd, figures);
                }
            }
            catch (JudgeUnavailableException ex)
            {
                judgeFailed += batch.Length;
                foreach (var entry in batch)
                {
                    entry.Card.Standing = "unsure";
                    entry.Card.Flags.Add("judge-failed");
                }

                context.Log.Record(new DecisionRecord
                {
                    Phase = PhaseName,
                    Kind = "decision-check",
                    Subject = $"{batch.Length} candidate(s)",
                    Decision = "the judge could not read them: " + ex.Reason + "; they stay unsure and are judged again next run",
                    By = DecidedBy.Rule,
                    Flags = ["judge-failed"],
                });
            }

            judged += batch.Length;
            if (judged % 50 == 0 || judged == toJudge.Count)
            {
                context.Progress($"Decisions: {judged} of {toJudge.Count} candidates judged");
            }
        }

        // 3b. Near pairs among the drafts through the judge (backlog TR-27): the same rule in other words becomes one draft with all its sources.
        await MergeNearDraftsAsync(context, cards, groups, salt, thresholds, cancellationToken).ConfigureAwait(false);

        // 4. The drafts: the reader writes a record for each decision that gets one, from its evidence.
        var toDraft = cards.Values.Where(c => c.Standing == "draft").ToList();
        var system = context.SystemPrompt("decision-draft", "decision-draft");
        var drafted = 0;
        var draftFailed = 0;
        var fromState = 0;
        var pending = new List<(DecisionCard Card, string Hash)>();
        foreach (var card in toDraft)
        {
            var hash = RunContext.HashOf("draft-v1", card.Text, string.Join("\n", card.Sources.Select(s => s.Quote ?? s.Title ?? "")), salt);
            var done = context.State.DoneUnit(PhaseName, DraftUnit + card.Key, hash);
            if (done?.Result is { } result && Deserialize<DecisionDraft>(result) is { } draft)
            {
                card.Draft = draft;
                card.By = DecidedBy.Reader;
                fromState++;
                continue;
            }

            pending.Add((card, hash));
        }

        var concurrency = Math.Max(1, context.Run.Options.Reader.Concurrency);
        var counter = 0;
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken }, async (entry, token) =>
        {
            var (card, hash) = entry;
            try
            {
                var (draft, usd) = await DraftAsync(context, card, system, documents, token).ConfigureAwait(false);
                if (draft is not null)
                {
                    card.Draft = draft;
                    card.By = DecidedBy.Reader;
                    context.State.CompleteUnit(PhaseName, context.RunId, DraftUnit + card.Key, hash, usd, draft);
                    Interlocked.Increment(ref drafted);
                }
                else
                {
                    Interlocked.Increment(ref draftFailed);
                }
            }
            catch (Exception ex) when (ex is ReaderException or JudgeUnavailableException)
            {
                card.Flags.Add("draft-failed");
                Interlocked.Increment(ref draftFailed);
                context.Log.Record(new DecisionRecord
                {
                    Phase = PhaseName,
                    Kind = "decision",
                    Subject = card.Key,
                    Decision = "the draft could not be written: " + ex.Message + "; the decision keeps its text and is drafted again next run",
                    By = DecidedBy.Rule,
                    Flags = ["draft-failed"],
                });
            }

            var n = Interlocked.Increment(ref counter);
            if (n % 10 == 0 || n == pending.Count)
            {
                context.Progress($"Decisions: {n} of {pending.Count} drafts written");
            }
        }).ConfigureAwait(false);

        // 5. Numbers for the drafts (kept across runs), the items the text names, and the cards saved.
        var numbers = DecisionCatalogue.LoadNumbers(context.State);
        var next = numbers.Count == 0 ? 1 : numbers.Values.Max() + 1;
        foreach (var card in cards.Values.Where(c => c.Standing == "draft").OrderBy(c => c.Date ?? "9999-99-99", StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            if (numbers.TryGetValue(card.Key, out var number))
            {
                card.Number = number;
            }
            else
            {
                card.Number = next++;
                numbers[card.Key] = card.Number.Value;
                DecisionCatalogue.SaveNumber(context, card.Key, card.Number.Value);
            }
        }

        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            byKey[item.Key] = item.Key;
            foreach (var alias in item.Aliases)
            {
                byKey.TryAdd(alias, item.Key);
            }
        }

        foreach (var card in cards.Values)
        {
            card.Items = ItemsNamed(card, schemes, byKey);
            card.Title = card.Draft?.Title ?? Cut(card.Text, 80);
            DecisionCatalogue.SaveOne(context, card);
        }

        var gone = 0;
        var keptBack = 0;
        foreach (var (key, old) in previous)
        {
            if (!cards.ContainsKey(key) && old.Sources.Count > 0 && old.Sources.All(s => s.Kind == "document" && s.Path is not null && context.File(s.Path) is { NotRead: not null }))
            {
                // Every source is a file that is in the input and is kept back now (it looks like it holds a secret, a person
                // excluded it, it is a backup copy): the decision leaves with its text (ADR 0028), unlike one whose source is gone.
                context.State.DeleteRecord(DecisionCatalogue.DecisionKind, key);
                context.State.DeleteRecord(DecisionCatalogue.NumberKind, key);
                keptBack++;
            }
            else if (!cards.ContainsKey(key))
            {
                // Its sources are no longer in the repository; kept and marked, like an item whose row is gone.
                if (!old.Flags.Contains("source-gone"))
                {
                    old.Flags.Add("source-gone");
                }

                old.RunId = context.RunId;
                DecisionCatalogue.SaveOne(context, old);
                gone++;
            }
        }

        if (keptBack > 0)
        {
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "decision", Subject = $"{keptBack} decision(s) of earlier runs", Decision = "removed: every source of theirs is a file that is kept back now (it looks like it holds a secret, a person excluded it, or it is a backup copy)", By = DecidedBy.Rule });
        }

        foreach (var card in cards.Values.Where(c => c.Number is not null).OrderBy(c => c.Number))
        {
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "decision",
                Subject = DecisionCatalogue.FileName(card),
                Decision = $"drafted from {card.Sources.Count} source(s): \"{Cut(card.Draft?.Title ?? card.Text, 120)}\" (the judge: made {Figure(card.Made)}, significant {Figure(card.Significant)}, recorded {Figure(card.Recorded)})",
                By = card.By,
                Model = card.By == DecidedBy.Reader ? context.Run.Options.Models.For(PhaseName) : null,
                Flags = card.Flags,
                Confidence = card.Draft?.Confidence,
            });
        }

        var standing = cards.Values.GroupBy(c => c.Standing).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "catalogue",
            Subject = "the decisions",
            Decision = $"{cards.Count} decision(s): {string.Join(", ", standing.OrderByDescending(s => s.Value).Select(s => $"{s.Key} {s.Value}"))}; "
                + $"{cards.Values.Count(c => c.Flags.Contains("cites-adr"))} cite an ADR, of which {cards.Values.Count(c => c.Flags.Contains("correction-candidate"))} correct it (attached to it as correction candidates) and the rest decide something of their own; "
                + $"{toDraft.Count} get a draft ({drafted} written this run, {fromState} from the state, {draftFailed} failed); the judge read {toJudge.Count} this run ({judgeFailed} failed)"
                + (gone > 0 ? $"; {gone} from an earlier run whose sources are gone, kept and marked" : ""),
            By = DecidedBy.Rule,
            Figures = standing.ToDictionary(s => s.Key, s => (double)s.Value),
        });
        context.Progress($"Decisions: {cards.Count} found, {toDraft.Count} drafted");
    }

    // ----- the candidates and their merging -----

    private static int AddCommitCandidates(List<Candidate> candidates, IReadOnlyList<Commit> commits, string kind, IReadOnlyList<string> words)
    {
        var added = 0;
        foreach (var commit in commits)
        {
            var text = commit.Subject.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var body = commit.Body.Trim();
            var whole = body.Length == 0 ? text : text + " " + Cut(body, 300);
            if (!LooksLikeDecision(whole, words))
            {
                continue;
            }

            candidates.Add(new Candidate(whole, new DecisionSource
            {
                Kind = kind,
                Path = commit.Hash,
                Quote = Cut(body, 300),
                Title = text,
                Date = commit.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }));
            added++;
        }

        return added;
    }

    internal static bool LooksLikeDecision(string text, IReadOnlyList<string> words)
    {
        var lower = " " + DecisionCatalogue.Normalise(text) + " ";
        return words.Any(w => lower.Contains(" " + w + " ", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> DecisionWords(Playbook playbook)
    {
        var data = playbook.Data("decision-words");
        return data["words"] is JsonArray array
            ? array.Select(n => DecisionCatalogue.Normalise(n?.GetValue<string>() ?? "")).Where(w => w.Length > 0).ToList()
            : [];
    }

    /// <summary>Groups the candidates: the same words, or four fifths of them, are the same decision. The leading member is the earliest one, an ADR's first.</summary>
    private static List<Group> Merge(List<Candidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(c => string.Equals(c.Source.DocumentKind, "adr", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Source.Date ?? "9999-99-99", StringComparer.Ordinal)
            .ThenBy(c => c.Source.Path, StringComparer.Ordinal)
            .ToList();
        var groups = new List<Group>();
        var byKey = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var candidate in ordered)
        {
            var key = DecisionCatalogue.KeyOf(candidate.Text);
            if (byKey.TryGetValue(key, out var same))
            {
                same.Members.Add(candidate);
                continue;
            }

            var words = DecisionCatalogue.Words(candidate.Text);
            Group? near = null;
            if (words.Count >= 4)
            {
                foreach (var group in groups)
                {
                    if (group.Words.Count >= 4 && DecisionCatalogue.Similarity(words, group.Words) >= 0.8)
                    {
                        near = group;
                        break;
                    }
                }
            }

            if (near is not null)
            {
                near.Members.Add(candidate);
                continue;
            }

            var created = new Group(key, candidate, words);
            groups.Add(created);
            byKey[key] = created;
        }

        return groups;
    }

    private static DecisionCard ToCard(Group group, string runId)
    {
        var card = new DecisionCard
        {
            Key = group.Key,
            Text = group.Lead.Text,
            RunId = runId,
            Standing = "candidate",
        };
        foreach (var member in group.Members)
        {
            if (!card.Sources.Any(s => s.Kind == member.Source.Kind && s.Path == member.Source.Path && s.Line == member.Source.Line))
            {
                card.Sources.Add(member.Source);
            }
        }

        card.Date = card.Sources.Select(s => s.Date).Where(d => d is not null).OrderBy(d => d, StringComparer.Ordinal).FirstOrDefault();
        return card;
    }

    /// <summary>The ADR a candidate cites by its id (D061, 0002) in its text or quotes, when the repository has that ADR.</summary>
    private static string? CitedAdr(Group group, Dictionary<string, string> adrByHead)
    {
        if (adrByHead.Count == 0)
        {
            return null;
        }

        var text = group.Lead.Text + " " + string.Join(" ", group.Members.Select(m => m.Source.Quote ?? ""));
        foreach (Match match in IdLike().Matches(text))
        {
            if (adrByHead.TryGetValue(HeadKey(match.Value), out var path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>A file-name head or a token as a key: letters upper-cased, leading zeros dropped, so 0002 and D61 and D061 meet.</summary>
    private static string HeadKey(string token)
    {
        var match = HeadShape().Match(token.Trim());
        return match.Success
            ? match.Groups["letters"].Value.ToUpperInvariant() + "-" + match.Groups["number"].Value.TrimStart('0').PadLeft(1, '0')
            : token.Trim().ToLowerInvariant();
    }

    private static List<AdrIndex> Nearest(Group group, List<AdrIndex> adrs, int count) =>
        adrs.Select(a => (Adr: a, Likeness: DecisionCatalogue.Similarity(group.Words, a.Words)))
            .Where(x => x.Likeness >= 0.08)
            .OrderByDescending(x => x.Likeness)
            .Take(count)
            .Select(x => x.Adr)
            .ToList();

    // ----- the judge -----

    private static async Task<List<((Group Group, DecisionCard Card, List<AdrIndex> Near, string Hash) Entry, JudgeFigures Figures, decimal Usd)>> JudgeBatchAsync(
        SurveyContext context, JsonObject template, (Group Group, DecisionCard Card, List<AdrIndex> Near, string Hash)[] batch, CancellationToken cancellationToken)
    {
        var state = new JsonObject();
        var candidates = new JsonObject();
        var records = new JsonObject();
        var questions = new JsonObject();
        for (var i = 0; i < batch.Length; i++)
        {
            var field = "c" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var card = batch[i].Card;
            var lead = card.Sources[0];
            candidates[field] = new JsonObject
            {
                ["text"] = Cut(card.Text, 400),
                ["quote"] = Cut(lead.Quote ?? "", 300),
                ["found_in"] = Describe(lead) + (card.Sources.Count > 1 ? $" and {card.Sources.Count - 1} other place(s)" : ""),
            };
            var filled = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["field"] = $"candidates.{field}" });
            questions[$"{field}_made"] = filled["made"]?.DeepClone();
            questions[$"{field}_carried_out"] = filled["carried_out"]?.DeepClone();
            questions[$"{field}_significant"] = filled["significant"]?.DeepClone();
            if (batch[i].Near.Count > 0)
            {
                var near = new JsonObject();
                for (var k = 0; k < batch[i].Near.Count; k++)
                {
                    var a = "a" + (k + 1).ToString(CultureInfo.InvariantCulture);
                    var adr = batch[i].Near[k].Card;
                    near[a] = new JsonObject
                    {
                        ["path"] = adr.Path,
                        ["title"] = adr.Title ?? "",
                        ["summary"] = Cut(adr.Summary, 600),
                        ["decisions"] = new JsonArray(adr.Decisions.Take(8).Select(d => (JsonNode)Cut(d.Text, 160)).ToArray()),
                    };
                    var withAdr = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["field"] = $"candidates.{field}", ["adr"] = $"records.{field}.{a}" });
                    questions[$"{field}_recorded_{a}"] = withAdr["recorded"]?.DeepClone();
                    if (k == 0 && card.Flags.Contains("cites-adr"))
                    {
                        questions[$"{field}_corrects"] = withAdr["corrects"]?.DeepClone();
                    }
                }

                records[field] = near;
            }
        }

        state["candidates"] = candidates;
        if (records.Count > 0)
        {
            state["records"] = records;
        }

        var result = await context.Judge.AskAsync(PhaseName, $"{batch.Length} decision candidate(s)", state, questions, cancellationToken).ConfigureAwait(false);
        var share = batch.Length == 0 ? 0m : result.Cost.Usd / batch.Length;
        var figures = new List<((Group Group, DecisionCard Card, List<AdrIndex> Near, string Hash) Entry, JudgeFigures Figures, decimal Usd)>();
        var made = 0;
        var significant = 0;
        var recorded = 0;
        for (var i = 0; i < batch.Length; i++)
        {
            var field = "c" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var f = new JudgeFigures
            {
                Made = result.Reply.Noul($"{field}_made"),
                CarriedOut = questions.ContainsKey($"{field}_carried_out") ? result.Reply.Noul($"{field}_carried_out") : 0,
                Significant = result.Reply.Noul($"{field}_significant"),
            };
            for (var k = 0; k < batch[i].Near.Count; k++)
            {
                var a = "a" + (k + 1).ToString(CultureInfo.InvariantCulture);
                var noul = result.Reply.Noul($"{field}_recorded_{a}");
                if (noul > f.Recorded)
                {
                    f.Recorded = noul;
                    f.RecordedIn = batch[i].Near[k].Card.Path;
                }
            }

            if (questions.ContainsKey($"{field}_corrects") && batch[i].Near.Count > 0)
            {
                f.Corrects = result.Reply.Noul($"{field}_corrects");
                f.CitedIn = batch[i].Near[0].Card.Path;
            }

            var thresholds = context.Run.Options.Thresholds;
            made += f.Made >= thresholds.Yes || (f.Made > thresholds.No && f.CarriedOut >= thresholds.Yes) ? 1 : 0;
            significant += f.Significant >= thresholds.Yes ? 1 : 0;
            recorded += f.Recorded >= thresholds.Yes ? 1 : 0;
            figures.Add((batch[i], f, share));
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "decision-check",
            Subject = $"{batch.Length} candidate(s)",
            Decision = $"the judge read them: {made} record a decision that was made, {significant} of lasting effect, {recorded} already recorded in an ADR",
            By = DecidedBy.Judge,
            Model = context.Judge.Options.Model,
            Figures = figures.SelectMany(x => new[] { (Key: Cut(x.Entry.Card.Text, 60) + " made", Value: x.Figures.Made), (Key: Cut(x.Entry.Card.Text, 60) + " carried out", Value: x.Figures.CarriedOut) })
                .GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Math.Round(g.First().Value, 3), StringComparer.Ordinal),
        });
        return figures;
    }

    private static void Apply(DecisionCard card, JudgeFigures figures, JudgeThresholds thresholds, bool draftRoutine)
    {
        // Two readings of "was a decision made" (backlog TR-15): the choice stated, or the change told as carried out. The
        // second settles the unsure band only: a text read as a change carried out is often a commit's account of work
        // (on Stomme it lifted 125 of 485 non-decisions), so it never lifts a no, and never lowers a yes.
        var unsure = figures.Made > thresholds.No && figures.Made < thresholds.Yes;
        card.Made = unsure && figures.CarriedOut >= thresholds.Yes ? Math.Max(figures.Made, figures.CarriedOut) : figures.Made;
        card.CarriedOut = figures.CarriedOut;
        card.Significant = figures.Significant;
        card.Recorded = figures.Recorded;
        card.By = DecidedBy.Judge;
        figures = new JudgeFigures { Made = card.Made.Value, CarriedOut = figures.CarriedOut, Significant = figures.Significant, Recorded = figures.Recorded, RecordedIn = figures.RecordedIn, Corrects = figures.Corrects, CitedIn = figures.CitedIn };
        if (figures.CitedIn is not null && figures.Corrects >= thresholds.Yes)
        {
            // A correction of the cited decision: a candidate for that ADR, not a draft of its own.
            card.Standing = "recorded";
            card.RecordedIn = figures.CitedIn;
            card.Flags.Add("correction-candidate");
            return;
        }

        if (figures.Made <= thresholds.No)
        {
            card.Standing = "not-a-decision";
        }
        else if (figures.Recorded >= thresholds.Yes && figures.RecordedIn is not null)
        {
            card.Standing = "recorded";
            card.RecordedIn = figures.RecordedIn;
        }
        else if (figures.Made < thresholds.Yes)
        {
            card.Standing = "unsure";
            card.Flags.Add("decision-unsure");
        }
        else if (figures.Significant >= thresholds.Yes || draftRoutine)
        {
            card.Standing = "draft";
        }
        else
        {
            card.Standing = "record";
        }
    }

    // ----- the near pairs (backlog TR-27) -----

    /// <summary>
    /// Two drafts whose words are alike but not alike enough for the merge by code (likeness 0.5 to 0.8) go to the
    /// judge as a pair; when it reads them as one decision, the later one is merged into the earlier: its sources
    /// move over, its standing becomes merged and it gets no draft. One unit per pair, kept across runs.
    /// </summary>
    private static async Task MergeNearDraftsAsync(SurveyContext context, Dictionary<string, DecisionCard> cards, List<Group> groups, string salt, JudgeThresholds thresholds, CancellationToken cancellationToken)
    {
        var words = groups.ToDictionary(g => g.Key, g => g.Words, StringComparer.Ordinal);
        var drafts = cards.Values.Where(c => c.Standing == "draft" && words.TryGetValue(c.Key, out var w) && w.Count >= 4)
            .OrderBy(c => c.Date ?? "9999-99-99", StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        var pairs = new List<(DecisionCard First, DecisionCard Second, double Likeness)>();
        for (var i = 0; i < drafts.Count; i++)
        {
            for (var j = i + 1; j < drafts.Count; j++)
            {
                var likeness = DecisionCatalogue.Similarity(words[drafts[i].Key], words[drafts[j].Key]);
                if (likeness >= 0.5)
                {
                    pairs.Add((drafts[i], drafts[j], likeness));
                }
            }
        }

        if (pairs.Count == 0)
        {
            return;
        }

        var mergeSalt = context.Playbook.Salt([], [], [], ["decisions-merge"]);
        var template = context.Playbook.JudgeSet("decisions-merge");
        var merged = 0;
        var asked = 0;
        var failed = 0;
        var pending = new List<((DecisionCard First, DecisionCard Second, double Likeness) Pair, string Key, string Hash)>();
        var verdicts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var key = MergeUnit + pair.First.Key + "|" + pair.Second.Key;
            var hash = RunContext.HashOf("merge-v1", pair.First.Text, pair.Second.Text, mergeSalt, salt);
            var done = context.State.DoneUnit(PhaseName, key, hash);
            if (done?.Result is { } result && Deserialize<Dictionary<string, double>>(result) is { } figures && figures.TryGetValue("same", out var same))
            {
                verdicts[key] = same;
                continue;
            }

            pending.Add((pair, key, hash));
        }

        foreach (var batch in pending.Chunk(10))
        {
            context.Touch($"decisions: the judge on {batch.Length} near pair(s) of drafts");
            var state = new JsonObject();
            var pairsNode = new JsonObject();
            var questions = new JsonObject();
            for (var i = 0; i < batch.Length; i++)
            {
                var field = "p" + (i + 1).ToString(CultureInfo.InvariantCulture);
                var (first, second, _) = batch[i].Pair;
                pairsNode[field] = new JsonObject
                {
                    ["first"] = Cut(first.Text, 400),
                    ["first_from"] = Describe(first.Sources[0]),
                    ["second"] = Cut(second.Text, 400),
                    ["second_from"] = Describe(second.Sources[0]),
                };
                var filled = JudgeQuestions.Fill(template, new Dictionary<string, string> { ["pair"] = $"pairs.{field}" });
                questions[$"{field}_same"] = filled["same_decision"]?.DeepClone();
            }

            state["pairs"] = pairsNode;
            try
            {
                var result = await context.Judge.AskAsync(PhaseName, $"{batch.Length} near pair(s) of drafts", state, questions, cancellationToken).ConfigureAwait(false);
                var share = result.Cost.Usd / batch.Length;
                for (var i = 0; i < batch.Length; i++)
                {
                    var field = "p" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    var same = result.Reply.Noul($"{field}_same");
                    verdicts[batch[i].Key] = same;
                    context.State.CompleteUnit(PhaseName, context.RunId, batch[i].Key, batch[i].Hash, share, new Dictionary<string, double> { ["same"] = same, ["likeness"] = Math.Round(batch[i].Pair.Likeness, 3) });
                    asked++;
                }
            }
            catch (JudgeUnavailableException ex)
            {
                failed += batch.Length;
                context.Log.Record(new DecisionRecord
                {
                    Phase = PhaseName,
                    Kind = "decision-merge",
                    Subject = $"{batch.Length} near pair(s)",
                    Decision = "the judge could not read them: " + ex.Reason + "; both stay drafts and are asked again next run",
                    By = DecidedBy.Rule,
                    Flags = ["judge-failed"],
                });
            }
        }

        foreach (var pair in pairs)
        {
            var key = MergeUnit + pair.First.Key + "|" + pair.Second.Key;
            if (!verdicts.TryGetValue(key, out var same) || same < thresholds.Yes || pair.Second.Standing != "draft")
            {
                continue;
            }

            // The earlier card is the lead, or the card the lead was merged into.
            var lead = pair.First;
            while (lead.Standing == "merged" && lead.Flags.FirstOrDefault(f => f.StartsWith("merged-into:", StringComparison.Ordinal)) is { } into && cards.TryGetValue(into["merged-into:".Length..], out var further) && further != lead)
            {
                lead = further;
            }

            if (lead == pair.Second)
            {
                continue;
            }

            foreach (var source in pair.Second.Sources.Where(s => !lead.Sources.Any(l => l.Kind == s.Kind && l.Path == s.Path && l.Line == s.Line)))
            {
                lead.Sources.Add(source);
            }

            pair.Second.Standing = "merged";
            pair.Second.Flags.Add("merged-into:" + lead.Key);
            merged++;
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "decision-merge",
                Subject = pair.Second.Key,
                Decision = $"merged into {lead.Key}: the judge read \"{Cut(pair.Second.Text, 80)}\" and \"{Cut(lead.Text, 80)}\" as one decision ({same:0.00}; likeness by words {pair.Likeness:0.00})",
                By = DecidedBy.Judge,
                Model = context.Judge.Options.Model,
                Confidence = same,
                Figures = new Dictionary<string, double> { ["same"] = same, ["likeness"] = pair.Likeness },
            });
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = PhaseName,
            Kind = "decision-merge",
            Subject = "the near pairs of drafts",
            Decision = $"{pairs.Count} pair(s) of drafts alike by half or more of their words but not merged by code; the judge read {asked} this run ({verdicts.Count} answered in all, {failed} failed); {merged} draft(s) merged into an earlier one with their sources",
            By = DecidedBy.Rule,
            Figures = new Dictionary<string, double> { ["pairs"] = pairs.Count, ["merged"] = merged },
        });
    }

    // ----- the drafts -----

    private static async Task<(DecisionDraft? Draft, decimal Usd)> DraftAsync(SurveyContext context, DecisionCard card, string system, List<DocumentCard> documents, CancellationToken cancellationToken)
    {
        var parts = new List<ReaderPart>
        {
            new ReaderPart.Instruction($"The decision as noted: \"{Cut(card.Text, 400)}\". Noted in {card.Sources.Count} place(s): {string.Join("; ", card.Sources.Take(4).Select(Describe))}. The INPUT TEXT below is the evidence from each, as data."),
        };
        var evidence = new StringBuilder();
        foreach (var source in card.Sources.Take(4))
        {
            if (source.Kind == "document")
            {
                var document = documents.FirstOrDefault(d => string.Equals(d.Path, source.Path, StringComparison.OrdinalIgnoreCase));
                evidence.Append("From ").Append(source.Path).Append(source.Line is { } line ? $" line {line}" : "").Append(':').Append('\n');
                if (source.Quote is { Length: > 0 } quote)
                {
                    evidence.Append("  Quote: ").Append(Cut(quote, 600)).Append('\n');
                }

                if (document is not null)
                {
                    evidence.Append("  The document, as the survey summarised it: ").Append(Cut(document.Summary, 800)).Append('\n');
                }
            }
            else
            {
                evidence.Append("From commit ").Append(source.Path.Length > 12 ? source.Path[..12] : source.Path).Append(source.Date is { } d ? $" of {d}" : "").Append(':').Append('\n');
                evidence.Append("  Message: ").Append(Cut(source.Title ?? "", 200)).Append('\n');
                if (source.Quote is { Length: > 0 } body)
                {
                    evidence.Append("  Body: ").Append(Cut(body, 600)).Append('\n');
                }
            }

            evidence.Append('\n');
        }

        var text = evidence.ToString();
        parts.Add(new ReaderPart.InputText(card.Sources[0].Path, 1, Math.Max(1, text.Count(c => c == '\n')), text) { Position = "the evidence" });
        var request = new ReaderRequest(PhaseName, $"the draft of decision {card.Key}", context.Run.Options.Models.For(PhaseName), system, parts)
        {
            MaxOutputTokens = 4_000,
            GlossaryText = context.Glossary.Render(),

            // ADR 0025: the thinking is off for drafting from given evidence, unless the run was told otherwise.
            Thinking = context.Run.Options.Reader.Thinking == ThinkingMode.Default ? ThinkingMode.Off : null,
        };
        var reply = await context.Reader.AskAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Discarded || reply.Reply is null)
        {
            card.Flags.Add("draft-discarded");
            return (null, reply.Reply?.Cost.Usd ?? 0m);
        }

        if (reply.FlaggedParts.Count > 0)
        {
            card.Flags.Add("read-with-flagged-text");
        }

        var json = reply.Json;
        var draft = new DecisionDraft
        {
            Title = Clean(Str(json["title"]), 80),
            Context = Clean(Str(json["context"]), 1_500),
            Decision = Clean(Str(json["decision"]), 1_000),
            Consequences = Clean(Str(json["consequences"]), 1_000),
            Date = DateOrNull(Str(json["date"])),
            Confidence = Dbl(json["confidence"]),
            Reasoning = Clean(Str(json["reasoning"]), 400),
        };
        if (draft.Title.Length == 0)
        {
            draft.Title = Cut(card.Text, 80);
        }

        if (draft.Decision.Length == 0)
        {
            draft.Decision = card.Text;
        }

        return (draft, reply.Reply.Cost.Usd);
    }

    // ----- the history (ADR 0021) -----

    private static async Task<HistoryResult> ReadHistoryAsync(SurveyContext context, IReadOnlyList<Commit> commits, List<DocumentCard> documents, CancellationToken cancellationToken)
    {
        var gap = DetectGap(commits, documents);
        var path = context.Options.HistoryPath;
        if (gap.Detected)
        {
            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "history",
                Subject = "the repository's history",
                Decision = "an older history seems to exist (ADR 0021): " + gap.Reason + (path is null ? "; asked for" : "; read from the path given"),
                By = DecidedBy.Rule,
                Evidence = [new Evidence(null, null, gap.Evidence)],
            });
        }

        if (path is null && gap.Detected)
        {
            var question = new Question("history:older", PhaseName,
                $"The repository's history seems to start after the events its documents describe: {gap.Reason}. Does an older history exist, and where? Answer with the path on this machine to a clone of it (a folder with its .git); a bundle or a zip is not read yet.",
                [], "none")
            {
                Context = gap.Evidence,
            };
            var answer = await context.AskAsync(question, "the repository's history", "no answer; the history before the root commit is not read (ADR 0020 rule 7)", cancellationToken).ConfigureAwait(false);
            if (answer is { Text.Length: > 0 } && !string.Equals(answer.Text.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                path = answer.Text.Trim();
            }
        }

        if (!gap.Detected)
        {
            // A gap an earlier run saw (a licence's dates) and this run does not: the question goes.
            context.Book.Withdraw("history:older");
        }

        var result = new HistoryResult([], path, gap.Detected, gap.Reason);
        if (path is not null)
        {
            var git = Directory.Exists(path) ? GitInfo.Read(path) : null;
            if (git is null)
            {
                context.Log.Record(new DecisionRecord
                {
                    Phase = PhaseName,
                    Kind = "history",
                    Subject = path,
                    Decision = "the older history could not be read there: not a folder with a git repository; nothing is claimed from it",
                    By = DecidedBy.Rule,
                    Flags = ["history-unreadable"],
                });
            }
            else
            {
                var log = git.Log(max: 20_000);
                result = new HistoryResult(log, path, gap.Detected, gap.Reason);
                context.Log.Record(new DecisionRecord
                {
                    Phase = PhaseName,
                    Kind = "history",
                    Subject = path,
                    Decision = $"the older history was read: {log.Count} commit(s)" + (log.Count > 0 ? $" from {log[^1].Date:yyyy-MM-dd} to {log[0].Date:yyyy-MM-dd}" : "") + "; the bundle carries the hash, date and message of what it takes, never a path into the clone (ADR 0021)",
                    By = DecidedBy.Rule,
                    Figures = new Dictionary<string, double> { ["commits"] = log.Count },
                });
            }
        }

        context.State.SaveRecord(DecisionCatalogue.HistoryKind, "history", JsonSerializer.Serialize(new HistoryRecord
        {
            Gap = gap.Detected,
            Reason = gap.Reason,
            Read = result.Commits.Count > 0,
            Where = result.Commits.Count > 0 ? path : null,
            Commits = result.Commits.Count,
        }, RunContext.JsonOptions), context.RunId);
        return result;
    }

    internal static (bool Detected, string Reason, string Evidence) DetectGap(IReadOnlyList<Commit> commits, List<DocumentCard> documents)
    {
        if (commits.Count == 0)
        {
            return (false, "", "");
        }

        var root = commits[^1];
        var text = root.Subject + " " + root.Body;
        if (ContinuesAnOlderLine().IsMatch(text))
        {
            return (true, $"the root commit {Cut(root.Hash, 12)} of {root.Date:yyyy-MM-dd} says \"{Cut(root.Subject, 100)}\"", Cut(text, 400));
        }

        var rootDay = root.Date.Date;
        var dated = new List<(string Path, DateTime Date)>();
        foreach (var document in documents.Where(d => HistoryKinds.Contains(d.Kind)))
        {
            foreach (var date in document.Dates)
            {
                if (DateTime.TryParseExact(date.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    dated.Add((document.Path, day));
                }
            }
        }

        var older = dated.Where(d => d.Date < rootDay.AddDays(-1)).OrderBy(d => d.Date).ToList();
        if (dated.Count >= 10 && older.Count * 5 >= dated.Count)
        {
            return (true, $"{older.Count} of {dated.Count} dates in the documents lie before the root commit's day {rootDay:yyyy-MM-dd} ({commits.Count} commits)",
                string.Join("; ", older.Take(5).Select(d => $"{d.Path}: {d.Date:yyyy-MM-dd}")));
        }

        return (false, "", "");
    }

    // ----- helpers -----

    private static List<string> ItemsNamed(DecisionCard card, List<IdScheme> schemes, Dictionary<string, string> byKey)
    {
        var found = new List<string>();
        if (schemes.Count == 0 || byKey.Count == 0)
        {
            return found;
        }

        var text = card.Text + " " + string.Join(" ", card.Sources.Select(s => s.Quote ?? ""));
        foreach (Match match in IdLike().Matches(text))
        {
            var normalised = IdNormaliser.Normalise(match.Value, schemes);
            if (normalised is not null && byKey.TryGetValue(normalised.Key, out var key) && !found.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(key);
            }
        }

        return found;
    }

    private static string? DateOf(DocumentCard card)
    {
        string? earliest = null;
        foreach (var date in card.Dates)
        {
            if (DateTime.TryParseExact(date.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                && (earliest is null || string.CompareOrdinal(date.Date, earliest) < 0))
            {
                earliest = date.Date;
            }
        }

        return earliest;
    }

    private static string Describe(DecisionSource source) => source.Kind == "document"
        ? $"{source.Path}{(source.Line is { } line ? $" line {line}" : "")}"
        : $"commit {(source.Path.Length > 12 ? source.Path[..12] : source.Path)}{(source.Kind == "history-commit" ? " of the older history" : "")}{(source.Date is { } d ? $" ({d})" : "")}";

    private static T? Deserialize<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, RunContext.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static double? Dbl(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var d) && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : null;

    private static string Clean(string? text, int max)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }

    private static string? DateOrNull(string? text) =>
        text is not null && DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? text.Trim() : null;

    private static string Figure(double? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "null";

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    [GeneratedRegex(@"\bfrom\s+[0-9a-f]{7,40}\b|\b(continues|continued|squash|squashed|imported|history|migrated from|renamed from|fresh start)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuesAnOlderLine();

    [GeneratedRegex(@"\b[A-Za-z]{1,8}-\d{1,6}[A-Za-z]?\b|\b[A-Z]{1,3}\d{2,5}\b|\b\d{3,5}\b")]
    private static partial Regex IdLike();

    [GeneratedRegex(@"^(?<letters>[A-Za-z]{0,8})-?0*(?<number>\d{1,6})$")]
    private static partial Regex HeadShape();

    private sealed record Candidate(string Text, DecisionSource Source);

    private sealed class Group(string key, Candidate lead, HashSet<string> words)
    {
        public string Key { get; } = key;

        public Candidate Lead { get; } = lead;

        public HashSet<string> Words { get; } = words;

        public List<Candidate> Members { get; } = [lead];
    }

    private sealed record AdrIndex(DocumentCard Card, HashSet<string> Words);

    private sealed record HistoryResult(IReadOnlyList<Commit> Commits, string? Path, bool Gap, string Reason);

    private sealed class HistoryRecord
    {
        public bool Gap { get; set; }

        public string Reason { get; set; } = "";

        public bool Read { get; set; }

        public string? Where { get; set; }

        public int Commits { get; set; }
    }

    internal sealed class JudgeFigures
    {
        public double Made { get; set; }

        /// <summary>How surely the text tells of a change carried out (backlog TR-15); the standing takes the higher of this and <see cref="Made"/>.</summary>
        public double CarriedOut { get; set; }

        public double Significant { get; set; }

        public double Recorded { get; set; }

        public string? RecordedIn { get; set; }

        /// <summary>How surely the text corrects the ADR it cites; 0 when it cites none.</summary>
        public double Corrects { get; set; }

        public string? CitedIn { get; set; }
    }
}
