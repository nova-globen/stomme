#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// The states a list of runs is filtered by (n8n's execution filters), as a person names them: each one or more of
    /// the engine's sub-statuses. ONE list, offered by the runs page and read by the server's query.
    /// </summary>
    public static class PipelineRunStates
    {
        public const string Failed = "failed";
        public const string Running = "running";
        public const string Queued = "queued";
        public const string Waiting = "waiting";
        public const string Finished = "finished";
        public const string Cancelled = "cancelled";

        /// <summary>The states in the order the page offers them, with what it calls each, and the engine's sub-statuses each covers.</summary>
        public static IReadOnlyList<(string State, string Label, string[] SubStatuses)> All { get; } = new[]
        {
            (Failed, "Failed", new[] { "Faulted" }),
            (Running, "Running", new[] { "Executing" }),
            // Stored and not started: a run waiting its turn (a pipeline that runs one at a time, the rest in turn).
            (Queued, "Queued", new[] { "Pending" }),
            (Waiting, "Waiting", new[] { "Suspended" }),
            (Finished, "Finished", new[] { "Finished" }),
            (Cancelled, "Cancelled", new[] { "Cancelled" }),
        };

        /// <summary>The engine's sub-statuses a state covers; none for no state (every run) and for a state this list does not know.</summary>
        public static IReadOnlyList<string> SubStatusesOf(string? state) =>
            All.FirstOrDefault(entry => string.Equals(entry.State, state?.Trim(), StringComparison.OrdinalIgnoreCase)).SubStatuses ?? Array.Empty<string>();

        public static bool IsKnown(string? state) => SubStatusesOf(state).Count > 0;

        /// <summary>The state a run is in, as this list names it (Faulted is "Failed"); the engine's word for one it does not cover.</summary>
        public static string LabelOf(string? subStatus) =>
            All.FirstOrDefault(entry => entry.SubStatuses.Contains(subStatus, StringComparer.OrdinalIgnoreCase)).Label ?? subStatus ?? "";

        /// <summary>The state's own name for a run's sub-status (<see cref="Failed"/>...), for a page's styling; empty for one not covered.</summary>
        public static string StateOf(string? subStatus) =>
            All.FirstOrDefault(entry => entry.SubStatuses.Contains(subStatus, StringComparer.OrdinalIgnoreCase)).State ?? "";
    }

    /// <summary>
    /// What a step's pass did, as the editor's canvas and the run's timeline name it: completed, handled (it failed and
    /// the run went on past it), faulted, canceled, waiting (parked on a bookmark - a Wait, an approval, an event) or running.
    /// A pass that never ended, in a run that has, was ended with the run: canceled. The engine leaves the record of a step its
    /// run abandoned as it was - measured after a Stop: "Running", and waiting, in a cancelled run. A test of the whole pipeline
    /// that reached a wait is shown as a finished run whose sub-status is Suspended: its waiting step did stop there, waiting.
    /// </summary>
    public static class PipelineStepStates
    {
        public const string Canceled = "canceled";

        public static string Of(PipelineStepRunView pass, PipelineRunView? run) => pass.Status switch
        {
            "Completed" when pass.HandledFailure is not null => "handled",
            "Completed" => "completed",
            "Faulted" => "faulted",
            "Canceled" => Canceled,
            _ when run is { IsFinished: true } && run.SubStatus != "Suspended" => Canceled,
            _ when pass.Waiting => "waiting",
            _ => "running",
        };

        /// <summary>The engine's word for a pass, as the inspector says it - but "Canceled" for one its finished run abandoned.</summary>
        public static string StatusOf(PipelineStepRunView pass, PipelineRunView? run) => Of(pass, run) == Canceled ? "Canceled" : pass.Status;
    }

    /// <summary>How long a run took, as the runs list says it (n8n's run time): from its start to its finish.</summary>
    public static class PipelineRunDurations
    {
        /// <summary>The run's time - "running" while it is not finished, "queued" while it waits its turn.</summary>
        public static string Of(PipelineRunView run) =>
            run.FinishedOnUtc is { } finished ? Describe(finished - run.CreatedOnUtc)
            : string.Equals(run.SubStatus, "Pending", StringComparison.OrdinalIgnoreCase) ? "queued"
            : "running";

        public static string Describe(TimeSpan took) =>
            took < TimeSpan.Zero ? "0 ms"
            : took.TotalSeconds < 1 ? $"{Math.Round(took.TotalMilliseconds)} ms"
            : took.TotalMinutes < 1 ? took.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"
            : took.TotalHours < 1 ? $"{(int)took.TotalMinutes} min {took.Seconds} s"
            : $"{(int)took.TotalHours} h {took.Minutes} min";
    }
}
