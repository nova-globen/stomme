#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Portability.Application;
using Stomme.Modules.Portability.Domain;
using Stomme.Modules.Portability.Execution;
using Stomme.Portability;
using Stomme.Portability.Persistence;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Modules.Portability.Services
{
    /// <summary>A request the server refuses, with a reason written to be shown to the requester.</summary>

    /// <summary>
    /// The SERVER side of the portability surface (BL-82, D107 items 3, 5, 9): every decision - who the caller is, which
    /// site, which keys they hold, whose package it is - is made here from the request, never from an argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One answer for "not there" and "not yours".</b> A package of another site, or one the caller may not see,
    /// answers exactly as a package that does not exist - the id space is not an oracle.
    /// </para>
    /// <para>
    /// <b>Who may download what.</b> A configuration package: its requester, or a <c>portability:export</c> holder. A
    /// person's data package: its requester, its subject, or a <c>portability:subject-data</c> holder - and nobody
    /// else, whatever else they hold (refutation 14: a <c>view</c> holder sees the row, never the bytes).
    /// </para>
    /// </remarks>
    public sealed class ServerPortabilityService(
        IPortabilityPackageRepository packages,
        IPortabilityAuditRepository audit,
        PortabilityContributorRegistry registry,
        PortabilityImporter importer,
        PortabilityArtifactStore artifacts,
        PortabilityOptions options,
        IUserRepository users,
        IUserRoleRepository userRoles,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        SiteState siteState,
        IAccessGuard guard) : IPortabilityService
    {
        public const int ListLimit = 200;

        private int SiteId => siteState.Alias?.SiteId is > 0 and var site ? site : throw new InvalidOperationException("No site is resolved in this scope.");

        private int UserId => currentUser.IsAuthenticated && currentUser.UserId is > 0 and var id ? id : throw new InvalidOperationException("No signed-in user.");

        private Tenancy.Domain.TenantId TenantId => tenantContext.CurrentTenantId ?? throw new InvalidOperationException("No tenant is resolved in this scope.");

        private Task<bool> HoldsAsync(string key, CancellationToken ct = default) => guard.IsAllowedAsync(null, key, ct);

        public Task<bool> IsEnabledAsync() => Task.FromResult(options.Enabled);

        public async Task<List<PortabilityPackageView>> ListAsync()
        {
            if (!currentUser.IsAuthenticated)
            {
                return new List<PortabilityPackageView>();
            }

            var all = await HoldsAsync(PortabilityPermissions.View);
            var rows = all
                ? await packages.ListForSiteAsync(SiteId, ListLimit)
                : (await packages.ListForSiteAsync(SiteId, ListLimit)).Where(p => p.RequestedByUserId == UserId || p.SubjectUserId == UserId).ToList();

            var views = new List<PortabilityPackageView>();
            foreach (var row in rows)
            {
                views.Add(ToView(row, await MayDownloadAsync(row)));
            }

            return views;
        }

        public async Task<PortabilityPackageView> RequestConfigAsync(PortabilityConfigRequest request)
        {
            var types = (request?.EntityTypes ?? new List<string>()).Where(type => !string.IsNullOrWhiteSpace(type)).Distinct(StringComparer.Ordinal).ToList();
            if (types.Count == 0)
            {
                throw new PortabilityRequestRejectedException("Choose at least one kind of configuration to export.");
            }

            var unknown = types.Where(type => registry.Find(type) is null).ToList();
            if (unknown.Count > 0)
            {
                throw new PortabilityRequestRejectedException($"Nothing on this installation exports: {string.Join(", ", unknown)}.");
            }

            var roots = JsonSerializer.Serialize(types.ToDictionary(type => type, _ => (string[]?)null));
            return await QueueAsync(PortabilityPackageKind.ConfigTransfer, subject: null, roots);
        }

        public async Task<PortabilityPackageView> RequestSubjectAsync(PortabilitySubjectRequest request)
        {
            var username = request?.Username?.Trim() ?? string.Empty;
            var subject = username.Length == 0 ? null : await users.GetUserAsync(username);

            // The person must belong to THIS site - by a SITE role; a global role alone (Host) does not make someone a
            // site's to disclose (refutation 11).
            var siteRoles = subject is null
                ? new List<Models.UserRole>()
                : (await userRoles.GetUserRolesAsync(subject.UserId, SiteId)).ToList();
            if (subject is null || subject.IsDeleted || !siteRoles.Any(role => role.Role?.SiteId == SiteId))
            {
                throw new PortabilityRequestRejectedException("No such person on this site.");
            }

            if (await IsHostAsync(subject.UserId))
            {
                if (!currentUser.Roles.Contains(RoleNames.Host))
                {
                    throw new PortabilityRequestRejectedException("Only a host can request a host's data.");
                }
            }

            return await QueueAsync(PortabilityPackageKind.SubjectData, subject.UserId, null);
        }

        public Task<PortabilityPackageView> RequestMineAsync() => QueueAsync(PortabilityPackageKind.SubjectData, UserId, null);

        /// <summary>The package's bytes and a file name, or null - one answer for absent, not ready, and not yours.</summary>
        public async Task<(byte[] Bytes, string FileName)?> DownloadAsync(Guid id, CancellationToken ct = default)
        {
            var package = await packages.FindForSiteAsync(SiteId, id, ct);
            if (package is null || package.Status != PortabilityPackageStatus.Ready || !await MayDownloadAsync(package))
            {
                return null;
            }

            var bytes = await artifacts.ReadAsync(package, ct);
            if (bytes is null)
            {
                return null;
            }

            await audit.AddAsync(PortabilityAuditEntryAggregate.Record(
                TenantId, SiteId, package.Id, PortabilityAuditAction.Downloaded, package.Kind, package.EntityCount, UserId, package.SubjectUserId, DateTime.UtcNow), ct);
            var name = (package.Kind == PortabilityPackageKind.SubjectData ? "personal-data-" : "configuration-") + package.Id.ToString("N")[..8] + ".json";
            return (bytes, name);
        }

        public async Task<PortabilityImportResult> ImportAsync(string json, ConflictStrategy strategy, CancellationToken ct = default)
        {
            var report = await importer.ImportAsync(json ?? string.Empty, options.MaxPackageBytes, new ImportContext(SiteId, strategy, new PortableIdMap()), ct);
            var now = DateTime.UtcNow;
            Guid? recordId = null;
            if (report.Accepted)
            {
                var manifest = JsonSerializer.Serialize(report.Document!.Manifest.Entries.Select(line => new { t = line.EntityType, v = line.SchemaVersion, n = line.Count }));
                var record = PortabilityPackageAggregate.RecordImport(TenantId, SiteId, UserId, report.Document.Manifest.Format.ToString(), report.Lines.Count, manifest, now);
                await packages.AddAsync(record, ct);
                recordId = record.Id;
            }

            await audit.AddAsync(PortabilityAuditEntryAggregate.Record(
                TenantId, SiteId, recordId, report.Accepted ? PortabilityAuditAction.Imported : PortabilityAuditAction.ImportRefused,
                PortabilityPackageKind.ConfigTransfer, report.Lines.Count, UserId, null, now), ct);

            return new PortabilityImportResult
            {
                Accepted = report.Accepted,
                Refusals = report.Refusals.ToList(),
                Lines = report.Lines.Select(line => new PortabilityImportLine
                {
                    EntityType = line.EntityType, EntityId = line.EntityId, DisplayName = line.DisplayName,
                    Result = line.Result.ToString(), Reason = line.Reason ?? string.Empty,
                }).ToList(),
            };
        }

        private async Task<PortabilityPackageView> QueueAsync(PortabilityPackageKind kind, int? subject, string? request)
        {
            var now = DateTime.UtcNow;
            var package = PortabilityPackageAggregate.StartBuild(TenantId, SiteId, kind, UserId, subject, now, request);
            await packages.AddAsync(package);
            await audit.AddAsync(PortabilityAuditEntryAggregate.Record(TenantId, SiteId, package.Id, PortabilityAuditAction.BuildRequested, kind, 0, UserId, subject, now));
            return ToView(package, canDownload: false);
        }

        private async Task<bool> MayDownloadAsync(PortabilityPackageAggregate package)
        {
            if (!currentUser.IsAuthenticated)
            {
                return false;
            }

            if (package.Kind == PortabilityPackageKind.SubjectData)
            {
                return package.RequestedByUserId == UserId || package.SubjectUserId == UserId || await HoldsAsync(PortabilityPermissions.SubjectData);
            }

            return package.RequestedByUserId == UserId || await HoldsAsync(PortabilityPermissions.Export);
        }

        private async Task<bool> IsHostAsync(int userId) =>
            (await userRoles.GetUserRolesAsync(userId, SiteId)).Any(role => role.Role?.Name == RoleNames.Host);

        private static PortabilityPackageView ToView(PortabilityPackageAggregate package, bool canDownload) => new()
        {
            Id = package.Id,
            Kind = package.Kind.ToString(),
            Status = package.Status.ToString(),
            RequestedByUserId = package.RequestedByUserId,
            SubjectUserId = package.SubjectUserId,
            EntityCount = package.EntityCount,
            BundleFormat = package.BundleFormat,
            FailureText = package.FailureText,
            CreatedOnUtc = package.CreatedOnUtc,
            ReadyOnUtc = package.ReadyOnUtc,
            ExpiresOnUtc = package.ExpiresOnUtc,
            CanDownload = canDownload && package.Status == PortabilityPackageStatus.Ready,
        };
    }
}
