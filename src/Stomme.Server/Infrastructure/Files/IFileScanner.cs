#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The outcome of scanning one file's content.
    /// </summary>
    /// <param name="IsClean">True when the scanner found nothing wrong.</param>
    /// <param name="ThreatName">What the scanner found, when <paramref name="IsClean"/> is false.</param>
    public sealed record FileScanResult(bool IsClean, string? ThreatName = null)
    {
        public static FileScanResult Clean { get; } = new(true);
    }

    /// <summary>
    /// The malware-scanner hook every file-ingestion path runs through <see cref="IFileIngestionGuard"/> before a
    /// file reaches its folder. The stock registration is <see cref="NullFileScanner"/>;
    /// a real scanner (ClamAV is one candidate) replaces it by registering its own <see cref="IFileScanner"/>
    /// before <c>AddStomme</c> runs, because the framework registers the null scanner with <c>TryAdd</c>.
    /// </summary>
    public interface IFileScanner
    {
        /// <summary>Scans a staged file. The stream is positioned at the start and is read-only.</summary>
        Task<FileScanResult> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken);
    }

    /// <summary>The stock scanner: every file is clean. It exists so the hook is always wired.</summary>
    public sealed class NullFileScanner : IFileScanner
    {
        public Task<FileScanResult> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken)
            => Task.FromResult(FileScanResult.Clean);
    }
}