using System.Text.Json;
using Stomme.Cartographer.Atlas;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Survey;

/// <summary>Everything a survey phase needs: the engine's context, plus the repository as scanned, the decoders, the atlas paths and the map.</summary>
public sealed class SurveyContext
{
    private readonly Dictionary<string, DecodedDocument?> _decoded = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private RepositorySnapshot? _shown;

    public SurveyContext(RunContext run, SurveyOptions options, FileTable table, RepositorySnapshot snapshot, DecoderRegistry decoders)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Table = table ?? throw new ArgumentNullException(nameof(table));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
        Paths = new AtlasPaths(run.Paths.Root);
        Map = LoadMap();
    }

    /// <summary>The engine's context (ADR 0018).</summary>
    public RunContext Run { get; }

    public SurveyOptions Options { get; }

    public FileTable Table { get; }

    public RepositorySnapshot Snapshot { get; }

    /// <summary>The input as a provider may see its tree (ADR 0028): without the files the person excluded, whose names stay on this machine too.</summary>
    public RepositorySnapshot Shown => _shown ??= Snapshot.Files.Any(f => f.ExcludedBy is not null)
        ? Snapshot with { Files = Snapshot.Files.Where(f => f.ExcludedBy is null).ToList() }
        : Snapshot;

    public DecoderRegistry Decoders { get; }

    public AtlasPaths Paths { get; }

    /// <summary>The orientation phase's result; null before it ran.</summary>
    public RepositoryMap? Map { get; set; }

    // The engine's pieces, by the names the phases use.
    public DecisionLog Log => Run.Log;
    public GatedReader Reader => Run.Reader;
    public Judge Judge => Run.Judge;
    public RunState State => Run.State;
    public Glossary Glossary => Run.Glossary;
    public Playbook Playbook => Run.Playbook;
    public QuestionBook Book => Run.Book;
    public AnswerStore Answers => Run.Answers;
    public SpendBook Spend => Run.Spend;
    public string RunId => Run.RunId;
    public Action<string> Progress => Run.Progress;
    public List<string> PhasesDone => Run.PhasesDone;

    public SourceFile? File(string path) =>
        Snapshot.Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Decodes a file once and keeps it; null when no decoder reads it.</summary>
    public DecodedDocument? Decode(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        lock (_gate)
        {
            if (_decoded.TryGetValue(file.Path, out var cached))
            {
                return cached;
            }
        }

        var decoded = Decoders.Decode(file);
        lock (_gate)
        {
            _decoded[file.Path] = decoded;
        }

        return decoded;
    }

    public Task<Answer?> AskAsync(Question question, string subject, string decisionWithoutAnswer, CancellationToken cancellationToken) =>
        Run.AskAsync(question, subject, decisionWithoutAnswer, cancellationToken);

    public void Touch(string doingNow, bool force = false) => Run.Touch(doingNow, force);

    public string SystemPrompt(string taskPrompt, string schema, IReadOnlyDictionary<string, string>? values = null) =>
        Run.SystemPrompt(taskPrompt, schema, values);

    /// <summary>The map into the state (ADR 0017, 0019): the record the later phases retrieve their rows from.</summary>
    public void SaveMap(RepositoryMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        State.SaveRecord("map", "repository-map", JsonSerializer.Serialize(map, RunContext.JsonOptions), RunId);
        Map = map;
    }

    public RepositoryMap? LoadMap()
    {
        var json = State.LoadRecord("map", "repository-map");
        return json is null ? null : JsonSerializer.Deserialize<RepositoryMap>(json, RunContext.JsonOptions);
    }
}
