#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Evaluations: a published pipeline run over a data table's rows as test cases, one run per row, each run
    /// started at its "When evaluating" step with the row as its result; an Evaluation step in it records scores (metrics) and
    /// outputs for the row. What one evaluation is: its pipeline and table, who started it, its state, each row and the averages.
    /// </summary>
    public class PipelineEvaluationView
    {
        public string Id { get; set; } = "";

        public string DefinitionId { get; set; } = "";

        public string? PipelineName { get; set; }

        /// <summary>The published version the rows ran.</summary>
        public int Version { get; set; }

        public string TableId { get; set; } = "";

        public string? TableName { get; set; }

        /// <summary><see cref="PipelineEvaluations.Running"/>, <see cref="PipelineEvaluations.Finished"/> or <see cref="PipelineEvaluations.Cancelled"/>.</summary>
        public string State { get; set; } = PipelineEvaluations.Running;

        public string? StartedBy { get; set; }

        public DateTime StartedOnUtc { get; set; }

        public DateTime? FinishedOnUtc { get; set; }

        /// <summary>Every row, in the table's order - in a list of evaluations, empty (only the counts and averages travel).</summary>
        public List<PipelineEvaluationRowView> Rows { get; set; } = new();

        public int RowCount { get; set; }

        /// <summary>The rows whose run has ended, whichever way.</summary>
        public int RowsDone { get; set; }

        /// <summary>The rows whose run failed, was cancelled or timed out.</summary>
        public int RowsFailed { get; set; }

        /// <summary>Each metric's average over the rows that recorded it, by its name.</summary>
        public Dictionary<string, decimal> Averages { get; set; } = new();
    }

    /// <summary>One test case of an evaluation: its row, its run and what the run recorded for it.</summary>
    public class PipelineEvaluationRowView
    {
        public string RowId { get; set; } = "";

        /// <summary>The row's values - what the run was given.</summary>
        public Dictionary<string, string> Values { get; set; } = new();

        public string? RunId { get; set; }

        /// <summary><see cref="PipelineEvaluations.Waiting"/>, <see cref="PipelineEvaluations.RowRunning"/>, <see cref="PipelineEvaluations.RowFinished"/>, <see cref="PipelineEvaluations.RowFailed"/>, <see cref="PipelineEvaluations.RowCancelled"/> or <see cref="PipelineEvaluations.RowTimedOut"/>.</summary>
        public string State { get; set; } = PipelineEvaluations.Waiting;

        public Dictionary<string, decimal> Metrics { get; set; } = new();

        public Dictionary<string, string> Outputs { get; set; } = new();

        public string? Error { get; set; }
    }

    /// <summary>What starting an evaluation asks for: the data table whose rows are the test cases, and how many of them.</summary>
    public class PipelineEvaluationRequest
    {
        public string TableId { get; set; } = "";

        /// <summary>At most this many rows, from the top; none: up to <see cref="PipelineEvaluations.MaxRows"/>.</summary>
        public int? MaxRows { get; set; }
    }

    /// <summary>The evaluations' rules and words - the one place both sides read.</summary>
    public static class PipelineEvaluations
    {
        /// <summary>The step a pipeline's test cases enter at (engine type name).</summary>
        public const string TriggerType = "Stomme.EvaluationTrigger";

        public const int MaxRows = 100;

        public const int MaxMetrics = 20;

        public const int MaxOutputs = 20;

        public const int MaxNameLength = 60;

        public const int MaxOutputLength = 4000;

        public const string Running = "Running";

        public const string Finished = "Finished";

        public const string Cancelled = "Cancelled";

        public const string Waiting = "Waiting";

        public const string RowRunning = "Running";

        public const string RowFinished = "Finished";

        public const string RowFailed = "Failed";

        public const string RowCancelled = "Cancelled";

        public const string RowTimedOut = "Timed out";

        /// <summary>Whether a row's run has ended, whichever way.</summary>
        public static bool IsDone(string? rowState) => rowState is RowFinished or RowFailed or RowCancelled or RowTimedOut;

        /// <summary>Whether a row's run ended without finishing well.</summary>
        public static bool IsFailed(string? rowState) => rowState is RowFailed or RowCancelled or RowTimedOut;

        /// <summary>Each metric's average over the rows that recorded it.</summary>
        public static Dictionary<string, decimal> Averages(IEnumerable<IReadOnlyDictionary<string, decimal>> rows) =>
            rows.SelectMany(metrics => metrics)
                .GroupBy(metric => metric.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Math.Round(group.Average(metric => metric.Value), 4), StringComparer.Ordinal);

        /// <summary>
        /// Each metric now beside what an earlier evaluation had for it (n8n's comparison of evaluation runs): the value, the earlier
        /// one when there was one, and the change - in the metrics' order now, then any the earlier one had alone.
        /// </summary>
        public static List<(string Name, decimal? Now, decimal? Before, decimal? Change)> Compare(IReadOnlyDictionary<string, decimal>? now, IReadOnlyDictionary<string, decimal>? before)
        {
            now ??= new Dictionary<string, decimal>();
            before ??= new Dictionary<string, decimal>();
            var compared = now.Select(metric => (metric.Key, (decimal?)metric.Value,
                before.TryGetValue(metric.Key, out var earlier) ? (decimal?)earlier : null,
                before.TryGetValue(metric.Key, out var then) ? (decimal?)Math.Round(metric.Value - then, 4) : null)).ToList();
            compared.AddRange(before.Where(metric => !now.ContainsKey(metric.Key)).Select(metric => (metric.Key, (decimal?)null, (decimal?)metric.Value, (decimal?)null)));
            return compared;
        }

        /// <summary>A change as the page shows it: "+0.25", "-1", "±0".</summary>
        public static string Change(decimal change) =>
            change == 0 ? "±0" : (change > 0 ? "+" : "-") + Math.Abs(change).ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// 'name: number' lines (or a JSON object of numbers, read by the caller) as metrics - each name once, at most
        /// <see cref="MaxNameLength"/> characters; null with the reason when a line is not that.
        /// </summary>
        public static Dictionary<string, decimal>? ParseMetrics(string? text, out string? error)
        {
            error = null;
            var metrics = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var colon = line.IndexOf(':');
                var name = colon > 0 ? line[..colon].Trim() : "";
                var value = colon > 0 ? line[(colon + 1)..].Trim() : "";
                if (name.Length == 0 || name.Length > MaxNameLength)
                {
                    error = $"'{line}' is not 'name: number' (a name of at most {MaxNameLength} characters).";
                    return null;
                }

                if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"'{value}' is not a number for the metric '{name}'.";
                    return null;
                }

                metrics[name] = number;
            }

            if (metrics.Count > MaxMetrics)
            {
                error = $"An evaluation keeps at most {MaxMetrics} metrics per row.";
                return null;
            }

            return metrics;
        }

        /// <summary>How alike two texts are, 0 to 1: one minus their edit distance over the longer one's length (1 for two empty texts).</summary>
        public static decimal Similarity(string? a, string? b)
        {
            a ??= "";
            b ??= "";
            var longer = Math.Max(a.Length, b.Length);
            if (longer == 0)
            {
                return 1m;
            }

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return Math.Round(1m - (decimal)previous[b.Length] / longer, 4);
        }
    }
}
