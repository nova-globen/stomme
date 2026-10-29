#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.Portability.Application.Format;
using Stomme.Modules.Portability.Domain;
using Stomme.Modules.Portability.Execution;
using Stomme.Portability;
using Stomme.Portability.Persistence;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// The build JOB and retention on a really installed site: the job enters the tenant and
    /// site scopes itself, a queued package becomes a File row plus bytes in the site's private <c>Portability/</c> folder,
    /// a throwing contributor fails the package with its exception TYPE and nothing of its message, a build left too long
    /// is abandoned, and retention retires artifacts - a subject package after seven days whatever the setting says.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class PortabilityBuildDriveTests
    {
        private const string Host = "localhost";
        private const string Secret = "Server=db;Password=hunter2";

        private readonly ITestOutputHelper _output;

        public PortabilityBuildDriveTests(ITestOutputHelper output) => _output = output;

        private static JsonObject Settings(bool enabled) => new()
        {
            ["Portability"] = new JsonObject { ["Enabled"] = enabled },
        };

        private static void AddFixtures(WebApplicationBuilder builder)
        {
            builder.Services.AddScoped<IPortableEntityContributor, GateThingContributor>();
            builder.Services.AddScoped<IPortableEntityContributor, BrokenContributor>();
            builder.Services.AddScoped<ISubjectDataContributor, GateSubjectContributor>();
        }

        private async Task<WebApplication> InstallAsync(ScratchGateInstall scratch)
        {
            var app = await GateDrive.InstallAsync(scratch, _output, AddFixtures, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(app);
            return app;
        }

        private static Task<string> RunJobAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => new PortabilityBuildJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));

        private static Task<Guid> QueueAsync(WebApplication app, PortabilityPackageKind kind, string? request, int? subject = null, DateTime? createdUtc = null) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var package = PortabilityPackageAggregate.StartBuild(
                    sp.GetRequiredService<ITenantContext>().CurrentTenantId!, alias.SiteId, kind, 1, subject, createdUtc ?? DateTime.UtcNow, request);
                await sp.GetRequiredService<IPortabilityPackageRepository>().AddAsync(package);
                return package.Id;
            });

        private static Task<PortabilityPackageAggregate> ReadAsync(WebApplication app, Guid id) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) => (await sp.GetRequiredService<IPortabilityPackageRepository>().FindForSiteAsync(alias.SiteId, id))!);

        [Fact]
        public async Task The_job_builds_queued_packages_into_the_private_folder_and_fails_a_broken_one_by_TYPE()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await InstallAsync(scratch);
            try
            {
                var config = await QueueAsync(app, PortabilityPackageKind.ConfigTransfer, """{"gate-thing":null}""");
                var subject = await QueueAsync(app, PortabilityPackageKind.SubjectData, null, subject: 1);
                var broken = await QueueAsync(app, PortabilityPackageKind.ConfigTransfer, """{"gate-broken":null}""");

                var log = await RunJobAsync(app);
                _output.WriteLine("[job] " + log);
                Assert.DoesNotContain("hunter2", log, StringComparison.Ordinal);

                // ---- the configuration package: Ready, a File row in THIS site's private folder, a readable document ----
                var ready = await ReadAsync(app, config);
                Assert.Equal(PortabilityPackageStatus.Ready, ready.Status);
                Assert.Equal((2, "1.1"), (ready.EntityCount, ready.BundleFormat));
                Assert.NotNull(ready.ExpiresOnUtc);
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var file = (await sp.GetRequiredService<IFileRepository>().GetFileAsync(ready.ArtifactFileId!.Value))!;
                    var folder = (await sp.GetRequiredService<IFolderRepository>().GetFolderAsync(file.FolderId))!;
                    Assert.Equal((PortabilityFolder.FolderPath, FolderTypes.Private, alias.SiteId), (folder.Path, folder.Type, folder.SiteId));

                    var permissions = await sp.GetRequiredService<IPermissionRepository>().GetPermissionsAsync(alias.SiteId, EntityNames.Folder, folder.FolderId);
                    Assert.All(permissions, permission => Assert.Equal(RoleNames.Admin, permission.RoleName));

                    var path = Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "Content", "Tenants", alias.TenantId.ToString(), "Sites", alias.SiteId.ToString(), "Portability", ready.Id.ToString("N") + ".json");
                    Assert.True(System.IO.File.Exists(path), $"the artifact is not at {path}");
                    var read = new PortabilityBundleSerializer().Read(await System.IO.File.ReadAllTextAsync(path));
                    Assert.True(read.Success, string.Join("; ", read.Report));
                    Assert.Equal(new[] { "gate-thing:a", "gate-thing:b" }, read.Document!.Entities.Select(e => $"{e.EntityType}:{e.EntityId}"));
                    Assert.Equal((int?)1, read.Document.Provenance.ExportedByUserId);

                    var audit = await sp.GetRequiredService<IPortabilityAuditRepository>().ListRecentAsync(alias.SiteId, DateTime.UtcNow.AddHours(-1), 50);
                    Assert.Contains(audit, line => line.PackageId == config && line.Action == PortabilityAuditAction.Built);
                    Assert.Contains(audit, line => line.PackageId == broken && line.Action == PortabilityAuditAction.BuildFailed);
                    return true;
                });

                // ---- the subject package: Ready, and it names its subject ------------------------------------------------
                var subjectPackage = await ReadAsync(app, subject);
                Assert.Equal(PortabilityPackageStatus.Ready, subjectPackage.Status);
                Assert.True(subjectPackage.EntityCount >= 2, $"the subject package holds {subjectPackage.EntityCount} entities - the fixture's AND the shipped contributors' were expected");

                // ---- the broken one: Failed, by TYPE, nothing of the message ---------------------------------------------
                var failed = await ReadAsync(app, broken);
                Assert.Equal((PortabilityPackageStatus.Failed, nameof(InvalidOperationException)), (failed.Status, failed.FailureText));
                Assert.Null(failed.ArtifactFileId);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Disabled_builds_nothing_and_a_build_left_too_long_is_abandoned()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: false));
            var app = await InstallAsync(scratch);
            try
            {
                var queued = await QueueAsync(app, PortabilityPackageKind.ConfigTransfer, """{"gate-thing":null}""");
                Assert.Contains("Disabled", await RunJobAsync(app), StringComparison.Ordinal);
                Assert.Equal(PortabilityPackageStatus.Building, (await ReadAsync(app, queued)).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }

            using var enabled = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            app = await InstallAsync(enabled);
            try
            {
                var stale = await QueueAsync(app, PortabilityPackageKind.ConfigTransfer, """{"gate-thing":null}""", createdUtc: DateTime.UtcNow.AddHours(-25));
                await RunJobAsync(app);
                var abandoned = await ReadAsync(app, stale);
                Assert.Equal((PortabilityPackageStatus.Failed, "Abandoned"), (abandoned.Status, abandoned.FailureText));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Retention_retires_the_artifact_and_a_subject_package_goes_after_seven_days_whatever_the_setting()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await InstallAsync(scratch);
            try
            {
                // Two packages built eight days ago, one of each kind, with their artifacts really on disk.
                var (config, subject, configPath, subjectPath) = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenant = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var store = sp.GetRequiredService<PortabilityArtifactStore>();
                    var repository = sp.GetRequiredService<IPortabilityPackageRepository>();
                    var then = DateTime.UtcNow.AddDays(-8);
                    async Task<(Guid, string)> Old(PortabilityPackageKind kind, int? who)
                    {
                        var package = PortabilityPackageAggregate.StartBuild(tenant, alias.SiteId, kind, 1, who, then);
                        var fileId = await store.WriteAsync(package, "{}");
                        package.MarkReady(fileId, "1.1", 0, "[]", then, then.AddDays(30));
                        await repository.AddAsync(package);
                        return (package.Id, await sp.GetRequiredService<IFileRepository>().GetFilePathAsync(fileId));
                    }

                    var (c, cp) = await Old(PortabilityPackageKind.ConfigTransfer, null);
                    var (s, spath) = await Old(PortabilityPackageKind.SubjectData, 1);
                    return (c, s, cp, spath);
                });
                Assert.True(System.IO.File.Exists(configPath) && System.IO.File.Exists(subjectPath));

                // A site setting of 30 days: the configuration package stays, the subject package does not.
                var purged = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    await sp.GetServices<IRetentionPurger>().OfType<PortabilityRetentionPurger>().Single().PurgeAsync(alias.SiteId, 30));

                Assert.Equal(1, purged);
                Assert.Equal(PortabilityPackageStatus.Ready, (await ReadAsync(app, config)).Status);
                var expired = await ReadAsync(app, subject);
                Assert.Equal(PortabilityPackageStatus.Expired, expired.Status);
                Assert.Null(expired.ArtifactFileId);
                Assert.True(System.IO.File.Exists(configPath));
                Assert.False(System.IO.File.Exists(subjectPath), "the expired subject package's bytes are still on disk");

                // A setting of 7 retires the configuration package too.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    await sp.GetServices<IRetentionPurger>().OfType<PortabilityRetentionPurger>().Single().PurgeAsync(alias.SiteId, 7));
                Assert.Equal(PortabilityPackageStatus.Expired, (await ReadAsync(app, config)).Status);
                Assert.False(System.IO.File.Exists(configPath));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private sealed class GateThingContributor : IPortableEntityContributor
        {
            public string EntityType => "gate-thing";

            public int CurrentSchemaVersion => 1;

            public IReadOnlyCollection<int> ReadableSchemaVersions => new[] { 1 };

            public Task<IReadOnlyList<PortableEntity>> ExportAsync(PortabilityScope scope, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PortableEntity>>(new[] { "b", "a" }
                    .Where(id => scope.EntityIds is null || scope.EntityIds.Contains(id))
                    .Select(id => new PortableEntity(new PortableEntityRef(EntityType, id), "thing " + id, 1, "{\"site\":" + scope.SiteId + "}"))
                    .ToList());

            public Task<string?> ValidateImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

            public Task<EntityImportOutcome> ImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default) =>
                Task.FromResult(new EntityImportOutcome(EntityImportResult.Imported, entity.Ref.EntityId));
        }

        private sealed class BrokenContributor : IPortableEntityContributor
        {
            public string EntityType => "gate-broken";

            public int CurrentSchemaVersion => 1;

            public IReadOnlyCollection<int> ReadableSchemaVersions => new[] { 1 };

            public Task<IReadOnlyList<PortableEntity>> ExportAsync(PortabilityScope scope, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException(Secret);

            public Task<string?> ValidateImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

            public Task<EntityImportOutcome> ImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException(Secret);
        }

        private sealed class GateSubjectContributor : ISubjectDataContributor
        {
            public string SubjectDataType => "gate-subject";

            public Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PortableEntity>>(new[] { new PortableEntity(new PortableEntityRef("gate-subject", scope.UserId.ToString()), "me", 1, "{}") });
        }
    }
}
