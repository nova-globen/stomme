#nullable enable
using System;
using Stomme.Modules.Backup.Domain;

namespace Stomme.Modules.Backup.Infrastructure
{
    /// <summary>
    /// The archive stores of this host (BL-169): the local one, always, and the S3 one when <c>Backup:Archive:S3</c> is
    /// configured. NEW snapshots go to <see cref="Current"/>, the one <c>Backup:Archive:Provider</c> names; an existing snapshot
    /// is read from the store its row names (<see cref="BackupSnapshotAggregate.ArchiveStore"/>) - so switching the provider
    /// leaves every earlier snapshot restorable where it is, and a row older than the field reads as Local.
    /// </summary>
    /// <remarks>
    /// Fail-closed both ways: a provider that is S3 but not configured, or that names no store at all, fails the snapshot
    /// with the setting named - it never quietly writes locally, which would lose exactly the off-host copy the operator
    /// asked for.
    /// </remarks>
    public sealed class BackupArchiveStores
    {
        private readonly IBackupArchiveStore _local;
        private readonly Lazy<IBackupArchiveStore>? _s3;
        private readonly string? _s3Problem;
        private readonly string _provider;

        public BackupArchiveStores(IBackupArchiveStore local, Func<IBackupArchiveStore>? s3, string? s3Problem, string? provider)
        {
            _local = local ?? throw new ArgumentNullException(nameof(local));
            _s3 = s3 is null ? null : new Lazy<IBackupArchiveStore>(s3, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
            _s3Problem = s3Problem;
            _provider = string.IsNullOrWhiteSpace(provider) ? BackupArchiveOptions.LocalProvider : provider.Trim();
        }

        /// <summary>The name recorded on a new snapshot: <c>Local</c> or <c>S3</c>, as <c>Backup:Archive:Provider</c> says.</summary>
        public string CurrentName =>
            string.Equals(_provider, BackupArchiveOptions.S3Provider, StringComparison.OrdinalIgnoreCase) ? BackupArchiveOptions.S3Provider
            : string.Equals(_provider, BackupArchiveOptions.LocalProvider, StringComparison.OrdinalIgnoreCase) ? BackupArchiveOptions.LocalProvider
            : throw new InvalidOperationException($"Backup:Archive:Provider is '{_provider}'; it is Local or S3.");

        /// <summary>The store new snapshots go to.</summary>
        public IBackupArchiveStore Current => For(CurrentName);

        /// <summary>The store a snapshot's archive is in.</summary>
        public IBackupArchiveStore For(BackupSnapshotAggregate snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            return For(snapshot.ArchiveStore);
        }

        /// <summary>A store by the name a row records; empty is Local (a row written before the field existed).</summary>
        public IBackupArchiveStore For(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, BackupArchiveOptions.LocalProvider, StringComparison.OrdinalIgnoreCase))
            {
                return _local;
            }

            if (string.Equals(name, BackupArchiveOptions.S3Provider, StringComparison.OrdinalIgnoreCase))
            {
                return _s3?.Value ?? throw new InvalidOperationException(
                    "This archive is in the S3 object store, which this host has not configured: " + (_s3Problem ?? "Backup:Archive:S3 is not set."));
            }

            throw new InvalidOperationException($"An archive store named '{name}' is not one this host has (Local, S3).");
        }
    }
}
