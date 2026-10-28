#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>One step of two runs side by side: what it did in each (null where it did not run) and what differs.</summary>
    public sealed class PipelineRunStepComparison
    {
        public string StepId { get; set; } = "";

        public string? StepName { get; set; }

        public PipelineRunStepSide? This { get; set; }

        public PipelineRunStepSide? Other { get; set; }

        /// <summary>What differs, in words - empty when the step went the same way in both.</summary>
        public List<string> Differences { get; set; } = new();
    }

    /// <summary>A step in one run: its last pass's state, how many passes, their time together and its last output.</summary>
    public sealed class PipelineRunStepSide
    {
        public string Status { get; set; } = "";

        public int Passes { get; set; }

        /// <summary>The passes' time together - null when none of them ended (a pass that faulted, or is still waiting).</summary>
        public TimeSpan? Took { get; set; }

        public string? OutputsJson { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>
    /// BL-229 - two runs of a pipeline compared step by step (beyond n8n, which shows one execution at a time): for each step,
    /// whether it ran in both, its state, how many passes, and whether what it produced is the same - so "it worked yesterday"
    /// is answered by the step where the two runs part. Outputs are compared only where both are known (an author's view; a
    /// viewer is sent none); time is said, never counted as a difference.
    /// </summary>
    public static class PipelineRunComparison
    {
        public static List<PipelineRunStepComparison> Compare(IEnumerable<PipelineStepRunView>? thisRun, IEnumerable<PipelineStepRunView>? otherRun)
        {
            var here = Sides(thisRun);
            var there = Sides(otherRun);
            var order = (thisRun ?? Enumerable.Empty<PipelineStepRunView>()).Concat(otherRun ?? Enumerable.Empty<PipelineStepRunView>())
                .Where(pass => !string.IsNullOrEmpty(pass?.StepId))
                .OrderBy(pass => pass.StartedAtUtc)
                .Select(pass => pass.StepId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var rows = new List<PipelineRunStepComparison>();
            foreach (var stepId in order)
            {
                var ranHere = here.TryGetValue(stepId, out var a);
                var ranThere = there.TryGetValue(stepId, out var b);
                var row = new PipelineRunStepComparison
                {
                    StepId = stepId,
                    StepName = ranHere ? a.Name : b.Name,
                    This = ranHere ? a.Side : null,
                    Other = ranThere ? b.Side : null,
                };
                if (!ranHere)
                {
                    row.Differences.Add("ran only in the other run");
                }
                else if (!ranThere)
                {
                    row.Differences.Add("ran only in this run");
                }
                else
                {
                    if (!string.Equals(a.Side.Status, b.Side.Status, StringComparison.Ordinal))
                    {
                        row.Differences.Add($"{a.Side.Status} here, {b.Side.Status} there");
                    }

                    if (a.Side.Passes != b.Side.Passes)
                    {
                        row.Differences.Add($"{Times(a.Side.Passes)} here, {Times(b.Side.Passes)} there");
                    }

                    if (a.Side.OutputsJson is not null && b.Side.OutputsJson is not null && !string.Equals(a.Side.OutputsJson, b.Side.OutputsJson, StringComparison.Ordinal))
                    {
                        row.Differences.Add("produced something else");
                    }
                }

                rows.Add(row);
            }

            return rows;
        }

        /// <summary>The comparison in a line: how many steps went another way, or that none did.</summary>
        public static string Summary(IReadOnlyCollection<PipelineRunStepComparison> rows)
        {
            var differ = rows.Count(row => row.Differences.Count > 0);
            return differ == 0 ? "The two runs went the same way, step by step."
                : differ == 1 ? $"1 of {rows.Count} steps went another way."
                : $"{differ} of {rows.Count} steps went another way.";
        }

        private static string Times(int passes) => passes == 1 ? "once" : passes + " times";

        // Each step's passes in one run, as one side: the last pass's state, output and error; every pass's time together.
        private static Dictionary<string, (string? Name, PipelineRunStepSide Side)> Sides(IEnumerable<PipelineStepRunView>? passes) =>
            (passes ?? Enumerable.Empty<PipelineStepRunView>())
                .Where(pass => !string.IsNullOrEmpty(pass?.StepId))
                .GroupBy(pass => pass.StepId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group =>
                {
                    var ordered = group.OrderBy(pass => pass.StartedAtUtc).ToList();
                    var last = ordered[^1];
                    var ended = ordered.Where(pass => pass.CompletedAtUtc is { } done && done >= pass.StartedAtUtc).ToList();
                    TimeSpan? took = ended.Count == 0 ? null : ended.Aggregate(TimeSpan.Zero, (sum, pass) => sum + (pass.CompletedAtUtc!.Value - pass.StartedAtUtc));
                    return (last.StepName, new PipelineRunStepSide { Status = last.Status ?? "", Passes = ordered.Count, Took = took, OutputsJson = last.OutputsJson, Error = last.Error });
                }, StringComparer.Ordinal);
    }
}
