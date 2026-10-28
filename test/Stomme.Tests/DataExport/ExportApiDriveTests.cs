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
    /// BL-85 (D108 item 5; refutations 10, 11 and 14) - the export HTTP surface, driven with real sessions.
    /// </summary>
    /// <remarks>
    /// The question throughout is WHO gets WHAT: a requester reaches their own export and nobody else's; a manage
    /// holder reaches any on the site; every refusal that would otherwise reveal whether an export exists is the same
    /// 404; and the framework's own file endpoints - which authorise by folder View and never by requester - refuse
    /// an ordinary requester outright, so the module's download is the only way to the artifact.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class ExportApiDriveTests
    {
        private const string Host = "localhost";
        private const string Api = "/api/DataExport";

        private readonly ITestOutputHelper _output;

        public ExportApiDriveTests(ITestOutputHelper output) => _output = output;

        private static JsonObject Settings(bool enabled) => new() { ["DataExport"] = new JsonObject { ["Enabled"] = enabled } };

        private static void AddSources(WebApplicationBuilder builder)
        {
            builder.Services.AddSingleton<IExportSource>(new ApiTestSource("test.rows", "export:rows", TableCardinality.Single));
            builder.Services.AddSingleton<IExportSource>(new ApiTestSource("test.sheets", "export:rows", TableCardinality.Multiple));
            builder.Services.AddSingleton<IExportSource>(new ApiTestSource("test.secret", "export:secret", TableCardinality.Single));
        }

        [Theory]
        [InlineData("sqlite")]
        [InlineData("postgres")]
        [InlineData("raven")]
        public async Task A_requester_reaches_their_own_export_and_nobody_elses(string provider)
        {
            IGateStore store = provider switch
            {
                "sqlite" => new SqliteGateStore(),
                "postgres" => new PostgresGateStore(),
                _ => new RavenGateStore(),
            };

            // Registered before AddStomme composes the host, so it pre-empts the module's TryAdd of the system clock.
            var clock = new ShiftableClock();
            using var scratch = new ScratchGateInstall(store, Host, Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output, builder =>
            {
                AddSources(builder);
                builder.Services.AddSingleton<IExportClock>(clock);
            });

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var exporters = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var managers = await GateDrive.EnsureRoleAsync(app, Host, "Export Managers");

                var requester = await GateDrive.AddUserAsync(app, Host, "exp-requester", "Requ3ster!Pass$2026", "exp-requester@gate.test", "Exporters");
                var other = await GateDrive.AddUserAsync(app, Host, "exp-other", "0ther!Pass$2026x", "exp-other@gate.test", "Exporters");
                var manager = await GateDrive.AddUserAsync(app, Host, "exp-manager", "Manag3r!Pass$2026", "exp-manager@gate.test", "Export Managers");
                var plain = await GateDrive.AddUserAsync(app, Host, "exp-plain", "Pla1n!Pass$2026xx", "exp-plain@gate.test");

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    async Task Grant(Role role, params string[] operations)
                    {
                        foreach (var operation in operations)
                        {
                            await permissions.AddPermissionAsync(new Permission
                            {
                                SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                                PermissionName = operation, RoleId = role.RoleId, IsAuthorized = true,
                            });
                        }
                    }

                    // Exporters may create and view, and read the "rows" data - NOT the "secret" data, NOT manage.
                    await Grant(exporters, "create", "view", "rows");
                    await Grant(managers, "create", "view", "manage", "rows");
                    return true;
                });

                var asRequester = await SessionAsync(app, "exp-requester", "Requ3ster!Pass$2026", requester.UserId);
                var asOther = await SessionAsync(app, "exp-other", "0ther!Pass$2026x", other.UserId);
                var asManager = await SessionAsync(app, "exp-manager", "Manag3r!Pass$2026", manager.UserId);
                var asPlain = await SessionAsync(app, "exp-plain", "Pla1n!Pass$2026xx", plain.UserId);
                var anonymous = new Session(GateDrive.NewClient(app, Host), null, null);

                // ---- who may even ask -----------------------------------------------------------------------------
                Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(app, asPlain, Api, new { SourceKey = "test.rows", Format = "Csv" }, "plain user requests")).Status);
                Assert.NotEqual(HttpStatusCode.OK, (await PostAsync(app, anonymous, Api, new { SourceKey = "test.rows", Format = "Csv" }, "anonymous requests")).Status);

                // ---- what the server refuses, with a reason that is safe to show --------------------------------------
                async Task Refused(Session who, object body, string label, string expectedInBody)
                {
                    var response = await PostAsync(app, who, Api, body, label);
                    Assert.Equal(HttpStatusCode.BadRequest, response.Status);
                    Assert.Contains(expectedInBody, response.Body, StringComparison.OrdinalIgnoreCase);
                }

                await Refused(asRequester, new { SourceKey = "test.nothing", Format = "Csv" }, "unknown source", "does not exist");
                // The SAME answer for a source whose data the caller may not read - the source list is not probeable.
                await Refused(asRequester, new { SourceKey = "test.secret", Format = "Csv" }, "source without the data permission", "does not exist");
                await Refused(asRequester, new { SourceKey = "Test.Rows", Format = "Csv" }, "non-canonical key", "does not exist");
                await Refused(asRequester, new { SourceKey = "test.rows", Format = "Docx" }, "unknown format", "format");
                await Refused(asRequester, new { SourceKey = "test.rows", Format = "Csv", CsvDelimiter = "x" }, "delimiter off the allowlist", "delimiter");
                await Refused(asRequester, new { SourceKey = "test.sheets", Format = "Csv" }, "CSV for a multi-table source", "several tables");
                await Refused(asRequester, new { SourceKey = "test.rows", Format = "Csv", NeutraliseFormulas = false }, "guard off without manage", "export:manage");
                await Refused(asRequester, new { SourceKey = "test.rows", Format = "Csv", ArgumentsJson = "{\"reject\":true}" }, "arguments the source refuses", "rejected by the source");

                // ---- and what it accepts ------------------------------------------------------------------------------
                var mine = await CreatedAsync(app, asRequester, new { SourceKey = "test.rows", Format = "Csv", CsvDelimiter = ";" }, "requester: csv");
                var sheets = await CreatedAsync(app, asRequester, new { SourceKey = "test.sheets", Format = "Xlsx" }, "requester: multi-table as xlsx");
                var managed = await CreatedAsync(app, asManager, new { SourceKey = "test.rows", Format = "Csv", NeutraliseFormulas = false }, "manager: guard off");
                var toCancel = await CreatedAsync(app, asRequester, new { SourceKey = "test.rows", Format = "Json" }, "requester: to be cancelled");

                // ---- cancel: yours, while it is outstanding, and one answer otherwise --------------------------------
                Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, asOther, $"{Api}/{toCancel}/cancel", null, "other cancels mine")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, asRequester, $"{Api}/{Guid.NewGuid()}/cancel", null, "cancel an unknown id")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, asRequester, $"{Api}/{toCancel}/cancel", null, "requester cancels own")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, asRequester, $"{Api}/{toCancel}/cancel", null, "cancel it twice")).Status);

                // ---- render ---------------------------------------------------------------------------------------------
                var log = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                _output.WriteLine($"[{provider}][job] {log}");

                // This host serves HTTP, so it is FULLY started and the real per-minute ExportQueueJob is live beside this
                // manual run. Whichever of the two claims a job renders it - the lease at work, observed: the live job took
                // two of the three once. So the requirement is the outcome, not who produced it.
                var outstanding = 3;
                for (var attempt = 0; attempt < 240 && outstanding > 0; attempt++)
                {
                    await Task.Delay(500);
                    outstanding = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                        (await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId))
                            .Count(job => job.Status is ExportJobStatus.Queued or ExportJobStatus.Rendering));
                }

                Assert.Equal(0, outstanding);
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var all = await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId);
                    Assert.Equal(3, all.Count(job => job.Status == ExportJobStatus.Ready));
                    Assert.DoesNotContain(all, job => job.Status == ExportJobStatus.Failed);
                    // Rendered once each, whoever did it.
                    Assert.All(all.Where(job => job.Status == ExportJobStatus.Ready), job => Assert.Equal(1, job.Attempts));
                    return true;
                });

                // ---- lists --------------------------------------------------------------------------------------------
                var requesterList = await ListAsync(asRequester, $"{Api}/mine", "requester: mine");
                Assert.Equal(new[] { mine, sheets, toCancel }.OrderBy(i => i), requesterList.Select(j => j.Id).OrderBy(i => i));
                Assert.True(requesterList.Single(j => j.Id == mine).CanDownload);
                Assert.Equal("Cancelled", requesterList.Single(j => j.Id == toCancel).Status);
                Assert.False(requesterList.Single(j => j.Id == toCancel).CanDownload);

                Assert.Empty(await ListAsync(asOther, $"{Api}/mine", "other: mine"));
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(asOther.Client, asOther.Cookies, Host, $"{Api}/site", _output, "other: site list")).Status);
                Assert.Equal(4, (await ListAsync(asManager, $"{Api}/site", "manager: site list")).Count);

                // ---- by id: ONE answer for "not yours", "not here" and "no such thing" ----------------------------------
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(asRequester.Client, asRequester.Cookies, Host, $"{Api}/{mine}", _output, "requester: own by id")).Status);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(asManager.Client, asManager.Cookies, Host, $"{Api}/{mine}", _output, "manager: any by id")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(asOther.Client, asOther.Cookies, Host, $"{Api}/{mine}", _output, "other: mine by id")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(asOther.Client, asOther.Cookies, Host, $"{Api}/{Guid.NewGuid()}", _output, "other: unknown id")).Status);

                // ---- download -----------------------------------------------------------------------------------------
                var own = await DownloadAsync(asRequester, mine, "requester downloads own");
                Assert.Equal(HttpStatusCode.OK, own.Status);
                Assert.Equal("text/csv", own.ContentType);
                Assert.StartsWith("N;Text\r\n1;row 1\r\n", own.Body);
                // Neutralised, because the requester could not turn the guard off...
                Assert.Contains("'=1+1", own.Body);
                Assert.Contains("attachment", own.Disposition);
                Assert.Contains(".csv", own.Disposition);
                Assert.Contains("no-store", own.CacheControl);

                // ...and NOT neutralised in the manager's, who could and did.
                var managers2 = await DownloadAsync(asManager, managed, "manager downloads own (guard off)");
                Assert.Contains("\r\n3,=1+1\r\n", managers2.Body);

                Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(asManager, mine, "manager downloads requester's")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(asOther, mine, "OTHER downloads requester's")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(asRequester, toCancel, "download a cancelled export")).Status);
                Assert.NotEqual(HttpStatusCode.OK, (await DownloadAsync(anonymous, mine, "anonymous downloads")).Status);

                var xlsx = await DownloadAsync(asRequester, sheets, "requester downloads xlsx");
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.ContentType);

                // A resumed download: authorised again, NOT audited again.
                var resumed = await DownloadAsync(asRequester, mine, "requester resumes (Range: bytes=5-)", range: "bytes=5-");
                Assert.Equal(HttpStatusCode.PartialContent, resumed.Status);

                // ---- the framework's own doors are SHUT to a requester --------------------------------------------------
                var fileId = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<IExportJobRepository>().GetByIdAsync(mine))!.FileId!.Value);
                var fileName = mine.ToString("N") + ".csv";
                foreach (var path in new[] { $"/api/File/download/{fileId}", $"/api/File/download/{fileId}/attach", $"/files/id/{fileId}", $"/files/Exports/{fileName}" })
                {
                    var door = await GateDrive.GetAsync(asRequester.Client, asRequester.Cookies, Host, path, _output, "requester via " + path);
                    Assert.True(door.Status != HttpStatusCode.OK || !door.Body.Contains("row 1", StringComparison.Ordinal),
                        $"{path} handed the artifact to an ordinary requester ({(int)door.Status})");
                    // BL-159: a REFUSAL, said as one. This scratch install ships no wwwroot/images/error.png, which used to turn
                    // every one of these into a 500 - a null action result on the API, a FileNotFoundException on the page.
                    Assert.Equal(HttpStatusCode.Forbidden, door.Status);
                    var anon = await GateDrive.GetAsync(anonymous.Client, null, Host, path, _output, "anonymous via " + path);
                    Assert.True(anon.Status != HttpStatusCode.OK || !anon.Body.Contains("row 1", StringComparison.Ordinal),
                        $"{path} handed the artifact to an ANONYMOUS caller ({(int)anon.Status})");
                    Assert.Equal(HttpStatusCode.Forbidden, anon.Status);
                }

                // A file that does not exist is a 404 on every door - never a 500, and never a 200 with a placeholder.
                foreach (var path in new[] { "/api/File/download/987654", "/api/File/download/987654/attach", "/files/id/987654", "/files/Exports/no-such-file.csv" })
                {
                    var missing = await GateDrive.GetAsync(asRequester.Client, asRequester.Cookies, Host, path, _output, "missing via " + path);
                    _output.WriteLine($"[{provider}][missing] {path} -> {(int)missing.Status}");
                    Assert.NotEqual(HttpStatusCode.InternalServerError, missing.Status);
                    Assert.NotEqual(HttpStatusCode.OK, missing.Status);
                }

                // The other direction, or "nobody got the file" could simply mean the doors are broken. The SAME request
                // as an Administrator returns the artifact: the file exists, the door works, and what refused the two
                // callers above was the folder's Administrators-only View row, not a door that refuses everyone.
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var asHost = await SessionAsync(app, GateDrive.HostUsername, GateDrive.HostPassword, hostId);
                var asAdmin = await GateDrive.GetAsync(asHost.Client, asHost.Cookies, Host, $"/api/File/download/{fileId}", _output, "ADMINISTRATOR via the framework door");
                Assert.Equal(HttpStatusCode.OK, asAdmin.Status);
                Assert.Contains("row 1", asAdmin.Body);

                // ---- the audit trail ------------------------------------------------------------------------------------
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var audit = await sp.GetRequiredService<IExportAuditRepository>().ListByJobAsync(siteId, mine);
                    _output.WriteLine($"[{provider}][audit] " + string.Join(", ", audit.OrderBy(e => e.OccurredOnUtc).Select(e => $"{e.Action}:{e.ActorUserId?.ToString() ?? "system"}")));
                    Assert.Contains(audit, e => e.Action == ExportAuditAction.Requested && e.ActorUserId == requester.UserId);
                    Assert.Contains(audit, e => e.Action == ExportAuditAction.Completed && e.ActorUserId is null);
                    // The requester once and the manager once. NOT the refused one, NOT the Range continuation.
                    Assert.Equal(
                        new int?[] { manager.UserId, requester.UserId }.OrderBy(i => i),
                        audit.Where(e => e.Action == ExportAuditAction.Downloaded).Select(e => e.ActorUserId).OrderBy(i => i));

                    var cancelled = await sp.GetRequiredService<IExportAuditRepository>().ListByJobAsync(siteId, toCancel);
                    Assert.Contains(cancelled, e => e.Action == ExportAuditAction.Cancelled && e.ActorUserId == requester.UserId);
                    return true;
                });

                // ---- refutation 14: moved out of Exports, the module refuses its OWN requester ------------------------
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var files = sp.GetRequiredService<IFileRepository>();
                    var root = await sp.GetRequiredService<IFolderRepository>().GetFolderAsync(siteId, string.Empty);
                    var file = await files.GetFileAsync(fileId, false);
                    var from = await files.GetFilePathAsync(fileId);
                    file.FolderId = root.FolderId;
                    await files.UpdateFileAsync(file);

                    // A real move relocates the BYTES too (FileController does). Without this the download would 404 on
                    // "file missing" whether or not the module checked the folder, and the check would be untested.
                    var to = await files.GetFilePathAsync(fileId);
                    Assert.NotEqual(from, to);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                    System.IO.File.Move(from, to);
                    Assert.True(System.IO.File.Exists(to));
                    return true;
                });

                Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(asRequester, mine, "requester downloads a MOVED artifact")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(asManager, mine, "manager downloads a MOVED artifact")).Status);

                // ---- past its expiry but NOT YET PURGED: the bytes are still there, the answer is already no -----------
                Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(asRequester, sheets, "requester downloads xlsx before it expires")).Status);
                clock.Shift(TimeSpan.FromDays(8));
                Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(asRequester, sheets, "requester downloads an EXPIRED, unpurged artifact")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(asManager, sheets, "manager downloads an EXPIRED, unpurged artifact")).Status);
                Assert.False((await ListAsync(asRequester, $"{Api}/mine", "requester: mine, after expiry")).Single(j => j.Id == sheets).CanDownload);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_feature_OFF_every_route_is_a_404_even_for_the_host()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output, AddSources);

            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var asHost = await SessionAsync(app, GateDrive.HostUsername, GateDrive.HostPassword, hostId);
                var id = Guid.NewGuid();

                foreach (var path in new[] { $"{Api}/sources", $"{Api}/mine", $"{Api}/site", $"{Api}/{id}", $"{Api}/{id}/download" })
                {
                    var response = await GateDrive.GetAsync(asHost.Client, asHost.Cookies, Host, path, _output, "off: " + path);
                    // 404, not 403: a disabled feature has no surface to be refused from, and the host - who holds no
                    // export:* rows here - would otherwise learn the routes exist.
                    Assert.Equal(HttpStatusCode.NotFound, response.Status);
                }

                // A POST with no matching action is answered by whatever answers ANY unrouted POST. The property that
                // matters is that it is indistinguishable from a route that does not exist.
                var create = await PostAsync(app, asHost, Api, new { SourceKey = "test.rows", Format = "Csv" }, "off: create");
                var nowhere = await PostAsync(app, asHost, "/api/NoSuchController", new { SourceKey = "test.rows", Format = "Csv" }, "off: a route that does not exist");
                Assert.NotEqual(HttpStatusCode.OK, create.Status);
                Assert.Equal(nowhere.Status, create.Status);
                Assert.Equal(nowhere.Body, create.Body);

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    Assert.Empty(await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(alias.SiteId));
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- helpers ---------------------------------------------------------------------------------------------------

        private sealed record Session(HttpClient Client, CookieContainer? Cookies, int? UserId);

        private sealed record JobRow(Guid Id, string Status, bool CanDownload);

        private sealed class ShiftableClock : IExportClock
        {
            private long _offsetTicks;

            public DateTime UtcNow => DateTime.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

            public void Shift(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
        }

        private async Task<Session> SessionAsync(WebApplication app, string username, string password, int userId)
        {
            var client = GateDrive.NewClient(app, Host);
            return new Session(client, await GateDrive.LoginAsync(app, client, Host, username, password, _output), userId);
        }

        private Task<(HttpStatusCode Status, string Body)> PostAsync(WebApplication app, Session who, string path, object? body, string label) =>
            GateDrive.SendJsonAsync(app, who.Client, who.Cookies, Host, who.UserId, HttpMethod.Post, path, body, _output, label);

        private async Task<Guid> CreatedAsync(WebApplication app, Session who, object body, string label)
        {
            var response = await PostAsync(app, who, Api, body, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            return JsonSerializer.Deserialize<Guid>(response.Body);
        }

        private async Task<List<JobRow>> ListAsync(Session who, string path, string label)
        {
            var response = await GateDrive.GetAsync(who.Client, who.Cookies, Host, path, _output, label);
            Assert.Equal(HttpStatusCode.OK, response.Status);
            using var parsed = JsonDocument.Parse(response.Body);
            return parsed.RootElement.EnumerateArray()
                .Select(e => new JobRow(e.GetProperty("id").GetGuid(), e.GetProperty("status").GetString()!, e.GetProperty("canDownload").GetBoolean()))
                .ToList();
        }

        private async Task<(HttpStatusCode Status, string Body, string ContentType, string Disposition, string CacheControl)> DownloadAsync(
            Session who, Guid id, string label, string? range = null)
        {
            var uri = new Uri(new Uri("http://" + Host), $"{Api}/{id}/download");
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var cookie = who.Cookies?.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            if (range is not null)
            {
                request.Headers.TryAddWithoutValidation("Range", range);
            }

            var response = await who.Client.SendAsync(request);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            // An XLSX is binary; only text bodies are read back as text.
            var body = contentType.StartsWith("text/", StringComparison.Ordinal) || contentType.Contains("json", StringComparison.Ordinal)
                ? await response.Content.ReadAsStringAsync()
                : string.Empty;
            _output.WriteLine($"[{label}] -> {(int)response.StatusCode} {contentType}");
            return (response.StatusCode, body, contentType,
                response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty,
                response.Headers.CacheControl?.ToString() ?? string.Empty);
        }

        /// <summary>Three rows, the last formula-shaped; one table or two; refuses <c>{"reject":true}</c>.</summary>
        private sealed class ApiTestSource(string key, string permission, TableCardinality cardinality) : IExportSource
        {
            public string Key => key;

            public string RequiredPermission => permission;

            public TableCardinality TableCardinality => cardinality;

            public string? Validate(ExportSourceContext context) =>
                context.ArgumentsJson is { } json && json.Contains("reject", StringComparison.Ordinal) ? "The arguments were rejected by the source." : null;

            public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default) =>
                Task.FromResult<ITabularDocument>(new Document(cardinality == TableCardinality.Multiple ? 2 : 1));

            private sealed class Document(int tables) : ITabularDocument
            {
                public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    for (var i = 1; i <= tables; i++)
                    {
                        yield return new Table("Sheet " + i);
                    }

                    await Task.CompletedTask;
                }

                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }

            private sealed class Table(string name) : ITabularTable
            {
                public string Name => name;

                public IReadOnlyList<ExportColumn> Columns { get; } = new[]
                {
                    new ExportColumn("N", ExportValueKind.Integer),
                    new ExportColumn("Text", ExportValueKind.Text),
                };

                public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    yield return new object?[] { 1, "row 1" };
                    yield return new object?[] { 2, "row 2" };
                    yield return new object?[] { 3, "=1+1" };
                    await Task.CompletedTask;
                }
            }
        }
    }
}
