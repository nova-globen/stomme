#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// What runs next: each published pipeline's schedules with their next runs within a window, the soonest first -
    /// beyond n8n, which shows a schedule only inside its workflow. Read off the published bodies as the schedules task reads
    /// them: a schedule that starts its pipeline (nothing leads to it, not in a body, not switched off), its cron in its time zone.
    /// </summary>
    public class PipelineUpcomingView
    {
        /// <summary>The window, in hours.</summary>
        public int Hours { get; set; }

        /// <summary>Whether the site runs schedules at all (<c>Pipelines:Schedules:Enabled</c>): off, nothing below fires.</summary>
        public bool Enabled { get; set; }

        public List<PipelineUpcomingSchedule> Schedules { get; set; } = new();
    }

    public class PipelineUpcomingSchedule
    {
        public string DefinitionId { get; set; } = "";

        public string? PipelineName { get; set; }

        public string StepId { get; set; } = "";

        public string? StepName { get; set; }

        public string Cron { get; set; } = "";

        /// <summary>The zone its cron is read in; null for UTC.</summary>
        public string? TimeZone { get; set; }

        /// <summary>Its next runs within the window, the first <see cref="PipelineUpcoming.Shown"/> (UTC).</summary>
        public List<DateTime> NextRunsUtc { get; set; } = new();

        /// <summary>How many runs it has within the window, counted to <see cref="PipelineUpcoming.MaxCounted"/>.</summary>
        public int RunsInWindow { get; set; }
    }

    public static class PipelineUpcoming
    {
        /// <summary>The windows offered, in hours: the next day, the next week.</summary>
        public static readonly IReadOnlyList<int> Windows = new[] { 24, 168 };

        /// <summary>How many of a schedule's next runs are listed.</summary>
        public const int Shown = 3;

        /// <summary>The most runs of one schedule counted in a window (an every-minute schedule over a week is more).</summary>
        public const int MaxCounted = 1000;

        /// <summary>A window offered, or the next day.</summary>
        public static int Window(int hours) => Windows.Contains(hours) ? hours : 24;

        /// <summary>How soon a run comes, as the list says it: in 5 min, in 3 h 10 min, in 2 d 4 h - "now" within a minute.</summary>
        public static string Until(DateTime nextUtc, DateTime nowUtc)
        {
            var span = nextUtc - nowUtc;
            return span.TotalMinutes < 1 ? "now"
                : span.TotalHours < 1 ? $"in {(int)span.TotalMinutes} min"
                : span.TotalDays < 1 ? $"in {(int)span.TotalHours} h" + (span.Minutes == 0 ? "" : $" {span.Minutes} min")
                : $"in {(int)span.TotalDays} d" + (span.Hours == 0 ? "" : $" {span.Hours} h");
        }
    }
}
