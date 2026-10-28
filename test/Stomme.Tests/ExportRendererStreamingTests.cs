#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Modules.DataExport.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-85 (D108) - the streaming promise MEASURED for the three renderers the XLSX measurement did not cover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contract says a million-row export never exists in memory at once. For CSV, JSON and PDF that was asserted
    /// only structurally (the renderer holds at most one row). This measures it: 1,000,000 rows into a sink that
    /// discards what it is given, with the LIVE managed heap sampled from inside the row source while the render is in
    /// flight - the only moment a buffered artifact would be visible.
    /// </para>
    /// <para>
    /// <b>Both directions.</b> A planted renderer that does what the predecessor platform's did - build the artifact in a
    /// <see cref="MemoryStream"/> and copy it out at the end - is measured the same way and REQUIRED to blow the bound.
    /// Without it, a flat reading could as easily mean "the sampler cannot see buffering" as "nothing is buffered".
    /// </para>
    /// <para>Shares the XLSX measurement's non-parallel collection: the instrument is the process heap.</para>
    /// </remarks>
    [Collection(nameof(XlsxStreamingMeasurementTests))]
    public class ExportRendererStreamingTests
    {
        private const int Rows = 1_000_000;

        /// <summary>The XLSX measurement's bound, for the same reason: collector noise, not headroom for a buffer.</summary>
        private const long StreamingBoundBytes = 16L * 1024 * 1024;

        private readonly ITestOutputHelper _output;

        public ExportRendererStreamingTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(ExportFormat.Csv)]
        [InlineData(ExportFormat.Json)]
        [InlineData(ExportFormat.Pdf)]
        public async Task One_million_rows_render_without_the_artifact_ever_being_held(ExportFormat format)
        {
            ITabularRenderer renderer = format switch
            {
                ExportFormat.Csv => new CsvTabularRenderer(),
                ExportFormat.Json => new JsonTabularRenderer(),
                _ => new PdfTabularRenderer(),
            };

            var measured = await MeasureAsync(renderer);
            _output.WriteLine(
                $"[{format}] {Rows:N0} rows: peak live heap +{Mb(measured.PeakGrowth)} MB, {Mb(measured.BytesWritten)} MB written, {measured.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");

            Assert.Equal(Rows, measured.RowCount);
            // The sink really did receive a million rows' worth, or a flat heap would prove nothing.
            Assert.True(measured.BytesWritten > 20L * 1024 * 1024, $"only {measured.BytesWritten:N0} bytes were written");
            Assert.True(measured.PeakGrowth < StreamingBoundBytes,
                $"the live heap grew by {Mb(measured.PeakGrowth)} MB while rendering {Rows:N0} rows as {format}; the bound is {Mb(StreamingBoundBytes)} MB");
        }

        [Fact]
        public async Task A_renderer_that_BUFFERS_the_artifact_is_seen_to()
        {
            // BL-213: the instrument is the PROCESS heap, and this collection running alone does not stop memory an earlier
            // test left for the collector from being freed inside the window, netted against the buffer - measured once in the
            // full rest-a part, "+15.4 MB", green alone. A renderer that buffers does it on EVERY render, while such leftovers
            // are chance, so up to three windows are measured and one clear reading shows the instrument can see a buffer; the
            // bound is unchanged, and a renderer that does not buffer fails all three.
            var readings = new List<long>();
            while (readings.Count < 3 && (readings.Count == 0 || readings.Max() <= 2 * StreamingBoundBytes))
            {
                var measured = await MeasureAsync(new BufferingCsvRenderer());
                readings.Add(measured.PeakGrowth);
                _output.WriteLine($"[planted buffering] {Rows:N0} rows, window {readings.Count}: peak live heap +{Mb(measured.PeakGrowth)} MB");
            }

            Assert.True(readings.Max() > 2 * StreamingBoundBytes,
                $"the planted buffering renderer grew the heap by at most {Mb(readings.Max())} MB in {readings.Count} windows - the instrument cannot see buffering, so the streaming results are unproven");
        }

        private static async Task<(long RowCount, long PeakGrowth, long BytesWritten, TimeSpan Elapsed)> MeasureAsync(ITabularRenderer renderer)
        {
            var table = new SamplingTable(Rows);
            var sink = new DiscardingSink();
            var watch = Stopwatch.StartNew();

            table.Baseline = LiveHeap();
            var outcome = await renderer.RenderAsync(new SingleTable(table), sink, ExportRenderOptions.Default, new PassThrough());
            return (outcome.RowCount, table.PeakGrowth, sink.BytesWritten, watch.Elapsed);
        }

        private static long LiveHeap()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture);

        /// <summary>What the predecessor platform's renderers did: the whole artifact in memory, copied out at the end.</summary>
        private sealed class BufferingCsvRenderer : ITabularRenderer
        {
            public ExportFormat Format => ExportFormat.Csv;

            public async Task<RenderOutcome> RenderAsync(
                ITabularDocument document, Stream destination, ExportRenderOptions options, IExportCellFormatter formatter, CancellationToken ct = default)
            {
                using var buffer = new MemoryStream();
                var outcome = await new CsvTabularRenderer().RenderAsync(document, buffer, options, formatter, ct);
                var bytes = buffer.ToArray();
                await destination.WriteAsync(bytes, ct);
                return outcome;
            }
        }

        private sealed class SamplingTable : ITabularTable
        {
            private const int SampleEvery = 100_000;
            private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            private readonly int _rows;

            public SamplingTable(int rows) => _rows = rows;

            public long Baseline { get; set; }

            public long PeakGrowth { get; private set; }

            public string Name => "measurement";

            public IReadOnlyList<ExportColumn> Columns { get; } = new[]
            {
                new ExportColumn("Id", ExportValueKind.Integer),
                new ExportColumn("Message", ExportValueKind.Text),
                new ExportColumn("Amount", ExportValueKind.Decimal),
                new ExportColumn("When", ExportValueKind.DateTimeUtc),
            };

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                for (var i = 1; i <= _rows; i++)
                {
                    yield return new object?[] { i, "row " + Guid.NewGuid().ToString("N"), i * 1.25m, Epoch.AddSeconds(i) };

                    if (i % SampleEvery == 0)
                    {
                        PeakGrowth = Math.Max(PeakGrowth, LiveHeap() - Baseline);
                        await Task.Yield();
                    }
                }
            }
        }

        private sealed class SingleTable(ITabularTable table) : ITabularDocument
        {
            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return table;
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class PassThrough : IExportCellFormatter
        {
            public string? Format(object? value, ExportColumn column) => null;
        }

        private sealed class DiscardingSink : Stream
        {
            public long BytesWritten { get; private set; }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;

            public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                BytesWritten += buffer.Length;
                return ValueTask.CompletedTask;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                BytesWritten += count;
                return Task.CompletedTask;
            }
        }
    }
}
