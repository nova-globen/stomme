using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// STEP-5 (Goal-2) DIVERGENCE DRIVE — OBSERVE, on REAL PostgreSQL and REAL RavenDB, seam behaviour behind
    /// the persistence seam rather than infer it from the code path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam's promise (MISSION Goal-2) is IDENTICAL observable behaviour across providers. This installs a
    /// real site on each provider through the production host and drives the real repositories against it — the
    /// same host <see cref="Program.Main"/> serves, only the transport swapped for TestServer — so what it
    /// records is the live provider's answer, not a fixture's.
    /// </para>
    /// <list type="number">
    /// <item><b>Un-normalized string <c>==</c> equality (case sensitivity) — NOW FIXED AND ASSERTED AS
    /// AGREEMENT.</b> <c>UrlMapping.Url</c> WAS looked up with a bare <c>item.Url == url</c>. PostgreSQL's
    /// <c>=</c> is case-SENSITIVE under the column collation; RavenDB's is case-INSENSITIVE — DRIVEN here in a
    /// prior increment (a row stored <c>"Foo/Bar"</c> and looked up <c>"foo/bar"</c> MISSED on PostgreSQL and
    /// HIT on RavenDB). D57 normalizes the DATA (a lowercase <c>UrlNormalized</c> column) and the ARGUMENT (in
    /// <see cref="UrlMappingRepository.GetUrlMappingAsync(int, string, string)"/>), so both providers now HIT.
    /// This test asserts that AGREEMENT: store <c>"Foo/Bar"</c>, look up <c>"foo/bar"</c>, both providers return
    /// the row (case-INSENSITIVE, matching the Folder/File precedent).</item>
    /// <item><b>Page.Path un-normalized <c>==</c> equality — NOW FIXED AND ASSERTED AS AGREEMENT.</b>
    /// The direct analog of item 1: <c>PageRepository.GetPageAsync(path, siteId)</c> filtered
    /// <c>item.Path == path</c> raw on the render RESOLVE fallback (<c>SiteRouter.razor</c>, reached when the
    /// in-memory <c>OrdinalIgnoreCase</c> match misses) and the admin page lookup. D58 adds a lowercase
    /// <c>PathNormalized</c> column and normalizes the argument, so a page stored <c>Foo/Bar</c> is found by
    /// <c>foo/bar</c> on BOTH providers. Asserted as agreement.</item>
    /// <item><b>Folder.Path un-normalized <c>==</c> equality — STILL A DIVERGENCE, DELIBERATELY, and
    /// OBSERVED here as an accepted gap.</b> <c>FolderRepository.GetFolderAsync(siteId, path)</c> filters
    /// <c>item.Path == path</c> raw and is documented as case-SENSITIVE on purpose (its callers pass the
    /// STORED case of well-known folders). PostgreSQL honours that intent (a mismatched-case lookup MISSES);
    /// RavenDB's <c>==</c> is case-INSENSITIVE and cannot (it HITS). Not fixed here — see the D58 evidence
    /// doc for why closing it would override D48/D49's deliberate choice. Pinned as an observation.</item>
    /// <item><b>The <see cref="IDeletable"/> delete-stamp — NOW FIXED AND ASSERTED AS AGREEMENT.</b> On a
    /// soft delete (<c>IsDeleted = true</c> + Update) the relational path stamps <c>DeletedBy</c>/<c>DeletedOn</c>
    /// in <c>DbContextUtils.SaveChanges</c>. This WAS relational-only — <c>RavenAuditListener</c> stamped
    /// <see cref="IAuditable"/> ONLY and <see cref="Notification"/> is <see cref="IDeletable"/>-only, so RavenDB
    /// stamped nothing (DRIVEN here in a prior increment: <c>IsDeleted=true</c> on both, <c>DeletedOn</c> only on
    /// PostgreSQL). D59 ports the rule to the document path (<c>AuditStamp.StampDeleted</c>, keyed on
    /// <c>IsDeleted == true &amp;&amp; DeletedOn == null</c>, no DB load), so both providers now read back
    /// <c>IsDeleted = true</c> AND a populated <c>DeletedOn</c> AND the same <c>DeletedBy</c> (<c>''</c> under the
    /// unauthenticated host). This test asserts that AGREEMENT.</item>
    /// </list>
    /// <para>
    /// GATED — FAILS rather than skips when either store is unreachable, the doctrine the render guard and the
    /// conformance suite share: a one-provider run proves nothing about parity. PostgreSQL is the local
    /// PostgreSQL service on 127.0.0.1:5432; RavenDB is the local install at
    /// <c>RAVENDB_TEST_URL</c> (default http://127.0.0.1:7000). Any RavenDB database this creates is
    /// hard-deleted on the way out.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class Step5DivergenceDriveTests
    {
        private const string PostgresType = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";

        private static string RavenUrl => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;

        public Step5DivergenceDriveTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task UrlMapping_lookup_and_notification_stamp_agree_on_postgres_and_raven()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}, so nothing " +
                "here proves parity. Set " +
                "POSTGRES_TEST_HOST/PORT/USER/PASSWORD.");

            var ravenEndpoint = new Uri(RavenUrl);
            Assert.True(IsReachable(ravenEndpoint.Host, ravenEndpoint.Port),
                $"RavenDB is not reachable at {RavenUrl}, so nothing here proves parity. " +
                "Start the local RavenDB instance or point RAVENDB_TEST_URL at a reachable server.");

            var pg = await DriveProviderAsync("PostgreSQL", PostgresType, PostgresConnectionString(out var pgDatabase), ravenDatabase: null, postgresDatabase: pgDatabase);
            var raven = await DriveProviderAsync("RavenDB", RavenType, RavenConnectionString(out var ravenDatabase), ravenDatabase);

            // ---- REPORT: everything OBSERVED, before any assertion can short-circuit ------------------------
            _output.WriteLine("======== AGREEMENT 1 (D57 FIX) — UrlMapping normalized lookup (stored 'Foo/Bar', looked up 'foo/bar') ========");
            _output.WriteLine($"  PostgreSQL  direct-session query : {pg.UrlMappingSessionResult}");
            _output.WriteLine($"  PostgreSQL  repository lookup     : {pg.UrlMappingRepoResult}");
            _output.WriteLine($"  RavenDB     direct-session query : {raven.UrlMappingSessionResult}");
            _output.WriteLine($"  RavenDB     repository lookup     : {raven.UrlMappingRepoResult}");
            _output.WriteLine("======== AGREEMENT 3 (D58 FIX) — Page normalized lookup (stored 'Foo/Bar', looked up 'foo/bar') ============");
            _output.WriteLine($"  PostgreSQL  direct-session query : {pg.PageSessionResult}");
            _output.WriteLine($"  PostgreSQL  repository lookup     : {pg.PageRepoResult}");
            _output.WriteLine($"  RavenDB     direct-session query : {raven.PageSessionResult}");
            _output.WriteLine($"  RavenDB     repository lookup     : {raven.PageRepoResult}");
            _output.WriteLine("======== DIVERGENCE 4 (accepted, D48/D49) — Folder.Path RAW == (stored 'Users/Zzz/', looked up 'users/zzz/') =");
            _output.WriteLine($"  PostgreSQL  {pg.FolderResult}");
            _output.WriteLine($"  RavenDB     {raven.FolderResult}");
            _output.WriteLine("======== AGREEMENT 2 (D59 FIX) — Notification IDeletable stamp (soft-delete, read back) ======================");
            _output.WriteLine($"  PostgreSQL  {pg.NotificationResult}");
            _output.WriteLine($"  RavenDB     {raven.NotificationResult}");
            _output.WriteLine("==============================================================================================================");

            // ---- AGREEMENT 3 verdict — Page.Path normalized lookup now agrees ------------------------
            Assert.True(pg.PageSessionHit,
                "PostgreSQL did NOT match 'foo/bar' against stored 'Foo/Bar' via PathNormalized — expected a " +
                "case-insensitive AGREEMENT after D58. If this fires, the normalized read is broken on PostgreSQL.");
            Assert.True(raven.PageSessionHit,
                "RavenDB did NOT match 'foo/bar' against stored 'Foo/Bar' via PathNormalized — expected a hit.");
            Assert.Equal("Foo/Bar", pg.PageStoredCase);
            Assert.Equal("Foo/Bar", raven.PageStoredCase);
            Assert.True(pg.PageRepoHit, "PostgreSQL repository lookup returned NULL for 'foo/bar' — the page resolve path still diverges.");
            Assert.True(raven.PageRepoHit, "RavenDB repository lookup returned NULL for 'foo/bar'.");

            // ---- DIVERGENCE 4 verdict — Folder.Path RAW == is DELIBERATELY case-sensitive --
            // OBSERVED and PINNED, not fixed: PostgreSQL honours the case-sensitive intent (MISS on a
            // mismatched-case lookup); RavenDB's == is case-INSENSITIVE and cannot honour it (HIT). This is the
            // remaining accepted gap the D58 evidence doc raises for an owner decision. If either half flips,
            // the divergence has changed and this fires so the record stays honest.
            Assert.False(pg.FolderSessionHit,
                "PostgreSQL matched 'users/zzz/' against stored 'Users/Zzz/' via a RAW item.Path == — expected a " +
                "case-SENSITIVE MISS (D48/D49). If this fires, PostgreSQL's folder path lookup is no longer case-sensitive.");
            Assert.True(raven.FolderSessionHit,
                "RavenDB did NOT match 'users/zzz/' against stored 'Users/Zzz/' via a RAW item.Path == — expected a " +
                "case-INSENSITIVE HIT. If this fires, the Folder.Path divergence has been closed and the D58 verdict is stale.");

            // ---- AGREEMENT 1 verdict ---------------------------------------------------------------
            // The pure observation is the direct session query (the repository's exact read expression against
            // the normalized column, no side effects). BOTH providers must HIT the mixed-case row now, and the
            // STORED case must survive untouched on both.
            Assert.True(pg.UrlMappingSessionHit,
                "PostgreSQL did NOT match 'foo/bar' against stored 'Foo/Bar' via UrlNormalized — expected a " +
                "case-insensitive AGREEMENT after D57. If this fires, the normalized read is broken on PostgreSQL.");
            Assert.True(raven.UrlMappingSessionHit,
                "RavenDB did NOT match 'foo/bar' against stored 'Foo/Bar' via UrlNormalized — expected a hit.");
            Assert.Equal("Foo/Bar", pg.UrlMappingStoredCase);
            Assert.Equal("Foo/Bar", raven.UrlMappingStoredCase);

            // The real product method (GetUrlMappingAsync, which normalizes its argument) must return the row on
            // BOTH — this is the render 404 path that was silently provider-dependent before D57.
            Assert.True(pg.UrlMappingRepoHit, "PostgreSQL repository lookup returned NULL for 'foo/bar' — the render-path read still diverges.");
            Assert.True(raven.UrlMappingRepoHit, "RavenDB repository lookup returned NULL for 'foo/bar'.");

            // ---- AGREEMENT 2 verdict (D59 FIX) — the IDeletable soft-delete stamp now AGREES -------------
            // Before D59: PostgreSQL stamped DeletedBy(='')/DeletedOn via DbContextUtils, RavenDB stamped
            // NOTHING (RavenAuditListener was IAuditable-only, and Notification is IDeletable-only). D59 ported
            // the rule to the document path (AuditStamp.StampDeleted, keyed on IsDeleted==true && DeletedOn==null,
            // no DB load), so both providers now stamp identically. IsDeleted was always mapped on both.
            Assert.True(pg.NotificationIsDeleted, "PostgreSQL did not persist IsDeleted=true on soft delete.");
            Assert.True(raven.NotificationIsDeleted, "RavenDB did not persist IsDeleted=true on soft delete.");
            Assert.True(pg.NotificationDeletedOn.HasValue,
                "PostgreSQL did NOT stamp DeletedOn on soft delete — expected DbContextUtils to stamp it.");
            Assert.True(raven.NotificationDeletedOn.HasValue,
                "RavenDB did NOT stamp DeletedOn on soft delete — expected D59's AuditStamp.StampDeleted to stamp it. " +
                "If this fires, the RavenDB delete-stamp port has regressed.");
            // DeletedBy matches EF's observable outcome including the unauthenticated '' case (both hosts write
            // with no ambient user: EF's DbContextUtils username='' and Raven's SystemAuditContext(string.Empty)).
            Assert.True(pg.NotificationDeletedByStamped, "PostgreSQL DeletedBy was null on soft delete — expected '' (stamped, unauthenticated).");
            Assert.True(raven.NotificationDeletedByStamped, "RavenDB DeletedBy was null on soft delete — expected '' (stamped, unauthenticated).");
            Assert.Equal(pg.NotificationDeletedBy, raven.NotificationDeletedBy);
        }

        private async Task<Observation> DriveProviderAsync(string label, string databaseType, string connectionString, string? ravenDatabase, string? postgresDatabase = null)
        {
            using var scratch = new ScratchInstallation(databaseType, connectionString);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            var observation = new Observation();
            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException($"The {label} installer did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
                }

                var install = await installTask;
                Assert.True(install.Success, $"{label} installer did not report success: {install.Message}");

                var alias = await GetDefaultAliasAsync(app);
                Assert.NotNull(alias);
                var siteId = alias.SiteId;
                _output.WriteLine($"[{label}] installed. alias='{alias.Name}' tenantId={alias.TenantId} siteId={siteId}");

                await DriveUrlMappingAgreementAsync(app, alias, siteId, observation);
                await DrivePageAgreementAsync(app, alias, siteId, observation);
                await DriveFolderDivergenceAsync(app, alias, siteId, observation);
                await DriveNotificationDivergenceAsync(app, alias, siteId, observation);
            }
            finally
            {
                await SafeShutdownAsync(app);
                if (ravenDatabase != null)
                {
                    await BestEffortDropRavenAsync(ravenDatabase);
                }
                if (postgresDatabase != null)
                {
                    // This suite was the ONE PostgreSQL-installing test with no drop — every gate
                    // run left one stomme_step5_* database on the SHARED infra instance (11 found by
                    // BL-16's hygiene check; the Raven side always dropped). Same best-effort shape as the
                    // eight sibling suites that already call DropDatabase.
                    BestEffortDropPostgres(postgresDatabase);
                }
            }

            return observation;
        }

        // --- Agreement 1 ------------------------------------------------------------------------------

        private async Task DriveUrlMappingAgreementAsync(WebApplication app, Alias alias, int siteId, Observation observation)
        {
            // 1) Write a row with a MIXED-CASE url. Fresh scope => fresh store/context, as one request would get.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<IUrlMappingRepository>();
                await repo.AddUrlMappingAsync(new UrlMapping
                {
                    SiteId = siteId,
                    Url = "Foo/Bar",
                    MappedUrl = "",
                    Requests = 1,
                    Referrer = "",
                    CreatedOn = DateTime.UtcNow,
                    RequestedOn = DateTime.UtcNow
                });
            });

            // 2) Look it up with a DIFFERENT case through the seam's own read session — the repository's EXACT
            //    read expression after D57 (UrlMappingRepository.GetUrlMappingAsync: item.UrlNormalized ==
            //    Normalize(url)), with NO side effects. Both providers must HIT, and the stored case survives.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var sessions = sp.GetRequiredService<IFrameworkStoreSessionFactory>();
                var session = sessions.OpenQuerySession();
                try
                {
                    var normalized = NormalizedText.Normalize("foo/bar");
                    var hit = session.Query<UrlMappingAggregate>()
                        .FirstOrDefault(item => item.SiteId == siteId && item.UrlNormalized == normalized);
                    observation.UrlMappingSessionHit = hit != null;
                    observation.UrlMappingStoredCase = hit?.Url;
                    observation.UrlMappingSessionResult = hit == null
                        ? "NULL (no row matched normalized 'foo/bar')"
                        : $"HIT — stored Url='{hit.Url}', UrlNormalized='{hit.UrlNormalized}'";
                }
                finally
                {
                    await session.DisposeAsync();
                }
            });

            // 3) Exercise the real product method — the render 404 path. It normalizes its own argument, so a
            //    mixed-case-stored row is found by a lowercased request on BOTH providers.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<IUrlMappingRepository>();
                var result = await repo.GetUrlMappingAsync(siteId, "foo/bar");
                observation.UrlMappingRepoHit = result != null;
                observation.UrlMappingRepoResult = result == null
                    ? "NULL"
                    : $"Url='{result.Url}' Requests={result.Requests}";
            });
        }

        // --- Agreement 3 — Page.Path -----------------------------------------------------------------

        private async Task DrivePageAgreementAsync(WebApplication app, Alias alias, int siteId, Observation observation)
        {
            // 1) Write a page with a MIXED-CASE path. Fresh scope => fresh store/context.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<IPageRepository>();
                await repo.AddPageAsync(new Page
                {
                    SiteId = siteId,
                    Path = "Foo/Bar",
                    Name = "Foo Bar",
                    Title = "Foo Bar",
                    Icon = "",
                    Order = 0,
                    IsNavigation = false,
                    IsClickable = true,
                    IsPersonalizable = false,
                    Url = "",
                    ThemeType = "",
                    DefaultContainerType = "",
                    HeadContent = "",
                    BodyContent = "",
                    PermissionList = new List<Permission>()
                });
            });

            // 2) Look it up with a DIFFERENT case through the seam's own read session — the repository's EXACT
            //    read expression after D58 (PageRepository.GetPageAsync: item.PathNormalized == Normalize(path)),
            //    with NO side effects. Both providers must HIT, and the stored case survives.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var sessions = sp.GetRequiredService<IFrameworkStoreSessionFactory>();
                var session = sessions.OpenQuerySession();
                try
                {
                    var normalized = NormalizedText.Normalize("foo/bar");
                    var hit = session.Query<PageAggregate>()
                        .FirstOrDefault(item => item.PathNormalized == normalized && item.SiteId == siteId);
                    observation.PageSessionHit = hit != null;
                    observation.PageStoredCase = hit?.Path;
                    observation.PageSessionResult = hit == null
                        ? "NULL (no row matched normalized 'foo/bar')"
                        : $"HIT — stored Path='{hit.Path}', PathNormalized='{hit.PathNormalized}'";
                }
                finally
                {
                    await session.DisposeAsync();
                }
            });

            // 3) Exercise the real product method — the render RESOLVE fallback / admin page lookup. It
            //    normalizes its own argument, so a mixed-case-stored page is found by a lowercased request on
            //    BOTH providers.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<IPageRepository>();
                var result = await repo.GetPageAsync("foo/bar", siteId);
                observation.PageRepoHit = result != null;
                observation.PageRepoResult = result == null ? "NULL" : $"Path='{result.Path}'";
            });
        }

        // --- Divergence 4 — Folder.Path, DELIBERATELY case-SENSITIVE, so it does NOT agree ---
        //
        // Unlike Page/UrlMapping, FolderRepository.GetFolderAsync(siteId, path) filters item.Path RAW and is
        // documented as case-SENSITIVE on purpose (its callers pass the STORED case of well-known folders).
        // That intent is honoured on PostgreSQL (case-SENSITIVE ==) but CANNOT be honoured on RavenDB, whose
        // == is case-INSENSITIVE — the exact divergence D48 measured and "reported, not fixed". This drive
        // OBSERVES and pins the remaining gap; it is NOT a fix, because making them agree case-INSENSITIVELY
        // would override D48/D49's deliberate choice (see the D58 evidence doc). Store 'Users/Zzz/', look up
        // 'users/zzz/'.
        private async Task DriveFolderDivergenceAsync(WebApplication app, Alias alias, int siteId, Observation observation)
        {
            const string storedPath = "Users/Zzz/";
            const string lookupPath = "users/zzz/";

            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<IFolderRepository>();
                await repo.AddFolderAsync(new Folder
                {
                    SiteId = siteId,
                    Name = "Zzz",
                    Path = storedPath,
                    Type = FolderTypes.Private,
                    Order = 1,
                    IsSystem = false,
                    ImageSizes = "",
                    PermissionList = new List<Permission>()
                });
            });

            // The repository's EXACT read expression (item.Path == path, RAW), with no side effects. This is
            // the divergent shape.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var sessions = sp.GetRequiredService<IFrameworkStoreSessionFactory>();
                var session = sessions.OpenQuerySession();
                try
                {
                    var hit = session.Query<FolderAggregate>()
                        .FirstOrDefault(item => item.SiteId == siteId && item.Path == lookupPath);
                    observation.FolderSessionHit = hit != null;
                }
                finally
                {
                    await session.DisposeAsync();
                }
            });

            // The real product method, for completeness.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<IFolderRepository>();
                var result = await repo.GetFolderAsync(siteId, lookupPath);
                observation.FolderRepoHit = result != null;
                observation.FolderResult =
                    $"session(item.Path=='{lookupPath}')={observation.FolderSessionHit} | repo.GetFolderAsync={(result != null)}";
            });
        }

        // --- Divergence 2 -------------------------------------------------------------------------------------

        private async Task DriveNotificationDivergenceAsync(WebApplication app, Alias alias, int siteId, Observation observation)
        {
            var notificationId = 0;

            // 1) Create a notification (IsDeleted=false). Fresh scope/store.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<INotificationRepository>();
                var notification = new Notification(siteId, "Recipient", "recipient@example.com", "Step5 subject", "Step5 body");
                var saved = await repo.AddNotificationAsync(notification);
                notificationId = saved.NotificationId;
            });

            // 2) Soft-delete it: read (through a query session), set IsDeleted, Update (through the store). A
            //    fresh scope, exactly as NotificationController.Put is one request.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<INotificationRepository>();
                var notification = await repo.GetNotificationAsync(notificationId);
                notification.IsDeleted = true;
                await repo.UpdateNotificationAsync(notification);
            });

            // 3) Read it back from a fresh scope and record the PERSISTED stamps.
            await InTenantScopeAsync(app, alias, async sp =>
            {
                var repo = sp.GetRequiredService<INotificationRepository>();
                var readback = await repo.GetNotificationAsync(notificationId);
                observation.NotificationIsDeleted = readback.IsDeleted;
                observation.NotificationDeletedOn = readback.DeletedOn;
                observation.NotificationDeletedByStamped = readback.DeletedBy != null;
                observation.NotificationDeletedBy = readback.DeletedBy;
                observation.NotificationResult =
                    $"IsDeleted={readback.IsDeleted} | DeletedBy={Render(readback.DeletedBy)} | DeletedOn={Render(readback.DeletedOn)}";
            });
        }

        // --- scope / install plumbing -------------------------------------------------------------------------

        private static async Task<Alias> GetDefaultAliasAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var aliases = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasesAsync();
            return aliases.FirstOrDefault()!;
        }

        /// <summary>
        /// Opens a DI scope, routes it at <paramref name="alias"/> (which warms the tenant bindings, exactly as
        /// TenantMiddleware does per request), then runs <paramref name="body"/>. Every store/context resolved
        /// inside routes to this tenant's database — the same path a real request takes.
        /// </summary>
        private static async Task InTenantScopeAsync(WebApplication app, Alias alias, Func<IServiceProvider, Task> body)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            await body(sp);
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            await BestEffortAsync(() => app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token));
            await BestEffortAsync(() => app.DisposeAsync().AsTask());
        }

        private static async Task BestEffortAsync(Func<Task> operation)
        {
            try
            {
                var task = operation();
                await Task.WhenAny(task, Task.Delay(ShutdownTimeout));
            }
            catch
            {
                // best effort
            }
        }

        private static async Task BestEffortDropRavenAsync(string database)
        {
            try
            {
                using var store = (DocumentStore)new DocumentStore { Urls = [RavenUrl], Database = database }.Initialize();
                await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(database, hardDelete: true));
            }
            catch
            {
                // best effort — a leftover database on a disposable server is harmless, but we try not to leak
            }
        }

        private static void BestEffortDropPostgres(string database)
        {
            try
            {
                PostgresTestEndpoint.DropDatabase(database);
            }
            catch
            {
                // best effort — but on SHARED infra a leftover is not harmless; the janitorial sweep is manual
            }
        }

        private static string PostgresConnectionString(out string database)
        {
            database = $"stomme_step5_{Guid.NewGuid():N}";
            return PostgresTestEndpoint.ConnectionString(database);
        }

        private static string RavenConnectionString(out string database)
        {
            database = "stomme_step5_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            return $"Url={RavenUrl};Database={database}";
        }

        private static string Render(string? value) => value is null ? "<null>" : $"'{value}'";

        private static string Render(DateTime? value) => value.HasValue ? value.Value.ToString("O") : "<null>";

        private static bool IsReachable(string host, int port)
        {
            try
            {
                using var tcp = new TcpClient();
                return tcp.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch
            {
                return false;
            }
        }

        private sealed class Observation
        {
            public bool UrlMappingSessionHit { get; set; }
            public string? UrlMappingStoredCase { get; set; }
            public string UrlMappingSessionResult { get; set; } = "(not run)";
            public bool UrlMappingRepoHit { get; set; }
            public string UrlMappingRepoResult { get; set; } = "(not run)";

            public bool PageSessionHit { get; set; }
            public string? PageStoredCase { get; set; }
            public string PageSessionResult { get; set; } = "(not run)";
            public bool PageRepoHit { get; set; }
            public string PageRepoResult { get; set; } = "(not run)";

            public bool FolderSessionHit { get; set; }
            public bool FolderRepoHit { get; set; }
            public string FolderResult { get; set; } = "(not run)";

            public bool NotificationIsDeleted { get; set; }
            public DateTime? NotificationDeletedOn { get; set; }
            public bool NotificationDeletedByStamped { get; set; }
            public string? NotificationDeletedBy { get; set; }
            public string NotificationResult { get; set; } = "(not run)";
        }

        // A throwaway content root plus the override the installer's self-writes land in, so the repository's
        // appsettings.json is never touched — the isolation the render guard and the D54 tests share.
        private sealed class ScratchInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstallation(string databaseType, string connectionString)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-step5-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = databaseType },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = connectionString },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "Step5!Drive$2026",
                        ["HostEmail"] = "step5-drive@example.com",
                        ["HostName"] = "Host"
                    }
                };

                System.IO.File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);

                // Release this install's SQLite pools first, or the delete below fails on a file the host still
                // holds - which is what the catch was quietly absorbing.
                ScratchSqlite.ReleaseInstall(ContentRoot);

                try
                {
                    Directory.Delete(ContentRoot, true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }
}
