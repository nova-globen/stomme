#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// How the site's pipelines have been running (BL-229, n8n's Insights): over the last day, week or month, how many runs each
    /// pipeline started, how many failed or were cancelled, how many are still going, the failure rate of those that ended, and
    /// how long a run takes - the MEDIAN of the runs that ended, as a run waiting days for an approval would make an average say
    /// nothing. ONE computation (<see cref="PipelineInsights.Of"/>), the server's query only reads the window's runs.
    /// </summary>
    public class PipelineInsightsView
    {
        /// <summary>The window, in days.</summary>
        public int Days { get; set; }

        public DateTime SinceUtc { get; set; }

        /// <summary>Every pipeline's runs together.</summary>
        public PipelineInsight Total { get; set; } = new();

        /// <summary>Each pipeline that ran in the window, the most runs first.</summary>
        public List<PipelineInsight> Pipelines { get; set; } = new();

        /// <summary>More runs started in the window than are read (<see cref="PipelineInsights.MaxRuns"/>): the figures cover the newest.</summary>
        public bool Capped { get; set; }
    }

    public class PipelineInsight
    {
        public string? DefinitionId { get; set; }

        public string? PipelineName { get; set; }

        public int Runs { get; set; }

        public int Failed { get; set; }

        public int Cancelled { get; set; }

        /// <summary>Started in the window and not ended yet - running, or waiting for a time or a decision.</summary>
        public int Unfinished { get; set; }

        /// <summary>The median time of the runs that ended, in milliseconds; null when none has.</summary>
        public long? MedianMilliseconds { get; set; }

        /// <summary>The share of the runs that ended which failed; null when none has.</summary>
        public double? FailureRate { get; set; }

        public string? LastFailedRunId { get; set; }

        public DateTime? LastFailedOnUtc { get; set; }

        /// <summary>
        /// BL-229 - n8n's time saved: the minutes a person would have spent, as the pipeline says one run that finishes well saves
        /// them (<see cref="PipelineGraph.MinutesSavedPerRun"/>), times the window's runs that did. In all, the pipelines' that
        /// say it, added up. Null when none of them says.
        /// </summary>
        public long? MinutesSaved { get; set; }

        /// <summary>The minutes the pipeline says one run that finishes well saves; null when it does not say (or in all).</summary>
        public int? MinutesSavedPerRun { get; set; }
    }

    /// <summary>
    /// BL-229 - how one step of a pipeline has been doing over the window's runs - better than n8n's Insights, which stop at the
    /// workflow: how many times it ran (a loop's body counts each pass), how many of those failed - faulted, or failed and gone on
    /// down its Failed path or past it - and the median time of those that ended.
    /// </summary>
    public class PipelineStepInsight
    {
        public string StepId { get; set; } = "";

        /// <summary>The step's name as its passes recorded it - for whoever reads the figures without the canvas (MCP).</summary>
        public string? StepName { get; set; }

        public int Passes { get; set; }

        public int Failed { get; set; }

        public long? MedianMilliseconds { get; set; }
    }

    /// <summary>A pipeline's steps over a window: the runs read (the newest <see cref="PipelineInsights.MaxRunsForSteps"/>) and each step.</summary>
    public class PipelineStepInsightsView
    {
        public int Days { get; set; }

        public string? DefinitionId { get; set; }

        public int Runs { get; set; }

        public bool Capped { get; set; }

        public List<PipelineStepInsight> Steps { get; set; } = new();
    }

    public static class PipelineInsights
    {
        /// <summary>The most runs of one pipeline whose steps one look reads - the newest.</summary>
        public const int MaxRunsForSteps = 500;

        /// <summary>Each step's figures from its passes in the window's runs (BL-229).</summary>
        public static List<PipelineStepInsight> OfSteps(IEnumerable<PipelineStepRunView> passes) =>
            passes.Where(pass => !string.IsNullOrEmpty(pass.StepId))
                .GroupBy(pass => pass.StepId, StringComparer.Ordinal)
                .Select(step => new PipelineStepInsight
                {
                    StepId = step.Key,
                    StepName = step.Select(pass => pass.StepName).FirstOrDefault(name => !string.IsNullOrEmpty(name)),
                    Passes = step.Count(),
                    Failed = step.Count(pass => pass.Status == "Faulted" || (pass.Status == "Completed" && pass.HandledFailure is not null)),
                    MedianMilliseconds = Median(step.Where(pass => pass.CompletedAtUtc is not null)
                        .Select(pass => (long)Math.Max(0, (pass.CompletedAtUtc!.Value - pass.StartedAtUtc).TotalMilliseconds)).ToList()),
                })
                .ToList();

        /// <summary>A step's figures as its node says them: 24× · 2 failed · 120 ms.</summary>
        public static string Describe(PipelineStepInsight step) =>
            step.Passes + "×"
            + (step.Failed > 0 ? " · " + step.Failed + " failed" : "")
            + (step.MedianMilliseconds is { } median ? " · " + PipelineRunDurations.Describe(TimeSpan.FromMilliseconds(median)) : "");

        /// <summary>The windows offered, in days.</summary>
        public static readonly IReadOnlyList<int> Windows = new[] { 1, 7, 30 };

        /// <summary>The most runs one look reads - the newest.</summary>
        public const int MaxRuns = 5000;

        /// <summary>A window offered, or a week.</summary>
        public static int Window(int days) => Windows.Contains(days) ? days : 7;

        /// <summary>The most minutes one run can say it saves: a week of them.</summary>
        public const int MaxMinutesSavedPerRun = 7 * 24 * 60;

        /// <summary>Minutes saved per run as a pipeline keeps them: from 1 to <see cref="MaxMinutesSavedPerRun"/>, else none.</summary>
        public static int? MinutesSaved(int? minutes) => minutes is > 0 ? Math.Min(minutes.Value, MaxMinutesSavedPerRun) : null;

        /// <summary>
        /// The figures for the window's runs, each pipeline's and all of them together; with each pipeline's minutes saved per run
        /// (by definition id), the time its runs that finished well saved.
        /// </summary>
        public static PipelineInsightsView Of(IEnumerable<PipelineRunView> runs, int days, DateTime sinceUtc, bool capped, IReadOnlyDictionary<string, int>? minutesSavedPerRun = null)
        {
            var list = runs.ToList();
            var pipelines = list
                .GroupBy(run => run.DefinitionId ?? "", StringComparer.Ordinal)
                .Select(group => Figures(group.ToList(), group.Key.Length == 0 ? null : group.Key, group.Select(run => run.PipelineName).FirstOrDefault(name => !string.IsNullOrEmpty(name))))
                .OrderByDescending(insight => insight.Runs)
                .ThenBy(insight => insight.PipelineName ?? insight.DefinitionId, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (var insight in pipelines)
            {
                if (insight.DefinitionId is { } id && minutesSavedPerRun is not null && minutesSavedPerRun.TryGetValue(id, out var perRun) && MinutesSaved(perRun) is { } minutes)
                {
                    insight.MinutesSavedPerRun = minutes;
                    insight.MinutesSaved = (long)minutes * list.Count(run => run.DefinitionId == id && run.SubStatus == "Finished" && run.FinishedOnUtc is not null);
                }
            }

            var total = Figures(list, null, null);
            total.MinutesSaved = pipelines.Any(insight => insight.MinutesSaved is not null) ? pipelines.Sum(insight => insight.MinutesSaved ?? 0) : null;
            return new PipelineInsightsView { Days = days, SinceUtc = sinceUtc, Capped = capped, Total = total, Pipelines = pipelines };
        }

        /// <summary>Time saved as the page says it: 45 min, 2 h, 2 h 5 min - in hours past the first, a person's hours of work.</summary>
        public static string Saved(long minutes) =>
            minutes < 60 ? minutes + " min" : (minutes / 60).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " h" + (minutes % 60 == 0 ? "" : " " + minutes % 60 + " min");

        private static PipelineInsight Figures(IReadOnlyList<PipelineRunView> runs, string? definitionId, string? name)
        {
            var ended = runs.Where(run => run.FinishedOnUtc is not null).ToList();
            var failed = runs.Where(run => run.SubStatus == "Faulted").OrderByDescending(run => run.CreatedOnUtc).ToList();
            return new PipelineInsight
            {
                DefinitionId = definitionId,
                PipelineName = name,
                Runs = runs.Count,
                Failed = failed.Count,
                Cancelled = runs.Count(run => run.SubStatus == "Cancelled"),
                Unfinished = runs.Count - ended.Count,
                MedianMilliseconds = Median(ended.Select(run => (long)Math.Max(0, (run.FinishedOnUtc!.Value - run.CreatedOnUtc).TotalMilliseconds)).ToList()),
                FailureRate = ended.Count == 0 ? null : (double)failed.Count(run => run.FinishedOnUtc is not null) / ended.Count,
                LastFailedRunId = failed.FirstOrDefault()?.Id,
                LastFailedOnUtc = failed.FirstOrDefault()?.CreatedOnUtc,
            };
        }

        private static long? Median(List<long> values)
        {
            if (values.Count == 0)
            {
                return null;
            }

            values.Sort();
            var middle = values.Count / 2;
            return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
        }

        /// <summary>A rate as the page says it: 12.5 %, 0 %, and "—" for none.</summary>
        public static string Percent(double? rate) =>
            rate is null ? "—" : (rate.Value * 100).ToString(rate.Value * 100 % 1 == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " %";
    }
}
