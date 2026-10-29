#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What a site's Host users are mailed of its error log is held back when it repeats (found in a real burst: every
    /// anonymous probe a permission key refused mailed the host, and the mail relay allows about 300 mails a day): the same
    /// kind is told once an hour, a site at most ten an hour, and the next telling of a kind says how many were held back -
    /// while every entry is still written to the site's log.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class LogNotificationThrottleTests(ITestOutputHelper output)
    {
        private sealed class ManualClock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

            public override DateTimeOffset GetUtcNow() => Now;
        }

        [Fact]
        public void A_kind_is_told_once_an_hour_and_the_next_telling_says_how_many_were_held_back()
        {
            var clock = new ManualClock();
            var throttle = new LogNotificationThrottle(clock);
            const string refused = "User {User} Does Not Hold Permission Key {Key}";

            Assert.True(throttle.ShouldTell(1, "Error", refused, out var heldBack));
            Assert.Equal(0, heldBack);
            clock.Now = clock.Now.AddMinutes(10);
            Assert.False(throttle.ShouldTell(1, "Error", refused, out _));
            Assert.False(throttle.ShouldTell(1, "Error", refused, out _));
            Assert.True(throttle.ShouldTell(2, "Error", refused, out _)); // another site's is its own
            Assert.True(throttle.ShouldTell(1, "Critical", refused, out _)); // so is another level
            Assert.True(throttle.ShouldTell(1, "Error", "Another {Thing} Failed", out _)); // and another kind

            clock.Now = clock.Now.AddMinutes(50); // an hour after the first telling
            Assert.True(throttle.ShouldTell(1, "Error", refused, out heldBack));
            Assert.Equal(2, heldBack);
            clock.Now = clock.Now.AddMinutes(61);
            Assert.True(throttle.ShouldTell(1, "Error", refused, out heldBack));
            Assert.Equal(0, heldBack); // counted afresh once told
        }

        [Fact]
        public void A_site_is_told_of_at_most_ten_entries_an_hour_whatever_they_are()
        {
            var clock = new ManualClock();
            var throttle = new LogNotificationThrottle(clock);
            var told = Enumerable.Range(0, 15).Count(at => throttle.ShouldTell(1, "Error", "Kind " + at, out _));
            Assert.Equal(LogNotificationThrottle.MaxPerWindow, told);
            Assert.True(throttle.ShouldTell(2, "Error", "Kind 0", out _)); // another site has its own ten

            clock.Now = clock.Now.AddHours(1);
            Assert.True(throttle.ShouldTell(1, "Error", "Kind 14", out var heldBack)); // the hour gone, told - with its own held back
            Assert.Equal(1, heldBack);
        }

        // The drain itself, on an installed host: one refusal logged three times is three rows in the log and one mail queued
        // for the host.
        [Fact]
        public async Task An_error_logged_three_times_is_logged_three_times_and_mailed_once()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var siteId = (await GateDrive.AliasAsync(app, "localhost")).SiteId;
                var marker = "throttle-" + Guid.NewGuid().ToString("N");
                await GateDrive.InTenantAsync(app, "localhost", (sp, alias) =>
                {
                    var logs = sp.GetRequiredService<ILogManager>();
                    for (var at = 0; at < 3; at++)
                    {
                        logs.Log(siteId, Shared.LogLevel.Error, this, LogFunction.Security, "Probe {Marker} Refused {At}", marker, at);
                    }

                    return Task.FromResult(true);
                });

                int written = 0, mailed = 0;
                for (var attempt = 0; attempt < 100 && written < 3; attempt++)
                {
                    await Task.Delay(100);
                    (written, mailed) = await GateDrive.InTenantAsync(app, "localhost", async (sp, alias) =>
                    {
                        var rows = (await sp.GetRequiredService<ILogRepository>().GetLogsAsync(siteId, "Error", null, 200)).Count(log => log.Message.Contains(marker, StringComparison.Ordinal));
                        var queued = (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(siteId, -1, -1)).Count(note => note.Body.Contains(marker, StringComparison.Ordinal));
                        return (rows, queued);
                    });
                }

                Assert.Equal((3, 1), (written, mailed));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
