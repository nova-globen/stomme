using System.Text;
using System.Text.Json;

namespace Stomme.Conductor.Ledger;

/// <summary>Who spent: the reader (a generative model) or the judge; their ceilings are kept apart (ADR 0024).</summary>
public enum SpendKind
{
    Reader,
    Judge,
}

/// <summary>One run's line in the spend book.</summary>
public sealed class SpendRun
{
    public required string RunId { get; set; }

    /// <summary>The output folder the run wrote into; absent in lines written before the engine was named.</summary>
    public string? Output { get; set; }

    public DateTimeOffset Started { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public decimal Usd { get; set; }
    public decimal ReaderUsd { get; set; }
    public decimal JudgeUsd { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>The spend book's shape.</summary>
public sealed class SpendState
{
    public decimal TotalUsd { get; set; }
    public decimal ReaderUsd { get; set; }
    public decimal JudgeUsd { get; set; }
    public List<SpendRun> Runs { get; set; } = [];
}

/// <summary>
/// What every run spent, the reader's and the judge's amounts apart (ADR 0024), kept next to the cache so the
/// ceilings can apply across runs (ADR 0008). Written after every call; small enough that this costs nothing.
/// A line written before the amounts were kept apart counts as the reader's: the judge's share of it was cents.
/// </summary>
public sealed class SpendBook
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _gate = new();
    private readonly SpendState _state;
    private readonly SpendRun _run;

    public SpendBook(string path, string runId, string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _state = File.Exists(path)
            ? JsonSerializer.Deserialize<SpendState>(File.ReadAllText(path), JsonOptions) ?? new SpendState()
            : new SpendState();
        foreach (var run in _state.Runs.Where(r => r.ReaderUsd == 0 && r.JudgeUsd == 0 && r.Usd > 0))
        {
            run.ReaderUsd = run.Usd;
        }

        _state.ReaderUsd = _state.Runs.Sum(r => r.ReaderUsd);
        _state.JudgeUsd = _state.Runs.Sum(r => r.JudgeUsd);
        _run = _state.Runs.FirstOrDefault(r => r.RunId == runId)
            ?? new SpendRun { RunId = runId, Output = output, Started = DateTimeOffset.Now };
        if (!_state.Runs.Contains(_run))
        {
            _state.Runs.Add(_run);
        }

        Save();
    }

    public string Path { get; }

    public decimal RunUsd
    {
        get
        {
            lock (_gate)
            {
                return _run.Usd;
            }
        }
    }

    public decimal AllRunsUsd
    {
        get
        {
            lock (_gate)
            {
                return _state.TotalUsd;
            }
        }
    }

    public int RunCalls
    {
        get
        {
            lock (_gate)
            {
                return _run.Calls;
            }
        }
    }

    /// <summary>This run's spend on the reader or the judge.</summary>
    public decimal RunUsdOf(SpendKind kind)
    {
        lock (_gate)
        {
            return kind == SpendKind.Judge ? _run.JudgeUsd : _run.ReaderUsd;
        }
    }

    /// <summary>Every run's spend on the reader or the judge, together.</summary>
    public decimal AllRunsUsdOf(SpendKind kind)
    {
        lock (_gate)
        {
            return kind == SpendKind.Judge ? _state.JudgeUsd : _state.ReaderUsd;
        }
    }

    public void Add(SpendKind kind, CallCost cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        lock (_gate)
        {
            _run.Usd += cost.Usd;
            _run.Calls++;
            _run.InputTokens += cost.InputTokens;
            _run.OutputTokens += cost.OutputTokens;
            _run.Updated = DateTimeOffset.Now;
            _state.TotalUsd += cost.Usd;
            if (kind == SpendKind.Judge)
            {
                _run.JudgeUsd += cost.Usd;
                _state.JudgeUsd += cost.Usd;
            }
            else
            {
                _run.ReaderUsd += cost.Usd;
                _state.ReaderUsd += cost.Usd;
            }

            Save();
        }
    }

    private void Save()
    {
        var folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(Path, JsonSerializer.Serialize(_state, JsonOptions), new UTF8Encoding(false));
    }
}
