#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Pipelines;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// A pipeline run's folder as a connector's files (BL-229, n8n's binary data on its file nodes): <see cref="IConnectorFiles"/>
    /// over <see cref="PipelineFileSandbox"/> - the site's folder the file steps read and write, each path held to the sandbox's
    /// rules (no rooted path, no UNC path, nothing outside the folder), at most <c>Pipelines:Files:MaxFileBytes</c>, a write
    /// streamed beside its target and moved over it only when complete. The folder is found on first use, so a host with no
    /// <c>Pipelines:Files:Root</c> refuses the file operation, not every extraction.
    /// </summary>
    internal sealed class PipelineConnectorFiles : IConnectorFiles
    {
        private readonly Func<string> root;

        private PipelineConnectorFiles(Func<string> root, long maxBytes)
        {
            this.root = root;
            MaxBytes = maxBytes;
        }

        public long MaxBytes { get; }

        /// <summary>The run's site's folder, at the configured cap.</summary>
        public static PipelineConnectorFiles For(StepExecutionContext context) =>
            new(() => PipelineFileSandbox.SiteRoot(context), context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new FileStepOptions().MaxFileBytes);

        /// <summary>A folder given outright, for the tests.</summary>
        internal static PipelineConnectorFiles Under(string root, long maxBytes) => new(() => root, maxBytes);

        public Task<ConnectorFileContent> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        {
            var full = Resolve(path);
            if (!File.Exists(full))
            {
                throw new InvalidOperationException($"'{path}' is not a file in the site's pipeline folder.");
            }

            var length = new FileInfo(full).Length;
            if (length > MaxBytes)
            {
                throw new InvalidOperationException($"'{path}' is {length} bytes, over Pipelines:Files:MaxFileBytes ({MaxBytes}).");
            }

            return Task.FromResult(new ConnectorFileContent(path, length, new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)));
        }

        public async Task<long> WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(content);
            var full = Resolve(path);
            var written = await WriteBoundedAsync(content, full, MaxBytes, cancellationToken).ConfigureAwait(false);
            return written >= 0 ? written : throw new InvalidOperationException($"The file is over Pipelines:Files:MaxFileBytes ({MaxBytes}); nothing was written to '{path}'.");
        }

        private string Resolve(string path) =>
            PipelineFileSandbox.Resolve(root(), path ?? string.Empty, out var refusal) ?? throw new InvalidOperationException(refusal);

        /// <summary>
        /// <paramref name="source"/> streamed into a file beside <paramref name="path"/> and moved over it when complete, so a failed
        /// or oversized write never leaves half a file under the name; -1 when it passes <paramref name="maxBytes"/> (nothing kept).
        /// The HTTP step's saved response and a connector's download alike.
        /// </summary>
        internal static async Task<long> WriteBoundedAsync(Stream source, string path, long maxBytes, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var part = path + ".part";
            var total = 0L;
            try
            {
                await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var chunk = new byte[81920];
                    while (true)
                    {
                        var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                        if (total > maxBytes)
                        {
                            break;
                        }

                        await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }

                if (total > maxBytes)
                {
                    File.Delete(part);
                    return -1;
                }

                File.Move(part, path, overwrite: true);
                return total;
            }
            catch
            {
                File.Delete(part);
                throw;
            }
        }
    }
}
