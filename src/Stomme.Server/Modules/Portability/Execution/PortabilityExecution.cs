#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.Portability.Application;
using Stomme.Modules.Portability.Application.Format;
using Stomme.Modules.Portability.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Portability;
using Stomme.Portability.Persistence;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Logging;
using File = Stomme.Models.File;

namespace Stomme.Modules.Portability.Execution
{
    /// <summary>The <c>Portability</c> configuration section (D107 item 10), with its defaults.</summary>
    public sealed class PortabilityOptions
    {
        public const string SectionName = "Portability";

        /// <summary>Off by default: a stock install gains no page, no API, no folder, no build.</summary>
        public bool Enabled { get; set; }

        /// <summary>An import larger than this is refused before it is read (D107 refutation 12).</summary>
        public long MaxPackageBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>A package still Building after this long is Failed as abandoned.</summary>
        public int BuildTimeoutHours { get; set; } = 24;

        /// <summary>How long the portability audit is kept - three years, pending BL-116's erasure posture (D107 Q3).</summary>
        public int AuditRetentionDays { get; set; } = 1095;

        public int FolderCapacityMb { get; set; } = 256;

        public TimeSpan BuildTimeout => TimeSpan.FromHours(Math.Max(1, BuildTimeoutHours));
    }

    /// <summary>
    /// The site's private <c>Portability/</c> folder - the <c>Exports/</c> folder's twin (D108): <see cref="FolderTypes.Private"/>
    /// so it is never served as a static file, Browse/View/Edit for Administrators alone, created on first use and left
    /// as found afterwards.
    /// </summary>
    /// <remarks>
    /// The framework's own file routes authorise by folder View, so this folder's rows are what keeps one person's data
    /// package from a site visitor; the module's download narrows further to the requester and the subject. Nothing in
    /// this module trusts a File id alone: <see cref="ContainsAsync"/> re-asserts the file is in THIS site's folder.
    /// </remarks>
    public sealed class PortabilityFolder(IFolderRepository folders, PortabilityOptions options)
    {
        public const string FolderName = "Portability";

        public const string FolderPath = "Portability/";

        public Task<Folder> FindAsync(int siteId) => folders.GetFolderAsync(siteId, FolderPath);

        public async Task<Folder> EnsureAsync(int siteId)
        {
            var existing = await FindAsync(siteId);
            if (existing is not null)
            {
                return existing;
            }

            var root = await folders.GetFolderAsync(siteId, string.Empty)
                ?? throw new InvalidOperationException($"Site {siteId} has no root folder; its Portability folder cannot be created.");

            return await folders.AddFolderAsync(new Folder
            {
                SiteId = siteId,
                ParentId = root.FolderId,
                Name = FolderName,
                Type = FolderTypes.Private,
                Path = FolderPath,
                Order = 6,
                ImageSizes = string.Empty,
                Capacity = Math.Max(0, options.FolderCapacityMb),
                IsSystem = true,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.Browse, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true),
                },
            });
        }

        public async Task<bool> ContainsAsync(int siteId, File? file)
        {
            if (file is null)
            {
                return false;
            }

            var folder = await FindAsync(siteId);
            return folder is not null && file.FolderId == folder.FolderId;
        }
    }

    /// <summary>A package's bytes as a framework File row in the site's <c>Portability/</c> folder.</summary>
    public sealed class PortabilityArtifactStore(PortabilityFolder portability, IFolderRepository folders, IFileRepository files)
    {
        public async Task<int> WriteAsync(PortabilityPackageAggregate package, string json, CancellationToken ct = default)
        {
            var folder = await portability.EnsureAsync(package.SiteId);
            var directory = await folders.GetFolderPathAsync(folder);
            Directory.CreateDirectory(directory);

            var name = package.Id.ToString("N") + ".json";
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > int.MaxValue)
            {
                throw new InvalidOperationException("The package is too large to store as a site file.");
            }

            await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, ct);
            var row = await files.AddFileAsync(new File
            {
                FolderId = folder.FolderId,
                Name = name,
                Extension = "json",
                Size = bytes.Length,
                ImageHeight = 0,
                ImageWidth = 0,
                Description = package.Kind == PortabilityPackageKind.SubjectData ? "Personal data package" : "Configuration package",
            });

            return row.FileId;
        }

        /// <summary>The artifact's bytes, or null when the file is gone or no longer in this site's Portability folder.</summary>
        public async Task<byte[]?> ReadAsync(PortabilityPackageAggregate package, CancellationToken ct = default)
        {
            if (package.ArtifactFileId is not { } fileId)
            {
                return null;
            }

            var file = await files.GetFileAsync(fileId);
            if (!await portability.ContainsAsync(package.SiteId, file))
            {
                return null;
            }

            var path = await files.GetFilePathAsync(file);
            return System.IO.File.Exists(path) ? await System.IO.File.ReadAllBytesAsync(path, ct) : null;
        }

        /// <summary>Removes the artifact - the physical file AND its row. Idempotent; never touches a file outside the folder.</summary>
        public async Task DeleteAsync(PortabilityPackageAggregate package, CancellationToken ct = default)
        {
            if (package.ArtifactFileId is not { } fileId)
            {
                return;
            }

            var file = await files.GetFileAsync(fileId);
            if (!await portability.ContainsAsync(package.SiteId, file))
            {
                return;
            }

            var path = await files.GetFilePathAsync(file);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }

            await files.DeleteFileAsync(fileId);
        }
    }

    /// <summary>
    /// Builds a site's queued packages (D107 item 3): each Building row becomes Ready with an artifact, or Failed with an
    /// exception TYPE - never a message, which can carry a connection string. A row Building for longer than the timeout
    /// is Failed as abandoned.
    /// </summary>
    public sealed class PortabilityBuilder(
        IPortabilityPackageRepository packages,
        IPortabilityAuditRepository audit,
        PortabilityExporter exporter,
        IEnumerable<ISubjectDataContributor> subjectContributors,
        PortabilityBundleSerializer serializer,
        PortabilityArtifactStore artifacts,
        PortabilityOptions options,
        ITenantContext tenantContext,
        ILogger<PortabilityBuilder> logger)
    {
        /// <summary>How long a Ready or Failed package is kept before retention retires it (the display estimate).</summary>
        public const int DefaultRetentionDays = 7;

        public async Task<(int Built, int Failed)> DrainSiteAsync(int siteId, CancellationToken ct = default)
        {
            var built = 0;
            var failed = 0;
            foreach (var queued in await packages.ListByStatusAsync(siteId, PortabilityPackageStatus.Building, ct))
            {
                if (await BuildAsync(queued, ct))
                {
                    built++;
                }
                else
                {
                    failed++;
                }
            }

            return (built, failed);
        }

        /// <summary>Builds one package; true when it is Ready.</summary>
        public async Task<bool> BuildAsync(PortabilityPackageAggregate package, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            if (now - package.CreatedOnUtc > options.BuildTimeout)
            {
                return await FailAsync(package, "Abandoned", now);
            }

            try
            {
                IReadOnlyList<PortableEntity> entities;
                PortabilityBundleSubject? subject = null;
                if (package.Kind == PortabilityPackageKind.SubjectData)
                {
                    subject = new PortabilityBundleSubject(package.SubjectUserId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "user");
                    entities = await PortabilityExporter.ExportSubjectAsync(subjectContributors, new SubjectScope(package.SiteId, package.SubjectUserId.Value), ct);
                }
                else
                {
                    entities = await exporter.ExportConfigAsync(package.SiteId, ParseRoots(package.Request), ct);
                }

                var document = PortabilityBundleDocument.Create(
                    package.Kind == PortabilityPackageKind.SubjectData ? PortabilityPackageKinds.SubjectData : PortabilityPackageKinds.ConfigTransfer,
                    $"{tenantContext.CurrentTenantId?.Value}:{package.SiteId}",
                    subject,
                    new PortabilityBundleProvenance(package.RequestedByUserId, now, Environment.MachineName),
                    entities);
                var json = serializer.Serialize(document);
                var fileId = await artifacts.WriteAsync(package, json, ct);

                package.MarkReady(fileId, document.Manifest.Format.ToString(), document.Entities.Count, ManifestSummary(document), now, now.AddDays(DefaultRetentionDays));
                await packages.UpdateAsync(package, ct);
                await audit.AddAsync(PortabilityAuditEntryAggregate.Record(
                    package.TenantId, package.SiteId, package.Id, PortabilityAuditAction.Built, package.Kind, package.EntityCount, package.RequestedByUserId, package.SubjectUserId, now), ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Portability package {PackageId} of site {SiteId} could not be built.", package.Id, package.SiteId);
                return await FailAsync(package, ex.GetType().Name, now);
            }
        }

        private async Task<bool> FailAsync(PortabilityPackageAggregate package, string reason, DateTime now)
        {
            package.MarkFailed(reason, now, now.AddDays(DefaultRetentionDays));
            await packages.UpdateAsync(package);
            await audit.AddAsync(PortabilityAuditEntryAggregate.Record(
                package.TenantId, package.SiteId, package.Id, PortabilityAuditAction.BuildFailed, package.Kind, 0, package.RequestedByUserId, package.SubjectUserId, now));
            return false;
        }

        /// <summary>The request a configuration build was queued with: <c>{"type": ["id", ...] | null}</c>.</summary>
        public static IReadOnlyDictionary<string, IReadOnlyCollection<string>?> ParseRoots(string request)
        {
            var roots = new Dictionary<string, IReadOnlyCollection<string>?>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(request))
            {
                return roots;
            }

            using var document = JsonDocument.Parse(request);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                roots[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                    ? property.Value.EnumerateArray().Select(id => id.GetString() ?? string.Empty).Where(id => id.Length > 0).ToList()
                    : null;
            }

            return roots;
        }

        private static string ManifestSummary(PortabilityBundleDocument document) =>
            JsonSerializer.Serialize(document.Manifest.Entries.Select(line => new { t = line.EntityType, v = line.SchemaVersion, n = line.Count }));
    }

    /// <summary>
    /// Portability's retention, drained per site by <c>PurgeJob</c> (D104): a Ready or Failed package whose stated expiry
    /// has passed, or that is older than the site's <c>PortabilityRetention</c>, loses its artifact (file AND row) and
    /// becomes Expired; a subject package is never kept past seven days whatever the setting says (D107 Q3). Audit rows
    /// go after <see cref="PortabilityOptions.AuditRetentionDays"/>. Does nothing while <c>Portability:Enabled</c> is off.
    /// </summary>
    public sealed class PortabilityRetentionPurger(
        IPortabilityPackageRepository packages,
        IPortabilityAuditRepository audit,
        PortabilityArtifactStore artifacts,
        PortabilityOptions options) : IRetentionPurger
    {
        public const int SubjectCeilingDays = 7;

        public string SettingKey => "PortabilityRetention";

        public int DefaultDays => PortabilityBuilder.DefaultRetentionDays;

        public string DisplayName => "Portability";

        public async Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
        {
            if (!options.Enabled)
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            var purged = 0;
            var due = new Dictionary<Guid, PortabilityPackageAggregate>();
            foreach (var package in await packages.ListDueForExpiryAsync(siteId, now, cancellationToken))
            {
                due[package.Id] = package;
            }

            foreach (var status in new[] { PortabilityPackageStatus.Ready, PortabilityPackageStatus.Failed })
            {
                foreach (var package in await packages.ListByStatusAsync(siteId, status, cancellationToken))
                {
                    var limit = package.Kind == PortabilityPackageKind.SubjectData ? Math.Min(days, SubjectCeilingDays) : days;
                    if ((package.ReadyOnUtc ?? package.FailedOnUtc ?? package.CreatedOnUtc) < now.AddDays(-limit))
                    {
                        due[package.Id] = package;
                    }
                }
            }

            foreach (var package in due.Values.Where(package => package.Status is PortabilityPackageStatus.Ready or PortabilityPackageStatus.Failed))
            {
                await artifacts.DeleteAsync(package, cancellationToken);
                package.MarkExpired();
                await packages.UpdateAsync(package, cancellationToken);
                await audit.AddAsync(PortabilityAuditEntryAggregate.Record(
                    package.TenantId, package.SiteId, package.Id, PortabilityAuditAction.Expired, package.Kind, package.EntityCount, package.RequestedByUserId, package.SubjectUserId, now), cancellationToken);
                purged++;
            }

            foreach (var line in await audit.ListOlderThanAsync(siteId, now.AddDays(-Math.Max(1, options.AuditRetentionDays)), cancellationToken))
            {
                await audit.DeleteAsync(line, cancellationToken);
                purged++;
            }

            return purged;
        }
    }
}
