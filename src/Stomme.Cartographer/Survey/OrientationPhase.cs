using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stomme.Conductor.Models;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Policies;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;

namespace Stomme.Cartographer.Survey;

/// <summary>
/// Phase 0 (the plan): orient in the repository from its guides, its tree and the shape of its history, then
/// name every file's kind. The reader proposes the nature, the id schemes, the vocabulary and the ledgers from
/// the guides; the reader names each document's kind from its head; rules name code, data, images and binaries;
/// the judge checks the kinds with the biggest cost of error; the engine confirms the id schemes by scanning
/// (ADR 0015) and decides whether documents are thin (ADR 0010, 0016). Every decision is logged. The phase
/// runs as units of work over the state (ADR 0017): the nature, one unit per document's kind, one per ledger
/// check, one for the id schemes; a unit done from the same input is not redone, and a person's answer to a
/// question about a file wins over what was stored (ADR 0007).
/// </summary>
public sealed class OrientationPhase(SurveyContext survey) : IPhase
{
    internal const string PhaseName = "orientation";

    public string Name => PhaseName;

    public Task RunAsync(CancellationToken cancellationToken) => RunAsync(survey, cancellationToken);

    private static async Task RunAsync(SurveyContext context, CancellationToken cancellationToken)
    {
        var map = new RepositoryMap();
        var files = context.Snapshot.Files;
        var documentLike = files.Where(f => f.Category is FileCategory.Document or FileCategory.TextUnknown).ToList();

        // 1. The nature, the schemes and the vocabulary, from the guides, the tree and the history: one unit.
        // What is not read (a backup copy, a file that looks like it holds a secret, a file the person excluded) is no guide and is not listed to the reader.
        var readable = documentLike.Where(f => f.NotRead is null).ToList();
        var guides = readable.Where(f => context.Table.IsGuide(f.Path)).OrderBy(f => GuideRank(f.Path)).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();
        map.Guides = guides.Select(g => g.Path).ToList();
        var natureHash = RunContext.HashOf("nature-v1", context.Playbook.Salt(["rules", "orientation-nature"], ["repository-map"], ["files", "kinds"], []),
            string.Join("|", guides.Select(g => g.Sha256)), string.Join("|", documentLike.Select(d => d.Path)));
        // The hash is over every document-like path, read or not: a file that is kept back now drops out of the map by rule,
        // and the nature the reader gave stands, so nothing is asked again and no earlier unit is read again for it.
        var storedNature = context.State.DoneUnit(PhaseName, "nature", natureHash);
        NatureResult nature;
        if (storedNature?.Result is not null && Deserialize<NatureResult>(storedNature.Result) is { } stored)
        {
            nature = stored;
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "nature", Subject = "the repository", Decision = $"resumed from the state (decided in run {storedNature.RunId})", By = DecidedBy.Rule });
        }
        else
        {
            context.Touch("orientation: the repository's nature");
            nature = await AskNatureAsync(context, guides, readable, cancellationToken).ConfigureAwait(false);
            context.State.CompleteUnit(PhaseName, context.RunId, "nature", natureHash, nature.Usd, nature);
        }

        ApplyNature(context, map, nature, fresh: storedNature is null);
        var natureKey = RunContext.HashOf(JsonSerializer.Serialize(nature.Reply, RunContext.JsonOptions));

        // 2. Every document's kind, from its head, in batches: one unit per file.
        await AskKindsAsync(context, map, documentLike, natureKey, cancellationToken).ConfigureAwait(false);

        // 3. A person's answer about a file wins (ADR 0007).
        foreach (var mapped in map.Files)
        {
            var answered = context.Answers.Get("kind:" + mapped.Path);
            if (answered is not null && KindIsKnown(context, NormaliseKind(answered)) && NormaliseKind(answered) != mapped.Kind)
            {
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "file-kind", Subject = mapped.Path, Decision = $"{NormaliseKind(answered)} (a person's answer; the reader had said {mapped.Kind})", By = DecidedBy.Person });
                mapped.Kind = NormaliseKind(answered);
                mapped.By = DecidedBy.Person;
            }
        }

        // 4. Kinds by rule for everything else.
        foreach (var file in files.Where(f => f.Category is not (FileCategory.Document or FileCategory.TextUnknown)))
        {
            var kind = file.Category switch
            {
                FileCategory.Code => "code",
                FileCategory.Data => "data",
                FileCategory.Config => "config",
                FileCategory.Image => "image",
                FileCategory.Design => "design-file",
                FileCategory.Archive => "archive",
                _ => "binary",
            };
            map.Files.Add(new MappedFile
            {
                Path = file.Path,
                Size = file.Size,
                Category = file.Category.ToString(),
                Kind = kind,
                Language = file.Language,
                By = DecidedBy.Rule,
                Reasoning = kind == "binary" ? "no decoder reads this extension; catalogued only" : $"by the playbook's file table ({file.Extension})",
            });
        }

        // 5. The id schemes, confirmed by scanning (ADR 0015): one unit. Before the ledger checks, which use them to find rows.
        var schemesHash = RunContext.HashOf("ids-v2", context.Snapshot.FilesHash, natureKey);
        var storedSchemes = context.State.DoneUnit(PhaseName, "id-schemes", schemesHash);
        if (storedSchemes?.Result is not null && Deserialize<List<IdScheme>>(storedSchemes.Result) is { } schemes)
        {
            map.IdSchemes = schemes;
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "id-scheme", Subject = "all schemes", Decision = $"resumed from the state (confirmed in run {storedSchemes.RunId})", By = DecidedBy.Rule });
        }
        else
        {
            context.Touch("orientation: confirming the id schemes by scanning");
            ConfirmIdSchemes(context, map);
            context.State.CompleteUnit(PhaseName, context.RunId, "id-schemes", schemesHash, 0m, map.IdSchemes);
        }

        // 6. The judge checks the ledgers, the kinds with the biggest cost of error: one unit per candidate.
        await CheckLedgersAsync(context, map, cancellationToken).ConfigureAwait(false);

        // 6b. Work the repository names and this copy does not hold: a ledger that is not there, a tracker outside it.
        await AskAboutWorkElsewhereAsync(context, map, cancellationToken).ConfigureAwait(false);

        // 7. Thin documents (ADR 0010, 0016), the counts, and the map into the state.
        DecideThin(context, map);
        map.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        map.CountsByKind = map.Files.GroupBy(f => f.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        map.CountsByCategory = map.Files.GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        map.FlaggedChunks = context.Reader.FlaggedCount;

        context.SaveMap(map);
        context.State.SaveRecord("digest", "map", MapDigest.Build(map, context.Shown, context.Options.Budgets.DigestChars), context.RunId);
        context.Glossary.AddFromMap(map);
        context.Touch("orientation done");
    }

    /// <summary>
    /// Work the repository names and this copy does not hold (the verdict's redo, change 12): a ledger the guides name
    /// that is not in the input, and a place outside the repository where its work is tracked. Each is on the map, a
    /// question for a person and a finding of the verify phase; never only a line of the log. Runs every run.
    /// </summary>
    private static async Task AskAboutWorkElsewhereAsync(SurveyContext context, RepositoryMap map, CancellationToken cancellationToken)
    {
        var asked = new HashSet<string>(StringComparer.Ordinal);
        var guideLines = new List<(string Path, IReadOnlyList<string> Lines)>();
        foreach (var path in map.Guides)
        {
            if (context.File(path) is { } file && context.Decode(file) is { } decoded)
            {
                guideLines.Add((path, decoded.Lines));
            }
        }

        string? FindIn(string needle)
        {
            foreach (var (path, lines) in guideLines)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{path}:{i + 1}";
                    }
                }
            }

            return null;
        }

        // A ledger that is not there. The guides' own words decide whether it was named at all: the reader's word alone is a guess.
        foreach (var missing in map.MissingLedgers.ToList())
        {
            var name = missing.Path[(missing.Path.LastIndexOf('/') + 1)..];
            missing.NamedIn = FindIn(missing.Path) ?? (name.Length >= 6 ? FindIn(name) : null);
            if (missing.NamedIn is null)
            {
                map.MissingLedgers.Remove(missing);
                continue;
            }

            if (missing.Kind != "change-log")
            {
                var question = new Question($"ledger-missing:{missing.Path}", PhaseName,
                    $"The repository's guides name {missing.Path} as a ledger of its work ({missing.NamedIn}), and no such file is in this copy. Its rows are not in this import. What is true of it?",
                    [new QuestionOption("not-in-this-copy", "the ledger exists elsewhere or was removed from this copy; go on without its rows"), new QuestionOption("guide-outdated", "the guide is out of date; there is no such ledger now")],
                    "not-in-this-copy")
                {
                    About = new QuestionAbout("ledger", [missing.Path], [], null),
                    DefaultReason = "the engine reads only what is in the input, and a guide still names the file",
                };
                asked.Add(question.Key);
                var answer = await context.AskAsync(question, missing.Path, "no answer; the import goes on without its rows and says so", cancellationToken).ConfigureAwait(false);
                missing.Dismissed = answer?.Option == "guide-outdated";
            }

            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "ledger",
                Subject = missing.Path,
                Decision = missing.Dismissed
                    ? $"named as {missing.Kind} at {missing.NamedIn} and not in the input; a person says the guide is out of date, so nothing is missing"
                    : $"named as {missing.Kind} at {missing.NamedIn} and not in the input: its rows are not in this import; a finding of the verify phase{(missing.Kind == "change-log" ? "" : " and a question for a person")}",
                By = missing.Dismissed ? DecidedBy.Person : DecidedBy.Rule,
                Reasoning = missing.Reasoning,
                Flags = ["missing", "ledger-missing"],
            });
        }

        // A tracker's address in a guide, by code.
        foreach (var (path, lines) in guideLines)
        {
            for (var i = 0; i < lines.Count && map.WorkElsewhere.Count < 8; i++)
            {
                foreach (var address in OutsideTrackers.In(lines[i]))
                {
                    if (!map.WorkElsewhere.Any(w => string.Equals(w.Where, address, StringComparison.OrdinalIgnoreCase)))
                    {
                        map.WorkElsewhere.Add(new WorkElsewhere { Where = address, Path = path, Line = i + 1, NamedIn = $"{path}:{i + 1}", By = DecidedBy.Rule });
                    }
                }
            }
        }

        // What the reader noted about the repository, read by the judge: one unit per note.
        var thresholds = context.Run.Options.Thresholds;
        var salt = context.Playbook.Salt([], [], [], ["orientation-notes"]);
        foreach (var note in map.Notes.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).Take(12))
        {
            var hash = RunContext.HashOf("note-tracker-v1", note, salt);
            var unit = "note-tracker/" + hash[..Math.Min(16, hash.Length)];
            double figure;
            if (context.State.DoneUnit(PhaseName, unit, hash)?.Result is { } stored && Deserialize<NoteCheck>(stored) is { } check)
            {
                figure = check.TrackedOutside;
            }
            else
            {
                try
                {
                    var result = await context.Judge.AskAsync("orientation", "does a note of the reader's say the work is tracked outside the repository", new JsonObject { ["note"] = note }, JudgeQuestions.Fill(context.Playbook.JudgeSet("orientation-notes")), cancellationToken).ConfigureAwait(false);
                    figure = result.Reply.Noul("tracked_outside");
                    context.State.CompleteUnit(PhaseName, context.RunId, unit, hash, result.Cost.Usd, new NoteCheck { TrackedOutside = figure });
                }
                catch (JudgeUnavailableException ex)
                {
                    context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "work-elsewhere", Subject = Cut(note, 80), Decision = "the judge gave no answer (" + ex.Reason + "); the note is read again next run", By = DecidedBy.Rule, Flags = ["judge-unavailable"] });
                    continue;
                }
            }

            if (figure >= thresholds.Yes)
            {
                map.WorkElsewhere.Add(new WorkElsewhere { Where = note.Trim(), NamedIn = "the reader's notes on the repository", By = DecidedBy.Judge, Probability = Math.Round(figure, 3) });
            }
        }

        if (map.WorkElsewhere.Count > 0)
        {
            var places = string.Join("; ", map.WorkElsewhere.Take(5).Select(w => $"{Cut(w.Where, 200)} ({w.NamedIn})"));
            var question = new Question("work-elsewhere", PhaseName,
                $"The repository points to a place outside it where its work is tracked: {places}. This import reads only the repository's own files, so nothing kept there is in it. Is there work there that belongs with this import?",
                [new QuestionOption("not-imported", "yes or perhaps; go on, and say that the import does not hold it"), new QuestionOption("nothing-there", "no; nothing kept there belongs with this import")],
                "not-imported")
            {
                About = new QuestionAbout("work-elsewhere", map.WorkElsewhere.Where(w => w.Path is not null).Select(w => w.Path!).Distinct(StringComparer.Ordinal).ToList(), [], null),
                DefaultReason = "the engine reads only the input folder; what is kept elsewhere is named, never fetched",
            };
            asked.Add(question.Key);
            var answer = await context.AskAsync(question, "work tracked outside the repository", "no answer; the import goes on and says what it does not hold", cancellationToken).ConfigureAwait(false);
            var dismissed = answer?.Option == "nothing-there";
            foreach (var place in map.WorkElsewhere)
            {
                place.Dismissed = dismissed;
            }

            context.Log.Record(new DecisionRecord
            {
                Phase = PhaseName,
                Kind = "work-elsewhere",
                Subject = $"{map.WorkElsewhere.Count} place(s) outside the repository",
                Decision = dismissed
                    ? $"named ({places}); a person says nothing kept there belongs with this import"
                    : $"the repository says its work is tracked there ({places}): not read, not in this import; a finding of the verify phase and a question for a person",
                By = dismissed ? DecidedBy.Person : DecidedBy.Rule,
                Flags = ["work-elsewhere"],
            });
        }

        // A question of an earlier run about something that is no longer so (the ledger is there now, the address is gone) leaves.
        foreach (var open in context.Book.Open().Where(q => (q.Question.Key.StartsWith("ledger-missing:", StringComparison.Ordinal) || q.Question.Key == "work-elsewhere") && !asked.Contains(q.Question.Key)).ToList())
        {
            context.Book.Withdraw(open.Question.Key);
        }
    }

    private sealed class NoteCheck
    {
        public double TrackedOutside { get; set; }
    }

    private static int GuideRank(string path) => path.ToUpperInvariant() switch
    {
        "CLAUDE.MD" => 0,
        "AGENTS.MD" => 1,
        var p when p.StartsWith("README", StringComparison.Ordinal) => 2,
        var p when p.StartsWith("CONTRIBUTING", StringComparison.Ordinal) => 3,
        _ => 4,
    };

    private static async Task<NatureResult> AskNatureAsync(
        SurveyContext context, List<SourceFile> guides, List<SourceFile> documentLike, CancellationToken cancellationToken)
    {
        var budgets = context.Options.Budgets;
        var parts = new List<ReaderPart>
        {
            new ReaderPart.Instruction(TreeSummary.Build(context.Shown, context.Table, documentLike, 40_000)),
            new ReaderPart.Instruction(HistoryShape(context.Snapshot)),
        };
        foreach (var guide in guides)
        {
            var decoded = context.Decode(guide);
            if (decoded is null)
            {
                continue;
            }

            var text = decoded.Text;
            var endLine = decoded.Lines.Count;
            if (text.Length > budgets.GuideChars)
            {
                (text, endLine) = Chunker.Head(decoded, budgets.GuideChars);
                text += "\n[cut by the engine: the file goes on]";
            }

            parts.Add(new ReaderPart.InputText(guide.Path, 1, endLine, text));
        }

        if (guides.Count == 0)
        {
            parts.Add(new ReaderPart.Instruction("The repository has no guide file (no README, CLAUDE.md, AGENTS.md or CONTRIBUTING); orient from the tree and the history alone, and say so in your reasoning."));
        }

        var system = context.SystemPrompt("orientation-nature", "repository-map");
        var request = new ReaderRequest("orientation", "the repository's nature, id schemes, vocabulary and ledgers", context.Run.Options.Models.For(PhaseName), system, parts)
        {
            GlossaryText = context.Glossary.Render(),
        };
        var reply = await context.Reader.AskAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Reply is null)
        {
            throw new ReaderException("The orientation reply was discarded by the injection gate: " + reply.DiscardReason);
        }

        var usd = reply.Reply.Cost.Usd;
        var parsed = Parse<NatureReply>(reply.Json) ?? new NatureReply();
        var lookedCloser = new List<string>();

        // One round of "look closer" (ADR 0002): the reader may ask for a file in full or a folder listing.
        var requests = (parsed.Requests ?? []).Take(budgets.LookCloserRequests).ToList();
        if (requests.Count > 0)
        {
            var extra = new List<ReaderPart>();
            var charsLeft = budgets.LookCloserChars;
            foreach (var lookCloser in requests)
            {
                if (string.IsNullOrWhiteSpace(lookCloser.Path))
                {
                    continue;
                }

                var path = lookCloser.Path.Replace('\\', '/').TrimStart('/').TrimEnd('/');
                if (lookCloser.Kind == "listing")
                {
                    var listing = TreeSummary.Listing(context.Shown, path, 400);
                    extra.Add(new ReaderPart.Instruction($"Listing of {path}/ as you asked:\n{listing}"));
                    lookedCloser.Add("listing " + path);
                    context.Log.Record(new DecisionRecord { Phase = "orientation", Kind = "look-closer", Subject = path, Decision = "the reader asked for a folder listing and got it", By = DecidedBy.Reader, Reasoning = lookCloser.Why, Call = reply.Reply.CallSeq });
                }
                else
                {
                    var file = context.File(path);
                    var decoded = file is null ? null : context.Decode(file);
                    if (decoded is null)
                    {
                        extra.Add(new ReaderPart.Instruction($"You asked for {path}; there is no readable file at that path."));
                        context.Log.Record(new DecisionRecord { Phase = "orientation", Kind = "look-closer", Subject = path, Decision = "the reader asked for a file that does not exist or cannot be read", By = DecidedBy.Reader, Reasoning = lookCloser.Why, Flags = ["missing"] });
                        continue;
                    }

                    var (text, endLine) = Chunker.Head(decoded, Math.Min(charsLeft, budgets.LookCloserChars / Math.Max(1, requests.Count)));
                    charsLeft -= text.Length;
                    extra.Add(new ReaderPart.InputText(decoded.Path, 1, endLine, text));
                    lookedCloser.Add("file " + path);
                    context.Log.Record(new DecisionRecord { Phase = "orientation", Kind = "look-closer", Subject = path, Decision = $"the reader asked for this file and was shown its first {endLine} lines", By = DecidedBy.Reader, Reasoning = lookCloser.Why, Call = reply.Reply.CallSeq });
                }
            }

            if (extra.Count > 0)
            {
                var second = new List<ReaderPart>(parts) { new ReaderPart.Instruction("Your first answer asked to look closer; the material follows. Answer the whole task again, in full, with no requests this time.") };
                second.AddRange(extra);
                var secondReply = await context.Reader.AskAsync(request with { Parts = second, Purpose = request.Purpose + " (after looking closer)" }, cancellationToken).ConfigureAwait(false);
                if (secondReply.Reply is not null)
                {
                    reply = secondReply;
                    usd += secondReply.Reply.Cost.Usd;
                    parsed = Parse<NatureReply>(reply.Json) ?? parsed;
                }
            }
        }

        return new NatureResult
        {
            Reply = parsed,
            AnsweredBy = reply.Reply!.AnsweredBy ?? reply.Reply.Model,
            CallSeq = reply.Reply.CallSeq,
            Usd = usd,
            FlaggedParts = reply.FlaggedParts.ToList(),
            LookedCloser = lookedCloser,
        };
    }

    /// <summary>Fills the map from the nature reply; logs the decisions when the unit was freshly computed.</summary>
    private static void ApplyNature(SurveyContext context, RepositoryMap map, NatureResult result, bool fresh)
    {
        var nature = result.Reply;
        var model = result.AnsweredBy;
        var callSeq = result.CallSeq;
        map.Nature = new NatureEntry
        {
            Kind = (nature.Nature?.Kind ?? "unknown").Trim().ToLowerInvariant(),
            Description = nature.Nature?.Description ?? "",
            Confidence = nature.Nature?.Confidence ?? 0,
            Reasoning = nature.Nature?.Reasoning ?? "",
            Evidence = (nature.Nature?.Evidence ?? []).Select(ToEntry).ToList(),
        };
        map.NaturalLanguages = nature.NaturalLanguages ?? [];
        map.ProgrammingLanguages = nature.ProgrammingLanguages ?? [];
        map.DocumentRoots = nature.DocumentRoots ?? [];
        map.Notes = nature.Notes ?? [];
        map.Reasoning = nature.Reasoning ?? "";
        map.ReaderThinView = nature.DocumentsThin is null ? null : $"{(nature.DocumentsThin.Value ? "thin" : "not thin")}: {nature.DocumentsThin.Reasoning}";
        if (fresh)
        {
            context.Log.Record(new DecisionRecord
            {
                Phase = "orientation",
                Kind = "nature",
                Subject = "the repository",
                Decision = $"{map.Nature.Kind}: {map.Nature.Description}",
                By = DecidedBy.Reader,
                Model = model,
                Evidence = map.Nature.Evidence.Select(e => new Evidence(e.Path, e.Line, e.Quote)).ToList(),
                Reasoning = map.Nature.Reasoning,
                Confidence = map.Nature.Confidence,
                Call = callSeq,
                Flags = result.FlaggedParts.Count > 0 ? ["read-with-flagged-text"] : [],
            });
        }

        foreach (var scheme in nature.IdSchemes ?? [])
        {
            if (string.IsNullOrWhiteSpace(scheme.Example))
            {
                continue;
            }

            map.IdSchemes.Add(new IdScheme
            {
                Name = scheme.Name ?? scheme.Example,
                Example = scheme.Example,
                Regex = scheme.Regex ?? "",
                NumericGroup = scheme.NumericGroup,
                RefersTo = scheme.RefersTo ?? "unknown",
                WhereDefined = scheme.WhereDefined,
                Reasoning = scheme.Reasoning ?? "",
            });
        }

        foreach (var word in nature.StatusVocabulary ?? [])
        {
            if (string.IsNullOrWhiteSpace(word.Word))
            {
                continue;
            }

            map.StatusVocabulary.Add(new VocabularyEntry { Word = word.Word, Meaning = word.Meaning ?? "", Mapped = word.Mapped });
            if (fresh)
            {
                context.Log.Record(new DecisionRecord
                {
                    Phase = "orientation",
                    Kind = "status-word",
                    Subject = word.Word,
                    Decision = $"means \"{word.Meaning}\"; mapped to {word.Mapped ?? "(nothing)"}",
                    By = DecidedBy.Reader,
                    Model = model,
                    Evidence = word.Evidence is null ? [] : [new Evidence(word.Evidence.Path, word.Evidence.Line, word.Evidence.Quote)],
                    Call = callSeq,
                });
            }
        }

        foreach (var word in nature.TierVocabulary ?? [])
        {
            if (string.IsNullOrWhiteSpace(word.Word))
            {
                continue;
            }

            map.TierVocabulary.Add(new VocabularyEntry { Word = word.Word, Meaning = word.Meaning ?? "", Mapped = word.Mapped });
            if (fresh)
            {
                context.Log.Record(new DecisionRecord
                {
                    Phase = "orientation",
                    Kind = "tier-word",
                    Subject = word.Word,
                    Decision = $"means \"{word.Meaning}\"",
                    By = DecidedBy.Reader,
                    Model = model,
                    Evidence = word.Evidence is null ? [] : [new Evidence(word.Evidence.Path, word.Evidence.Line, word.Evidence.Quote)],
                    Call = callSeq,
                });
            }
        }

        if (nature.SpecIndex is { Path.Length: > 0 } index)
        {
            var exists = context.File(index.Path) is not null;
            map.SpecIndex = exists ? new SpecIndexEntry { Path = index.Path, Anchor = index.Anchor, Reasoning = index.Reasoning ?? "" } : null;
            if (fresh)
            {
                context.Log.Record(new DecisionRecord
                {
                    Phase = "orientation",
                    Kind = "spec-index",
                    Subject = index.Path,
                    Decision = exists ? $"specs are indexed at {index.Path}{(index.Anchor is null ? "" : "#" + index.Anchor)}" : $"the reader named {index.Path} as the spec index, but no such file exists; dropped",
                    By = exists ? DecidedBy.Reader : DecidedBy.Rule,
                    Model = model,
                    Reasoning = index.Reasoning,
                    Call = callSeq,
                    Flags = exists ? [] : ["missing"],
                });
            }
        }

        foreach (var ledger in nature.Ledgers ?? [])
        {
            if (string.IsNullOrWhiteSpace(ledger.Path))
            {
                continue;
            }

            var file = context.File(ledger.Path);
            if (file is null)
            {
                if (fresh)
                {
                    context.Log.Record(new DecisionRecord { Phase = "orientation", Kind = "ledger", Subject = ledger.Path, Decision = "the reader named this as a ledger, but no such file exists; dropped", By = DecidedBy.Rule, Reasoning = ledger.Reasoning, Flags = ["missing"], Call = callSeq });
                }

                // Whether the guides themselves name it is settled by code after the ledger checks; a guess of the reader's ends here.
                map.MissingLedgers.Add(new MissingLedger { Path = ledger.Path.Trim().Replace('\\', '/'), Kind = NormaliseKind(ledger.Kind), Reasoning = ledger.Reasoning ?? "" });
                continue;
            }

            map.Ledgers.Add(new LedgerEntry { Path = file.Path, Kind = NormaliseKind(ledger.Kind), Reasoning = ledger.Reasoning ?? "", NamedBy = "the guides, read by the reader" });
            if (fresh)
            {
                context.Log.Record(new DecisionRecord
                {
                    Phase = "orientation",
                    Kind = "ledger",
                    Subject = file.Path,
                    Decision = $"named as {NormaliseKind(ledger.Kind)} by the guides",
                    By = DecidedBy.Reader,
                    Model = model,
                    Evidence = ledger.Evidence is null ? [] : [new Evidence(ledger.Evidence.Path, ledger.Evidence.Line, ledger.Evidence.Quote)],
                    Reasoning = ledger.Reasoning,
                    Call = callSeq,
                });
            }
        }
    }

    private static async Task AskKindsAsync(SurveyContext context, RepositoryMap map, List<SourceFile> documentLike, string natureKey, CancellationToken cancellationToken)
    {
        var budgets = context.Options.Budgets;
        var pending = new List<(SourceFile File, DecodedDocument Doc, string Head, int EndLine, string Hash)>();
        var resumed = 0;
        var immediate = new List<(string Key, string InputHash, decimal Usd, object? Result)>();
        foreach (var file in documentLike)
        {
            if (!context.Decoders.CanDecode(file))
            {
                // Not read now, whatever an earlier run made of it: bytes under a document's extension, a backup copy, a
                // file withheld or excluded. It is on the map as unreadable and goes no further.
                map.Files.Add(new MappedFile
                {
                    Path = file.Path,
                    Size = file.Size,
                    Category = file.Category.ToString(),
                    Kind = "unreadable",
                    By = DecidedBy.Rule,
                    Reasoning = file.NotRead is null ? "no decoder could read this file" : "not read: " + file.NotRead,
                    Flags = file.NotRead is null ? ["unreadable"] : ["unreadable", "not-read"],
                });
                continue;
            }

            var hash = RunContext.HashOf("kind-v1", file.Sha256, context.Playbook.Salt(["rules", "orientation-kinds"], ["file-kinds"], ["kinds"], []), natureKey);
            var done = context.State.DoneUnit(PhaseName, "kind/" + file.Path, hash);
            if (done?.Result is not null && Deserialize<MappedFile>(done.Result) is { } mapped)
            {
                map.Files.Add(mapped);
                resumed++;
                continue;
            }

            var decoded = context.Decode(file);
            if (decoded is null)
            {
                var unreadable = new MappedFile { Path = file.Path, Size = file.Size, Category = file.Category.ToString(), Kind = "unreadable", By = DecidedBy.Rule, Reasoning = "no decoder could read this file", Flags = ["unreadable"] };
                map.Files.Add(unreadable);
                immediate.Add(("kind/" + file.Path, hash, 0m, unreadable));
                continue;
            }

            var (head, endLine) = Chunker.Head(decoded, budgets.HeadChars);
            pending.Add((file, decoded, head, endLine, hash));
        }

        if (immediate.Count > 0)
        {
            context.State.CompleteUnits(PhaseName, context.RunId, immediate);
        }

        if (resumed > 0)
        {
            context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "file-kind", Subject = $"{resumed} document(s)", Decision = "their kinds were resumed from the state: unchanged files, unchanged playbook, unchanged nature", By = DecidedBy.Rule });
        }

        var batches = new List<List<(SourceFile File, DecodedDocument Doc, string Head, int EndLine, string Hash)>>();
        var batch = new List<(SourceFile File, DecodedDocument Doc, string Head, int EndLine, string Hash)>();
        var chars = 0;
        foreach (var head in pending)
        {
            if (batch.Count > 0 && (chars + head.Head.Length > budgets.OrientationBatchChars || batch.Count >= 40))
            {
                batches.Add(batch);
                batch = [];
                chars = 0;
            }

            batch.Add(head);
            chars += head.Head.Length;
        }

        if (batch.Count > 0)
        {
            batches.Add(batch);
        }

        var mapGate = new Lock();
        var vocabulary = KindVocabulary(context);
        var mapSummary = MapSummary(map);
        var batchesDone = 0;
        await Parallel.ForEachAsync(batches.Select((b, i) => (Batch: b, Index: i)),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Run.Options.Reader.Concurrency), CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                var (results, usd) = await AskKindBatchAsync(context, item.Batch, item.Index, batches.Count, vocabulary, mapSummary, ct).ConfigureAwait(false);
                var share = results.Count == 0 ? 0m : usd / results.Count;
                var hashes = item.Batch.ToDictionary(b => b.File.Path, b => b.Hash, StringComparer.Ordinal);
                context.State.CompleteUnits(PhaseName, context.RunId,
                    results.Where(r => hashes.ContainsKey(r.Path)).Select(r => ("kind/" + r.Path, hashes[r.Path], share, (object?)r)));
                lock (mapGate)
                {
                    map.Files.AddRange(results);
                }

                var n = Interlocked.Increment(ref batchesDone);
                context.Progress($"Orientation: kinds batch {n} of {batches.Count}");
                context.Touch($"orientation: kinds, {n} of {batches.Count} batches done");
            }).ConfigureAwait(false);
    }

    private static async Task<(List<MappedFile> Results, decimal Usd)> AskKindBatchAsync(
        SurveyContext context, List<(SourceFile File, DecodedDocument Doc, string Head, int EndLine, string Hash)> batch, int index, int count,
        string vocabulary, string mapSummary, CancellationToken cancellationToken)
    {
        var listing = new StringBuilder();
        listing.AppendLine(CultureInfo.InvariantCulture, $"Batch {index + 1} of {count}: {batch.Count} files. For EACH path below, give one entry in files[]. Path, size in bytes, line count:");
        foreach (var (file, doc, _, _, _) in batch)
        {
            listing.AppendLine(CultureInfo.InvariantCulture, $"- {file.Path} ({file.Size} bytes, {doc.Lines.Count} lines)");
        }

        var parts = new List<ReaderPart>
        {
            new ReaderPart.Instruction(mapSummary),
            new ReaderPart.Instruction(listing.ToString()),
        };
        foreach (var (file, doc, head, endLine, _) in batch)
        {
            parts.Add(new ReaderPart.InputText(file.Path, 1, endLine, head) { Position = endLine < doc.Lines.Count ? $"the first {endLine} of {doc.Lines.Count} lines" : "the whole file" });
        }

        var system = context.SystemPrompt("orientation-kinds", "file-kinds", new Dictionary<string, string> { ["kinds"] = vocabulary, ["not_documents"] = KindVocabulary(context, "not-a-document") });
        var request = new ReaderRequest("orientation", $"the kind of each document, batch {index + 1} of {count}", context.Run.Options.Models.For(PhaseName), system, parts)
        {
            GlossaryText = context.Glossary.Render(),
        };
        var reply = await context.Reader.AskAsync(request, cancellationToken).ConfigureAwait(false);
        var usd = reply.Reply?.Cost.Usd ?? 0m;
        var results = new List<MappedFile>();
        var answered = new Dictionary<string, FileKindReply>(StringComparer.OrdinalIgnoreCase);
        if (reply.Reply is not null)
        {
            var parsed = Parse<FileKindsReply>(reply.Json);
            foreach (var entry in parsed?.Files ?? [])
            {
                if (!string.IsNullOrWhiteSpace(entry.Path))
                {
                    answered[entry.Path.Replace('\\', '/').TrimStart('/')] = entry;
                }
            }
        }

        var missing = batch.Where(b => !answered.ContainsKey(b.File.Path)).ToList();
        if (missing.Count > 0 && missing.Count < batch.Count && reply.Reply is not null)
        {
            // One more try for the files the reader skipped, in a batch of their own.
            var (again, moreUsd) = await AskKindBatchAsync(context, missing, index, count, vocabulary, mapSummary, cancellationToken).ConfigureAwait(false);
            results.AddRange(again);
            usd += moreUsd;
            missing.Clear();
        }

        foreach (var (file, _, _, _, _) in batch)
        {
            if (!answered.TryGetValue(file.Path, out var entry))
            {
                if (missing.Any(m => m.File.Path == file.Path))
                {
                    var question = new Question($"kind:{file.Path}", "orientation",
                        $"What kind of document is {file.Path}? The reader gave no answer for it{(reply.Discarded ? " (its output was discarded by the injection gate: " + reply.DiscardReason + ")" : "")}.",
                        KindOptions(context), "other");
                    var answer = await context.AskAsync(question, file.Path, "no answer; kind other", cancellationToken).ConfigureAwait(false);
                    results.Add(new MappedFile
                    {
                        Path = file.Path,
                        Size = file.Size,
                        Category = file.Category.ToString(),
                        Kind = answer?.Option ?? "other",
                        By = answer is null ? DecidedBy.Rule : DecidedBy.Person,
                        Reasoning = answer is null ? "the reader gave no kind for this file" : "answered by a person",
                        Flags = answer is null ? ["reader-skipped"] : [],
                    });
                }

                continue;
            }

            var kind = NormaliseKind(entry.Kind);
            var flags = new List<string>();
            if (reply.FlaggedParts.Any(p => p.StartsWith(file.Path + ":", StringComparison.Ordinal)))
            {
                flags.Add("read-with-flagged-text");
            }

            if (!KindIsKnown(context, kind))
            {
                flags.Add("unknown-kind:" + kind);
                kind = "other";
            }

            var mapped = new MappedFile
            {
                Path = file.Path,
                Size = file.Size,
                Category = file.Category.ToString(),
                Kind = kind,
                Title = entry.Title,
                Language = Catalogue.DocumentsPhase.LanguageCode(entry.Language),
                Confidence = entry.Confidence,
                EvidenceLine = entry.Evidence?.Line,
                EvidenceQuote = entry.Evidence?.Quote,
                Reasoning = entry.Reasoning,
                By = DecidedBy.Reader,
                Flags = flags,
                Alternatives = entry.Alternatives ?? [],
            };
            results.Add(mapped);
            context.Log.Record(new DecisionRecord
            {
                Phase = "orientation",
                Kind = "file-kind",
                Subject = file.Path,
                Decision = kind + (entry.Title is null ? "" : $" (title: {entry.Title})") + (entry.Language is null ? "" : $", language {entry.Language}"),
                By = DecidedBy.Reader,
                Model = reply.Reply!.AnsweredBy ?? reply.Reply.Model,
                Evidence = [new Evidence(file.Path, entry.Evidence?.Line, entry.Evidence?.Quote)],
                Reasoning = entry.Reasoning,
                Confidence = entry.Confidence,
                Alternatives = entry.Alternatives ?? [],
                Call = reply.Reply.CallSeq,
                Flags = flags,
            });
        }

        return (results, usd);
    }

    private static async Task CheckLedgersAsync(SurveyContext context, RepositoryMap map, CancellationToken cancellationToken)
    {
        var candidates = map.Files.Where(f => f.Kind is "open-ledger" or "finished-ledger" or "change-log").ToList();
        foreach (var ledger in map.Ledgers)
        {
            var file = map.File(ledger.Path);
            if (file is not null && !candidates.Contains(file))
            {
                candidates.Add(file);
            }
        }

        var thresholds = context.Run.Options.Thresholds;
        var idPatterns = new List<Regex>();
        foreach (var scheme in map.IdSchemes.Where(s => s.Confirmed && !string.IsNullOrWhiteSpace(s.Regex)))
        {
            try
            {
                idPatterns.Add(new Regex(scheme.Regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            }
            catch (ArgumentException)
            {
            }
        }

        foreach (var mapped in candidates)
        {
            var file = context.File(mapped.Path);
            var decoded = file is null ? null : context.Decode(file);
            if (file is null || decoded is null)
            {
                continue;
            }

            var unitHash = RunContext.HashOf("ledger-v2", file.Sha256, context.Playbook.Salt([], [], ["kinds"], ["orientation", "orientation-change-log"]), mapped.Kind);
            var stored = context.State.DoneUnit(PhaseName, "ledger-check/" + mapped.Path, unitHash);
            if (stored?.Result is not null && Deserialize<LedgerCheckResult>(stored.Result) is { } previous)
            {
                ApplyLedgerCheck(map, mapped, previous);
                WithdrawSettledQuestions(context, mapped.Path, previous);
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "kind-check", Subject = mapped.Path, Decision = $"resumed from the state (judged in run {stored.RunId}): {previous.Decision}", By = DecidedBy.Rule });
                continue;
            }

            context.Touch($"orientation: the judge checks {mapped.Path}");
            var (head, endLine) = LedgerSample(decoded, Math.Min(context.Judge.Options.MaxStateChars / 2, 12_000), idPatterns);
            var state = new JsonObject { ["text"] = head, ["path"] = mapped.Path };
            var isChangeLogKind = mapped.Kind == "change-log";
            var questions = JudgeQuestions.Fill(context.Playbook.JudgeSet(isChangeLogKind ? "orientation-change-log" : "orientation"));
            JudgeResult result;
            try
            {
                result = await context.Judge.AskAsync("orientation", isChangeLogKind ? $"is {mapped.Path} a change log" : $"is {mapped.Path} a ledger of work items", state, questions, cancellationToken).ConfigureAwait(false);
            }
            catch (JudgeUnavailableException ex)
            {
                context.Log.Record(new DecisionRecord { Phase = "orientation", Kind = "kind-check", Subject = mapped.Path, Decision = "the judge gave no answer (" + ex.Reason + "); the reader's kind stands, unchecked", By = DecidedBy.Rule, Flags = ["judge-unavailable"] });
                mapped.Flags.Add("unchecked");
                continue;
            }

            var readerKind = mapped.Kind;
            var guideKind = map.Ledgers.FirstOrDefault(l => l.Path == mapped.Path)?.Kind;
            if (isChangeLogKind)
            {
                var isChangeLog = result.Reply.Noul("is_change_log");
                var changeLogCheck = new LedgerCheckResult
                {
                    Kind = readerKind,
                    Figures = new Dictionary<string, double> { ["is_change_log"] = isChangeLog },
                    Confirmed = isChangeLog >= thresholds.Yes,
                    Model = result.Reply.Model,
                };
                if (isChangeLog >= thresholds.Yes)
                {
                    changeLogCheck.Decision = $"confirmed as a change log ({isChangeLog:0.00})";
                }
                else if (isChangeLog <= thresholds.No)
                {
                    changeLogCheck.Decision = $"the judge does not read this as a change log ({isChangeLog:0.00}); the kind becomes other and a person is asked";
                    mapped.Kind = "other";
                    mapped.Flags.Add("downgraded-by-judge");
                    var question = new Question($"kind:{mapped.Path}", "orientation",
                        $"The reader called {mapped.Path} a change log, but the judge does not read its head and sample as dated entries of what landed (probability {isChangeLog:0.00}). What is it?",
                        KindOptions(context), "other")
                    {
                        Context = (mapped.EvidenceQuote ?? "") + "\n" + mapped.Reasoning,
                    };
                    var answer = await context.AskAsync(question, mapped.Path, "no answer; kind other", cancellationToken).ConfigureAwait(false);
                    if (answer?.Option is { } picked)
                    {
                        mapped.Kind = picked;
                        mapped.By = DecidedBy.Person;
                    }

                    changeLogCheck.QuestionKey = question.Key;
                    changeLogCheck.Flags.Add("downgraded");
                }
                else
                {
                    changeLogCheck.Decision = $"the judge is unsure this is a change log ({isChangeLog:0.00}); the reader's change-log stands and a person is asked";
                    changeLogCheck.QuestionKey = (await AskUnsureLedgerQuestionAsync(context, mapped, isChangeLog, cancellationToken).ConfigureAwait(false)).Key;
                    changeLogCheck.Flags.Add("unsettled");
                }

                changeLogCheck.Kind = mapped.Kind;
                changeLogCheck.By = mapped.By;
                ApplyLedgerCheck(map, mapped, changeLogCheck);
                WithdrawSettledQuestions(context, mapped.Path, changeLogCheck);
                context.State.CompleteUnit(PhaseName, context.RunId, "ledger-check/" + mapped.Path, unitHash, result.Cost.Usd, changeLogCheck);
                context.Log.Record(new DecisionRecord
                {
                    Phase = "orientation",
                    Kind = "kind-check",
                    Subject = mapped.Path,
                    Decision = changeLogCheck.Decision,
                    By = DecidedBy.Judge,
                    Model = result.Reply.Model,
                    Evidence = [new Evidence(mapped.Path, 1, $"the head and a sample of entries, {endLine} lines in all")],
                    Confidence = isChangeLog,
                    Figures = changeLogCheck.Figures,
                    Flags = changeLogCheck.Flags,
                    Question = changeLogCheck.QuestionKey,
                    Call = result.CallSeq,
                });
                continue;
            }

            var isLedger = result.Reply.Noul("is_ledger");
            var isFinished = result.Reply.Noul("is_finished");
            var figures = new Dictionary<string, double> { ["is_ledger"] = isLedger, ["is_finished"] = isFinished };
            var check = new LedgerCheckResult { Kind = readerKind, Figures = figures, Confirmed = isLedger >= thresholds.Yes, Model = result.Reply.Model };
            if (isLedger >= thresholds.Yes)
            {
                var finished = isFinished >= thresholds.Yes ? "finished-ledger" : isFinished <= thresholds.No ? "open-ledger" : null;
                if (readerKind == "change-log")
                {
                    check.Decision = $"confirmed as a ledger-like file ({isLedger:0.00}); kept as {readerKind} as the reader said";
                }
                else if (finished is null)
                {
                    check.Decision = $"confirmed as a ledger ({isLedger:0.00}); whether it holds finished or open work is unsettled ({isFinished:0.00}); the reader's {readerKind} stands and a person is asked";
                    check.QuestionKey = await AskLedgerQuestionAsync(context, mapped, isFinished, cancellationToken).ConfigureAwait(false);
                    check.Flags.Add("unsettled");
                }
                else if (finished == readerKind || (guideKind is not null && finished == guideKind))
                {
                    check.Decision = $"confirmed as {finished} ({isLedger:0.00}; finished {isFinished:0.00})";
                }
                else
                {
                    check.Decision = $"confirmed as a ledger ({isLedger:0.00}), but the judge reads it as {finished} ({isFinished:0.00}) where the reader said {readerKind}; the reader's kind stands and a person is asked";
                    check.QuestionKey = await AskLedgerQuestionAsync(context, mapped, isFinished, cancellationToken).ConfigureAwait(false);
                    check.Flags.Add("disputed");
                }
            }
            else if (isLedger <= thresholds.No)
            {
                check.Decision = $"the judge does not read this as a ledger of work items ({isLedger:0.00}); the kind becomes other and a person is asked";
                mapped.Kind = "other";
                mapped.Flags.Add("downgraded-by-judge");
                var question = new Question($"kind:{mapped.Path}", "orientation",
                    $"The reader called {mapped.Path} a {readerKind}, but the judge does not read its head as a ledger of work items (probability {isLedger:0.00}). What is it?",
                    KindOptions(context), "other")
                {
                    Context = (mapped.EvidenceQuote ?? "") + "\n" + mapped.Reasoning,
                };
                var answer = await context.AskAsync(question, mapped.Path, "no answer; kind other", cancellationToken).ConfigureAwait(false);
                if (answer?.Option is { } picked)
                {
                    mapped.Kind = picked;
                    mapped.By = DecidedBy.Person;
                }

                check.QuestionKey = question.Key;
                check.Flags.Add("downgraded");
            }
            else
            {
                check.Decision = $"the judge is unsure this is a ledger ({isLedger:0.00}); the reader's {readerKind} stands and a person is asked";
                check.QuestionKey = (await AskUnsureLedgerQuestionAsync(context, mapped, isLedger, cancellationToken).ConfigureAwait(false)).Key;
                check.Flags.Add("unsettled");
            }

            check.Kind = mapped.Kind;
            check.By = mapped.By;
            ApplyLedgerCheck(map, mapped, check);
            WithdrawSettledQuestions(context, mapped.Path, check);
            context.State.CompleteUnit(PhaseName, context.RunId, "ledger-check/" + mapped.Path, unitHash, result.Cost.Usd, check);
            context.Log.Record(new DecisionRecord
            {
                Phase = "orientation",
                Kind = "kind-check",
                Subject = mapped.Path,
                Decision = check.Decision,
                By = DecidedBy.Judge,
                Model = result.Reply.Model,
                Evidence = [new Evidence(mapped.Path, 1, $"the first {endLine} lines")],
                Confidence = isLedger,
                Figures = figures,
                Flags = check.Flags,
                Question = check.QuestionKey,
                Call = result.CallSeq,
            });
        }
    }

    /// <summary>
    /// What the judge sees of a ledger candidate: the head, then (after a marker) a sample starting at the first
    /// entry beyond the head, so that a long preamble does not hide the rows. An entry is a table row or a list
    /// line; a row carrying one of the repository's confirmed ids is preferred, then a row with a bold first
    /// cell, then any row. A slice from the middle of the file follows when the budget allows.
    /// </summary>
    internal static (string Text, int EndLine) LedgerSample(DecodedDocument decoded, int maxChars, IReadOnlyList<Regex>? idPatterns = null)
    {
        var headChars = Math.Min(2_500, maxChars / 4);
        var (head, headEnd) = Chunker.Head(decoded, headChars);
        var lines = decoded.Lines;
        if (headEnd >= lines.Count)
        {
            return (head, headEnd);
        }

        static bool IsRow(string line) =>
            line.StartsWith('|') && !line.StartsWith("|-", StringComparison.Ordinal) && !line.StartsWith("| -", StringComparison.Ordinal);
        static bool IsListLine(string line) =>
            line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
            || (line.Length > 2 && char.IsDigit(line[0]) && line.Contains(". ", StringComparison.Ordinal));

        var firstEntry = -1;
        if (idPatterns is { Count: > 0 })
        {
            for (var i = headEnd; i < lines.Count && firstEntry < 0; i++)
            {
                var line = lines[i].TrimStart();
                if ((IsRow(line) || IsListLine(line)) && idPatterns.Any(p => SafeMatch(p, line)))
                {
                    firstEntry = i;
                }
            }
        }

        if (firstEntry < 0)
        {
            for (var i = headEnd; i < lines.Count && firstEntry < 0; i++)
            {
                if (lines[i].TrimStart().StartsWith("| **", StringComparison.Ordinal))
                {
                    firstEntry = i;
                }
            }
        }

        if (firstEntry < 0)
        {
            for (var i = headEnd; i < lines.Count && firstEntry < 0; i++)
            {
                var line = lines[i].TrimStart();
                if (IsRow(line) || IsListLine(line))
                {
                    firstEntry = i;
                }
            }
        }

        if (firstEntry < 0)
        {
            return Chunker.Head(decoded, maxChars);
        }

        var budget = maxChars - head.Length - 160;
        var sample = new StringBuilder();
        var start = Math.Max(headEnd, firstEntry - 2);
        var end = start;
        while (end < lines.Count && sample.Length + lines[end].Length + 1 <= budget * 2 / 3)
        {
            sample.AppendLine(lines[end]);
            end++;
        }

        var text = new StringBuilder(head).Append("\n[... the engine skipped to the first entries ...]\n").Append(sample);
        var middle = lines.Count / 2;
        if (middle > end + 20)
        {
            var slice = new StringBuilder();
            var at = middle;
            while (at < lines.Count && text.Length + slice.Length + lines[at].Length + 1 <= maxChars - 60)
            {
                slice.AppendLine(lines[at]);
                at++;
            }

            if (slice.Length > 0)
            {
                text.Append("\n[... the engine skipped to the middle of the file ...]\n").Append(slice);
                end = at;
            }
        }

        return (text.ToString(), end);
    }

    private static bool SafeMatch(Regex pattern, string line)
    {
        try
        {
            return pattern.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>A check that asked nothing settles the questions an earlier check of the same file left open (ADR 0007).</summary>
    private static void WithdrawSettledQuestions(SurveyContext context, string path, LedgerCheckResult check)
    {
        if (check.QuestionKey is not null)
        {
            return;
        }

        foreach (var key in new[] { "kind:" + path, "ledger-state:" + path })
        {
            if (context.Book.Withdraw(key))
            {
                context.Log.Record(new DecisionRecord { Phase = PhaseName, Kind = "question", Subject = path, Decision = $"the open question {key} was withdrawn: a later check settled it ({check.Decision})", By = DecidedBy.Rule, Question = key });
            }
        }
    }

    private static void ApplyLedgerCheck(RepositoryMap map, MappedFile mapped, LedgerCheckResult check)
    {
        mapped.Kind = check.Kind;
        mapped.By = check.By ?? mapped.By;
        foreach (var flag in check.Flags.Where(f => !mapped.Flags.Contains(f)))
        {
            mapped.Flags.Add(flag);
        }

        var entry = map.Ledgers.FirstOrDefault(l => l.Path == mapped.Path);
        if (entry is null)
        {
            entry = new LedgerEntry { Path = mapped.Path, Kind = mapped.Kind, Reasoning = mapped.Reasoning ?? "", NamedBy = "the reader, from the file's head" };
            map.Ledgers.Add(entry);
        }

        entry.Confirmed = check.Confirmed;
        entry.JudgeFigures = check.Figures;
        entry.Kind = mapped.Kind;
    }

    private static async Task<string> AskLedgerQuestionAsync(SurveyContext context, MappedFile mapped, double isFinished, CancellationToken cancellationToken)
    {
        var question = new Question($"ledger-state:{mapped.Path}", "orientation",
            $"Does {mapped.Path} hold finished work or open work? The reader said {mapped.Kind}; the judge put the probability that its entries are finished at {isFinished:0.00}.",
            [new QuestionOption("open-ledger", "open work items"), new QuestionOption("finished-ledger", "finished or closed work items"), new QuestionOption("change-log", "dated entries of what landed")],
            mapped.Kind is "open-ledger" or "finished-ledger" or "change-log" ? mapped.Kind : "open-ledger")
        {
            DefaultReason = $"the reader's reading of the file ({mapped.Kind})",
        };
        var answer = await context.AskAsync(question, mapped.Path, $"no answer; the reader's {mapped.Kind} stands", cancellationToken).ConfigureAwait(false);
        if (answer?.Option is { } picked)
        {
            mapped.Kind = picked;
            mapped.By = DecidedBy.Person;
        }

        return question.Key;
    }

    private static async Task<Question> AskUnsureLedgerQuestionAsync(SurveyContext context, MappedFile mapped, double isLedger, CancellationToken cancellationToken)
    {
        var question = new Question($"kind:{mapped.Path}", "orientation",
            $"Is {mapped.Path} a ledger of work items? The reader called it {mapped.Kind}; the judge put the probability at {isLedger:0.00}.",
            KindOptions(context), mapped.Kind)
        {
            Context = mapped.Reasoning,
        };
        var answer = await context.AskAsync(question, mapped.Path, $"no answer; the reader's {mapped.Kind} stands", cancellationToken).ConfigureAwait(false);
        if (answer?.Option is { } picked)
        {
            mapped.Kind = picked;
            mapped.By = DecidedBy.Person;
        }

        return question;
    }

    private static void ConfirmIdSchemes(SurveyContext context, RepositoryMap map)
    {
        if (map.IdSchemes.Count == 0)
        {
            context.Log.Record(new DecisionRecord { Phase = "orientation", Kind = "id-scheme", Subject = "none", Decision = "the reader found no id scheme; numbers will be assigned in source order (ADR 0015)", By = DecidedBy.Rule });
            return;
        }

        var textFiles = context.Snapshot.Files.Where(f => f.IsText && f.NotRead is null && f.Category is not (FileCategory.Image or FileCategory.Binary or FileCategory.Archive)).ToList();
        var texts = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        Parallel.ForEach(textFiles, file =>
        {
            try
            {
                if (file.Size <= 4_000_000)
                {
                    texts[file.Path] = TextBytes.Decode(File.ReadAllBytes(file.AbsolutePath));
                }
            }
            catch (IOException)
            {
            }
        });

        var documentPaths = map.Files.Where(f => f.Category is "Document" or "TextUnknown").Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var scheme in map.IdSchemes)
        {
            Regex? regex = null;
            string? note = null;
            if (!string.IsNullOrWhiteSpace(scheme.Regex))
            {
                try
                {
                    regex = new Regex(scheme.Regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                }
                catch (ArgumentException)
                {
                    note = "the reader's regular expression did not compile; one was built from the example";
                }
            }

            if (regex is null)
            {
                var pattern = @"(?<![\w-])" + Regex.Replace(Regex.Escape(scheme.Example), @"\d+", @"(\d+)") + @"(?![\w-])";
                scheme.Regex = pattern;
                scheme.NumericGroup = 1;
                regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                note ??= "no regular expression was given; one was built from the example";
            }

            var (matches, ids, inDocuments, timedOut) = Scan(regex, texts, documentPaths);
            if (timedOut is not null)
            {
                note = (note is null ? "" : note + "; ") + timedOut;
            }

            if (matches == 0 && texts.Values.Any(t => t.Contains(scheme.Example, StringComparison.Ordinal)))
            {
                // The reader's regular expression matches nothing although its own example occurs in the corpus
                // (a \b before a symbol such as the section sign can never match): one built from the example is used.
                var pattern = @"(?<![\w-])" + Regex.Replace(Regex.Escape(scheme.Example), @"\d+", @"(\d+)") + @"(?![\w-])";
                regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                scheme.Regex = pattern;
                scheme.NumericGroup = 1;
                note = (note is null ? "" : note + "; ") + "the reader's regular expression matched nothing although the example occurs in the corpus; one built from the example was used";
                (matches, ids, inDocuments, timedOut) = Scan(regex, texts, documentPaths);
            }

            scheme.Matches = matches;
            scheme.DistinctIds = ids.Count;
            scheme.MatchesInDocuments = inDocuments;
            scheme.Confirmed = matches >= 3 && ids.Count >= 2;
            scheme.Note = note;
            context.Log.Record(new DecisionRecord
            {
                Phase = "orientation",
                Kind = "id-scheme",
                Subject = scheme.Name,
                Decision = scheme.Confirmed
                    ? $"confirmed: {matches} matches, {ids.Count} distinct ids ({inDocuments} in documents); refers to {scheme.RefersTo}"
                    : $"dropped: only {matches} matches and {ids.Count} distinct ids in the corpus",
                By = DecidedBy.Rule,
                Evidence = [new Evidence(scheme.WhereDefined, null, scheme.Example)],
                Reasoning = (scheme.Reasoning + (note is null ? "" : " [" + note + "]")).Trim(),
                Figures = new Dictionary<string, double> { ["matches"] = matches, ["distinct"] = ids.Count, ["in_documents"] = inDocuments },
            });
        }
    }

    private static (int Matches, HashSet<string> Ids, int InDocuments, string? TimedOut) Scan(
        Regex regex, IReadOnlyDictionary<string, string> texts, IReadOnlySet<string> documentPaths)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = 0;
        var inDocuments = 0;
        string? timedOut = null;
        foreach (var (path, text) in texts)
        {
            try
            {
                foreach (Match m in regex.Matches(text))
                {
                    matches++;
                    ids.Add(m.Value);
                    if (documentPaths.Contains(path))
                    {
                        inDocuments++;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut = $"matching timed out in {path}";
            }
        }

        return (matches, ids, inDocuments, timedOut);
    }

    private static void DecideThin(SurveyContext context, RepositoryMap map)
    {
        var thresholds = context.Options.Thin;
        var documents = map.Files.Where(f => f.Category is "Document" or "TextUnknown" && f.Kind != "unreadable" && !IsNotADocument(context, f.Kind)).ToList();
        var bytes = documents.Sum(d => d.Size);
        var natureKind = map.Nature.Kind;
        bool thin;
        string reasoning;
        if (natureKind == "design")
        {
            thin = false;
            reasoning = $"a design repository is judged by its design files, not its prose (ADR 0016); {documents.Count} documents, {bytes:N0} bytes";
        }
        else
        {
            thin = documents.Count < thresholds.MinimumDocuments || bytes < thresholds.MinimumTextBytes;
            reasoning = $"{documents.Count} documents, {bytes:N0} bytes of text, against the thresholds of {thresholds.MinimumDocuments} documents and {thresholds.MinimumTextBytes:N0} bytes";
            if (natureKind == "documentation" && thin)
            {
                reasoning += "; a documentation repository has no code to survey, so only the question is raised";
            }
        }

        map.DocumentsThin = thin;
        map.ThinReasoning = reasoning;
        context.Log.Record(new DecisionRecord
        {
            Phase = "orientation",
            Kind = "thin-documents",
            Subject = "the documents",
            Decision = thin ? "thin: the run will ask for more documents and survey the code (ADR 0010)" : "not thin",
            By = DecidedBy.Rule,
            Reasoning = reasoning + (map.ReaderThinView is null ? "" : "; the reader's own view: " + map.ReaderThinView),
            Figures = new Dictionary<string, double> { ["documents"] = documents.Count, ["bytes"] = bytes },
        });
    }

    private static string HistoryShape(RepositorySnapshot snapshot)
    {
        if (snapshot.Git is null)
        {
            return "Git history: none (the input is not a git repository).";
        }

        var log = snapshot.Git.Log(max: 5000, bodyChars: 0);
        if (log.Count == 0)
        {
            return "Git history: no commits.";
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Git history: {log.Count} commits (the most recent {Math.Min(log.Count, 5000)} were read), from {log[^1].Date:yyyy-MM-dd} to {log[0].Date:yyyy-MM-dd}, {log.Select(c => c.Author).Distinct(StringComparer.Ordinal).Count()} author(s). HEAD is {snapshot.Git.Commit[..Math.Min(12, snapshot.Git.Commit.Length)]} on branch {snapshot.Git.Branch}; the tree is {(snapshot.Git.Clean ? "clean" : "not clean")}.");
        text.AppendLine("The newest 25 commit subjects:");
        foreach (var commit in log.Take(25))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {commit.Date:yyyy-MM-dd} {Cut(commit.Subject, 140)}");
        }

        text.AppendLine("The oldest 10 commit subjects:");
        foreach (var commit in log.TakeLast(10).Reverse())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {commit.Date:yyyy-MM-dd} {Cut(commit.Subject, 140)}");
        }

        return text.ToString();
    }

    private static string MapSummary(RepositoryMap map)
    {
        var text = new StringBuilder();
        text.AppendLine("What orientation found so far (from the guides and the tree; use it, and say so when a file contradicts it):");
        text.AppendLine(CultureInfo.InvariantCulture, $"- The repository is: {map.Nature.Kind}. {map.Nature.Description}");
        if (map.Ledgers.Count > 0)
        {
            text.AppendLine("- Ledgers named by the guides: " + string.Join("; ", map.Ledgers.Select(l => $"{l.Path} ({l.Kind})")));
        }

        if (map.IdSchemes.Count > 0)
        {
            text.AppendLine("- Id schemes: " + string.Join("; ", map.IdSchemes.Select(s => $"{s.Name} like {s.Example} ({s.RefersTo})")));
        }

        if (map.StatusVocabulary.Count > 0)
        {
            text.AppendLine("- Status words: " + string.Join("; ", map.StatusVocabulary.Select(s => $"{s.Word} = {s.Meaning}")));
        }

        if (map.SpecIndex is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Specs are indexed at {map.SpecIndex.Path}{(map.SpecIndex.Anchor is null ? "" : "#" + map.SpecIndex.Anchor)}");
        }

        if (map.DocumentRoots.Count > 0)
        {
            text.AppendLine("- Document roots: " + string.Join(", ", map.DocumentRoots));
        }

        return text.ToString();
    }

    internal static string KindVocabulary(SurveyContext context, string group = "document")
    {
        var kinds = context.Playbook.Data("kinds");
        var text = new StringBuilder();
        foreach (var (kind, node) in kinds[group] as JsonObject ?? [])
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {kind}: {node?.GetValue<string>()}");
        }

        return text.ToString();
    }

    private static List<QuestionOption> KindOptions(SurveyContext context)
    {
        var kinds = context.Playbook.Data("kinds");
        return (kinds["document"] as JsonObject ?? []).Concat(kinds["not-a-document"] as JsonObject ?? [])
            .Select(k => new QuestionOption(k.Key, k.Value?.GetValue<string>() ?? "")).ToList();
    }

    internal static bool KindIsKnown(SurveyContext context, string kind)
    {
        var kinds = context.Playbook.Data("kinds");
        return (kinds["document"] as JsonObject ?? []).ContainsKey(kind) || (kinds["not-a-document"] as JsonObject ?? []).ContainsKey(kind);
    }

    /// <summary>The kinds that say a text file is not a document (code, config, data, template, generated, empty).</summary>
    internal static bool IsNotADocument(SurveyContext context, string kind) =>
        !string.Equals(kind, "template", StringComparison.OrdinalIgnoreCase)
        && (context.Playbook.Data("kinds")["not-a-document"] as JsonObject ?? []).ContainsKey(kind);

    internal static string NormaliseKind(string? kind)
    {
        var k = (kind ?? "other").Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        return k switch
        {
            "openledger" or "open" or "backlog" => "open-ledger",
            "finishedledger" or "finished" or "done-ledger" or "closed-ledger" => "finished-ledger",
            "changelog" or "change-log" or "changes" => "change-log",
            "specification" => "spec",
            "decision" or "decision-record" => "adr",
            "release-note" or "releasenotes" => "release-notes",
            "" => "other",
            _ => k,
        };
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static EvidenceEntry ToEntry(EvidenceReply e) => new() { Path = e.Path, Line = e.Line, Quote = e.Quote };

    internal static T? Parse<T>(JsonNode json)
    {
        try
        {
            return json.Deserialize<T>(RunContext.ReaderJsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

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

    /// <summary>What the nature unit stores: the reply and the figures the log needs.</summary>
    internal sealed class NatureResult
    {
        public NatureReply Reply { get; set; } = new();
        public string AnsweredBy { get; set; } = "";
        public int CallSeq { get; set; }
        public decimal Usd { get; set; }
        public List<string> FlaggedParts { get; set; } = [];
        public List<string> LookedCloser { get; set; } = [];
    }

    /// <summary>What a ledger-check unit stores.</summary>
    internal sealed class LedgerCheckResult
    {
        public string Kind { get; set; } = "";
        public string? By { get; set; }
        public bool Confirmed { get; set; }
        public Dictionary<string, double> Figures { get; set; } = [];
        public List<string> Flags { get; set; } = [];
        public string? QuestionKey { get; set; }
        public string Decision { get; set; } = "";
        public string Model { get; set; } = "";
    }

    // The reader's reply shapes (schemas/repository-map.json and schemas/file-kinds.json), snake_case in the JSON.
    internal sealed class EvidenceReply
    {
        public string? Path { get; set; }
        public int? Line { get; set; }
        public string? Quote { get; set; }
    }

    internal sealed class NatureInfo
    {
        public string? Kind { get; set; }
        public string? Description { get; set; }
        public double? Confidence { get; set; }
        public string? Reasoning { get; set; }
        public List<EvidenceReply>? Evidence { get; set; }
    }

    internal sealed class IdSchemeReply
    {
        public string? Name { get; set; }
        public string? Example { get; set; }
        public string? Regex { get; set; }
        public int? NumericGroup { get; set; }
        public string? RefersTo { get; set; }
        public string? WhereDefined { get; set; }
        public string? Reasoning { get; set; }
        public EvidenceReply? Evidence { get; set; }
    }

    internal sealed class VocabReply
    {
        public string? Word { get; set; }
        public string? Meaning { get; set; }
        public string? Mapped { get; set; }
        public EvidenceReply? Evidence { get; set; }
    }

    internal sealed class SpecIndexReply
    {
        public string? Path { get; set; }
        public string? Anchor { get; set; }
        public string? Reasoning { get; set; }
    }

    internal sealed class LedgerReply
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
        public string? Reasoning { get; set; }
        public EvidenceReply? Evidence { get; set; }
    }

    internal sealed class ThinReply
    {
        public bool Value { get; set; }
        public string? Reasoning { get; set; }
    }

    internal sealed class LookCloserRequest
    {
        public string? Kind { get; set; }
        public string? Path { get; set; }
        public string? Why { get; set; }
    }

    internal sealed class NatureReply
    {
        public NatureInfo? Nature { get; set; }
        public List<string>? NaturalLanguages { get; set; }
        public List<string>? ProgrammingLanguages { get; set; }
        public List<IdSchemeReply>? IdSchemes { get; set; }
        public List<VocabReply>? StatusVocabulary { get; set; }
        public List<VocabReply>? TierVocabulary { get; set; }
        public SpecIndexReply? SpecIndex { get; set; }
        public List<LedgerReply>? Ledgers { get; set; }
        public List<string>? DocumentRoots { get; set; }
        public ThinReply? DocumentsThin { get; set; }
        public List<LookCloserRequest>? Requests { get; set; }
        public List<string>? Notes { get; set; }
        public string? Reasoning { get; set; }
    }

    internal sealed class FileKindReply
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
        public string? Title { get; set; }
        public string? Language { get; set; }
        public double? Confidence { get; set; }
        public EvidenceReply? Evidence { get; set; }
        public string? Reasoning { get; set; }
        public List<string>? Alternatives { get; set; }
    }

    internal sealed class FileKindsReply
    {
        public List<FileKindReply>? Files { get; set; }
    }
}

/// <summary>The tree as the reader sees it: folders with counts by extension, every document by name, the top-level files.</summary>
public static class TreeSummary
{
    public static string Build(RepositorySnapshot snapshot, FileTable table, IReadOnlyList<SourceFile> documentLike, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(table);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"The repository tree: {snapshot.Files.Count} files, listed by {snapshot.ListedBy}.");
        text.AppendLine("Counts by category: " + string.Join(", ", snapshot.Files.GroupBy(f => f.Category).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        var languages = snapshot.Files.Where(f => f.Language is not null).GroupBy(f => f.Language!).OrderByDescending(g => g.Count()).Take(12).ToList();
        if (languages.Count > 0)
        {
            text.AppendLine("Code by language (from extensions): " + string.Join(", ", languages.Select(g => $"{g.Key} {g.Count()}")));
        }

        var manifests = snapshot.Files.SelectMany(f => table.ManifestsOf(f.Path).Select(m => (m.Name, f.Path))).Take(40).ToList();
        if (manifests.Count > 0)
        {
            text.AppendLine("Manifest files met: " + string.Join("; ", manifests.Select(m => $"{m.Path} ({m.Name})")));
        }

        var sites = table.SitesAmong(snapshot.Files.Select(f => f.Path)).ToList();
        if (sites.Count > 0)
        {
            text.AppendLine("Documentation site shapes met: " + string.Join(", ", sites.Select(s => s.Name)));
        }

        text.AppendLine();
        text.AppendLine("Top-level files:");
        foreach (var file in snapshot.Files.Where(f => !f.Path.Contains('/', StringComparison.Ordinal)))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {file.Path} ({file.Size} bytes)");
        }

        text.AppendLine();
        text.AppendLine("Folders (to depth 3) with their file counts by extension:");
        var folders = snapshot.Files
            .Where(f => f.Path.Contains('/', StringComparison.Ordinal))
            .GroupBy(f => FolderOf(f.Path, 3))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            var byExtension = folder.GroupBy(f => f.Extension.Length == 0 ? "(none)" : f.Extension).OrderByDescending(g => g.Count()).Take(8);
            text.AppendLine(CultureInfo.InvariantCulture, $"- {folder.Key}/ ({folder.Count()} files: {string.Join(", ", byExtension.Select(g => $"{g.Count()} {g.Key}"))})");
            if (text.Length > maxChars / 2)
            {
                text.AppendLine("- [the folder list was cut by the engine]");
                break;
            }
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Every document-like file ({documentLike.Count}), path and size:");
        foreach (var file in documentLike.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {file.Path} ({file.Size})");
            if (text.Length > maxChars)
            {
                text.AppendLine("- [the document list was cut by the engine; ask for a listing of a folder to see more]");
                break;
            }
        }

        return text.ToString();
    }

    public static string Listing(RepositorySnapshot snapshot, string folder, int max)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var entries = snapshot.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        if (entries.Count == 0)
        {
            return "(no files under that folder)";
        }

        var text = new StringBuilder();
        foreach (var file in entries.Take(max))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {file.Path} ({file.Size} bytes, {file.Category})");
        }

        if (entries.Count > max)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- [{entries.Count - max} more files not shown]");
        }

        return text.ToString();
    }

    private static string FolderOf(string path, int depth)
    {
        var parts = path.Split('/');
        return string.Join('/', parts.Take(Math.Min(depth, parts.Length - 1)));
    }
}
