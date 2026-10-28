#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Backup
{
    /// <summary>
    /// One PART of an installation snapshot (BL-81, D106): a store, a module's aggregate, a content tree. It writes its data
    /// out, checks a part it is handed before anything is replaced, and replaces EVERYTHING it owns with that part - a
    /// whole-collection replace, never a merge, so no row from before the restore survives it.
    /// </summary>
    /// <remarks>
    /// Server-free by design: a module ships a participant referencing only <c>Stomme.Shared</c>. A part is an opaque byte
    /// stream the participant defines (JSON Lines is the house default); the archive encrypts, chunks and authenticates it.
    /// </remarks>
    public interface IBackupParticipant
    {
        /// <summary>The part's name, unique in an installation: the archive's key for it.</summary>
        string Name { get; }

        /// <summary>The shape of the part's bytes. A restore refuses a part whose schema version differs from this one.</summary>
        string SchemaVersion { get; }

        /// <summary>Replace order, ascending: what others refer to (tenants, sites) goes first.</summary>
        int RestoreOrder { get; }

        /// <summary>Writes everything this participant owns, across the whole installation.</summary>
        Task ExportAsync(IBackupPartWriter writer, BackupScope scope, CancellationToken cancellationToken);

        /// <summary>Reads the part and says whether <see cref="ReplaceAsync"/> could apply it. Writes NOTHING.</summary>
        Task<BackupPartValidation> ValidateAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken);

        /// <summary>Deletes everything this participant owns and writes the part in its place, then checks the count.</summary>
        Task ReplaceAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken);
    }

    /// <summary>Where a participant writes its part: a forward-only stream, encrypted as it is written.</summary>
    public interface IBackupPartWriter
    {
        string PartName { get; }

        Stream Content { get; }
    }

    /// <summary>A part to read: each <see cref="OpenRead"/> is a fresh, forward-only, verified pass over it.</summary>
    public interface IBackupPartReader
    {
        string PartName { get; }

        string SchemaVersion { get; }

        /// <summary>The part's length in bytes, as the (authenticated) manifest records it.</summary>
        long Length { get; }

        /// <summary>
        /// Opens the part from its start. Reading past a chunk that fails authentication, or reaching the end with a length or
        /// hash that disagrees with the manifest, throws - a participant never sees bytes the archive cannot vouch for.
        /// </summary>
        Stream OpenRead();
    }

    /// <summary>What a snapshot covers: the whole installation - the framework version it was taken on, and its tenants.</summary>
    public sealed record BackupScope
    {
        public BackupScope(string frameworkVersion, IReadOnlyList<int> tenantIds)
            : this(frameworkVersion, tenantIds, tenantIds)
        {
        }

        /// <summary>
        /// A scope narrower than the snapshot (BL-171): <paramref name="tenantIds"/> are the tenants this restore puts
        /// back, <paramref name="snapshotTenantIds"/> every tenant the snapshot holds. A participant leaves what is
        /// outside <see cref="TenantIds"/> - other tenants' rows, installation-wide rows - exactly as it is.
        /// </summary>
        public BackupScope(string frameworkVersion, IReadOnlyList<int> tenantIds, IReadOnlyList<int> snapshotTenantIds)
        {
            if (string.IsNullOrWhiteSpace(frameworkVersion))
            {
                throw new ArgumentException("A snapshot names the framework version it was taken on.", nameof(frameworkVersion));
            }

            FrameworkVersion = frameworkVersion;
            TenantIds = tenantIds ?? throw new ArgumentNullException(nameof(tenantIds));
            SnapshotTenantIds = snapshotTenantIds ?? throw new ArgumentNullException(nameof(snapshotTenantIds));
            foreach (var tenantId in TenantIds)
            {
                if (!SnapshotTenantIds.Contains(tenantId))
                {
                    throw new ArgumentException($"Tenant {tenantId} is not in the snapshot.", nameof(tenantIds));
                }
            }
        }

        public string FrameworkVersion { get; }

        /// <summary>The tenants this operation covers: every tenant of the installation for a snapshot, the selected ones for a restore.</summary>
        public IReadOnlyList<int> TenantIds { get; }

        /// <summary>Every tenant the snapshot holds. Equal to <see cref="TenantIds"/> unless the restore is partial.</summary>
        public IReadOnlyList<int> SnapshotTenantIds { get; }

        /// <summary>True for a tenant-scoped restore (BL-171): a participant then touches only <see cref="TenantIds"/>' rows and files, never installation-wide ones.</summary>
        public bool IsPartial => TenantIds.Count != SnapshotTenantIds.Count || TenantIds.Any(id => !SnapshotTenantIds.Contains(id));
    }

    /// <summary>A participant's verdict on a part, before anything is replaced.</summary>
    public sealed record BackupPartValidation
    {
        private BackupPartValidation(IReadOnlyList<string> problems) => Problems = problems;

        public static BackupPartValidation Valid { get; } = new(Array.Empty<string>());

        public IReadOnlyList<string> Problems { get; }

        public bool IsValid => Problems.Count == 0;

        public static BackupPartValidation Invalid(params string[] problems)
        {
            if (problems is null || problems.Length == 0)
            {
                throw new ArgumentException("An invalid part says what is wrong with it.", nameof(problems));
            }

            return new BackupPartValidation(problems);
        }
    }

    /// <summary>
    /// Marks data that a snapshot deliberately does NOT carry, and why - the catalogue driving a restore, a cache, an index
    /// that is rebuilt. The coverage gate accepts it in place of a participant; a blank reason is refused.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
    public sealed class BackupExcludedAttribute : Attribute
    {
        public BackupExcludedAttribute(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("An exclusion states its reason.", nameof(reason));
            }

            Reason = reason;
        }

        public string Reason { get; }
    }
}
