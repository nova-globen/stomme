using System.Globalization;
using System.Text;
using Stomme.Cartographer.Atlas;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Survey;

/// <summary>
/// The repository survey (ADR 0018): the first use of the engine. It lists the repository's files as the run's
/// inputs, builds the survey phases over the map, zips the documents it reads (ADR 0009) and writes the atlas
/// (ADR 0005): the map, the summary and the manifest now; the items, documents, decisions and graph as the
/// later phases land. The engine never writes into the input.
/// </summary>
public sealed class RepositorySurvey : IPlaybookHost
{
    private readonly SurveyOptions _options;
    private readonly RunServices _services;
    private SurveyContext? _survey;

    public RepositorySurvey(SurveyOptions options, RunServices services)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>Runs the survey playbook over the input into the output folder; refuses an output inside the input (ADR 0009).</summary>
    public Task<RunResult> RunAsync(CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(_options.InputPath);
        var output = Path.GetFullPath(_options.Conductor.OutputPath);
        if (!Directory.Exists(input))
        {
            throw new DirectoryNotFoundException($"The input folder does not exist: {input}");
        }

        if (IsInside(output, input))
        {
            throw new InvalidOperationException("The output folder lies inside the input; the engine refuses to write there (ADR 0009).");
        }

        return new PlaybookRun(_options.Conductor, _services, this).RunAsync(cancellationToken);
    }

    /// <summary>
    /// What a run would send and to whom (ADR 0028), worked out on this machine without calling any provider and without
    /// writing anything: the providers and models, the files whose text may be sent with their sizes, the amount of text,
    /// an estimate of the cost, and what is kept back. A consumer shows it for consent before <see cref="RunAsync"/>;
    /// <see cref="SurveyOptions.ExcludeFiles"/> and <see cref="SurveyOptions.AllowFiles"/> change it.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject Plan()
    {
        var input = Path.GetFullPath(_options.InputPath);
        if (!Directory.Exists(input))
        {
            throw new DirectoryNotFoundException($"The input folder does not exist: {input}");
        }

        var playbook = _options.Conductor.PlaybookPath is not null
            ? Stomme.Conductor.Playbooks.Playbook.Load(_options.Conductor.PlaybookPath)
            : _options.Conductor.Playbook ?? SurveyPlaybook.Embedded();
        var snapshot = RepositoryScanner.Scan(input, _options.ExtraDocumentFolders, new FileTable(playbook.Data("files")), new ScanRules(_options.ExcludeFiles, _options.AllowFiles));
        return ConsentPlan.Build(_options, snapshot);
    }

    public Task<IReadOnlyList<IPhase>> PrepareAsync(RunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = Path.GetFullPath(_options.InputPath);
        var table = new FileTable(context.Playbook.Data("files"));
        var snapshot = RepositoryScanner.Scan(input, _options.ExtraDocumentFolders, table, new ScanRules(_options.ExcludeFiles, _options.AllowFiles));
        var plan = ConsentPlan.Line(ConsentPlan.Build(_options, snapshot));
        context.Log.Record(new DecisionRecord { Phase = "run", Kind = "plan", Subject = input, Decision = plan, By = DecidedBy.Rule });
        context.Progress(plan);

        // What an earlier run learned from a file that is kept back now leaves the glossary, so no later prompt carries its name (ADR 0028).
        var keptBack = snapshot.Files.Where(f => f.ExcludedBy is not null || (f.Secret is not null && f.NotRead is not null)).Select(f => f.Path).ToList();
        if (keptBack.Count > 0)
        {
            var stale = context.State.Glossary()
                .Where(e => keptBack.Any(path => e.Meaning.Contains(path, StringComparison.OrdinalIgnoreCase) || e.Term.Contains(path, StringComparison.OrdinalIgnoreCase) || (e.Source ?? "").Contains(path, StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Id).ToList();
            if (stale.Count > 0)
            {
                context.State.RemoveGlossary(stale);
                context.Log.Record(new DecisionRecord { Phase = "run", Kind = "glossary", Subject = $"{stale.Count} entries", Decision = "removed: they name a file that is kept back now (it looks like it holds a secret, or a person excluded it)", By = DecidedBy.Rule });
            }
        }
        var tree = snapshot.Git is null ? "not git" : snapshot.Git.Clean ? "clean" : "with uncommitted changes";
        context.DescribeSource(snapshot.Root, snapshot.Git?.Commit, snapshot.Git is null ? tree : $"{snapshot.Git.Branch}, {tree}");
        context.Log.Record(new DecisionRecord
        {
            Phase = "run",
            Kind = "scan",
            Subject = input,
            Decision = $"{snapshot.Files.Count} files listed by {snapshot.ListedBy}; commit {snapshot.Git?.Commit ?? "(none)"} on {snapshot.Git?.Branch ?? "(no branch)"}, tree {tree}",
            By = DecidedBy.Rule,
            Reasoning = string.Join(" ", snapshot.Notes),
            Figures = snapshot.Files.GroupBy(f => f.Category).ToDictionary(g => g.Key.ToString(), g => (double)g.Count()),
        });
        var diff = context.ReconcileInputs(snapshot.Files.Select(f => new InputRecord(f.Path, f.Sha256, f.Size)));
        context.Progress($"{snapshot.Files.Count} files listed by {snapshot.ListedBy}; against the state: {diff.Unchanged} unchanged, {diff.Changed} changed, {diff.Added} added, {diff.Gone} gone");
        _survey = new SurveyContext(context, _options, table, snapshot, DecoderRegistry.Default);
        context.Book.Describe = DescribeQuestion;
        context.Book.Safest = SafestOption;
        return Task.FromResult<IReadOnlyList<IPhase>>([new OrientationPhase(_survey), new ItemsPhase(_survey), new DocumentsPhase(_survey), new DecisionsPhase(_survey), new LinksPhase(_survey), new VerifyPhase(_survey)]);
    }

    public Task<BackupResult?> BackUpAsync(RunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var survey = Survey();
        return Task.FromResult<BackupResult?>(ZipBackup.Run(survey.Snapshot, context.Paths.Root, context.RunId));
    }

    public void Finish(RunContext context, BackupResult? backup, RunOutcome outcome, string message)
    {
        var survey = _survey;
        if (survey is null)
        {
            // The run ended before the repository was listed; the engine's log and summary of the failure stand.
            return;
        }

        if (survey.Map is not null)
        {
            RepositoryMapWriter.Write(survey, survey.Map);
        }

        // A folder an earlier run wrote is written again even when its catalogue is empty now, so nothing stale stays in the bundle.
        if (Directory.Exists(survey.Paths.Items) || (survey.State.Count("records") > 0 && ItemCatalogue.Load(survey.State).Count > 0))
        {
            ItemWriter.WriteAll(survey);
        }

        if (Directory.Exists(survey.Paths.Documents) || (survey.State.Count("records") > 0 && DocumentCatalogue.Load(survey.State).Count > 0))
        {
            DocumentWriter.WriteAll(survey);
        }

        if (Directory.Exists(survey.Paths.Decisions) || (survey.State.Count("records") > 0 && DecisionCatalogue.Load(survey.State).Count > 0))
        {
            DecisionWriter.WriteAll(survey);
        }

        // The six catalogues a consumer reads are always written, with empty lists where a phase has nothing (change 10 of the verdict).
        StatusWordsWriter.Write(survey);
        CatalogueJsonWriter.WriteItems(survey);
        CatalogueJsonWriter.WriteDocuments(survey);
        CatalogueJsonWriter.WriteDecisions(survey);
        GraphWriter.WriteAll(survey, (survey.State.Count("records") > 0 ? GraphCatalogue.Load(survey.State) : null) ?? new Graph { RunId = context.RunId });

        SummaryWriter.Write(survey, backup, outcome, message);
        ManifestWriter.Write(survey, backup, outcome);
    }

    public void DescribeState(RunContext context, StringBuilder text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var survey = _survey;
        text.AppendLine("## The input");
        text.AppendLine();
        if (survey is null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- `{_options.InputPath}`, not listed yet.");
            text.AppendLine();
            return;
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"- `{survey.Snapshot.Root}` at {survey.Snapshot.Git?.Commit[..Math.Min(12, survey.Snapshot.Git.Commit.Length)] ?? "(not git)"}, {survey.Snapshot.Files.Count} files present.");
        text.AppendLine();
        if (survey.Map is { } map)
        {
            text.AppendLine("## The repository, as understood");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"- Nature: {map.Nature.Kind} ({map.Nature.Confidence:0.00}): {map.Nature.Description}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Id schemes confirmed: {string.Join(", ", map.IdSchemes.Where(s => s.Confirmed).Select(s => s.Name + " like " + s.Example)) switch { "" => "none", var s => s }}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Ledgers: {string.Join(", ", map.Ledgers.Select(l => l.Path + " (" + l.Kind + ")")) switch { "" => "none", var s => s }}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Files by kind, as the map reads them: {string.Join(", ", map.CountsByKind.OrderByDescending(c => c.Value).Take(12).Select(c => c.Key + " " + c.Value))}");
            text.AppendLine();
        }

        text.AppendLine("## The catalogue");
        text.AppendLine();
        var items = ItemCatalogue.Load(survey.State);
        text.AppendLine(CultureInfo.InvariantCulture, $"- Items {items.Count} ({items.Count(i => i.Placeholder)} placeholders, {items.Count(i => i.SourceGone)} from sources now gone).");
        var documents = DocumentCatalogue.Load(survey.State);
        text.AppendLine(CultureInfo.InvariantCulture, $"- Documents {documents.Count} ({documents.Count(d => d.Status is "superseded" or "deprecated" or "snapshot")} superseded, deprecated or snapshots by their own words; {documents.Count(d => d.ForAgents)} written for agents; {documents.Count(d => d.SourceGone)} from sources now gone); the edges are in `graph.json` when the links phase has run.");
        var decisions = DecisionCatalogue.Load(survey.State);
        if (decisions.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Decisions {decisions.Count} ({decisions.Count(d => d.Standing == "draft")} drafts in `decisions/`, {decisions.Count(d => d.Standing == "recorded")} already recorded in an ADR, {decisions.Count(d => d.Standing == "record")} kept as records, {decisions.Count(d => d.Standing == "not-a-decision")} not decisions, {decisions.Count(d => d.Standing == "unsure")} unsure).");
        }
        if (GraphCatalogue.Load(survey.State) is { } graph)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Graph {graph.Nodes.Count} nodes and {graph.Edges.Count} edges (`graph.json`, `index.sqlite`, `graph.graphml`, `graph.mmd`).");
        }
        if (VerifyPhase.Load(survey.State) is { } verified)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Verified (run {verified.RunId}): {verified.FilesByAccount.Values.Sum()} files each in one account, {verified.Rows} ledger rows of which {verified.RowsWithOneItem} are one item each, {verified.References} references of which {verified.ReferencesResolved.Values.Sum()} resolved; {(verified.Passed ? "nothing for a person to look at" : verified.Findings.Count + " finding(s) in `summary.md`")}.");
        }
        text.AppendLine();
    }

    private SurveyContext Survey() => _survey ?? throw new InvalidOperationException("The survey has not listed the repository yet.");

    /// <summary>What a question is about, from its key, for one asked without an about (docs/bundle-format.md lists the kinds).</summary>
    internal static QuestionAbout? DescribeQuestion(Question question)
    {
        var key = question.Key;
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon < 0 ? key : key[..colon];
        var rest = colon < 0 ? "" : key[(colon + 1)..];
        return prefix switch
        {
            "kind" => new QuestionAbout("document-kind", [rest], [], null),
            "ledger-state" or "ledger-fixture" or "ledger-missing" => new QuestionAbout("ledger", [rest], [], null),
            "work-elsewhere" => new QuestionAbout("work-elsewhere", [], [], null),
            "status" => new QuestionAbout("status-word", [], [], null),
            "prose-work" => new QuestionAbout("prose-work", [rest.Contains('#', StringComparison.Ordinal) ? rest[..rest.IndexOf('#', StringComparison.Ordinal)] : rest], [], null),
            "placeholders" => new QuestionAbout("placeholders", [], [], null),
            "open-in-finished" => new QuestionAbout("open-in-finished", [], [rest], 1),
            "ledger-no-rows" => new QuestionAbout("ledger-no-rows", [rest], [], null),
            "tier" => new QuestionAbout("tier-word", [], [], null),
            "history" => new QuestionAbout("history", [], [], null),
            "thin-documents" or "code-survey" => new QuestionAbout("code-survey", [], [], null),
            _ => new QuestionAbout("other", [], [], null),
        };
    }

    /// <summary>
    /// The option that holds when nobody answers and a question's default names none of its options or several: the one
    /// that changes least. Null leaves it to the first option.
    /// </summary>
    internal static string? SafestOption(Question question)
    {
        var key = question.Key;
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return (colon < 0 ? key : key[..colon]) switch
        {
            "kind" => "other",
            "ledger-fixture" => "set-aside",
            "ledger-missing" => "not-in-this-copy",
            "work-elsewhere" => "not-imported",
            "open-in-finished" => "open",
            "ledger-no-rows" => "leave",
            _ => null,
        };
    }

    private static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
