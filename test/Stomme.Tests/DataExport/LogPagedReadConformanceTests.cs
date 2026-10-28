#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Models;
using Stomme.Modules.DataExport.Sources;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// BL-85 (D108 refutation 5) - the keyset-paged log read, and the built-in event-log source over it, on a really
    /// installed SQLite, PostgreSQL and RavenDB site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture is built around the failure a keyset on <c>Id</c> exists to prevent: a burst of entries sharing ONE
    /// <c>LogDate</c>, sized so that it straddles several page boundaries. A cursor on <c>LogDate</c> would repeat
    /// those entries or drop them; the walk here must return every id exactly once.
    /// </para>
    /// <para>
    /// Everything is planted in 2001, far from the entries a starting host writes about itself, so the counts are
    /// exact rather than "at least". The conformance project's EF fixtures are both SQLite and cannot see
    /// PostgreSQL's answer; this suite runs the same assertions on all three real providers instead.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class LogPagedReadConformanceTests
    {
        private const string Host = "localhost";
        private static readonly DateTime From = new(2001, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime To = new(2001, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Burst = new(2001, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        private readonly ITestOutputHelper _output;

        public LogPagedReadConformanceTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("sqlite")]
        [InlineData("postgres")]
        [InlineData("raven")]
        public async Task The_walk_returns_every_entry_exactly_once_whatever_shares_an_instant(string provider)
        {
            IGateStore store = provider switch
            {
                "sqlite" => new SqliteGateStore(),
                "postgres" => new PostgresGateStore(),
                _ => new RavenGateStore(),
            };

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                // A REAL second site: the relational providers enforce Log.SiteId as a foreign key (RavenDB has none), so a
                // made-up id would fail the fixture on two providers out of three for a reason unrelated to the read.
                var otherSiteId = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                    {
                        Name = "Log Paging Site B", TenantId = alias.TenantId,
                        DefaultThemeType = Constants.DefaultTheme, DefaultContainerType = Constants.DefaultContainer,
                        AdminContainerType = Constants.DefaultAdminContainer, SiteTemplateType = Constants.DefaultSiteTemplate,
                        RenderMode = RenderModes.Static, Runtime = Runtimes.Server, Prerender = false, Hybrid = false,
                        EnhancedNavigation = true, CultureCode = "en", AllowRegistration = false, CaptureBrokenUrls = false,
                        VisitorTracking = false, PwaIsEnabled = false,
                    })).SiteId);
                Assert.NotEqual(siteId, otherSiteId);

                var planted = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var logs = sp.GetRequiredService<ILogRepository>();
                    var ids = new List<int>();
                    async Task Plant(int? site, DateTime at, string level, string message)
                    {
                        var entry = new Log
                        {
                            SiteId = site, LogDate = at, Url = "/", Server = "web-01", Category = "Stomme.Tests", Feature = "Paging",
                            Function = "Other", Level = level, Message = message, MessageTemplate = message,
                        };
                        await logs.AddLogAsync(entry);
                        if (site == siteId && at >= From && at < To)
                        {
                            ids.Add(entry.LogId);
                        }
                    }

                    // 30 spread entries, then 130 in ONE instant (straddling the 50-row pages twice over), then 30 more.
                    for (var i = 0; i < 30; i++) await Plant(siteId, From.AddMinutes(i), i % 3 == 0 ? "Error" : "Information", "early " + i);
                    for (var i = 0; i < 130; i++) await Plant(siteId, Burst, i % 2 == 0 ? "Error" : "Warning", "burst " + i);
                    for (var i = 0; i < 30; i++) await Plant(siteId, Burst.AddHours(1).AddSeconds(i), "Information", "late " + i);

                    // The edges of a HALF-OPEN range, another site, and no site at all.
                    await Plant(siteId, From, "Critical", "exactly at fromUtc - IN");
                    await Plant(siteId, To, "Critical", "exactly at toUtc - OUT");
                    await Plant(siteId, From.AddTicks(-TimeSpan.TicksPerSecond), "Critical", "a second before - OUT");
                    await Plant(otherSiteId, Burst, "Error", "another site - OUT");
                    await Plant(null, Burst, "Error", "no site - OUT");
                    return ids;
                });

                Assert.Equal(191, planted.Count);

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var logs = sp.GetRequiredService<ILogRepository>();

                    async Task<List<Log>> WalkAsync(string? level, int pageSize)
                    {
                        var all = new List<Log>();
                        var afterId = 0;
                        for (var guard = 0; guard < 1000; guard++)
                        {
                            var page = await logs.GetLogsPageAsync(siteId, From, To, level, afterId, pageSize);
                            Assert.True(page.Count <= pageSize, $"a page of {page.Count} for a take of {pageSize}");
                            if (page.Count == 0)
                            {
                                return all;
                            }

                            all.AddRange(page);
                            afterId = page[^1].LogId;
                        }

                        throw new InvalidOperationException("the walk did not terminate");
                    }

                    foreach (var pageSize in new[] { 50, 7, 1000 })
                    {
                        var walked = await WalkAsync(null, pageSize);
                        _output.WriteLine($"[{provider}] page size {pageSize}: {walked.Count} entries, {walked.Select(e => e.LogId).Distinct().Count()} distinct");

                        // Every id once: nothing the shared instant straddles is lost or repeated.
                        Assert.Equal(planted.OrderBy(i => i), walked.Select(e => e.LogId).OrderBy(i => i));
                        // In Id order, which is what makes "after the last id" a cursor at all.
                        Assert.Equal(walked.Select(e => e.LogId).OrderBy(i => i), walked.Select(e => e.LogId));
                        Assert.All(walked, e => Assert.Equal(siteId, e.SiteId));
                        Assert.DoesNotContain(walked, e => e.Message.EndsWith("OUT", StringComparison.Ordinal));
                        Assert.Contains(walked, e => e.Message == "exactly at fromUtc - IN");
                    }

                    // The level filter, in its exact stored spelling: 10 early + 65 burst.
                    var errors = await WalkAsync("Error", 50);
                    Assert.Equal(75, errors.Count);
                    Assert.All(errors, e => Assert.Equal("Error", e.Level));
                    Assert.Single(await WalkAsync("Critical", 50));
                    Assert.Empty(await WalkAsync("Trace", 50));

                    Assert.Empty(await logs.GetLogsPageAsync(siteId, From, To, null, 0, 0));
                    // The other site sees ITS one row and none of this site's 191.
                    Assert.Equal("another site - OUT", Assert.Single(await logs.GetLogsPageAsync(otherSiteId, From, To, null, 0, 50)).Message);
                    Assert.Empty(await logs.GetLogsPageAsync(otherSiteId, From, To, "Warning", 0, 50));
                    Assert.Empty(await logs.GetLogsPageAsync(siteId, From, To, null, planted.Max(), 50));

                    // ---- the SOURCE, end to end: the same rows, as a table, through its own paging ----
                    var source = new EventLogExportSource(logs);
                    var context = new ExportSourceContext(siteId, 1, "{\"fromUtc\":\"2001-03-01T00:00:00Z\",\"toUtc\":\"2001-03-02T00:00:00Z\",\"level\":\"warning\"}");
                    Assert.Null(source.Validate(context));

                    await using var document = await source.OpenAsync(context);
                    var rows = new List<object?[]>();
                    await foreach (var table in document.ReadTablesAsync())
                    {
                        Assert.Equal("Event Log", table.Name);
                        await foreach (var row in table.ReadRowsAsync())
                        {
                            Assert.Equal(table.Columns.Count, row.Length);
                            rows.Add(row);
                        }
                    }

                    // "warning" was canonicalised to the stored "Warning": 65 burst rows, on EVERY provider.
                    Assert.Equal(65, rows.Count);
                    Assert.All(rows, row => Assert.Equal("Warning", row[2]));
                    Assert.All(rows, row => Assert.Equal(DateTimeKind.Utc, ((DateTime)row[1]!).Kind));
                    Assert.All(rows, row => Assert.Equal(Burst, (DateTime)row[1]!));
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- the arguments: no host needed ---------------------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{}")]
        [InlineData("{\"level\":\"ERROR\"}")]
        [InlineData("{\"level\":null}")]
        [InlineData("{\"fromUtc\":\"2026-01-01T00:00:00Z\",\"toUtc\":\"2026-02-01T00:00:00Z\"}")]
        public void Acceptable_arguments_validate(string? json)
        {
            Assert.Null(new EventLogExportSource(ThrowawayLogRepository.Instance).Validate(new ExportSourceContext(1, 1, json)));
        }

        [Theory]
        [InlineData("not json", "not valid JSON")]
        [InlineData("[]", "JSON object")]
        [InlineData("{\"siteId\":2}", "not an argument")]
        [InlineData("{\"SiteId\":2}", "not an argument")]
        [InlineData("{\"level\":\"Shouty\"}", "level must be one of")]
        [InlineData("{\"level\":5}", "level must be one of")]
        [InlineData("{\"fromUtc\":\"yesterday\"}", "fromUtc")]
        [InlineData("{\"fromUtc\":\"2026-02-01T00:00:00Z\",\"toUtc\":\"2026-01-01T00:00:00Z\"}", "earlier than")]
        [InlineData("{\"fromUtc\":\"2020-01-01T00:00:00Z\",\"toUtc\":\"2026-01-01T00:00:00Z\"}", "at most")]
        public void Unacceptable_arguments_are_REJECTED_with_a_reason_and_an_unknown_member_is_never_ignored(string json, string expected)
        {
            // {"siteId": 2} in particular: ignoring it would export the caller's OWN site and look like it worked.
            var error = new EventLogExportSource(ThrowawayLogRepository.Instance).Validate(new ExportSourceContext(1, 1, json));

            Assert.NotNull(error);
            Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_source_declares_what_the_registry_demands()
        {
            var source = new EventLogExportSource(ThrowawayLogRepository.Instance);

            Assert.Equal("stomme.log", source.Key);
            Assert.True(ExportSourceKeys.IsCanonical(source.Key));
            Assert.Equal(ExportPermissions.Log, source.RequiredPermission);
            Assert.Equal(TableCardinality.Single, source.TableCardinality);
        }
    }

    internal static class ThrowawayLogRepository
    {
        /// <summary>Validation never touches the repository; this stands in so the constructor's null check is met.</summary>
        public static readonly ILogRepository Instance = new Unused();

        private sealed class Unused : ILogRepository
        {
            public Task<IEnumerable<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => throw new NotSupportedException();

            public Task<IReadOnlyList<Log>> GetLogsPageAsync(int siteId, DateTime fromUtc, DateTime toUtc, string exactLevel, int afterId, int take) => throw new NotSupportedException();

            public Task<Log> GetLogAsync(int logId) => throw new NotSupportedException();

            public Task AddLogAsync(Log log) => throw new NotSupportedException();

            public Task<int> DeleteLogsAsync(int siteId, int age) => throw new NotSupportedException();
        }
    }
}
