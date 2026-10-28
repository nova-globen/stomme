using System.IO.Compression;
using System.Text;

namespace Stomme.Engine.Common.Codecs;

/// <summary>
/// Represents a GZip compression strategy.
/// </summary>
public class GZip : ICompressionCodec
{
    /// <inheritdoc />
    public async ValueTask<string> CompressAsync(string input, CancellationToken cancellationToken)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        using var output = new MemoryStream();
        var compressionStream = new GZipStream(output, CompressionMode.Compress);
        await using var _compressionStreamAsyncDisposal = compressionStream.ConfigureAwait(false);
        await compressionStream.WriteAsync(inputBytes, 0, inputBytes.Length, cancellationToken).ConfigureAwait(false);
        await compressionStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToBase64String(output.ToArray());
    }

    /// <inheritdoc />
    public async ValueTask<string> DecompressAsync(string input, CancellationToken cancellationToken)
    {
        var inputBytes = Convert.FromBase64String(input);
        using var inputMemoryStream = new MemoryStream(inputBytes);
        var decompressionStream = new GZipStream(inputMemoryStream, CompressionMode.Decompress);
        await using var _decompressionStreamAsyncDisposal = decompressionStream.ConfigureAwait(false);
        using var outputMemoryStream = new MemoryStream();
        await decompressionStream.CopyToAsync(outputMemoryStream, cancellationToken).ConfigureAwait(false);
        var decompressedBytes = outputMemoryStream.ToArray();

        return Encoding.UTF8.GetString(decompressedBytes);
    }
}