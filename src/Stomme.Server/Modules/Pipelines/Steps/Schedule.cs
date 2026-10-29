#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cronos;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>The stimulus a UTC schedule trigger is indexed and fired by: the cron expression, normalised.</summary>
    /// <remarks>
    /// Left exactly as it was when schedules had no zone: a trigger is found by the HASH of its stimulus, and the engine's hash
    /// converter writes every property, a null one included (measured: <c>{"Cron":"0 6 * * *","TimeZone":null}</c>) - so a
    /// zone property here, even one left null, would have moved the hash of every schedule already published and stopped it
    /// firing until republished. A schedule in a zone is <see cref="ZonedScheduleStimulus"/>.
    /// </remarks>
    public sealed record ScheduleStimulus(string Cron);

    /// <summary>BL-229: the stimulus a schedule read in a time zone is indexed and fired by - the cron, and the zone's name.</summary>
    public sealed record ZonedScheduleStimulus(string Cron, string TimeZone);

    /// <summary>
    /// A cron schedule as an engine TRIGGER (BL-95 increment (e)): a published pipeline that starts with it is indexed
    /// by the engine into its trigger store, and <c>ScheduledPipelinesTask</c> (a recurring task the engine runs per
    /// site tenant) fires every schedule that is due through the engine's stimulus sender - which starts every
    /// pipeline indexed under that cron at once. The result is the fire time (UTC, ISO 8601). Five-field cron
    /// (minute hour day month weekday, UTC); an expression that does not parse is refused at publish. BL-229, n8n's trigger
    /// rules: several crons, one per line, each its own time - the trigger is indexed under each (the engine's many payloads),
    /// so each fires on its own; a schedule of ONE cron is indexed exactly as before, its hash unchanged.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline on a cron schedule, in UTC or a time zone.", DisplayName = "Schedule")]
    public class Schedule : Trigger<string>
    {
        public const string FiredAtInputKey = "__ScheduleFiredAt";

        [Input(UIHint = InputUIHints.MultiLine, Description = "A five-field cron expression: minute hour day month weekday - or several, one per line, each its own time. Examples: '*/15 * * * *', '0 6 * * 1-5'.")]
        public Input<string> Cron { get; set; } = null!;

        /// <summary>The most cron lines one schedule holds.</summary>
        public const int MaxLines = 10;

        /// <summary>The cron lines a schedule holds: one per line, blank lines aside, each normalized, each once.</summary>
        public static IReadOnlyList<string> Lines(string? cron) =>
            (cron ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Select(Normalize).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>Every line's parsed expression, or null with what is wrong - the first line that does not parse, named.</summary>
        public static IReadOnlyList<CronExpression>? TryParseAll(string? cron, out string? error)
        {
            var lines = Lines(cron);
            if (lines.Count == 0)
            {
                error = "The schedule needs a cron expression.";
                return null;
            }

            if (lines.Count > MaxLines)
            {
                error = $"A schedule holds at most {MaxLines} cron lines - this one has {lines.Count}.";
                return null;
            }

            var parsed = new List<CronExpression>();
            foreach (var line in lines)
            {
                if (TryParse(line, out error) is not { } expression)
                {
                    return null;
                }

                parsed.Add(expression);
            }

            error = null;
            return parsed;
        }

        /// <summary>The next occurrences of every expression after a moment, in a zone, merged in order - each moment once, at most so many.</summary>
        public static List<DateTime> Next(IEnumerable<CronExpression> expressions, DateTime fromUtc, TimeZoneInfo zone, int most, DateTime? untilUtc = null)
        {
            var moments = new SortedSet<DateTime>();
            foreach (var expression in expressions)
            {
                var from = fromUtc;
                for (var counted = 0; counted < most && expression.GetNextOccurrence(from, zone) is { } next && (untilUtc is null || next <= untilUtc); counted++)
                {
                    moments.Add(next);
                    from = next;
                }
            }

            return moments.Take(most).ToList();
        }

        /// <summary>BL-229 (n8n's schedule trigger has one): the zone the cron is read in. Empty is UTC.</summary>
        [Input(DisplayName = "Time zone", Description = "The time zone the schedule is read in - an IANA name such as Europe/Stockholm or America/New_York. Empty is UTC. Daylight saving is followed: '0 6 * * *' is six in the morning there all year.")]
        public Input<string?> TimeZone { get; set; } = null!;

        public static string Normalize(string cron) => string.Join(' ', cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        /// <summary>A zone as the stimulus carries it: null for UTC (nothing given, "UTC", "Etc/UTC"), else the name as given, trimmed.</summary>
        public static string? NormalizeZone(string? name) =>
            string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), "UTC", StringComparison.OrdinalIgnoreCase) || string.Equals(name.Trim(), "Etc/UTC", StringComparison.OrdinalIgnoreCase)
                ? null
                : name.Trim();

        /// <summary>The stimulus for a cron and a zone (null: UTC) - as it is indexed at publish and as the schedules task fires it.</summary>
        public static object Stimulus(string cron, string? zone) =>
            NormalizeZone(zone) is { } named ? new ZonedScheduleStimulus(cron, named) : new ScheduleStimulus(cron);

        /// <summary>The zone a schedule is read in - UTC when none is given - or null with the reason.</summary>
        public static TimeZoneInfo? FindZone(string? name, out string? error)
        {
            error = null;
            var normalized = NormalizeZone(name);
            if (normalized is null)
            {
                return TimeZoneInfo.Utc;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(normalized);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                error = $"'{name}' is not a time zone this server knows - use an IANA name such as Europe/Stockholm, or leave it empty for UTC.";
                return null;
            }
        }

        /// <summary>The parsed expression, or null with the reason.</summary>
        public static CronExpression? TryParse(string? cron, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(cron))
            {
                error = "The schedule needs a cron expression.";
                return null;
            }

            try
            {
                return CronExpression.Parse(Normalize(cron), CronFormat.Standard);
            }
            catch (CronFormatException ex)
            {
                error = $"'{cron}' is not a five-field cron expression: {ex.Message}";
                return null;
            }
        }

        // One payload a cron line: one line is indexed exactly as before, several fire each on its own.
        protected override IEnumerable<object> GetTriggerPayloads(TriggerIndexingContext context)
        {
            var cron = context.Get(Cron);
            if (TryParseAll(cron, out var error) is null)
            {
                throw new InvalidOperationException(error);
            }

            var zone = TimeZone is null ? null : context.Get(TimeZone);
            // A schedule with no zone of its own is read in the pipeline's (n8n's workflow timezone) - UTC when it has none.
            zone = NormalizeZone(zone) ?? Application.PipelineGraphCodec.TimeZone(context.PipelineIndexingContext.Pipeline.Root.Metadata);
            if (FindZone(zone, out var zoneError) is null)
            {
                throw new InvalidOperationException(zoneError);
            }

            return Lines(cron).Select(line => Stimulus(line, NormalizeZone(zone))).ToList();
        }

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself (measured - the run sat Suspended).
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var firedAt = context.TryGetPipelineInput<string>(FiredAtInputKey, out var value) ? value : DateTimeOffset.UtcNow.ToString("O");
            context.SetResult(firedAt);
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}
