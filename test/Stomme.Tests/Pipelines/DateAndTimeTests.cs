#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - Date &amp; time (n8n's Date &amp; Time node), run by the engine as the host composes it with a fixed clock: Add and
    /// Subtract by calendar (a month from the 31st, a day across a clock change in a zone keeps its time of day), Format (iso,
    /// date, time, unix, a pattern, in a zone), Difference (whole months and years, other units to two decimals, either way),
    /// Start of (a Monday week, a month in a zone), an empty date as now, a Unix time read, and what it refuses saying why.
    /// </summary>
    public sealed class DateAndTimeTests : IAsyncLifetime
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 6, 7, 30, 15, TimeSpan.Zero);

        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new Silent());
                module.AddStepsFrom<PipelineStepsMarker>();
            });
            services.AddSingleton<ISystemClock>(new FixedClock());
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class Silent : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => TextWriter.Null;
        }

        private sealed class FixedClock : ISystemClock
        {
            public DateTimeOffset UtcNow => Now;
        }

        private async Task<RunPipelineResult> RunAsync(DateAndTime step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private async Task<string> ResultOf(DateOperation operation, string? date, string? duration = null, string? format = null, string? zone = null, string? other = null, DateUnit unit = DateUnit.Days)
        {
            var run = await RunAsync(new DateAndTime
            {
                Operation = new(operation), Date = new(date), Duration = new(duration), Format = new(format), TimeZone = new(zone), OtherDate = new(other), Unit = new(unit),
            });
            Assert.True(run.PipelineState.SubStatus == PipelineSubStatus.Finished, string.Join(" | ", run.PipelineState.Incidents.Select(i => i.Message)));
            return (string)run.Journal.StepExecutionContexts.Single(c => c.Step is DateAndTime).GetOutputs()["Result"];
        }

        private async Task<string> FaultOf(DateOperation operation, string? date, string? duration = null, string? format = null, string? zone = null)
        {
            var run = await RunAsync(new DateAndTime { Operation = new(operation), Date = new(date), Duration = new(duration), Format = new(format), TimeZone = new(zone) });
            Assert.Equal(PipelineSubStatus.Faulted, run.PipelineState.SubStatus);
            return string.Join(" | ", run.PipelineState.Incidents.Select(i => i.Message));
        }

        [Fact]
        public async Task Add_and_subtract_go_by_the_calendar_and_keep_the_time_of_day_across_a_clock_change()
        {
            Assert.Equal("2026-03-03T10:00:00Z", await ResultOf(DateOperation.Add, "2026-01-31T10:00:00Z", "1 month 3 days")); // the 31st, a month on, is the 28th
            Assert.Equal("2026-10-25T10:00:00Z", await ResultOf(DateOperation.Add, "2026-10-24T10:00:00Z", "1 day"));
            Assert.Equal("2026-10-25T11:00:00Z", await ResultOf(DateOperation.Add, "2026-10-24T10:00:00Z", "1 day", zone: "Europe/Stockholm")); // noon to noon over the change
            Assert.Equal("2026-10-06T06:00:15Z", await ResultOf(DateOperation.Subtract, "", "90 minutes")); // empty is now
            Assert.Equal("2025-10-06T07:30:15.500Z", await ResultOf(DateOperation.Subtract, "2026-10-06T07:30:16Z", "1 year, 500 ms"));
        }

        [Fact]
        public async Task Format_writes_the_date_as_asked_in_a_zone_or_in_utc()
        {
            Assert.Equal("2026-10-06T07:30:15Z", await ResultOf(DateOperation.Format, null));
            Assert.Equal("2026-10-06T09:30:15+02:00", await ResultOf(DateOperation.Format, "2026-10-06T07:30:15Z", zone: "Europe/Stockholm"));
            Assert.Equal("2026-10-06", await ResultOf(DateOperation.Format, "2026-10-06T23:30:00+00:00", format: "date"));
            Assert.Equal("2026-10-07", await ResultOf(DateOperation.Format, "2026-10-06T23:30:00Z", format: "date", zone: "Europe/Stockholm"));
            Assert.Equal("09:30:15", await ResultOf(DateOperation.Format, "2026-10-06T07:30:15Z", format: "time", zone: "Europe/Stockholm"));
            Assert.Equal("06 Oct 2026 09:30", await ResultOf(DateOperation.Format, "2026-10-06T07:30:15Z", format: "dd MMM yyyy HH:mm", zone: "Europe/Stockholm"));
            Assert.Equal(Now.ToUnixTimeSeconds().ToString(), await ResultOf(DateOperation.Format, "2026-10-06T07:30:15Z", format: "unix"));
            Assert.Equal(Now.ToUnixTimeMilliseconds().ToString(), await ResultOf(DateOperation.Format, Now.ToUnixTimeSeconds().ToString(), format: "unix-ms")); // a Unix time read
            Assert.Equal("2026-10-06T07:30:15Z", await ResultOf(DateOperation.Format, Now.ToUnixTimeMilliseconds().ToString()));
        }

        [Fact]
        public async Task Difference_counts_whole_months_and_years_and_the_rest_to_two_decimals_either_way()
        {
            Assert.Equal("5.5", await ResultOf(DateOperation.Difference, "2026-10-01", other: "2026-10-06T12:00:00Z"));
            Assert.Equal("-5.5", await ResultOf(DateOperation.Difference, "2026-10-06T12:00:00Z", other: "2026-10-01"));
            Assert.Equal("132", await ResultOf(DateOperation.Difference, "2026-10-06T07:00:00Z", other: "2026-10-11T19:00:00Z", unit: DateUnit.Hours));
            Assert.Equal("1", await ResultOf(DateOperation.Difference, "2026-01-31", other: "2026-03-30", unit: DateUnit.Months)); // the 31st to the 30th: not two
            Assert.Equal("6", await ResultOf(DateOperation.Difference, "2020-02-29", other: "2026-02-28", unit: DateUnit.Years));
            Assert.Equal("0.33", await ResultOf(DateOperation.Difference, "2026-10-06", other: "2026-10-06T08:00:00Z", unit: DateUnit.Days));
        }

        [Fact]
        public async Task Start_of_takes_a_monday_week_and_a_month_in_the_zones_calendar()
        {
            Assert.Equal("2026-10-05T00:00:00Z", await ResultOf(DateOperation.StartOf, "2026-10-08T15:00:00Z", unit: DateUnit.Weeks)); // a Thursday's Monday
            Assert.Equal("2026-09-30T22:00:00Z", await ResultOf(DateOperation.StartOf, "2026-10-06T07:30:15Z", zone: "Europe/Stockholm", unit: DateUnit.Months));
            Assert.Equal("2026-10-06T00:00:00Z", await ResultOf(DateOperation.StartOf, null));
            Assert.Equal("2026-10-06T07:00:00Z", await ResultOf(DateOperation.StartOf, null, unit: DateUnit.Hours));
            Assert.Equal("2026-01-01T00:00:00Z", await ResultOf(DateOperation.StartOf, null, unit: DateUnit.Years));
        }

        [Fact]
        public async Task It_refuses_what_it_cannot_read_saying_why()
        {
            Assert.Contains("'soon' is not a date", await FaultOf(DateOperation.Format, "soon"));
            Assert.Contains("'2 fortnights' is not a unit", await FaultOf(DateOperation.Add, null, "2 fortnights"));
            Assert.Contains("is not a duration", await FaultOf(DateOperation.Add, null, "later"));
            Assert.Contains("is not a time zone", await FaultOf(DateOperation.Format, null, zone: "Mars/Olympus"));
            Assert.Contains("is not a date pattern", await FaultOf(DateOperation.Format, null, format: "%"));

            Assert.True(DateAndTime.TryReadDuration("1 year 2 months 3 weeks 4 days 5 hours 6 minutes 7 seconds 8 ms", out var duration, out _));
            Assert.Equal((1, 2, 25d), (duration.Years, duration.Months, duration.Days));
            Assert.Equal(new TimeSpan(0, 5, 6, 7, 8), duration.Time);
        }
    }
}
