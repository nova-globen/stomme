using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stomme.Conductor.Ledger;

/// <summary>
/// The decision log (ADR 0002): log.jsonl is the record, appended as decisions are made and flushed at once,
/// so a run that dies still leaves what it decided; log.md is the readable rendering. The log is opened
/// before anything else in the bundle is written.
/// </summary>
public sealed class DecisionLog : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Lock _gate = new();
    private readonly List<DecisionRecord> _records = [];
    private readonly StreamWriter _jsonl;
    private readonly string _runId;
    private int _seq;

    public DecisionLog(string outputFolder, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        _runId = runId;
        Directory.CreateDirectory(outputFolder);
        JsonlPath = Path.Combine(outputFolder, "log.jsonl");
        MarkdownPath = Path.Combine(outputFolder, "log.md");

        if (File.Exists(JsonlPath))
        {
            foreach (var line in File.ReadLines(JsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var record = JsonSerializer.Deserialize<DecisionRecord>(line, JsonOptions);
                if (record is not null)
                {
                    _records.Add(record);
                    _seq = Math.Max(_seq, record.Seq);
                }
            }
        }

        _jsonl = new StreamWriter(new FileStream(JsonlPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public string JsonlPath { get; }

    public string MarkdownPath { get; }

    public string RunId => _runId;

    /// <summary>Every record, this run's and earlier runs' into the same output folder.</summary>
    public IReadOnlyList<DecisionRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    /// <summary>Appends one record, numbering and timing it, and flushes it to log.jsonl at once.</summary>
    public DecisionRecord Record(DecisionRecord draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            var record = draft with { Seq = ++_seq, Time = DateTimeOffset.Now, Run = _runId };
            _records.Add(record);
            _jsonl.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            return record;
        }
    }

    /// <summary>The cost of every call of this run, or of one phase of it.</summary>
    public CallCost CostOfRun(string? phase = null)
    {
        var total = CallCost.None;
        foreach (var record in Records)
        {
            if (record.Run == _runId && record.Kind == "call" && record.Cost is not null
                && (phase is null || record.Phase == phase))
            {
                total = total.Add(record.Cost);
            }
        }

        return total;
    }

    public int CountOfRun(string kind, string? phase = null) =>
        Records.Count(r => r.Run == _runId && r.Kind == kind && (phase is null || r.Phase == phase));

    /// <summary>Writes log.md from every record, grouped by run and phase; calls as a table, decisions in full.</summary>
    public void RenderMarkdown()
    {
        var records = Records;
        var text = new StringBuilder();
        text.AppendLine("# Decision log");
        text.AppendLine();
        text.AppendLine("Every decision the run made, with its evidence and reasoning (ADR 0002), and every model call with its cost (ADR 0008). The record is `log.jsonl`; this rendering is for a person.");
        text.AppendLine();

        foreach (var run in records.GroupBy(r => r.Run))
        {
            var runRecords = run.ToList();
            var first = runRecords[0].Time;
            text.AppendLine(CultureInfo.InvariantCulture, $"## Run {run.Key} ({first:yyyy-MM-dd HH:mm})");
            text.AppendLine();
            var cost = runRecords.Where(r => r.Kind == "call" && r.Cost is not null).Aggregate(CallCost.None, (a, r) => a.Add(r.Cost!));
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Spent {Dollars(cost.Usd)} over {runRecords.Count(r => r.Kind == "call")} calls ({runRecords.Count(r => r.Kind == "call" && r.Cached)} from the cache): {cost.InputTokens:N0} input tokens ({cost.CachedInputTokens:N0} cached), {cost.OutputTokens:N0} output tokens ({cost.ReasoningTokens:N0} reasoning).");
            text.AppendLine();

            foreach (var phase in runRecords.GroupBy(r => r.Phase))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"### Phase: {phase.Key}");
                text.AppendLine();
                foreach (var record in phase.Where(r => r.Kind != "call"))
                {
                    RenderDecision(text, record);
                }

                var calls = phase.Where(r => r.Kind == "call").ToList();
                if (calls.Count > 0)
                {
                    text.AppendLine("#### Calls");
                    text.AppendLine();
                    text.AppendLine("| # | Purpose | Model | Input | Cached | Output | Reasoning | Cost | From cache |");
                    text.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|");
                    foreach (var call in calls)
                    {
                        var c = call.Cost ?? CallCost.None;
                        text.AppendLine(CultureInfo.InvariantCulture,
                            $"| {call.Seq} | {Cell(call.Subject)} | {call.Model} | {c.InputTokens:N0} | {c.CachedInputTokens:N0} | {c.OutputTokens:N0} | {c.ReasoningTokens:N0} | {Dollars(c.Usd)}{(c.Estimated ? " (estimated)" : "")} | {(call.Cached ? "yes" : "no")} |");
                    }

                    text.AppendLine();
                }
            }
        }

        File.WriteAllText(MarkdownPath, text.ToString(), new UTF8Encoding(false));
    }

    private static void RenderDecision(StringBuilder text, DecisionRecord record)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"#### {record.Seq}. {record.Kind}: {record.Subject}");
        text.AppendLine();
        var by = record.By + (record.Model is null ? "" : $" ({record.Model})");
        var confidence = record.Confidence is null ? "" : $", confidence {record.Confidence.Value.ToString("0.00", CultureInfo.InvariantCulture)}";
        text.AppendLine(CultureInfo.InvariantCulture, $"- Decision: {record.Decision} (by the {by}{confidence})");
        foreach (var evidence in record.Evidence)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Evidence: {evidence}");
        }

        if (!string.IsNullOrWhiteSpace(record.Reasoning))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Reasoning: {record.Reasoning.Trim()}");
        }

        if (record.Alternatives.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Alternatives considered: {string.Join("; ", record.Alternatives)}");
        }

        if (record.Figures is { Count: > 0 })
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- Figures: {string.Join(", ", record.Figures.Select(f => $"{f.Key} {f.Value.ToString("0.###", CultureInfo.InvariantCulture)}"))}");
        }

        if (record.Flags.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Flags: {string.Join(", ", record.Flags)}");
        }

        if (record.Question is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Question for a person: {record.Question}");
        }

        if (record.Call is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- From call {record.Call}");
        }

        text.AppendLine();
    }

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');

    private static string Dollars(decimal usd) => "$" + usd.ToString("0.0000", CultureInfo.InvariantCulture);

    public void Dispose() => _jsonl.Dispose();
}
