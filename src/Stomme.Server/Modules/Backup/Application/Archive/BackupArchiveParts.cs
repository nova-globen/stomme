#nullable enable
using System;
using System.IO;
using Stomme.Backup;

namespace Stomme.Modules.Backup.Application.Archive
{
    /// <summary>A participant's view of the part being written: the archive's part stream, under the participant's name.</summary>
    public sealed class ArchivePartWriter : IBackupPartWriter
    {
        public ArchivePartWriter(string partName, Stream content)
        {
            PartName = partName ?? throw new ArgumentNullException(nameof(partName));
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }

        public string PartName { get; }

        public Stream Content { get; }
    }

    /// <summary>A participant's view of a part to read: each <see cref="OpenRead"/> is a fresh, verified pass from the archive.</summary>
    public sealed class ArchivePartReader : IBackupPartReader
    {
        private readonly BackupArchiveReader _archive;
        private readonly BackupManifestPart _part;

        public ArchivePartReader(BackupArchiveReader archive, BackupManifestPart part)
        {
            _archive = archive ?? throw new ArgumentNullException(nameof(archive));
            _part = part ?? throw new ArgumentNullException(nameof(part));
        }

        public string PartName => _part.Name;

        public string SchemaVersion => _part.SchemaVersion;

        public long Length => _part.Length;

        public Stream OpenRead() => _archive.OpenPart(_part);
    }
}
