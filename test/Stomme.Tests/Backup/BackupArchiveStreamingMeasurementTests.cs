#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Backup.Application.Archive;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    [CollectionDefinition(nameof(BackupArchiveStreamingMeasurementTests), DisableParallelization = true)]
    public sealed class BackupArchiveStreamingMeasurementCollection
    {
    }

    /// <summary>
    /// A 256 MiB part is written and read back with the live managed heap BOUNDED: the archive holds
    /// one chunk, never the part (an earlier protector held the whole archive in memory, a ~2 GB ceiling). The instrument is
    /// <c>XlsxStreamingMeasurementTests</c>' - a forced collection and <see cref="GC.GetTotalMemory(bool)"/>, sampled from
    /// INSIDE the data source and the data sink while the archive works - and it is validated in the other direction by a
    /// sink that KEEPS what it is given, which the same instrument must see grow.
    /// </summary>
    [Collection(nameof(BackupArchiveStreamingMeasurementTests))]
    public class BackupArchiveStreamingMeasurementTests(ITestOutputHelper output)
    {
        private const long PartBytes = 256L * 1024 * 1024;
        private const long BoundBytes = 32L * 1024 * 1024;
        private const long SampleEvery = 16L * 1024 * 1024;

        [Fact]
        public async Task A_256_MiB_part_is_written_and_read_with_a_bounded_heap()
        {
            var path = Path.Combine(Path.GetTempPath(), "stomme-backup-stream-" + Guid.NewGuid().ToString("N") + ".dcbak");
            try
            {
                var baseline = Heap();
                var source = new SamplingSource(PartBytes, baseline);
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
                await using (var writer = await BackupArchiveWriter.CreateAsync(file, "streaming passphrase"))
                {
                    await using (var part = writer.OpenPart("big", "1", "1"))
                    {
                        await source.CopyToAsync(part, 256 * 1024);
                    }

                    await writer.CompleteAsync("10.2.3", Array.Empty<int>(), DateTime.UtcNow);
                }

                output.WriteLine($"write: file {new FileInfo(path).Length:N0} bytes, peak heap growth {source.PeakGrowth:N0}");
                Assert.True(new FileInfo(path).Length > PartBytes);
                Assert.True(source.PeakGrowth < BoundBytes, $"writing held {source.PeakGrowth:N0} bytes live (bound {BoundBytes:N0}).");

                baseline = Heap();
                var sink = new SamplingSink(baseline, keep: false);
                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
                using (var reader = await BackupArchiveReader.OpenAsync(file, "streaming passphrase"))
                await using (var part = reader.OpenPart("big"))
                {
                    await part.CopyToAsync(sink, 256 * 1024);
                }

                output.WriteLine($"read: {sink.Received:N0} bytes, peak heap growth {sink.PeakGrowth:N0}");
                Assert.Equal(PartBytes, sink.Received);
                Assert.True(sink.PeakGrowth < BoundBytes, $"reading held {sink.PeakGrowth:N0} bytes live (bound {BoundBytes:N0}).");
            }
            finally
            {
                File.Delete(path);
            }
        }

        // The instrument, validated: the same sampling over a sink that RETAINS what it reads must see the part.
        [Fact]
        public async Task The_instrument_sees_a_part_that_is_held()
        {
            const long held = 96L * 1024 * 1024;
            using var archive = new MemoryStream();
            await using (var writer = await BackupArchiveWriter.CreateAsync(archive, "streaming passphrase"))
            {
                await using (var part = writer.OpenPart("held", "1", "1"))
                {
                    await new SamplingSource(held, Heap()).CopyToAsync(part, 256 * 1024);
                }

                await writer.CompleteAsync("10.2.3", Array.Empty<int>(), DateTime.UtcNow);
            }

            // Memory an earlier test still held at the baseline and released inside the window nets
            // against the planted part (measured: 18.9 MB seen of 96 MiB inside the full test run, green alone). A sink that
            // keeps the part holds it in EVERY window while such a release is chance, so up to three windows are measured and
            // one clear reading proves the instrument; the bound is unchanged.
            var readings = new System.Collections.Generic.List<long>();
            for (var window = 0; window < 3; window++)
            {
                var growth = await HeldGrowthAsync(archive, keep: true);
                readings.Add(growth);
                if (growth > 2 * BoundBytes)
                {
                    break;
                }
            }

            output.WriteLine($"held: peak heap growth {string.Join(", ", readings.Select(reading => reading.ToString("N0")))}");
            Assert.True(readings.Max() > 2 * BoundBytes, $"the instrument saw at most {readings.Max():N0} bytes of a {held:N0}-byte part being held, in {readings.Count} window(s).");
        }

        private static async Task<long> HeldGrowthAsync(MemoryStream archive, bool keep)
        {
            archive.Position = 0;
            var sink = new SamplingSink(Heap(), keep);
            using (var reader = await BackupArchiveReader.OpenAsync(archive, "streaming passphrase"))
            await using (var part = reader.OpenPart("held"))
            {
                await part.CopyToAsync(sink, 256 * 1024);
            }

            GC.KeepAlive(sink);
            return sink.PeakGrowth;
        }

        private static long Heap()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        /// <summary>Generates <c>length</c> bytes on demand, sampling the heap every <see cref="SampleEvery"/> bytes.</summary>
        private sealed class SamplingSource(long length, long baseline) : Stream
        {
            private long _position;
            private long _nextSample = SampleEvery;

            public long PeakGrowth { get; private set; }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var count = (int)Math.Min(buffer.Length, length - _position);
                for (var index = 0; index < count; index++)
                {
                    buffer[index] = (byte)((_position + index) * 31);
                }

                _position += count;
                if (_position >= _nextSample)
                {
                    PeakGrowth = Math.Max(PeakGrowth, Heap() - baseline);
                    _nextSample += SampleEvery;
                }

                return count;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => length;

            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>Counts (and, when <c>keep</c>, retains) what it is given, sampling the heap every <see cref="SampleEvery"/> bytes.</summary>
        private sealed class SamplingSink(long baseline, bool keep) : Stream
        {
            private readonly MemoryStream _kept = new();
            private long _nextSample = SampleEvery;

            public long Received { get; private set; }

            public long PeakGrowth { get; private set; }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (keep)
                {
                    _kept.Write(buffer);
                }

                Received += buffer.Length;
                if (Received >= _nextSample)
                {
                    PeakGrowth = Math.Max(PeakGrowth, Heap() - baseline);
                    _nextSample += SampleEvery;
                }
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => Received;

            public override long Position { get => Received; set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
