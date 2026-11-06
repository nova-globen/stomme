using System.Text.Json.Nodes;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Tests;

/// <summary>One row as a scripted reader returns it.</summary>
internal sealed record ScriptedRow(string Title, string? Key = null, string? Status = null, string? AsWritten = null, string? Refs = null, string[]? Ids = null, string? Type = null);

/// <summary>
/// A repository of one or more ledgers and a reader scripted row by row, with no provider: what the items phase makes
/// of a ledger then turns on the code alone. Stops after the items phase unless told another.
/// </summary>
internal sealed class LedgerRig : IDisposable
{
    private readonly TempFolder _temp = new();

    private readonly string _stop;

    public LedgerRig(string stop = "items")
    {
        _stop = stop;
        Directory.CreateDirectory(Path.Combine(Input, "docs"));
        File.WriteAllText(Path.Combine(Input, "README.md"), "# Demo\n\nWork items live in docs/backlog.md. Ids look like BL-12.\n");
        Options = NewOptions(rerun: false);
        Services = new RunServices { QuestionPolicy = (s, b) => new WrittenQuestionPolicy(s, b), Backup = BackupKind.None, ChatClients = _ => Reader, JudgeModel = Judge };
        Reader.RowScript = user =>
        {
            var rows = new JsonArray();
            var start = user.IndexOf("=== INPUT TEXT", StringComparison.Ordinal);
            var head = user[start..user.IndexOf("===\n", start, StringComparison.Ordinal)];
            var dynamic = Dynamic?.Invoke(user);
            foreach (var (path, list) in dynamic is not null ? [new KeyValuePair<string, ScriptedRow[]>("", dynamic)] : Script.ToList())
            {
                if (dynamic is null && !head.Contains(path, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var r in list)
                {
                    rows.Add(new JsonObject
                    {
                        ["key"] = r.Key,
                        ["ids"] = new JsonArray((r.Ids ?? (r.Key is null ? [] : [r.Key])).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                        ["title"] = r.Title,
                        ["type"] = r.Type,
                        ["type_as_written"] = r.Type,
                        ["type_confidence"] = r.Type is null ? 0.0 : 0.8,
                        ["relations"] = new JsonArray(),
                        ["status_as_written"] = r.AsWritten,
                        ["status"] = r.Status,
                        ["condition"] = null,
                        ["tier"] = null,
                        ["size"] = null,
                        ["dates"] = new JsonArray(),
                        ["detail"] = "reader's account of " + r.Title,
                        ["references"] = r.Refs is null ? new JsonArray() : new JsonArray(new JsonObject { ["kind"] = "id", ["text"] = r.Refs }),
                        ["evidence"] = new JsonObject { ["line"] = 1, ["quote"] = r.Title },
                        ["confidence"] = 0.9,
                        ["reasoning"] = "scripted",
                    });
                }
            }

            return rows;
        };
    }

    public string Input => Path.Combine(_temp.Path, "repo");
    public string Output => Path.Combine(_temp.Path, "out");
    public ScriptedChatClient Reader { get; } = new();
    public ScriptedJudge Judge { get; } = new() { ReadsEveryLedgerAsOne = true };
    public SurveyOptions Options { get; set; }
    public RunServices Services { get; }
    public Dictionary<string, ScriptedRow[]> Script { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, the rows the reader returns for a request, from the request's own text (a ledger of several chunks); null leaves it to <see cref="Script"/>.</summary>
    public Func<string, ScriptedRow[]?>? Dynamic { get; set; }

    public SurveyOptions NewOptions(bool rerun) => new()
    {
        InputPath = Input,
        Conductor = new ConductorOptions
        {
            OutputPath = Output,
            PlaybookPath = TestPaths.Playbook,
            StopAfterPhase = _stop,
            Reader = new ReaderOptions { Retries = 0 },
            RerunPhases = rerun ? new HashSet<string>(["*"], StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal),
        },
    };

    public async Task<List<JsonNode>> Run()
    {
        var result = await new RepositorySurvey(Options, Services).RunAsync(CancellationToken.None);
        Assert.True(result.Outcome is RunOutcome.StoppedAfterPhase or RunOutcome.Completed, result.Outcome.ToString());
        return JsonNode.Parse(File.ReadAllText(Path.Combine(Output, "items.json")))!["items"]!.AsArray().Select(i => i!).ToList();
    }

    public void Ledgers(params (string Path, string Kind)[] ledgers) =>
        Reader.LedgersJson = "[" + string.Join(",", ledgers.Select(l => $"{{\"path\": \"{l.Path}\", \"kind\": \"{l.Kind}\", \"reasoning\": \"x\", \"evidence\": null}}")) + "]";

    public void Write(string rel, string text)
    {
        var p = Path.Combine(Input, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    public void Edit(string rel, Func<string, string> change)
    {
        var p = Path.Combine(Input, rel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(p, change(File.ReadAllText(p)));
    }

    /// <summary>Replaces one text of a ledger by another.</summary>
    public void Edit(string rel, string from, string to) => Edit(rel, text => text.Replace(from, to, StringComparison.Ordinal));

    /// <summary>The final accounting of a ledger, as the items phase left it in the state.</summary>
    public ItemsPhase.LedgerAccounting Accounting(string path)
    {
        using var state = RunState.Open(Path.Combine(Output, "state.sqlite"));
        var json = state.Records(ItemsPhase.AccountingKind).Single(r => r.Key == path).Json;
        return System.Text.Json.JsonSerializer.Deserialize<ItemsPhase.LedgerAccounting>(json, RunContext.JsonOptions)!;
    }

    /// <summary>The findings of the verify phase (a run that stopped after it).</summary>
    public IReadOnlyList<string> Findings()
    {
        using var state = RunState.Open(Path.Combine(Output, "state.sqlite"));
        return VerifyPhase.Load(state)!.Findings;
    }

    /// <summary>The questions as written for a person.</summary>
    public string Questions() => File.ReadAllText(Path.Combine(Output, "questions.md"));

    /// <summary>A person's answers, as a consumer writes them: the option chosen for each question key.</summary>
    public void Answer(params (string Key, string Option)[] answers) =>
        File.WriteAllText(Path.Combine(Output, "answers.json"), "{ " + string.Join(", ", answers.Select(a => $"\"{a.Key}\": {{ \"text\": \"\", \"option\": \"{a.Option}\", \"by\": \"person\" }}")) + " }");

    public void Dispose() => _temp.Dispose();
}
