#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Packaging;
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
    /// Alone, because the instrument is the PROCESS's managed heap: beside any other suite the reading is noise.
    /// </summary>
    [CollectionDefinition(nameof(XlsxStreamingMeasurementTests), DisableParallelization = true)]
    public sealed class XlsxStreamingMeasurementCollection
    {
    }

    /// <summary>
    /// The measurement required before XLSX may be called streaming.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim under test: a WRITE-ONLY package writes a 1,000,000-row sheet without holding it. The
    /// instrument is the live managed heap (a forced collection, then <see cref="GC.GetTotalMemory(bool)"/>) sampled from INSIDE the row source while the render is in
    /// flight - the only moment a buffered sheet would be visible - against a sink that discards what it is given
    /// and cannot seek, so nothing the renderer wrote can come back to be counted.
    /// </para>
    /// <para>
    /// <b>The instrument is validated in both directions.</b> The same sheet-writing code is run over a
    /// read-write package, where the heap is REQUIRED to grow with the row count. Without that case
    /// a flat reading could as easily mean "the sampler sees nothing" as "nothing is buffered".
    /// </para>
    /// </remarks>
    [Collection(nameof(XlsxStreamingMeasurementTests))]
    public class XlsxStreamingMeasurementTests
    {
        private const int Small = 100_000;
        private const int Large = 1_000_000;

        /// <summary>
        /// What the live heap may grow by during a render, whatever the row count.
        /// </summary>
        /// <remarks>
        /// Measured: +0.2 MB at 100,000 rows and +0.0 MB at 1,000,000 through the write-only package,
        /// against +384.1 MB for the same code over a read-write one. 16 MB is headroom for collector noise, and is
        /// still a twentieth of what buffering costs - the two outcomes cannot be confused.
        /// </remarks>
        private const long StreamingBoundBytes = 16L * 1024 * 1024;

        private readonly ITestOutputHelper _output;

        public XlsxStreamingMeasurementTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_write_only_package_renders_1_000_000_rows_without_holding_the_sheet()
        {
            var small = await MeasureWriteOnlyAsync(Small);
            var large = await MeasureWriteOnlyAsync(Large);

            _output.WriteLine($"[write-only] {Small:N0} rows: peak live heap +{Mb(small.PeakGrowth)} MB, {Mb(small.BytesWritten)} MB written, {small.Elapsed.TotalSeconds:F1}s");
            _output.WriteLine($"[write-only] {Large:N0} rows: peak live heap +{Mb(large.PeakGrowth)} MB, {Mb(large.BytesWritten)} MB written, {large.Elapsed.TotalSeconds:F1}s");

            Assert.Equal(Large, large.Rows);

            // The sink really did receive a million rows' worth - or a flat heap would prove nothing.
            Assert.True(large.BytesWritten > 5 * small.BytesWritten,
                $"ten times the rows wrote only {large.BytesWritten:N0} bytes against {small.BytesWritten:N0}");

            Assert.True(large.PeakGrowth < StreamingBoundBytes,
                $"the live heap grew by {Mb(large.PeakGrowth)} MB while rendering {Large:N0} rows; the bound is {Mb(StreamingBoundBytes)} MB");

            // Bounded INDEPENDENTLY of the row count is the actual claim: ten times the rows must not cost
            // anything like ten times the memory.
            Assert.True(large.PeakGrowth < small.PeakGrowth + (StreamingBoundBytes / 2),
                $"heap growth followed the row count: +{Mb(small.PeakGrowth)} MB at {Small:N0} rows, +{Mb(large.PeakGrowth)} MB at {Large:N0}");
        }

        [Fact]
        public async Task The_same_code_over_a_READ_WRITE_package_does_hold_the_sheet()
        {
            // The known-dirty direction. A read-write package is what SpreadsheetDocument.Create(stream) opens and
            // what anyone would write first; if the sampler cannot see THIS buffering, the flat reading above
            // means nothing.
            var path = Path.Combine(Path.GetTempPath(), "stomme-xlsx-measure-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                var table = new SamplingTable(Large);
                var watch = Stopwatch.StartNew();

                await using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                using (var package = Package.Open(file, FileMode.Create, FileAccess.ReadWrite))
                {
                    // Exactly what the renderer does, with ONE difference: FileAccess.ReadWrite.
                    var part = package.CreatePart(
                        new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative),
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                        CompressionOption.Normal);

                    table.Baseline = LiveHeap();
                    await using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
                    await XlsxTabularRenderer.WriteSheetAsync(
                        stream, table, ExportRenderOptions.Default, new PassThroughFormatter(), CancellationToken.None);
                }

                _output.WriteLine($"[read-write] {Large:N0} rows: peak live heap +{Mb(table.PeakGrowth)} MB, {watch.Elapsed.TotalSeconds:F1}s");

                Assert.True(table.PeakGrowth > 2 * StreamingBoundBytes,
                    $"the read-write package grew the heap by only {Mb(table.PeakGrowth)} MB - the instrument cannot see buffering, so the streaming result is unproven");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static async Task<Measurement> MeasureWriteOnlyAsync(int rows)
        {
            var table = new SamplingTable(rows);
            var sink = new DiscardingSink();
            var watch = Stopwatch.StartNew();

            table.Baseline = LiveHeap();
            var outcome = await new XlsxTabularRenderer().RenderAsync(
                new SingleTableDocument(table), sink, ExportRenderOptions.Default, new PassThroughFormatter());

            return new Measurement(outcome.RowCount, table.PeakGrowth, sink.BytesWritten, watch.Elapsed);
        }

        private static long LiveHeap()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        private readonly record struct Measurement(long Rows, long PeakGrowth, long BytesWritten, TimeSpan Elapsed);

        /// <summary>Generates rows on demand and samples the live heap while the renderer is mid-sheet.</summary>
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
                new ExportColumn("Flag", ExportValueKind.Boolean),
                new ExportColumn("When", ExportValueKind.DateTimeUtc),
            };

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                for (var i = 1; i <= _rows; i++)
                {
                    // Text that does not repeat, so compression cannot make a buffered sheet look small.
                    yield return new object?[] { i, "row " + Guid.NewGuid().ToString("N"), i * 1.25m, (i & 1) == 0, Epoch.AddSeconds(i) };

                    if (i % SampleEvery == 0)
                    {
                        PeakGrowth = Math.Max(PeakGrowth, LiveHeap() - Baseline);
                        await Task.Yield();
                    }
                }
            }
        }

        private sealed class SingleTableDocument : ITabularDocument
        {
            private readonly ITabularTable _table;

            public SingleTableDocument(ITabularTable table) => _table = table;

            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return _table;
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class PassThroughFormatter : IExportCellFormatter
        {
            public string? Format(object? value, ExportColumn column) => null;
        }

        /// <summary>
        /// Write-only, forward-only, keeps nothing: the shape of a counting stream over a response or a file
        /// being capped. If the renderer needed to seek or read back, this is where it would fail.
        /// </summary>
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
        }
    }
}
