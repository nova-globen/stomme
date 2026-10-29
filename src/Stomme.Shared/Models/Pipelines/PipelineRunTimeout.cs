#nullable enable
using System;
using System.Globalization;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// n8n's "Timeout Workflow": how long a run may be busy in one go before it is stopped
    /// (<see cref="PipelineGraph.TimeoutMinutes"/>). Busy is what runs without a pause: the time a run waits - for a time, a
    /// decision, a call - does not count, and each stretch after a wait has the whole of it again, as n8n's does. The step that
    /// is running when the time is up finishes; the steps after it do not start, and the run ends cancelled, saying why.
    /// </summary>
    public static class PipelineRunTimeout
    {
        /// <summary>A day: a run busy longer than that has a step that never ends or a loop that never stops.</summary>
        public const int MaxMinutes = 24 * 60;

        /// <summary>The minutes as a pipeline keeps them: from 1 to <see cref="MaxMinutes"/>, else none.</summary>
        public static int? Minutes(int? minutes) => minutes is > 0 ? Math.Min(minutes.Value, MaxMinutes) : null;

        /// <summary>n8n's <c>executionTimeout</c> - seconds, -1 for none - in whole minutes, rounded up.</summary>
        public static int? FromSeconds(double? seconds) =>
            seconds is > 0 and < double.MaxValue ? Minutes((int)Math.Min(Math.Ceiling(seconds.Value / 60), MaxMinutes)) : null;

        /// <summary>What the run's log says of a run stopped by it.</summary>
        public static string Reason(int minutes) =>
            "The run was stopped: it was busy longer than " + Said(minutes) + " in one go - the pipeline's time limit.";

        /// <summary>Minutes as they read: "1 minute", "45 minutes", "2 hours", "1 hour 30 minutes".</summary>
        public static string Said(int minutes)
        {
            var hours = minutes / 60;
            var rest = minutes % 60;
            var inHours = hours == 0 ? null : hours == 1 ? "1 hour" : hours.ToString(CultureInfo.InvariantCulture) + " hours";
            var inMinutes = rest == 0 ? null : rest == 1 ? "1 minute" : rest.ToString(CultureInfo.InvariantCulture) + " minutes";
            return inHours is null ? inMinutes ?? "0 minutes" : inMinutes is null ? inHours : inHours + " " + inMinutes;
        }
    }
}
