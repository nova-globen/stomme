#nullable enable
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What <see cref="DateAndTime"/> does with its date.</summary>
    public enum DateOperation
    {
        /// <summary>The date plus a duration.</summary>
        Add,

        /// <summary>The date less a duration.</summary>
        Subtract,

        /// <summary>The date written as text.</summary>
        Format,

        /// <summary>How much time lies from the date to the other date, in the unit.</summary>
        Difference,

        /// <summary>The start of the date's second, minute, hour, day, week (Monday), month or year.</summary>
        StartOf,
    }

    /// <summary>The unit a difference is counted in, or the period whose start is taken.</summary>
    public enum DateUnit
    {
        Seconds,
        Minutes,
        Hours,
        Days,
        Weeks,
        Months,
        Years,
    }

    /// <summary>
    /// Date &amp; time (n8n's Date &amp; Time node): add a duration to a date or take one from it, write it as text, count
    /// the time between two, or take the start of its day, week, month or year. A date is ISO 8601 (one without an offset is
    /// UTC), or a Unix time in seconds or milliseconds; empty, it is now - the engine's clock, as <c>{{ $now }}</c> is. Days,
    /// months and years are calendar ones: in a time zone, adding a day across a clock change keeps the time of day. The result
    /// is a date as ISO 8601 in UTC (to the second, or the millisecond when it has one), or for Format the text asked for, or
    /// for Difference a number - whole months or years, other units to two decimals.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Add to a date or take from it, format it, count the time between two, or take the start of its day, week, month or year.", DisplayName = "Date & time")]
    public class DateAndTime : DataStep
    {
        private static readonly Regex Part = new(@"(?<amount>\d+(?:[.,]\d+)?)\s*(?<unit>[a-z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [Input(Description = "Add | Subtract | Format | Difference | StartOf.", DefaultValue = DateOperation.Format, Order = 1)]
        public Input<DateOperation> Operation { get; set; } = new(new Literal<DateOperation>(DateOperation.Format));

        [Input(Description = "The date: 2026-10-06, 2026-10-06T09:30:00Z, a Unix time, or an expression such as {{ $now }}. Empty: now.", Order = 2)]
        public Input<string?>? Date { get; set; }

        [Input(Description = "Add and Subtract: how much - 2 days, 1 month 3 days, 90 minutes, 1 year.", Order = 3)]
        public Input<string?>? Duration { get; set; }

        [Input(Description = "Format: iso, date, time, unix, unix-ms, or a pattern such as dd MMM yyyy HH:mm. Empty: iso.", Order = 4)]
        public Input<string?>? Format { get; set; }

        [Input(DisplayName = "Time zone", Description = "The zone the date is read in for Add, Subtract, Format and Start of - an IANA name such as Europe/Stockholm. Empty: UTC.", Order = 5)]
        public Input<string?>? TimeZone { get; set; }

        [Input(DisplayName = "Other date", Description = "Difference: the date counted to.", Order = 6)]
        public Input<string?>? OtherDate { get; set; }

        [Input(Description = "Difference: what to count in. Start of: whose start - the day, the week, the month...", DefaultValue = DateUnit.Days, Order = 7)]
        public Input<DateUnit> Unit { get; set; } = new(new Literal<DateUnit>(DateUnit.Days));

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var now = context.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;
            if (!TryRead(Date?.GetOrDefault(context), now, out var date, out var error))
            {
                await FailAsync(context, "DATE_INVALID", error!).ConfigureAwait(false);
                return;
            }

            var zone = Schedule.FindZone(TimeZone?.GetOrDefault(context), out var zoneError);
            if (zone is null)
            {
                await FailAsync(context, "DATE_INVALID", zoneError!).ConfigureAwait(false);
                return;
            }

            string result;
            switch (Operation.GetOrDefault(context))
            {
                case DateOperation.Add:
                case DateOperation.Subtract:
                    if (!TryReadDuration(Duration?.GetOrDefault(context), out var duration, out error))
                    {
                        await FailAsync(context, "DATE_INVALID", error!).ConfigureAwait(false);
                        return;
                    }

                    result = Write(Shift(date, zone, duration, Operation.GetOrDefault(context) == DateOperation.Subtract ? -1 : 1));
                    break;
                case DateOperation.Difference:
                    if (!TryRead(OtherDate?.GetOrDefault(context), now, out var other, out error))
                    {
                        await FailAsync(context, "DATE_INVALID", "Other date: " + error).ConfigureAwait(false);
                        return;
                    }

                    result = Between(date, other, Unit.GetOrDefault(context)).ToString(CultureInfo.InvariantCulture);
                    break;
                case DateOperation.StartOf:
                    result = Write(StartOf(date, zone, Unit.GetOrDefault(context)));
                    break;
                default:
                    if (!TryFormat(date, zone, Format?.GetOrDefault(context), out result, out error))
                    {
                        await FailAsync(context, "DATE_INVALID", error!).ConfigureAwait(false);
                        return;
                    }

                    break;
            }

            await SucceedAsync(context, result).ConfigureAwait(false);
        }

        /// <summary>A date as the step reads one: empty is now; ISO 8601 (no offset is UTC); a Unix time in seconds (10 digits or fewer) or milliseconds.</summary>
        public static bool TryRead(string? text, DateTimeOffset now, out DateTimeOffset date, out string? error)
        {
            error = null;
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0)
            {
                date = now;
                return true;
            }

            if (Regex.IsMatch(trimmed, @"^-?\d{1,16}$") && long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                try
                {
                    date = trimmed.TrimStart('-').Length > 10 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    date = default;
                    error = $"'{trimmed}' is not a Unix time a date can have.";
                    return false;
                }
            }

            if (PipelineWaits.TryParseMoment(trimmed, out date, out _))
            {
                return true;
            }

            error = $"'{trimmed}' is not a date - write it as 2026-10-06, 2026-10-06T09:30:00Z, or a Unix time.";
            return false;
        }

        /// <summary>A calendar duration: years, months and the rest - "1 year 2 months", "3 days 4 hours", "90 minutes", "2 weeks".</summary>
        public static bool TryReadDuration(string? text, out (int Years, int Months, double Days, TimeSpan Time) duration, out string? error)
        {
            duration = default;
            error = null;
            var trimmed = (text ?? "").Trim();
            var matches = Part.Matches(trimmed);
            var rest = Part.Replace(trimmed, "");
            if (matches.Count == 0 || rest.Trim(' ', ',', '+').Replace("and", "", StringComparison.OrdinalIgnoreCase).Trim(' ', ',').Length > 0)
            {
                error = $"'{trimmed}' is not a duration - write it as 2 days, 1 month 3 days, 90 minutes or 1 year.";
                return false;
            }

            foreach (Match match in matches)
            {
                var amount = double.Parse(match.Groups["amount"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
                var unit = match.Groups["unit"].Value.ToLowerInvariant();
                switch (unit == "ms" ? unit : unit.TrimEnd('s'))
                {
                    case "y" or "yr" or "year":
                        duration.Years += (int)amount;
                        break;
                    case "mo" or "month":
                        duration.Months += (int)amount;
                        break;
                    case "w" or "wk" or "week":
                        duration.Days += amount * 7;
                        break;
                    case "d" or "day":
                        duration.Days += amount;
                        break;
                    case "h" or "hr" or "hour":
                        duration.Time += TimeSpan.FromHours(amount);
                        break;
                    case "m" or "min" or "minute":
                        duration.Time += TimeSpan.FromMinutes(amount);
                        break;
                    case "" or "sec" or "second":
                        duration.Time += TimeSpan.FromSeconds(amount);
                        break;
                    case "ms" or "millisecond":
                        duration.Time += TimeSpan.FromMilliseconds(amount);
                        break;
                    default:
                        error = $"'{match.Value}' is not a unit - use years, months, weeks, days, hours, minutes or seconds.";
                        return false;
                }
            }

            return true;
        }

        // Calendar units on the zone's clock (so a day across a clock change keeps the time of day), the rest as elapsed time.
        private static DateTimeOffset Shift(DateTimeOffset date, TimeZoneInfo zone, (int Years, int Months, double Days, TimeSpan Time) duration, int sign)
        {
            var local = TimeZoneInfo.ConvertTime(date, zone).DateTime;
            local = local.AddYears(sign * duration.Years).AddMonths(sign * duration.Months).AddDays(sign * duration.Days);
            var shifted = new DateTimeOffset(local, zone.GetUtcOffset(local));
            return shifted.Add(sign * duration.Time);
        }

        private static DateTimeOffset StartOf(DateTimeOffset date, TimeZoneInfo zone, DateUnit unit)
        {
            var local = TimeZoneInfo.ConvertTime(date, zone).DateTime;
            var start = unit switch
            {
                DateUnit.Seconds => new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second),
                DateUnit.Minutes => new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0),
                DateUnit.Hours => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0),
                DateUnit.Weeks => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)), // Monday, as ISO 8601 has it
                DateUnit.Months => new DateTime(local.Year, local.Month, 1),
                DateUnit.Years => new DateTime(local.Year, 1, 1),
                _ => local.Date,
            };
            return new DateTimeOffset(start, zone.GetUtcOffset(start));
        }

        // Whole months and years as the calendar counts them; the rest elapsed, to two decimals.
        private static decimal Between(DateTimeOffset from, DateTimeOffset to, DateUnit unit)
        {
            if (unit is DateUnit.Months or DateUnit.Years)
            {
                var sign = to >= from ? 1 : -1;
                var (early, late) = sign > 0 ? (from.UtcDateTime, to.UtcDateTime) : (to.UtcDateTime, from.UtcDateTime);
                var months = (late.Year - early.Year) * 12 + late.Month - early.Month;
                if (early.AddMonths(months) > late)
                {
                    months--;
                }

                return sign * (unit == DateUnit.Years ? months / 12 : months);
            }

            var span = to - from;
            var amount = unit switch
            {
                DateUnit.Seconds => span.TotalSeconds,
                DateUnit.Minutes => span.TotalMinutes,
                DateUnit.Hours => span.TotalHours,
                DateUnit.Weeks => span.TotalDays / 7,
                _ => span.TotalDays,
            };
            return Math.Round((decimal)amount, 2, MidpointRounding.AwayFromZero);
        }

        private static bool TryFormat(DateTimeOffset date, TimeZoneInfo zone, string? format, out string result, out string? error)
        {
            error = null;
            var local = TimeZoneInfo.ConvertTime(date, zone);
            switch ((format ?? "").Trim().ToLowerInvariant())
            {
                case "" or "iso":
                    result = zone == TimeZoneInfo.Utc ? Write(date) : local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                    return true;
                case "date":
                    result = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return true;
                case "time":
                    result = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    return true;
                case "unix":
                    result = date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    return true;
                case "unix-ms":
                    result = date.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                    return true;
            }

            try
            {
                result = local.ToString(format!.Trim(), CultureInfo.InvariantCulture);
                return true;
            }
            catch (FormatException)
            {
                result = string.Empty;
                error = $"'{format}' is not a date pattern - use iso, date, time, unix, unix-ms, or a pattern such as dd MMM yyyy HH:mm.";
                return false;
            }
        }

        // ISO 8601 in UTC, as $now is written - with its milliseconds when it has any.
        private static string Write(DateTimeOffset date)
        {
            var utc = date.ToUniversalTime();
            return utc.ToString(utc.Millisecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
    }
}
