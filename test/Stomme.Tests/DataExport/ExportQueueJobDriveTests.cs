#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The export queue JOB on a really installed site.
    /// </summary>
    /// <remarks>
    /// Everything the processor suites stub is real here: the hosted job enters the tenant and site scopes itself,
    /// the requester is re-authorised from rows the test wrote through the framework's own repositories, and the
    /// artifact is a File row plus bytes in the site's private <c>Exports/</c> folder. The assertions are about
    /// WHERE things ended up - the physical path under the right tenant and site, the folder's permission rows -
    /// because that is what a mis-set scope gets wrong while every in-memory check stays green.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class ExportQueueJobDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public ExportQueueJobDriveTests(ITestOutputHelper output) => _output = output;

        private static JsonObject Settings(bool enabled) => new()
        {
            ["DataExport"] = new JsonObject { ["Enabled"] = enabled, ["HeartbeatRows"] = 2 },
        };

        private static void AddTestSource(WebApplicationBuilder builder) =>
            builder.Services.AddSingleton<IExportSource, FiveRowSource>();

        [Theory]
        [InlineData("sqlite")]
        [InlineData("postgres")]
        [InlineData("raven")]
        public async Task The_job_renders_a_queued_export_into_the_sites_private_Exports_folder(string provider)
        {
            IGateStore store = provider switch
            {
                "sqlite" => new SqliteGateStore(),
                "postgres" => new PostgresGateStore(),
                _ => new RavenGateStore(),
            };

            using var scratch = new ScratchGateInstall(store, Host, Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output, AddTestSource, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);

                var alias = await GateDrive.AliasAsync(app, Host);
                var siteId = alias.SiteId;
                var exporters = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var allowed = await GateDrive.AddUserAsync(app, Host, "exp-allowed", "All0wed!Pass$2026", "exp-allowed@gate.test", "Exporters");
                var refused = await GateDrive.AddUserAsync(app, Host, "exp-refused", "Refus3d!Pass$2026", "exp-refused@gate.test");

                var (allowedJob, refusedJob) = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    foreach (var operation in new[] { "create", "fiverows" })
                    {
                        await permissions.AddPermissionAsync(new Permission
                        {
                            SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                            PermissionName = operation, RoleId = exporters.RoleId, IsAuthorized = true,
                        });
                    }

                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var jobs = sp.GetRequiredService<IExportJobRepository>();
                    ExportJobAggregate Queue(int userId) => ExportJobAggregate.Request(
                        Guid.NewGuid(), tenantId, siteId, userId, FiveRowSource.SourceKey, ExportFormat.Csv, null,
                        ExportOptionsJson.Serialize(ExportRenderOptions.Default), DateTime.UtcNow);

                    var first = Queue(allowed.UserId);
                    var second = Queue(refused.UserId);
                    await jobs.AddAsync(first);
                    await jobs.AddAsync(second);
                    return (first.Id, second.Id);
                });

                var log = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                _output.WriteLine($"[{provider}][job] {log}");
                Assert.Contains("1 Completed, 1 Failed", log);

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var jobs = sp.GetRequiredService<IExportJobRepository>();
                    var done = (await jobs.GetByIdAsync(allowedJob))!;
                    var denied = (await jobs.GetByIdAsync(refusedJob))!;

                    // ---- the refused requester: a CODE, no file, the source never opened for them ----
                    Assert.Equal(ExportJobStatus.Failed, denied.Status);
                    Assert.Equal(ExportFailureCode.NotAuthorized, denied.FailureCode);
                    Assert.Null(denied.FileId);

                    // ---- the allowed requester ----
                    Assert.Equal(ExportJobStatus.Ready, done.Status);
                    Assert.Equal(5, done.RowCount);
                    Assert.NotNull(done.FileId);

                    var file = await sp.GetRequiredService<IFileRepository>().GetFileAsync(done.FileId!.Value);
                    Assert.NotNull(file);
                    Assert.Equal(done.Id.ToString("N") + ".csv", file.Name);
                    Assert.Equal("csv", file.Extension);
                    Assert.Equal(done.SizeBytes, file.Size);

                    // ---- WHERE it is: the right tenant, the right site, under ContentRoot (never WebRoot) ----
                    var environment = sp.GetRequiredService<IWebHostEnvironment>();
                    var expectedDirectory = Path.Combine(
                        environment.ContentRootPath, "Content", "Tenants", alias.TenantId.ToString(), "Sites", siteId.ToString(), "Exports");
                    var path = Path.Combine(expectedDirectory, file.Name);
                    Assert.True(System.IO.File.Exists(path), $"the artifact is not at {path}");
                    Assert.False(path.StartsWith(environment.WebRootPath, StringComparison.OrdinalIgnoreCase), "the artifact sits under WebRoot, where it is a static file");

                    var text = await System.IO.File.ReadAllTextAsync(path);
                    _output.WriteLine($"[{provider}][artifact] {text.Replace("\r\n", " | ")}");
                    Assert.Equal(done.SizeBytes, new FileInfo(path).Length);
                    Assert.StartsWith("N,Text\r\n1,row 1\r\n", text);
                    // A formula-shaped value went through the REAL pipeline and came out neutralised.
                    Assert.Contains("\r\n5,\"'=HYPERLINK(\"\"http://x\"\")\"\r\n", text);

                    // No attempt's temp file is left beside it.
                    Assert.Empty(Directory.GetFiles(expectedDirectory, "*.tmp"));

                    // ---- the folder: private, and Administrators ALONE on all three ----
                    var exports = sp.GetRequiredService<ExportsFolder>();
                    var folder = await exports.FindAsync(siteId);
                    Assert.NotNull(folder);
                    Assert.Equal(FolderTypes.Private, folder.Type);
                    Assert.True(folder.IsSystem);

                    var rows = (await sp.GetRequiredService<IPermissionRepository>().GetPermissionsAsync(siteId, EntityNames.Folder, folder.FolderId)).ToList();
                    _output.WriteLine($"[{provider}][folder rows] {GateDrive.Describe(rows)}");
                    Assert.Equal(3, rows.Count);
                    Assert.All(rows, row =>
                    {
                        Assert.Equal(RoleNames.Admin, row.RoleName);
                        Assert.True(row.IsAuthorized);
                    });
                    Assert.Equal(
                        new[] { PermissionNames.Browse, PermissionNames.Edit, PermissionNames.View }.OrderBy(n => n),
                        rows.Select(row => row.PermissionName).OrderBy(n => n));

                    // ---- the audit trail, written at the transition ----
                    var audit = sp.GetRequiredService<IExportAuditRepository>();
                    Assert.Contains(await audit.ListByJobAsync(siteId, allowedJob), e => e.Action == ExportAuditAction.Completed);
                    Assert.Contains(await audit.ListByJobAsync(siteId, refusedJob), e => e.Action == ExportAuditAction.Failed && e.FailureCode == ExportFailureCode.NotAuthorized);

                    // ---- refutation 14: a file MOVED out of Exports is no longer the module's to hand out ----
                    Assert.True(await exports.ContainsAsync(siteId, file));
                    Assert.False(await exports.ContainsAsync(siteId + 1000, file));

                    var root = await sp.GetRequiredService<IFolderRepository>().GetFolderAsync(siteId, string.Empty);
                    var moved = await sp.GetRequiredService<IFileRepository>().GetFileAsync(file.FileId, false);
                    moved.FolderId = root.FolderId;
                    await sp.GetRequiredService<IFileRepository>().UpdateFileAsync(moved);
                    return true;
                });

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var done = (await sp.GetRequiredService<IExportJobRepository>().GetByIdAsync(allowedJob))!;
                    var file = await sp.GetRequiredService<IFileRepository>().GetFileAsync(done.FileId!.Value);
                    Assert.False(await sp.GetRequiredService<ExportsFolder>().ContainsAsync(siteId, file), "a file moved to another folder is still treated as an export artifact");
                    return true;
                });

                // ---- a second run finds nothing to do, and creates no second folder ----
                var again = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                Assert.DoesNotContain("Completed", again);

                var folders = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<IFolderRepository>().GetFoldersAsync(siteId)).Count(f => f.Path == ExportsFolder.FolderPath));
                Assert.Equal(1, folders);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_feature_OFF_the_job_does_nothing_and_a_stock_site_gains_no_folder()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output, AddTestSource, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                var jobId = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    Assert.False(sp.GetRequiredService<DataExportOptions>().Enabled, "DataExport:Enabled is on in a stock configuration");

                    var job = ExportJobAggregate.Request(
                        Guid.NewGuid(), sp.GetRequiredService<ITenantContext>().CurrentTenantId!, siteId, 1, FiveRowSource.SourceKey,
                        ExportFormat.Csv, null, "{}", DateTime.UtcNow);
                    await sp.GetRequiredService<IExportJobRepository>().AddAsync(job);
                    return job.Id;
                });

                var log = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                _output.WriteLine("[disabled][job] " + log);
                Assert.Contains("Disabled", log);

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    Assert.Equal(ExportJobStatus.Queued, (await sp.GetRequiredService<IExportJobRepository>().GetByIdAsync(jobId))!.Status);
                    Assert.Null(await sp.GetRequiredService<ExportsFolder>().FindAsync(siteId));
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_role_held_on_ONE_site_does_not_authorise_the_same_user_on_ANOTHER_site_in_the_same_run()
        {
            // The alias is what scopes the framework's caches: user roles are cached under (alias, "UserRoles:{id}").
            // A job that set the tenant but not the alias would share that key across sites, and the roles read for
            // site A would answer for site B. So: one user, a member of "Exporters" on A only; BOTH sites grant the
            // keys to a role of that NAME; A is drained first, which is what would warm the cache.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output, AddTestSource, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteA = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var siteB = (await CreateSecondSiteAsync(app)).SiteId;
                Assert.NotEqual(siteA, siteB);

                var exportersA = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var exportersB = await GateDrive.EnsureRoleAsync(app, SecondSiteHost, "Exporters");
                Assert.NotEqual(exportersA.RoleId, exportersB.RoleId);
                var user = await GateDrive.AddUserAsync(app, Host, "exp-site-a", "S1teA!Pass$2026", "exp-site-a@gate.test", "Exporters");

                foreach (var (host, site, role) in new[] { (Host, siteA, exportersA), (SecondSiteHost, siteB, exportersB) })
                {
                    await GateDrive.InTenantAsync(app, host, async (sp, _) =>
                    {
                        foreach (var operation in new[] { "create", "fiverows" })
                        {
                            await sp.GetRequiredService<IPermissionRepository>().AddPermissionAsync(new Permission
                            {
                                SiteId = site, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                                PermissionName = operation, RoleId = role.RoleId, IsAuthorized = true,
                            });
                        }

                        return true;
                    });
                }

                var (jobA, jobB) = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    ExportJobAggregate Queue(int site) => ExportJobAggregate.Request(
                        Guid.NewGuid(), tenantId, site, user.UserId, FiveRowSource.SourceKey, ExportFormat.Csv, null,
                        ExportOptionsJson.Serialize(ExportRenderOptions.Default), DateTime.UtcNow);

                    var a = Queue(siteA);
                    var b = Queue(siteB);
                    await sp.GetRequiredService<IExportJobRepository>().AddAsync(a);
                    await sp.GetRequiredService<IExportJobRepository>().AddAsync(b);
                    return (a.Id, b.Id);
                });

                var log = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                _output.WriteLine("[two sites][job] " + log);

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var jobs = sp.GetRequiredService<IExportJobRepository>();
                    Assert.Equal(ExportJobStatus.Ready, (await jobs.GetByIdAsync(jobA))!.Status);

                    var onB = (await jobs.GetByIdAsync(jobB))!;
                    Assert.True(onB.Status == ExportJobStatus.Failed && onB.FailureCode == ExportFailureCode.NotAuthorized,
                        $"site B's export is {onB.Status}/{onB.FailureCode}: a role held on site A authorised the user on site B");

                    // And the one artifact is under site A's directory, with nothing rendered into site B's.
                    var sites = Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "Content", "Tenants", alias.TenantId.ToString(), "Sites");
                    Assert.Single(Directory.GetFiles(Path.Combine(sites, siteA.ToString(), "Exports"), "*.csv"));
                    var exportsB = Path.Combine(sites, siteB.ToString(), "Exports");
                    Assert.True(!Directory.Exists(exportsB) || Directory.GetFiles(exportsB).Length == 0, "something was written into site B's Exports directory");
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_second_TENANTS_export_lands_under_the_second_tenants_directory_while_the_firsts_scope_is_open()
        {
            // Refutation 3: the physical path is built from the RESOLVED tenant. A job that ran tenant two's queue
            // with tenant one's scope would write tenant two's export under tenant one's directory.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output, AddTestSource, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var firstAlias = await GateDrive.AliasAsync(app, Host);
                var siteId = firstAlias.SiteId;

                var exporters = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var user = await GateDrive.AddUserAsync(app, Host, "exp-tenant-two", "T3nant2!Pass$2026", "exp-tenant-two@gate.test", "Exporters");
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    foreach (var operation in new[] { "create", "fiverows" })
                    {
                        await sp.GetRequiredService<IPermissionRepository>().AddPermissionAsync(new Permission
                        {
                            SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                            PermissionName = operation, RoleId = exporters.RoleId, IsAuthorized = true,
                        });
                    }

                    return true;
                });

                // A SECOND tenant over the SAME database, with an alias onto the same site (the metering fixture).
                Tenant secondTenant;
                using (var scope = app.Services.CreateScope())
                {
                    var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                    var master = (await tenants.GetTenantsAsync()).Single(tenant => tenant.TenantId == firstAlias.TenantId);
                    secondTenant = await tenants.AddTenantAsync(new Tenant
                    {
                        Name = "ExportSecond", DBConnectionString = master.DBConnectionString, DBType = master.DBType, Version = master.Version,
                    });
                    await scope.ServiceProvider.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                    {
                        Name = "second-tenant.localhost", TenantId = secondTenant.TenantId, SiteId = siteId, IsDefault = true,
                    });

                    // The boot and install path decides a RavenDB tenant's database; this row was added behind it, so it
                    // is told what that path decides for a tenant naming the installation's own database - to share it.
                    app.Services.GetService<Stomme.Repository.Stores.RavenTenantDatabases>()?.Set(secondTenant.TenantId, null);
                }

                var firstId = Stomme.Infrastructure.Tenancy.FrameworkTenantId.ToTenantIdOrNull(firstAlias.TenantId)!;
                var secondId = Stomme.Infrastructure.Tenancy.FrameworkTenantId.ToTenantIdOrNull(secondTenant.TenantId)!;
                Assert.NotEqual(firstId, secondId);

                Guid jobId;
                using (app.Services.GetRequiredService<Stomme.Persistence.Abstractions.ITenantScopeFactory>().BeginScope(secondId.Value))
                using (var scope = app.Services.CreateScope())
                {
                    var job = ExportJobAggregate.Request(
                        Guid.NewGuid(), secondId, siteId, user.UserId, FiveRowSource.SourceKey, ExportFormat.Csv, null,
                        ExportOptionsJson.Serialize(ExportRenderOptions.Default), DateTime.UtcNow);
                    await scope.ServiceProvider.GetRequiredService<IExportJobRepository>().AddAsync(job);
                    jobId = job.Id;
                }

                // Run the job FOR tenant two while tenant ONE's ambient scope is held open - what a run over several
                // tenants looks like from inside, and the state a job that trusted the ambient scope would get wrong.
                string log;
                using (app.Services.GetRequiredService<Stomme.Persistence.Abstractions.ITenantScopeFactory>().BeginScope(firstId.Value))
                using (var jobScope = app.Services.CreateScope())
                {
                    jobScope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(secondTenant);
                    log = await new ExportQueueJob(app.Services.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(jobScope.ServiceProvider);
                }

                _output.WriteLine("[two tenants][job] " + log);
                Assert.Contains("1 Completed", log);

                var tenantsRoot = Path.Combine(app.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "Content", "Tenants");
                var underSecond = Path.Combine(tenantsRoot, secondTenant.TenantId.ToString(), "Sites", siteId.ToString(), "Exports");
                var underFirst = Path.Combine(tenantsRoot, firstAlias.TenantId.ToString(), "Sites", siteId.ToString(), "Exports");
                _output.WriteLine($"[two tenants] expecting the artifact under {underSecond}");

                Assert.True(Directory.Exists(underSecond) && Directory.GetFiles(underSecond, jobId.ToString("N") + ".csv").Length == 1,
                    "tenant two's export is not under tenant two's directory");
                Assert.True(!Directory.Exists(underFirst) || Directory.GetFiles(underFirst, "*.csv").Length == 0,
                    "tenant two's export was written under tenant ONE's directory");

                // Tenant one cannot see the job at all.
                using (app.Services.GetRequiredService<Stomme.Persistence.Abstractions.ITenantScopeFactory>().BeginScope(firstId.Value))
                using (var scope = app.Services.CreateScope())
                {
                    Assert.Empty(await scope.ServiceProvider.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId));
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Expired_artifacts_are_retired_and_a_file_that_LEFT_the_folder_is_never_deleted()
        {
            var clock = new ShiftableClock();
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output, builder =>
            {
                AddTestSource(builder);
                builder.Services.AddSingleton<IExportClock>(clock);
            }, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var exporters = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var user = await GateDrive.AddUserAsync(app, Host, "exp-retention", "Ret3ntion!Pass$2026", "exp-retention@gate.test", "Exporters");

                var (kept, moved) = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    foreach (var operation in new[] { "create", "fiverows" })
                    {
                        await sp.GetRequiredService<IPermissionRepository>().AddPermissionAsync(new Permission
                        {
                            SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                            PermissionName = operation, RoleId = exporters.RoleId, IsAuthorized = true,
                        });
                    }

                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var jobs = sp.GetRequiredService<IExportJobRepository>();
                    ExportJobAggregate Queue() => ExportJobAggregate.Request(
                        Guid.NewGuid(), tenantId, siteId, user.UserId, FiveRowSource.SourceKey, ExportFormat.Csv, null,
                        ExportOptionsJson.Serialize(ExportRenderOptions.Default), clock.UtcNow);

                    var a = Queue();
                    var b = Queue();
                    await jobs.AddAsync(a);
                    await jobs.AddAsync(b);
                    return (a.Id, b.Id);
                });

                Task<string> RunJobAsync() => GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));

                Assert.Contains("2 Completed", await RunJobAsync());

                // One artifact is MOVED to the root folder - row and bytes, the way FileController moves a file.
                var (keptPath, movedPath, exportsDirectory) = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var files = sp.GetRequiredService<IFileRepository>();
                    var jobs = sp.GetRequiredService<IExportJobRepository>();
                    var keptFile = (await jobs.GetByIdAsync(kept))!.FileId!.Value;
                    var movedFile = (await jobs.GetByIdAsync(moved))!.FileId!.Value;

                    var keptAt = await files.GetFilePathAsync(keptFile);
                    var from = await files.GetFilePathAsync(movedFile);
                    var root = await sp.GetRequiredService<IFolderRepository>().GetFolderAsync(siteId, string.Empty);
                    var row = await files.GetFileAsync(movedFile, false);
                    row.FolderId = root.FolderId;
                    await files.UpdateFileAsync(row);
                    var to = await files.GetFilePathAsync(movedFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    System.IO.File.Move(from, to);

                    // Two temp files beside the artifacts: one abandoned long ago, one a render could still be writing.
                    var directory = Path.GetDirectoryName(keptAt)!;
                    System.IO.File.WriteAllText(Path.Combine(directory, "stale.1.tmp"), "x");
                    System.IO.File.SetLastWriteTimeUtc(Path.Combine(directory, "stale.1.tmp"), DateTime.UtcNow.AddDays(-2));
                    System.IO.File.WriteAllText(Path.Combine(directory, "fresh.1.tmp"), "x");
                    return (keptAt, to, directory);
                });

                // ---- inside the retention window nothing is touched --------------------------------------------------
                clock.Shift(TimeSpan.FromDays(6));
                var early = await RunJobAsync();
                _output.WriteLine("[retention][day 6] " + early);
                Assert.Contains("0 Expired", early);
                // The abandoned temp file goes on the first pass; the one a render could still be writing stays.
                Assert.Contains("1 Temp File(s) Swept", early);
                Assert.True(System.IO.File.Exists(keptPath));

                // ---- past it ------------------------------------------------------------------------------------------
                clock.Shift(TimeSpan.FromDays(2));
                var late = await RunJobAsync();
                _output.WriteLine("[retention][day 8] " + late);
                Assert.Contains("2 Expired (1 Deleted, 1 Left Alone)", late);

                Assert.False(System.IO.File.Exists(keptPath), "the expired artifact is still on disk");
                Assert.True(System.IO.File.Exists(movedPath), "the purge deleted a file that was no longer in the Exports folder");
                Assert.False(System.IO.File.Exists(Path.Combine(exportsDirectory, "stale.1.tmp")), "an abandoned temp file was left behind");
                Assert.True(System.IO.File.Exists(Path.Combine(exportsDirectory, "fresh.1.tmp")), "a temp file a render could still be writing was swept");

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var jobs = sp.GetRequiredService<IExportJobRepository>();
                    foreach (var id in new[] { kept, moved })
                    {
                        var job = (await jobs.GetByIdAsync(id))!;
                        Assert.Equal(ExportJobStatus.Expired, job.Status);
                        Assert.Null(job.FileId);
                        var trail = await sp.GetRequiredService<IExportAuditRepository>().ListByJobAsync(siteId, id);
                        Assert.Contains(trail, e => e.Action == ExportAuditAction.Expired && e.ActorUserId is null);
                        // The trail OUTLIVES the artifact: the eight-day-old Completed entry is still here although the
                        // seven-day artifact retention has passed. Purging audit on the artifact's clock would lose the
                        // record of an export at the very moment the export itself goes.
                        Assert.Contains(trail, e => e.Action == ExportAuditAction.Completed);
                    }

                    // The deleted artifact's File row is gone; the moved file's row is someone else's now and stays.
                    var rootFolder = await sp.GetRequiredService<IFolderRepository>().GetFolderAsync(siteId, string.Empty);
                    var exports = await sp.GetRequiredService<ExportsFolder>().FindAsync(siteId);
                    Assert.Empty(await sp.GetRequiredService<IFileRepository>().GetFilesAsync(exports.FolderId));
                    Assert.Single(await sp.GetRequiredService<IFileRepository>().GetFilesAsync(rootFolder.FolderId));
                    return true;
                });

                // ---- a third run has nothing left to do; the audit trail outlives the artifact, then goes too ---------
                Assert.Equal(string.Empty, await RunJobAsync());
                clock.Shift(TimeSpan.FromDays(100));
                var purge = await RunJobAsync();
                _output.WriteLine("[retention][day 108] " + purge);
                Assert.Contains("Audit Row(s) Purged", purge);

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    Assert.Empty(await sp.GetRequiredService<IExportAuditRepository>().ListBySiteAsync(siteId));
                    // The job rows themselves are history and stay.
                    Assert.Equal(2, (await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId)).Count);
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private sealed class ShiftableClock : IExportClock
        {
            private long _offsetTicks;

            public DateTime UtcNow => DateTime.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

            public void Shift(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
        }

        [Fact]
        public async Task A_SITE_sets_its_own_retention_and_a_value_it_cannot_mean_falls_back_to_the_configuration()
        {
            var clock = new ShiftableClock();
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output, builder =>
            {
                AddTestSource(builder);
                builder.Services.AddSingleton<IExportClock>(clock);
            }, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var exporters = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var user = await GateDrive.AddUserAsync(app, Host, "exp-site-retention", "Ret3ntion!Pass$2026", "exp-site-retention@gate.test", "Exporters");

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    foreach (var operation in new[] { "create", "fiverows" })
                    {
                        await sp.GetRequiredService<IPermissionRepository>().AddPermissionAsync(new Permission
                        {
                            SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                            PermissionName = operation, RoleId = exporters.RoleId, IsAuthorized = true,
                        });
                    }

                    return true;
                });

                // What the SITE says -> the days a freshly rendered artifact is kept. 7 is the configuration's answer.
                foreach (var (stored, expectedDays) in new (string?, int)[] { (null, 7), ("2", 2), ("abc", 7), ("0", 7), ("-3", 7), ("99999", 3650) })
                {
                    await SetSiteSettingAsync(app, siteId, ExportSiteSettingNames.Retention, stored);

                    var id = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    {
                        var job = ExportJobAggregate.Request(
                            Guid.NewGuid(), sp.GetRequiredService<ITenantContext>().CurrentTenantId!, siteId, user.UserId, FiveRowSource.SourceKey,
                            ExportFormat.Csv, null, ExportOptionsJson.Serialize(ExportRenderOptions.Default), clock.UtcNow);
                        await sp.GetRequiredService<IExportJobRepository>().AddAsync(job);
                        return job.Id;
                    });

                    Assert.Contains("1 Completed", await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                        new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp)));

                    var done = await GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IExportJobRepository>().GetByIdAsync(id))!);
                    var kept = (done.ExpiresOnUtc!.Value - done.CompletedOnUtc!.Value).TotalDays;
                    _output.WriteLine($"[site retention] stored={stored ?? "(absent)"} -> kept {kept} day(s)");
                    Assert.Equal(expectedDays, kept, 3);
                }

                // The trail outlives the artifact PER SITE: an audit retention shorter than the site's own artifact
                // retention is raised to it, whichever of the two the site overrode.
                foreach (var (artifact, audit, expectedDays) in new (string?, string?, int)[] { (null, null, 90), ("30", "5", 30), (null, "400", 400), ("200", null, 200) })
                {
                    await SetSiteSettingAsync(app, siteId, ExportSiteSettingNames.Retention, artifact);
                    await SetSiteSettingAsync(app, siteId, ExportSiteSettingNames.AuditRetention, audit);

                    var days = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                        (await sp.GetRequiredService<IExportSiteSettings>().GetAuditRetentionAsync(siteId)).TotalDays);
                    _output.WriteLine($"[site audit retention] artifact={artifact ?? "(absent)"} audit={audit ?? "(absent)"} -> {days} day(s)");
                    Assert.Equal(expectedDays, days);
                }

                // ...and the PURGE obeys the site, not only the service that answers for it. The configuration keeps the
                // trail 90 days; this site says 2 (its artifacts 1). Three days on, everything written above is past it.
                await SetSiteSettingAsync(app, siteId, ExportSiteSettingNames.Retention, "1");
                await SetSiteSettingAsync(app, siteId, ExportSiteSettingNames.AuditRetention, "2");
                var before = await GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IExportAuditRepository>().ListBySiteAsync(siteId)).Count);
                clock.Shift(TimeSpan.FromDays(3));
                var cutoff = clock.UtcNow - TimeSpan.FromDays(2);

                var purge = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                var left = await GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IExportAuditRepository>().ListBySiteAsync(siteId)).ToList());
                _output.WriteLine($"[site audit purge] {before} entries before; {left.Count} after; job: {purge}");

                // One Completed entry per export above (they were queued straight into the store, so no Requested entry).
                Assert.Equal(6, before);
                Assert.DoesNotContain(left, entry => entry.OccurredOnUtc < cutoff);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_REQUEST_can_ask_for_a_PDF_page_and_gets_it_and_a_page_this_engine_does_not_have_is_REFUSED()
        {
            // Found while porting cases by name: the renderer honoured a page size and an orientation, the stored
            // options carried them - and no request could set either. End to end here: the real handler, the real job, and
            // the /MediaBox of the bytes that landed in the Exports folder.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output, AddTestSource, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var exporters = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var user = await GateDrive.AddUserAsync(app, Host, "exp-pdf-page", "PdfP4ge!Pass$2026", "exp-pdf-page@gate.test", "Exporters");

                var id = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    foreach (var operation in new[] { "create", "fiverows" })
                    {
                        await sp.GetRequiredService<IPermissionRepository>().AddPermissionAsync(new Permission
                        {
                            SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                            PermissionName = operation, RoleId = exporters.RoleId, IsAuthorized = true,
                        });
                    }

                    var handler = new Stomme.Modules.DataExport.Application.RequestExportCommandHandler(
                        new Stomme.Modules.DataExport.Application.ExportCaller(
                            new FixedUser(user.UserId, siteId), sp.GetRequiredService<ITenantContext>(), sp.GetRequiredService<SiteState>(), new AllowEverything()),
                        sp.GetRequiredService<ExportSourceRegistry>(),
                        sp.GetRequiredService<Stomme.Modules.DataExport.Rendering.ExportRendererRegistry>(),
                        sp.GetRequiredService<IExportJobRepository>(),
                        sp.GetRequiredService<IExportAuditRepository>(),
                        sp.GetRequiredService<IExportClock>());

                    Stomme.Models.Export.ExportRequest Pdf(string? size, string? orientation) => new()
                    {
                        SourceKey = FiveRowSource.SourceKey, Format = "Pdf", PdfPageSize = size, PdfOrientation = orientation,
                    };

                    foreach (var (size, orientation, expected) in new (string?, string?, string)[] { ("Tabloid", null, "page size"), (null, "Sideways", "orientation"), ("A4 ", null, "page size") })
                    {
                        var refused = await Assert.ThrowsAsync<Stomme.Modules.DataExport.Application.ExportRequestRejectedException>(
                            () => handler.Handle(new(Pdf(size, orientation)), default));
                        _output.WriteLine($"[refused] size={size ?? "(null)"} orientation={orientation ?? "(null)"} -> {refused.Message}");
                        Assert.Contains(expected, refused.Message);
                    }

                    // Nothing was queued by a refusal.
                    Assert.Empty(await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId));

                    // Any case is accepted, and stored in its canonical spelling.
                    var accepted = await handler.Handle(new(Pdf("letter", "LANDSCAPE")), default);
                    var stored = ExportOptionsJson.Parse((await sp.GetRequiredService<IExportJobRepository>().GetByIdAsync(accepted))!.OptionsJson);
                    Assert.Equal("Letter", stored.PdfPageSize);
                    Assert.Equal("Landscape", stored.PdfOrientation);
                    return accepted;
                });

                Assert.Contains("1 Completed", await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp)));

                var pdf = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var job = (await sp.GetRequiredService<IExportJobRepository>().GetByIdAsync(id))!;
                    var path = await sp.GetRequiredService<IFileRepository>().GetFilePathAsync(job.FileId!.Value);
                    return System.Text.Encoding.Latin1.GetString(await System.IO.File.ReadAllBytesAsync(path));
                });

                // Letter is 612 x 792; landscape turns it.
                Assert.Contains("/MediaBox [0 0 792 612]", pdf, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<bool> SetSiteSettingAsync(WebApplication app, int siteId, string name, string? value) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                var settings = sp.GetRequiredService<ISettingRepository>();
                var existing = await settings.GetSettingAsync(EntityNames.Site, siteId, name);
                if (value is null)
                {
                    if (existing is not null)
                    {
                        await settings.DeleteSettingAsync(EntityNames.Site, existing.SettingId);
                    }
                }
                else if (existing is null)
                {
                    await settings.AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = siteId, SettingName = name, SettingValue = value, IsPrivate = true });
                }
                else
                {
                    existing.SettingValue = value;
                    await settings.UpdateSettingAsync(existing);
                }

                return true;
            });

        [Fact]
        public async Task A_LONG_LIVED_scope_still_sees_a_job_another_scope_changed()
        {
            // An Interactive Server CIRCUIT is one DI scope for as long as the page is open. Found by driving the page in a
            // browser: the queue job moved an export to Ready, the database said so, and the page - refreshed twice - kept
            // saying Queued, because the EF context behind the circuit was still tracking the instance it first read. No
            // HTTP or SSR test can see this (a request has a scope of its own), so the circuit is reproduced here as what
            // it is: one scope, read, changed from elsewhere, read again.
            //
            // This module first answered it with a fresh DI scope per handler (ExportStoreScope). The same staleness was later
            // measured on Metering, Notifications and Health, and fixed where it lives - the storage-blind module repository
            // no longer tracks (EfRepository.TrackReads) - so the handlers read through the circuit's own scope again, and
            // THIS is the test that says they may.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output, AddTestSource, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var alias = await GateDrive.AliasAsync(app, Host);

                await GateDrive.InTenantAsync(app, Host, async (circuit, _) =>
                {
                    var tenantId = circuit.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var job = ExportJobAggregate.Request(
                        Guid.NewGuid(), tenantId, alias.SiteId, 1, FiveRowSource.SourceKey, ExportFormat.Csv, null, "{}", DateTime.UtcNow);

                    var caller = new Stomme.Modules.DataExport.Application.ExportCaller(
                        new FixedUser(1, alias.SiteId), circuit.GetRequiredService<ITenantContext>(),
                        circuit.GetRequiredService<SiteState>(), new AllowEverything());
                    // The circuit's OWN repository, which is what the handlers are given.
                    var naive = circuit.GetRequiredService<IExportJobRepository>();
                    var clock = circuit.GetRequiredService<IExportClock>();
                    var list = new Stomme.Modules.DataExport.Application.ListSiteExportsQueryHandler(caller, naive, clock);
                    await naive.AddAsync(job);
                    Assert.Equal(ExportJobStatus.Queued, (await naive.ListBySiteAsync(alias.SiteId)).Single().Status);
                    Assert.Equal("Queued", (await list.Handle(new(), default)).Single().Status);

                    // Somebody else - the queue job, in a scope of its own - moves it on.
                    await GateDrive.InTenantAsync(app, Host, async (elsewhere, _) =>
                    {
                        var jobs = elsewhere.GetRequiredService<IExportJobRepository>();
                        var loaded = (await jobs.GetByIdAsync(job.Id))!;
                        loaded.Claim(DateTime.UtcNow, TimeSpan.FromMinutes(10));
                        await jobs.UpdateAsync(loaded);
                        return true;
                    });

                    // A re-read through the SAME long-lived scope says what the database says. (With tracking on it
                    // said Queued - remove TrackReads = false from AddModuleAggregatePersistence and it does again.)
                    var reread = (await naive.ListBySiteAsync(alias.SiteId)).Single().Status;
                    _output.WriteLine($"[circuit] re-read through the long-lived scope: {reread}");
                    Assert.Equal(ExportJobStatus.Rendering, reread);
                    Assert.Equal("Rendering", (await list.Handle(new(), default)).Single().Status);

                    // ...and a WRITE from that scope is checked against the row as it is NOW: the requester's Cancel lands
                    // on the first click, instead of being refused as a stale write of the Queued row the page first saw.
                    var cancel = new Stomme.Modules.DataExport.Application.CancelExportCommandHandler(
                        caller, naive, circuit.GetRequiredService<IExportAuditRepository>(), clock);
                    Assert.True(await cancel.Handle(new(job.Id), default));

                    var truth = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                        (await fresh.GetRequiredService<IExportJobRepository>().GetByIdAsync(job.Id))!.Status);
                    Assert.Equal(ExportJobStatus.Cancelled, truth);
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private sealed class FixedUser(int userId, int siteId) : Stomme.Security.Abstractions.ICurrentUser
        {
            public int? UserId => userId;

            public string? UserName => "circuit";

            public bool IsAuthenticated => true;

            public IReadOnlySet<string> Roles { get; } = new HashSet<string>();

            public int SiteId => siteId;
        }

        private sealed class AllowEverything : Stomme.Security.Abstractions.IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        private const string SecondSiteHost = "site-b.localhost";

        private static Task<Site> CreateSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Export Site B", TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme, DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer, SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static, Runtime = Runtimes.Server, Prerender = false, Hybrid = false,
                    EnhancedNavigation = true, CultureCode = "en", AllowRegistration = false, CaptureBrokenUrls = false,
                    VisitorTracking = false, PwaIsEnabled = false,
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = SecondSiteHost, TenantId = alias.TenantId, SiteId = site.SiteId, IsDefault = true,
                });

                return site;
            });

        /// <summary>Five rows, the last carrying a formula-shaped value, behind its own data permission.</summary>
        private sealed class FiveRowSource : IExportSource, ITabularDocument, ITabularTable
        {
            public const string SourceKey = "test.fiverows";

            public string Key => SourceKey;

            public string RequiredPermission => "export:fiverows";

            public TableCardinality TableCardinality => TableCardinality.Single;

            public string Name => "rows";

            public IReadOnlyList<ExportColumn> Columns { get; } = new[]
            {
                new ExportColumn("N", ExportValueKind.Integer),
                new ExportColumn("Text", ExportValueKind.Text),
            };

            public string? Validate(ExportSourceContext context) => null;

            public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default) =>
                Task.FromResult<ITabularDocument>(this);

            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return this;
                await Task.CompletedTask;
            }

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                for (var i = 1; i <= 4; i++)
                {
                    yield return new object?[] { i, "row " + i };
                }

                yield return new object?[] { 5, "=HYPERLINK(\"http://x\")" };
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
