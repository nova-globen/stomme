using System.Globalization;
using Microsoft.Extensions.AI;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Options;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Policies;
using Stomme.Conductor.State;

namespace Stomme.Conductor.Runner;

/// <summary>What the host supplies beside the options: the policies and, for tests, the models.</summary>
public sealed class RunServices
{
    /// <summary>Builds the question policy over the run's answer store and question book (ADR 0007).</summary>
    public required Func<AnswerStore, QuestionBook, IQuestionPolicy> QuestionPolicy { get; init; }

    /// <summary>The backup policy (ADR 0009); the host's <see cref="IPlaybookHost.BackUpAsync"/> does the archiving.</summary>
    public BackupKind Backup { get; init; } = BackupKind.Zip;

    /// <summary>A judge to use instead of TypeSafe (tests); null builds the TypeSafe adapter from the options.</summary>
    public IJudgeModel? JudgeModel { get; init; }

    /// <summary>Chat clients per model to use instead of the SDK (tests); null builds them from the options.</summary>
    public Func<string, IChatClient>? ChatClients { get; init; }

    /// <summary>Where a line of progress goes (a console prints under --ask or --verbose; the library stays silent).</summary>
    public Action<string>? Progress { get; init; }

    public HttpClient? HttpClient { get; init; }
}

/// <summary>How a run ended, and where its output is.</summary>
public sealed record RunResult(
    RunOutcome Outcome,
    string RunId,
    string OutputPath,
    decimal SpentUsd,
    decimal AllRunsUsd,
    int OpenQuestions,
    string Message,
    InputDiff? Diff);

/// <summary>
/// Runs a playbook with context (ADR 0018) into one output folder: the log first, then the state (ADR 0017),
/// the models and the policies; then the host's inputs and backup, the phases as units of work, and at the end
/// the run's row in the state, the questions and the host's own output. The engine's only writes are inside the
/// output folder.
/// </summary>
public sealed class PlaybookRun
{
    private readonly ConductorOptions _options;
    private readonly RunServices _services;
    private readonly IPlaybookHost _host;

    public PlaybookRun(ConductorOptions options, RunServices services, IPlaybookHost host)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task<RunResult> RunAsync(CancellationToken cancellationToken)
    {
        // Every figure the output carries is written the same way on every machine: the invariant culture
        // flows from here through every await and parallel body of this run (it is async-local in .NET).
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        var output = Path.GetFullPath(_options.OutputPath);
        Directory.CreateDirectory(output);
        var paths = new OutputPaths(output);
        // The time, plus four random hex characters so that two runs started within one second stay apart.
        var runId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2));
        var progress = _services.Progress ?? (_ => { });
        using var log = new DecisionLog(output, runId);
        progress($"Run {runId}: the log is {paths.LogJsonl}");

        var playbook = _options.PlaybookPath is not null
            ? Playbook.Load(_options.PlaybookPath)
            : _options.Playbook ?? throw new InvalidOperationException("No playbook: give ConductorOptions.PlaybookPath (a folder) or ConductorOptions.Playbook (one the host loaded).");
        log.Record(new DecisionRecord
        {
            Phase = "run",
            Kind = "playbook",
            Subject = playbook.Name,
            Decision = $"playbook {playbook.Name} version {playbook.Version} (hash {playbook.Hash}) from {playbook.Root}",
            By = DecidedBy.Rule,
        });

        // The keys come through the options (ADR 0022); the host says where it read them from, and the values are never recorded.
        var readerKey = string.IsNullOrWhiteSpace(_options.Reader.Key) ? null : _options.Reader.Key.Trim();
        var judgeKey = string.IsNullOrWhiteSpace(_options.Judge.Key) ? null : _options.Judge.Key.Trim();
        log.Record(new DecisionRecord
        {
            Phase = "run",
            Kind = "keys",
            Subject = "the keys",
            Decision = $"reader key {(readerKey is null ? "not given" : "given")}{(_services.ChatClients is null ? "" : " (a test reader stands in)")}; judge key {(judgeKey is null ? "not given" : "given")}{(_services.JudgeModel is null ? "" : " (a test judge stands in)")}; the values are never recorded",
            By = DecidedBy.Rule,
        });
        if (_services.ChatClients is null && readerKey is null)
        {
            throw new InvalidOperationException("No reader key: ReaderOptions.Key is empty. The host reads the key from its own configuration and passes it in (ADR 0004, 0022).");
        }

        if (_services.JudgeModel is null && judgeKey is null)
        {
            throw new InvalidOperationException("No judge key: JudgeOptions.Key is empty. The injection gates are always on (ADR 0013), so the run cannot start without it.");
        }

        if (_options.Fresh && File.Exists(paths.StateDatabase))
        {
            var kept = Path.Combine(output, $"state-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.sqlite");
            File.Move(paths.StateDatabase, kept);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(paths.StateDatabase + suffix))
                {
                    File.Delete(paths.StateDatabase + suffix);
                }
            }

            log.Record(new DecisionRecord { Phase = "run", Kind = "state", Subject = paths.StateDatabase, Decision = $"a fresh start was asked: the old state was kept as {Path.GetFileName(kept)} and a new one begins", By = DecidedBy.Rule });
        }

        using var state = RunState.Open(paths.StateDatabase);
        state.BeginRun(runId, playbook.Version, playbook.Hash);
        foreach (var prefix in _options.RerunUnits)
        {
            state.ForgetUnitsByPrefix(prefix);
            log.Record(new DecisionRecord { Phase = "run", Kind = "rerun", Subject = prefix, Decision = "the units of work whose key starts with it were forgotten, so they run again", By = DecidedBy.Rule });
        }

        foreach (var phase in _options.RerunPhases)
        {
            state.ForgetUnits(phase == "*" ? null : phase);
            log.Record(new DecisionRecord { Phase = "run", Kind = "rerun", Subject = phase, Decision = "its units of work were forgotten, so they run again (the cache still answers unchanged calls)", By = DecidedBy.Rule });
        }

        var spend = new SpendBook(_options.Spending.SpendPath ?? paths.Spend, runId, output);
        var cache = new ReplyCache(paths.Cache) { Indexed = state.IndexCache };
        var http = _services.HttpClient ?? new HttpClient();
        Func<string, ThinkingMode, IChatClient> clients;
        string provider;
        if (_services.ChatClients is not null)
        {
            var scripted = _services.ChatClients;
            clients = (model, _) => scripted(model);
            provider = "test";
        }
        else
        {
            var readerClients = new ReaderClients(_options.Reader, readerKey!);
            clients = readerClients.For;
            provider = readerClients.Provider;
        }

        var reader = new Reader(clients, provider, _options.Reader, cache, spend, log, _options.Spending.Prices);
        var judgeModel = _services.JudgeModel ?? new TypeSafeJudgeModel(http, judgeKey!, _options.Judge);
        var judge = new Judge(judgeModel, _options.Judge, cache, spend, log, _options.Spending.Prices);
        var gated = new GatedReader(reader, judge, playbook, _options.Thresholds, log);
        var answers = new AnswerStore(paths.Answers);
        var book = new QuestionBook(state, runId);
        var policy = _services.QuestionPolicy(answers, book);
        var glossary = new Glossary(state, log, runId, _options.Budgets.GlossaryTokens, playbook.GlossaryKinds);
        log.Record(new DecisionRecord
        {
            Phase = "run",
            Kind = "policies",
            Subject = "question and backup policies, the ceilings",
            Decision = $"questions: {policy.Name} ({answers.Count} answer(s) kept); backup: {_services.Backup}; the reader's ceiling {PriceTable.Dollars(_options.Spending.CeilingUsd)} over {_options.Spending.Scope} (spent so far: {PriceTable.Dollars(spend.AllRunsUsdOf(SpendKind.Reader))}); the judge's limit {PriceTable.Dollars(_options.Spending.JudgeCeilingUsd)} (spent so far: {PriceTable.Dollars(spend.AllRunsUsdOf(SpendKind.Judge))}); glossary budget {_options.Budgets.GlossaryTokens} tokens",
            By = DecidedBy.Rule,
        });

        var context = new RunContext
        {
            Options = _options,
            Playbook = playbook,
            Log = log,
            Reader = gated,
            Judge = judge,
            Questions = policy,
            Book = book,
            Answers = answers,
            Spend = spend,
            RunId = runId,
            Paths = paths,
            Progress = progress,
            State = state,
            Glossary = glossary,
            Host = _host,
        };
        var readerCeiling = new CeilingGuard("ceiling", "the reader's spending ceiling", SpendKind.Reader, _options.Spending.CeilingUsd, Math.Max(5m, Math.Round(_options.Spending.CeilingUsd * 0.25m, 2)), _options.Spending.Scope, spend, context);
        var judgeCeiling = new CeilingGuard("judge-ceiling", "the judge's spending limit", SpendKind.Judge, _options.Spending.JudgeCeilingUsd, 1m, CeilingScope.AllRuns, spend, context);
        reader.BeforeSpending = readerCeiling.CheckAsync;
        judge.BeforeSpending = judgeCeiling.CheckAsync;
        reader.Waiting = (purpose, elapsed) =>
        {
            progress($"Still waiting on the reader: {purpose} ({elapsed.TotalMinutes:0} min so far)");
            context.Touch($"waiting on the reader: {purpose} ({elapsed.TotalMinutes:0} min so far)", force: true);
        };
        context.Touch("starting", force: true);

        var outcome = RunOutcome.Completed;
        var message = "completed";
        BackupResult? backup = null;
        try
        {
            var phases = await _host.PrepareAsync(context, cancellationToken).ConfigureAwait(false);
            backup = await BackUpAsync(context, cancellationToken).ConfigureAwait(false);
            foreach (var phaseName in playbook.Phases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var phase = phases.FirstOrDefault(p => string.Equals(p.Name, phaseName, StringComparison.OrdinalIgnoreCase));
                if (phase is null)
                {
                    log.Record(new DecisionRecord
                    {
                        Phase = phaseName,
                        Kind = "phase",
                        Subject = phaseName,
                        Decision = "not built yet in this host; skipped",
                        By = DecidedBy.Rule,
                    });
                    continue;
                }

                progress($"Phase {phaseName}: running");
                context.Touch($"phase {phaseName}", force: true);
                var started = DateTimeOffset.Now;
                await phase.RunAsync(cancellationToken).ConfigureAwait(false);
                context.PhasesDone.Add(phaseName);
                var cost = log.CostOfRun(phaseName);
                log.Record(new DecisionRecord
                {
                    Phase = phaseName,
                    Kind = "phase",
                    Subject = phaseName,
                    Decision = $"completed in {(DateTimeOffset.Now - started).TotalSeconds:0}s; spent {PriceTable.Dollars(cost.Usd)} this run ({state.UnitsOf(phaseName).Count} units of work done in the state)",
                    By = DecidedBy.Rule,
                    Cost = cost,
                });
                progress($"Phase {phaseName}: done, {PriceTable.Dollars(cost.Usd)}");
                context.Touch($"phase {phaseName} done", force: true);

                if (string.Equals(_options.StopAfterPhase, phaseName, StringComparison.OrdinalIgnoreCase))
                {
                    var later = playbook.Phases.SkipWhile(p => !string.Equals(p, phaseName, StringComparison.OrdinalIgnoreCase)).Skip(1)
                        .Where(p => phases.Any(x => string.Equals(x.Name, p, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (later.Count == 0)
                    {
                        // Nothing with code comes after this phase: the stop left nothing out, and the run is complete.
                        message = $"completed; the run was asked to stop after the {phaseName} phase, which is the last phase with code";
                        break;
                    }

                    outcome = RunOutcome.StoppedAfterPhase;
                    message = $"stopped after the {phaseName} phase as asked; not run: {string.Join(", ", later)}";
                    break;
                }
            }
        }
        catch (RunStoppedException stopped)
        {
            outcome = stopped.Outcome;
            message = stopped.Message;
            log.Record(new DecisionRecord { Phase = "run", Kind = "stopped", Subject = "the run", Decision = stopped.Message, By = DecidedBy.Rule });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = RunOutcome.Failed;
            message = ex.Message;
            log.Record(new DecisionRecord { Phase = "run", Kind = "failed", Subject = "the run", Decision = "failed: " + ex.Message, By = DecidedBy.Rule, Flags = ["failed"] });
            WriteTail(context, backup, outcome, message);
            throw;
        }

        WriteTail(context, backup, outcome, message);
        return new RunResult(outcome, runId, output, spend.RunUsd, spend.AllRunsUsd, book.Unanswered, message, context.Diff);
    }

    /// <summary>The end of a run, whatever its outcome: the run's row, the questions, the host's output, the renderings.</summary>
    private void WriteTail(RunContext context, BackupResult? backup, RunOutcome outcome, string message)
    {
        context.State.EndRun(context.RunId, outcome.ToString(), message, context.Spend.RunUsd, context.Spend.RunCalls);
        context.Book.Write(context.Paths.Questions);
        _host.Finish(context, backup, outcome, message);
        context.Touch($"finished: {outcome}", force: true);
    }

    private async Task<BackupResult?> BackUpAsync(RunContext context, CancellationToken cancellationToken)
    {
        var kind = _services.Backup;
        if (kind == BackupKind.Ask)
        {
            var answer = await context.AskAsync(BackupQuestion.Build(), "backup", "no answer; the zip is taken (the safe default)", cancellationToken).ConfigureAwait(false);
            kind = answer?.Option == "no" ? BackupKind.None : BackupKind.Zip;
        }

        if (kind == BackupKind.None)
        {
            context.Log.Record(new DecisionRecord { Phase = "backup", Kind = "backup", Subject = "the inputs", Decision = "no zip taken (configured off); the state records a hash per input", By = DecidedBy.Rule });
            return null;
        }

        var result = await _host.BackUpAsync(context, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            context.Log.Record(new DecisionRecord { Phase = "backup", Kind = "backup", Subject = "the inputs", Decision = "the host had nothing to archive", By = DecidedBy.Rule });
            return null;
        }

        context.Log.Record(new DecisionRecord
        {
            Phase = "backup",
            Kind = "backup",
            Subject = result.ArchivePath ?? "backup",
            Decision = result.Note,
            By = DecidedBy.Rule,
        });
        context.Progress($"Backup: {result.Entries} entries archived");
        return result;
    }
}

/// <summary>
/// At a ceiling the run asks rather than stops (ADR 0008, 0024); a no, or no answer under a written policy, stops
/// it cleanly. One guard watches the reader's spend against its ceiling, another the judge's against its limit.
/// </summary>
internal sealed class CeilingGuard
{
    private readonly string _questionKey;
    private readonly string _name;
    private readonly SpendKind _kind;
    private readonly decimal _raise;
    private readonly CeilingScope _scope;
    private readonly SpendBook _spend;
    private readonly RunContext _context;
    private readonly SemaphoreSlim _asking = new(1, 1);
    private decimal _ceiling;
    private int _timesAsked;

    public CeilingGuard(string questionKey, string name, SpendKind kind, decimal ceiling, decimal raise, CeilingScope scope, SpendBook spend, RunContext context)
    {
        _questionKey = questionKey;
        _name = name;
        _kind = kind;
        _ceiling = ceiling;
        _raise = raise;
        _scope = scope;
        _spend = spend;
        _context = context;
    }

    private decimal Current => _scope == CeilingScope.AllRuns ? _spend.AllRunsUsdOf(_kind) : _spend.RunUsdOf(_kind);

    public async Task CheckAsync(decimal estimate, CancellationToken cancellationToken)
    {
        if (Current + estimate <= _ceiling)
        {
            return;
        }

        await _asking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Current;
            if (current + estimate <= _ceiling)
            {
                return;
            }

            _timesAsked++;
            var question = new Question(
                $"{_questionKey}:{_timesAsked}",
                "spending",
                $"The run has reached {_name} of {PriceTable.Dollars(_ceiling)} ({_scope}): spent {PriceTable.Dollars(_spend.AllRunsUsdOf(_kind))} on it across all runs and {PriceTable.Dollars(_spend.RunUsdOf(_kind))} this run; the next call is estimated at {PriceTable.Dollars(estimate)}. Done so far: {(_context.PhasesDone.Count == 0 ? "nothing complete yet" : string.Join(", ", _context.PhasesDone))}. Continue?",
                [new QuestionOption("continue", $"raise it by {PriceTable.Dollars(_raise)} and go on"), new QuestionOption("stop", "end the run cleanly with the output as far as it got")],
                "stop")
            {
                Blocking = true,
            };
            var answer = await _context.AskAsync(question, _name, "no answer; the run stops cleanly here", cancellationToken).ConfigureAwait(false);
            if (answer?.Option == "continue")
            {
                _ceiling = current + _raise;
                return;
            }

            throw new RunStoppedException(RunOutcome.StoppedAtCeiling,
                $"stopped at {_name} ({PriceTable.Dollars(_ceiling)}, {_scope}); spent {PriceTable.Dollars(_spend.RunUsd)} this run and {PriceTable.Dollars(_spend.AllRunsUsd)} across all runs");
        }
        finally
        {
            _asking.Release();
        }
    }
}
