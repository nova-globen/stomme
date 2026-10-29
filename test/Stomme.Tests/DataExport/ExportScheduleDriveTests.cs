#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// A recurring export, driven over HTTP and through the real export queue job:
    /// authored only by someone who may make the export now; each run an ordinary export requested AS THE CREATOR with the
    /// schedule's id, missed occurrences collapsed into one; the creator's access revoked - the next run PAUSES the schedule and
    /// queues nothing; a manager takes it over and resumes it - the next run is requested as the manager; someone else cannot
    /// touch it; the creator deletes it.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class ExportScheduleDriveTests
    {
        private const string Host = "localhost";
        private const string Api = "/api/DataExport";

        private readonly ITestOutputHelper _output;

        protected ExportScheduleDriveTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        [Fact]
        public async Task A_schedule_runs_as_its_owner_pauses_when_they_lose_access_and_runs_as_whoever_takes_it_over()
        {
            var clock = new ShiftableClock();
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["DataExport"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, _output, builder =>
            {
                builder.Services.AddSingleton<IExportSource>(new RowsSource("test.rows", "export:rows"));
                builder.Services.AddSingleton<IExportSource>(new RowsSource("test.secret", "export:secret"));
                builder.Services.AddSingleton<IExportClock>(clock);
            });

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var exporters = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var managers = await GateDrive.EnsureRoleAsync(app, Host, "Export Managers");
                var owner = await GateDrive.AddUserAsync(app, Host, "sch-owner", "Own3r!Pass$2026", "sch-owner@gate.test", "Exporters");
                var other = await GateDrive.AddUserAsync(app, Host, "sch-other", "0ther!Pass$2026x", "sch-other@gate.test", "Exporters");
                var manager = await GateDrive.AddUserAsync(app, Host, "sch-manager", "Manag3r!Pass$2026", "sch-manager@gate.test", "Export Managers");
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    foreach (var (role, operation) in new[] { (exporters, "create"), (exporters, "view"), (exporters, "rows"), (managers, "create"), (managers, "view"), (managers, "manage"), (managers, "rows") })
                    {
                        await permissions.AddPermissionAsync(new Permission
                        {
                            SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                            PermissionName = operation, RoleId = role.RoleId, IsAuthorized = true,
                        });
                    }

                    return true;
                });

                var asOwner = await SessionAsync(app, "sch-owner", "Own3r!Pass$2026", owner.UserId);
                var asOther = await SessionAsync(app, "sch-other", "0ther!Pass$2026x", other.UserId);
                var asManager = await SessionAsync(app, "sch-manager", "Manag3r!Pass$2026", manager.UserId);

                // ---- authored only as the request path would allow ----------------------------------------------------
                async Task Refused(object body, string label, string reason)
                {
                    var response = await PostAsync(app, asOwner, $"{Api}/schedules", body, label);
                    Assert.Equal(HttpStatusCode.BadRequest, response.Status);
                    Assert.Contains(reason, response.Body, StringComparison.OrdinalIgnoreCase);
                }

                await Refused(Schedule("Too often", "*/5 * * * *", "test.rows"), "every five minutes", "at least 15 minutes");
                await Refused(Schedule("Bad cron", "every day", "test.rows"), "not a cron", "five-field");
                await Refused(Schedule("Secret", "0 6 * * *", "test.secret"), "data the owner may not read", "does not exist");

                var created = await PostAsync(app, asOwner, $"{Api}/schedules", Schedule("Daily rows", "0 6 * * *", "test.rows"), "owner schedules");
                Assert.Equal(HttpStatusCode.OK, created.Status);
                var scheduleId = JsonNode.Parse(created.Body)!["id"]!.GetValue<Guid>();

                // ---- missed occurrences collapse: three days late is ONE run, requested as the creator -----------------
                clock.Shift(TimeSpan.FromDays(3));
                await RunQueueAsync(app);
                var (schedule, jobs) = await SettledAsync(app, scheduleId, expectedJobs: 1);
                var first = Assert.Single(jobs);
                Assert.Equal(owner.UserId, first.RequesterUserId);
                Assert.Equal(first.Id, schedule!.LastJobId);
                Assert.False(schedule.IsDue(clock.UtcNow));

                // ---- someone else may not touch it; the creator's list shows it ----------------------------------------
                Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, asOther, $"{Api}/schedules/{scheduleId}/pause", null, "other pauses")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app, asOther, HttpMethod.Delete, $"{Api}/schedules/{scheduleId}", "other deletes")).Status);
                Assert.Contains("Daily rows", (await GateDrive.GetAsync(asOwner.Client, asOwner.Cookies, Host, $"{Api}/schedules", _output, "owner lists")).Body);
                Assert.DoesNotContain("Daily rows", (await GateDrive.GetAsync(asOther.Client, asOther.Cookies, Host, $"{Api}/schedules", _output, "other lists")).Body);

                // ---- the creator loses the data permission: the next run PAUSES the schedule and queues nothing --------
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    var rows = (await permissions.GetPermissionsAsync(siteId, ExportPermissions.Resource, PermissionRowAccessGuard.KeyEntityId, "rows"))
                        .Where(row => row.RoleId == exporters.RoleId).ToList();
                    foreach (var row in rows)
                    {
                        await permissions.DeletePermissionAsync(row.PermissionId);
                    }

                    return true;
                });
                clock.Shift(TimeSpan.FromDays(1));
                await RunQueueAsync(app);
                (schedule, jobs) = await StateAsync(app, scheduleId);
                Assert.True(schedule!.IsPaused, "the owner lost the data permission, so the run must pause the schedule");
                Assert.Equal(ExportSchedulePauseReason.OwnerNotAllowed, schedule.PauseReason);
                Assert.Single(jobs);

                // ---- a manager takes it over and resumes it: the next run is requested as the manager ------------------
                Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, asManager, $"{Api}/schedules/{scheduleId}/takeover", null, "manager takes over")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, asManager, $"{Api}/schedules/{scheduleId}/resume", null, "manager resumes")).Status);
                clock.Shift(TimeSpan.FromDays(1));
                await RunQueueAsync(app);
                (schedule, jobs) = await SettledAsync(app, scheduleId, expectedJobs: 2);
                Assert.False(schedule!.IsPaused);
                Assert.Equal(manager.UserId, schedule.OwnerUserId);
                Assert.Equal(2, jobs.Count);
                Assert.Equal(manager.UserId, jobs.Single(job => job.Id == schedule.LastJobId).RequesterUserId);

                // ---- the original owner no longer owns it; the manager deletes it -------------------------------------
                Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, asOwner, $"{Api}/schedules/{scheduleId}/pause", null, "former owner pauses")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(app, asManager, HttpMethod.Delete, $"{Api}/schedules/{scheduleId}", "manager deletes")).Status);
                (schedule, _) = await StateAsync(app, scheduleId);
                Assert.Null(schedule);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static object Schedule(string name, string cron, string source) =>
            new { Name = name, Cron = cron, Request = new { SourceKey = source, Format = "Csv" } };

        // The real job, as the scheduler runs it; the host is started, so its own per-minute job may run beside this one -
        // the schedule's claim keeps an occurrence to one export either way.
        private Task RunQueueAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                _output.WriteLine("[job] " + await new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                return true;
            });

        // The live job may hold the claim: it records the run before it adds the export, so wait for the export to land.
        private static async Task<(ExportScheduleAggregate? Schedule, List<ExportJobAggregate> Jobs)> SettledAsync(WebApplication app, Guid scheduleId, int expectedJobs)
        {
            var state = await StateAsync(app, scheduleId);
            for (var attempt = 0; attempt < 60 && (state.Jobs.Count < expectedJobs || state.Jobs.All(job => job.Id != state.Schedule?.LastJobId)); attempt++)
            {
                await Task.Delay(500);
                state = await StateAsync(app, scheduleId);
            }

            return state;
        }

        private static Task<(ExportScheduleAggregate? Schedule, List<ExportJobAggregate> Jobs)> StateAsync(WebApplication app, Guid scheduleId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var schedule = await sp.GetRequiredService<IExportScheduleRepository>().GetForSiteAsync(scheduleId, alias.SiteId);
                var jobs = (await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(alias.SiteId)).Where(job => job.ScheduleId == scheduleId).ToList();
                return (schedule, jobs);
            });

        private sealed record Session(HttpClient Client, System.Net.CookieContainer? Cookies, int? UserId);

        private async Task<Session> SessionAsync(WebApplication app, string username, string password, int userId)
        {
            var client = GateDrive.NewClient(app, Host);
            return new Session(client, await GateDrive.LoginAsync(app, client, Host, username, password, _output), userId);
        }

        private Task<(HttpStatusCode Status, string Body)> PostAsync(WebApplication app, Session who, string path, object? body, string label) =>
            GateDrive.SendJsonAsync(app, who.Client, who.Cookies, Host, who.UserId, HttpMethod.Post, path, body, _output, label);

        private Task<(HttpStatusCode Status, string Body)> SendAsync(WebApplication app, Session who, HttpMethod method, string path, string label) =>
            GateDrive.SendJsonAsync(app, who.Client, who.Cookies, Host, who.UserId, method, path, null, _output, label);

        private sealed class ShiftableClock : IExportClock
        {
            private long _offsetTicks;

            public DateTime UtcNow => DateTime.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

            public void Shift(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
        }

        private sealed class RowsSource(string key, string permission) : IExportSource
        {
            public string Key => key;

            public string RequiredPermission => permission;

            public TableCardinality TableCardinality => TableCardinality.Single;

            public string? Validate(ExportSourceContext context) => null;

            public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default) =>
                Task.FromResult<ITabularDocument>(new Document());

            private sealed class Document : ITabularDocument
            {
                public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    yield return new Table();
                    await Task.CompletedTask;
                }

                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }

            private sealed class Table : ITabularTable
            {
                public string Name => "Rows";

                public IReadOnlyList<ExportColumn> Columns { get; } = new[] { new ExportColumn("N", ExportValueKind.Integer) };

                public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    yield return new object?[] { 1 };
                    await Task.CompletedTask;
                }
            }
        }
    }

    public sealed class SqliteExportScheduleDriveTests(ITestOutputHelper output) : ExportScheduleDriveTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresExportScheduleDriveTests(ITestOutputHelper output) : ExportScheduleDriveTests(output)
    {
        private protected override IGateStore Store() => new PostgresGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenExportScheduleDriveTests(ITestOutputHelper output) : ExportScheduleDriveTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();
    }
}
