#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - a pipeline run's folder as a connector's files (n8n's binary data on its file nodes, the S3 connector its first
    /// user): a file read and written under the site's folder only, at most the cap, a write landing whole or not at all - over
    /// the cap or failing midway it leaves nothing under the name or beside it, and an earlier file of that name as it was; and
    /// Extract from connection handing the port the run's own site folder at the configured cap.
    /// </summary>
    public sealed class PipelineConnectorFilesTests : IAsyncLifetime
    {
        private static readonly Guid ConnectionId = Guid.Parse("2c1f4b7e-8d3a-4e0b-9f5c-6a7d8e9f0a1b");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-connector-files-" + Guid.NewGuid().ToString("N"));
        private readonly FilesExtraction _extraction = new();
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            var options = new PipelineStepOptions();
            options.Files.Root = _root;
            options.Files.MaxFileBytes = 4096;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(options));
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = 1, SiteId = 1, Name = "localhost" } });
            services.AddSingleton<IConnectorExtractionService>(_extraction);
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static MemoryStream Bytes(int count) => new(Enumerable.Range(0, count).Select(i => (byte)i).ToArray());

        [Fact]
        public async Task A_file_is_written_whole_or_not_at_all_and_read_back_under_the_folder_alone()
        {
            var files = PipelineConnectorFiles.Under(_root, 16);
            Assert.Equal(16, files.MaxBytes);
            Assert.Equal(10, await files.WriteAsync("downloads/a.bin", Bytes(10)));
            var path = Path.Combine(_root, "downloads", "a.bin");
            Assert.Equal(Bytes(10).ToArray(), File.ReadAllBytes(path)); // its folder made, the bytes exact

            await using (var read = await files.OpenReadAsync("downloads/a.bin"))
            {
                using var copy = new MemoryStream();
                await read.Content.CopyToAsync(copy);
                Assert.Equal(("downloads/a.bin", "a.bin", 10L), (read.Path, read.Name, read.Length));
                Assert.Equal(Bytes(10).ToArray(), copy.ToArray());
            }

            // Over the cap: refused, nothing under the name or beside it - the earlier file of that name as it was.
            var over = await Assert.ThrowsAsync<InvalidOperationException>(() => files.WriteAsync("downloads/a.bin", Bytes(17)));
            Assert.Equal("The file is over Pipelines:Files:MaxFileBytes (16); nothing was written to 'downloads/a.bin'.", over.Message);
            Assert.Equal(Bytes(10).ToArray(), File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".part"));

            // Failing midway: the source's failure propagates, the same - nothing half-written.
            await Assert.ThrowsAsync<IOException>(() => files.WriteAsync("downloads/a.bin", new Failing()));
            Assert.Equal(Bytes(10).ToArray(), File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".part"));

            Assert.Equal(16, await files.WriteAsync("downloads/a.bin", Bytes(16))); // at the cap: written, over the old one
            Assert.Equal(16, new FileInfo(path).Length);

            Assert.Equal("'downloads/none.bin' is not a file in the site's pipeline folder.", (await Assert.ThrowsAsync<InvalidOperationException>(() => files.OpenReadAsync("downloads/none.bin"))).Message);
            File.WriteAllBytes(Path.Combine(_root, "big.bin"), new byte[17]);
            Assert.Equal("'big.bin' is 17 bytes, over Pipelines:Files:MaxFileBytes (16).", (await Assert.ThrowsAsync<InvalidOperationException>(() => files.OpenReadAsync("big.bin"))).Message);

            // The folder's own rules, both ways: never rooted, never a drive or a share, never outside.
            foreach (var (given, refusal) in new[]
            {
                ("/etc/passwd", "'/etc/passwd' is not a relative path; file steps read and write under the site's own folder only."),
                ("C:x.bin", "'C:x.bin' is not a relative path; file steps read and write under the site's own folder only."),
                ("\\\\server\\share\\x.bin", "'\\\\server\\share\\x.bin' is not a relative path; file steps read and write under the site's own folder only."),
                ("../outside.bin", "'../outside.bin' resolves outside the site's folder."),
                ("downloads/../../outside.bin", "'downloads/../../outside.bin' resolves outside the site's folder."),
                ("", "The path is empty."),
            })
            {
                Assert.Equal(refusal, (await Assert.ThrowsAsync<InvalidOperationException>(() => files.WriteAsync(given, Bytes(1)))).Message);
                Assert.Equal(refusal, (await Assert.ThrowsAsync<InvalidOperationException>(() => files.OpenReadAsync(given))).Message);
            }

            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.bin")));
        }

        [Fact]
        public async Task Extract_from_connection_hands_the_port_the_runs_site_folder_at_the_configured_cap()
        {
            using var scope = _services.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(new ExtractFromConnection { Connection = new(ConnectionId.ToString()), Operation = new("download") });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            Assert.Equal(4096, _extraction.MaxBytes); // the configured cap
            Assert.Equal("hej från porten", _extraction.ReadBack);
            var written = Assert.Single(Directory.GetFiles(_root, "x.txt", SearchOption.AllDirectories));
            var segments = Path.GetRelativePath(_root, written).Split(Path.DirectorySeparatorChar);
            Assert.Equal(3, segments.Length); // {the site's folder}/downloads/x.txt - under the root, in a folder of the site's own
            Assert.Equal(new[] { "downloads", "x.txt" }, segments[1..]);
        }

        // A source that fails after its first bytes.
        private sealed class Failing : Stream
        {
            private bool _given;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => Next(buffer.AsSpan(offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Next(buffer.Span));

            private int Next(Span<byte> buffer)
            {
                if (_given)
                {
                    throw new IOException("The connection was reset.");
                }

                _given = true;
                buffer[0] = 42;
                return 1;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // The port as a file connector uses it: a file written into the files handed over, then read back.
        private sealed class FilesExtraction : IConnectorExtractionService
        {
            public long MaxBytes { get; private set; }

            public string? ReadBack { get; private set; }

            public Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters, int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("The step should hand the port its files.");

            public async Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters, int maxRecords, IConnectorFiles? files, CancellationToken cancellationToken)
            {
                Assert.NotNull(files);
                MaxBytes = files!.MaxBytes;
                await files.WriteAsync("downloads/x.txt", new MemoryStream(Encoding.UTF8.GetBytes("hej från porten")), cancellationToken);
                await using var read = await files.OpenReadAsync("downloads/x.txt", cancellationToken);
                ReadBack = await new StreamReader(read.Content, Encoding.UTF8).ReadToEndAsync(cancellationToken);
                return ConnectorExtractionResult.Ok(Array.Empty<ConnectorRecord>(), false, 1);
            }
        }
    }
}
