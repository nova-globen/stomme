#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Connectivity
{
    /// <summary>
    /// Managed file storage the HOST hands a connector whose operation produces files (BL-96 increment 2 - the file sink
    /// D110 re-booked): a file is stored into a folder of the request's SITE through the host's ingestion guard (the name
    /// and extension allowlist, the size cap, the scanner, the folder's capacity) and registered as a File row, and the
    /// row's id comes back. A connector never writes a host path. Absent from a context when the host offers no managed
    /// storage, in which case an operation that needs it says so.
    /// </summary>
    public interface IConnectorFileSink
    {
        /// <summary>The host's per-file cap in bytes; a connector refuses a larger file before fetching it.</summary>
        long MaxBytes { get; }

        /// <summary>Stores <paramref name="content"/> as <paramref name="fileName"/> in the site folder at <paramref name="folderPath"/>; the File row's id.</summary>
        Task<int> StoreAsync(string fileName, string folderPath, Stream content, long length, CancellationToken cancellationToken = default);
    }
}
