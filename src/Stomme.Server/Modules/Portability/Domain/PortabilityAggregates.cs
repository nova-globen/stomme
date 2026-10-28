#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Portability.Domain
{
    public enum PortabilityPackageStatus
    {
        Building = 0,
        Ready = 1,
        Failed = 2,
        Imported = 3,
        Expired = 4,
    }

    public enum PortabilityPackageKind
    {
        ConfigTransfer = 0,
        SubjectData = 1,
    }

    /// <summary>
    /// One package (BL-82, D107 item 5): a configuration export or a person's data export being built, ready, failed or
    /// expired, or an imported configuration package's record. The payload lives ONLY in the artifact file; the row holds
    /// what a list shows and what retention needs.
    /// </summary>
    public sealed class PortabilityPackageAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumFailureLength = 512;
        public const int MaximumManifestLength = 4000;
        public const int MaximumRequestLength = 4000;

        private PortabilityPackageAggregate()
        {
            TenantId = null!;
            BundleFormat = string.Empty;
            ManifestSummary = string.Empty;
            FailureText = string.Empty;
            Request = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>What a configuration build was asked for: <c>{"type": ["id", ...] | null}</c>. Empty for a subject package.</summary>
        public string Request { get; private set; }

        public PortabilityPackageKind Kind { get; private set; }

        public PortabilityPackageStatus Status { get; private set; }

        /// <summary>The format the artifact was written in, e.g. "1.1".</summary>
        public string BundleFormat { get; private set; }

        public int? SubjectUserId { get; private set; }

        public int RequestedByUserId { get; private set; }

        public int EntityCount { get; private set; }

        /// <summary>The manifest as compact JSON (type, schema version, count per line) - bounded.</summary>
        public string ManifestSummary { get; private set; }

        public int? ArtifactFileId { get; private set; }

        public DateTime CreatedOnUtc { get; private set; }

        public DateTime? ReadyOnUtc { get; private set; }

        public DateTime? FailedOnUtc { get; private set; }

        public DateTime? ExpiresOnUtc { get; private set; }

        /// <summary>Why a build failed: an exception TYPE, never its message (a message can carry a connection string).</summary>
        public string FailureText { get; private set; }

        public static PortabilityPackageAggregate StartBuild(TenantId tenantId, int siteId, PortabilityPackageKind kind, int requestedByUserId, int? subjectUserId, DateTime nowUtc, string? request = null)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A package belongs to a site.");
            }

            if (request is { Length: > MaximumRequestLength })
            {
                throw new ArgumentException("The request is too long.", nameof(request));
            }

            if (kind == PortabilityPackageKind.SubjectData && subjectUserId is not > 0)
            {
                throw new ArgumentException("A subject-data package names its subject.", nameof(subjectUserId));
            }

            if (kind == PortabilityPackageKind.ConfigTransfer && subjectUserId is not null)
            {
                throw new ArgumentException("A configuration package has no subject.", nameof(subjectUserId));
            }

            return new PortabilityPackageAggregate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SiteId = siteId,
                Kind = kind,
                Status = PortabilityPackageStatus.Building,
                RequestedByUserId = requestedByUserId,
                SubjectUserId = subjectUserId,
                Request = request ?? string.Empty,
                CreatedOnUtc = Utc(nowUtc),
            };
        }

        /// <summary>The record of an import - created already in its final state; there is no artifact.</summary>
        public static PortabilityPackageAggregate RecordImport(TenantId tenantId, int siteId, int requestedByUserId, string bundleFormat, int entityCount, string manifestSummary, DateTime nowUtc)
        {
            var package = StartBuild(tenantId, siteId, PortabilityPackageKind.ConfigTransfer, requestedByUserId, null, nowUtc);
            package.Status = PortabilityPackageStatus.Imported;
            package.BundleFormat = Clip(bundleFormat, 16);
            package.EntityCount = entityCount;
            package.ManifestSummary = Clip(manifestSummary, MaximumManifestLength);
            package.ReadyOnUtc = package.CreatedOnUtc;
            return package;
        }

        public void MarkReady(int artifactFileId, string bundleFormat, int entityCount, string manifestSummary, DateTime nowUtc, DateTime expiresUtc)
        {
            Require(PortabilityPackageStatus.Building);
            Status = PortabilityPackageStatus.Ready;
            ArtifactFileId = artifactFileId;
            BundleFormat = Clip(bundleFormat, 16);
            EntityCount = entityCount;
            ManifestSummary = Clip(manifestSummary, MaximumManifestLength);
            ReadyOnUtc = Utc(nowUtc);
            ExpiresOnUtc = Utc(expiresUtc);
        }

        public void MarkFailed(string reason, DateTime nowUtc, DateTime expiresUtc)
        {
            Require(PortabilityPackageStatus.Building);
            Status = PortabilityPackageStatus.Failed;
            FailureText = Clip(reason, MaximumFailureLength);
            FailedOnUtc = Utc(nowUtc);
            ExpiresOnUtc = Utc(expiresUtc);
        }

        /// <summary>Retention: the artifact is gone; the row stays as the record that it existed.</summary>
        public void MarkExpired()
        {
            Status = PortabilityPackageStatus.Expired;
            ArtifactFileId = null;
        }

        private void Require(PortabilityPackageStatus expected)
        {
            if (Status != expected)
            {
                throw new InvalidOperationException($"The package is {Status}, not {expected}.");
            }
        }

        private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        private static string Clip(string? value, int max) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
    }

    public enum PortabilityAuditAction
    {
        BuildRequested = 0,
        Built = 1,
        BuildFailed = 2,
        Downloaded = 3,
        Imported = 4,
        ImportRefused = 5,
        Expired = 6,
    }

    /// <summary>One line of the portability audit (D107 item 5): who did what with which package, when. Immutable.</summary>
    public sealed class PortabilityAuditEntryAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private PortabilityAuditEntryAggregate()
        {
            TenantId = null!;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public Guid? PackageId { get; private set; }

        public PortabilityAuditAction Action { get; private set; }

        public PortabilityPackageKind Kind { get; private set; }

        public int EntityCount { get; private set; }

        public int RequestedByUserId { get; private set; }

        public int? SubjectUserId { get; private set; }

        public DateTime OccurredOnUtc { get; private set; }

        public static PortabilityAuditEntryAggregate Record(
            TenantId tenantId, int siteId, Guid? packageId, PortabilityAuditAction action, PortabilityPackageKind kind,
            int entityCount, int requestedByUserId, int? subjectUserId, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An audit line belongs to a site.");
            }

            return new PortabilityAuditEntryAggregate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SiteId = siteId,
                PackageId = packageId,
                Action = action,
                Kind = kind,
                EntityCount = entityCount,
                RequestedByUserId = requestedByUserId,
                SubjectUserId = subjectUserId,
                OccurredOnUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime(),
            };
        }
    }
}
