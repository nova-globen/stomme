#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Modules.DataExport.Execution;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The pieces around the export processor that need no store: stored options, the capped destination, the
    /// pulsing document and the source registry (D108 items 3, 4, 9, 11 and the source-registration gate).
    /// </summary>
    public class ExportExecutionSupportTests
    {
        // ---- stored options: canonical out, distrustful in -----------------------------------------------------

        [Fact]
        public void Options_round_trip_and_equal_options_store_IDENTICAL_text()
        {
            var options = new ExportRenderOptions
            {
                CsvDelimiter = ';', IncludeHeader = false, JsonIndented = true, PdfPageSize = "letter",
                PdfOrientation = "LANDSCAPE", NeutraliseFormulas = false,
            };

            var json = ExportOptionsJson.Serialize(options);
            var parsed = ExportOptionsJson.Parse(json);

            Assert.Equal(';', parsed.CsvDelimiter);
            Assert.False(parsed.IncludeHeader);
            Assert.True(parsed.JsonIndented);
            Assert.Equal("Letter", parsed.PdfPageSize);
            Assert.Equal("Landscape", parsed.PdfOrientation);
            Assert.False(parsed.NeutraliseFormulas);

            Assert.Equal(json, ExportOptionsJson.Serialize(parsed));
            Assert.True(json.Length <= Stomme.Modules.DataExport.Domain.ExportJobAggregate.MaximumOptionsLength);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"neutraliseFormulas\":null}")]
        [InlineData("{\"neutraliseFormulas\":\"false\"}")]
        [InlineData("{\"neutraliseFormulas\":0}")]
        [InlineData("{\"NeutraliseFormulas\":false}")]
        public void The_formula_guard_FAILS_CLOSED_on_anything_but_an_explicit_false(string? stored)
        {
            // Stored text is read back minutes later, possibly by another release. Only a JSON false under the exact
            // member name turns the guard off; damage, absence and near-misses all leave it ON.
            Assert.True(ExportOptionsJson.Parse(stored).NeutraliseFormulas);
        }

        [Fact]
        public void An_explicit_false_DOES_turn_the_guard_off()
        {
            // The other direction, or the theory above would pass for a parser that ignored the member entirely.
            Assert.False(ExportOptionsJson.Parse("{\"neutraliseFormulas\":false}").NeutraliseFormulas);
        }

        [Theory]
        [InlineData("{\"csvDelimiter\":\"x\"}")]
        [InlineData("{\"csvDelimiter\":\"\\n\"}")]
        [InlineData("{\"csvDelimiter\":\",,\"}")]
        [InlineData("{\"csvDelimiter\":5}")]
        public void A_stored_delimiter_outside_the_allowlist_falls_back_to_the_comma(string stored)
        {
            Assert.Equal(',', ExportOptionsJson.Parse(stored).CsvDelimiter);
        }

        [Fact]
        public void A_delimiter_outside_the_allowlist_cannot_be_STORED_either()
        {
            Assert.Throws<ArgumentException>(() => ExportOptionsJson.Serialize(new ExportRenderOptions { CsvDelimiter = 'x' }));
        }

        // ---- the capped destination ----------------------------------------------------------------------------

        [Fact]
        public void The_cap_refuses_the_write_that_would_cross_it_and_that_byte_never_lands()
        {
            using var inner = new MemoryStream();
            var capped = new CappedCountingStream(inner, 10);

            capped.Write(new byte[6], 0, 6);
            capped.Write(new byte[4].AsSpan());
            Assert.Equal(10, capped.BytesWritten);

            var error = Assert.Throws<ExportArtifactTooLargeException>(() => capped.WriteByte(1));
            Assert.Equal(10, error.LimitBytes);
            Assert.Equal(10, inner.Length);
            Assert.Equal(10, capped.BytesWritten);
        }

        [Fact]
        public async Task Every_write_path_is_counted_including_the_async_ones()
        {
            // A cap that guards Write(byte[]) and forgets WriteAsync is a cap a StreamWriter walks straight past.
            using var inner = new MemoryStream();
            var capped = new CappedCountingStream(inner, 100);

            capped.Write(new byte[1], 0, 1);
            capped.Write(new byte[2].AsSpan());
            await capped.WriteAsync(new byte[3], 0, 3);
            await capped.WriteAsync(new byte[4].AsMemory());
            capped.WriteByte(7);

            Assert.Equal(11, capped.BytesWritten);
            Assert.Equal(11, inner.Length);

            await Assert.ThrowsAsync<ExportArtifactTooLargeException>(() => capped.WriteAsync(new byte[90], 0, 90));
            await Assert.ThrowsAsync<ExportArtifactTooLargeException>(async () => await capped.WriteAsync(new byte[90].AsMemory()));
        }

        [Fact]
        public void The_destination_is_forward_only()
        {
            var capped = new CappedCountingStream(new MemoryStream(), 10);

            Assert.False(capped.CanSeek);
            Assert.False(capped.CanRead);
            Assert.Throws<NotSupportedException>(() => capped.Seek(0, SeekOrigin.Begin));
            Assert.Throws<NotSupportedException>(() => capped.Position = 0);
            Assert.Throws<NotSupportedException>(() => capped.Read(new byte[1], 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CappedCountingStream(new MemoryStream(), 0));
        }

        [Fact]
        public void The_configured_cap_is_clamped_to_what_a_File_row_can_record()
        {
            // File.Size is an int: an artifact larger than that could be written and never described.
            Assert.Equal(int.MaxValue, new DataExportOptions { MaxArtifactBytes = long.MaxValue }.EffectiveMaxArtifactBytes);
            Assert.Equal(1, new DataExportOptions { MaxArtifactBytes = -5 }.EffectiveMaxArtifactBytes);
            Assert.Equal(268_435_456, new DataExportOptions().EffectiveMaxArtifactBytes);
            Assert.False(new DataExportOptions().Enabled);
        }

        // ---- the pulsing document --------------------------------------------------------------------------------

        [Fact]
        public async Task It_pulses_every_N_rows_and_between_tables_and_changes_nothing_it_carries()
        {
            var pulses = new List<string>();
            var rowsSeen = 0;
            var document = new PulsingDocument(new TwoTables(25, 7), everyRows: 10, _ =>
            {
                pulses.Add($"after {rowsSeen}");
                return Task.CompletedTask;
            });

            var names = new List<string>();
            await foreach (var table in document.ReadTablesAsync())
            {
                names.Add(table.Name);
                Assert.Single(table.Columns);
                await foreach (var row in table.ReadRowsAsync())
                {
                    rowsSeen++;
                    Assert.Single(row);
                }
            }

            Assert.Equal(new[] { "first", "second" }, names);
            Assert.Equal(32, rowsSeen);
            // Rows 10 and 20 of the first table, then the table boundary; the 7-row table never reaches 10.
            Assert.Equal(new[] { "after 10", "after 20", "after 25" }, pulses);
        }

        [Fact]
        public async Task A_pulse_that_throws_STOPS_the_pull()
        {
            var pulled = 0;
            var document = new PulsingDocument(new TwoTables(1000, 0), everyRows: 10,
                _ => throw new ExportRenderAbandonedException("cancelled"));

            await Assert.ThrowsAsync<ExportRenderAbandonedException>(async () =>
            {
                await foreach (var table in document.ReadTablesAsync())
                {
                    await foreach (var _ in table.ReadRowsAsync())
                    {
                        pulled++;
                    }
                }
            });

            Assert.Equal(10, pulled);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PulsingDocument(new TwoTables(1, 1), 0, _ => Task.CompletedTask));
        }

        // ---- the source registry ---------------------------------------------------------------------------------

        [Fact]
        public void A_source_is_found_by_its_EXACT_key_and_a_near_miss_matches_nothing()
        {
            var registry = new ExportSourceRegistry(new IExportSource[] { new StubSource("stomme.log") });

            Assert.True(registry.TryGet("stomme.log", out var found));
            Assert.Equal("stomme.log", found.Key);

            // Never normalised: two spellings must not name one source, or a permission granted for one answers
            // for the other.
            Assert.False(registry.TryGet("Stomme.Log", out _));
            Assert.False(registry.TryGet(" stomme.log", out _));
            Assert.False(registry.TryGet(null, out _));
        }

        [Fact]
        public void A_source_with_no_permission_a_bad_key_or_an_undefined_cardinality_is_REFUSED()
        {
            Assert.Contains("RequiredPermission", Assert.Throws<InvalidOperationException>(() =>
                new ExportSourceRegistry(new IExportSource[] { new StubSource("a.b", permission: " ") })).Message);

            Assert.Contains("non-canonical", Assert.Throws<InvalidOperationException>(() =>
                new ExportSourceRegistry(new IExportSource[] { new StubSource("Not.Canonical") })).Message);

            Assert.Contains("TableCardinality", Assert.Throws<InvalidOperationException>(() =>
                new ExportSourceRegistry(new IExportSource[] { new StubSource("a.b", cardinality: (TableCardinality)9) })).Message);
        }

        [Fact]
        public void Two_sources_claiming_one_key_are_REFUSED_rather_than_first_wins()
        {
            // First-wins would let a later module answer for a key whose permission was granted to another source.
            var error = Assert.Throws<InvalidOperationException>(() =>
                new ExportSourceRegistry(new IExportSource[] { new StubSource("a.b"), new StubSource("a.b") }));

            Assert.Contains("a.b", error.Message);
            Assert.Empty(new ExportSourceRegistry(Array.Empty<IExportSource>()).Keys);
        }

        // ---- the job row -----------------------------------------------------------------------------------------

        [Fact]
        public void The_jobs_auto_heal_window_is_the_LEASE_and_not_the_render_timeout()
        {
            // Exactly-once is the job ROW's lease, so a second tick beside a live one is safe; a larger window only
            // lengthens how long exports stall after a host is killed mid-tick. See ExportQueueJob's remarks.
            var job = new ExportQueueJob(new ServiceCollection()
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
            var defaults = new DataExportOptions();

            Assert.Equal(defaults.LeaseMinutes, job.MaximumDuration);
            Assert.True(job.MaximumDuration < defaults.JobTimeoutMinutes);
            Assert.True(job.IsEnabled);
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        private sealed class StubSource(string key, string permission = "export:test", TableCardinality cardinality = TableCardinality.Single)
            : IExportSource
        {
            public string Key => key;

            public string RequiredPermission => permission;

            public TableCardinality TableCardinality => cardinality;

            public string? Validate(ExportSourceContext context) => null;

            public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default) =>
                throw new NotSupportedException();
        }

        private sealed class TwoTables(int firstRows, int secondRows) : ITabularDocument
        {
            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new Table("first", firstRows);
                yield return new Table("second", secondRows);
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private sealed class Table(string name, int rows) : ITabularTable
            {
                public string Name => name;

                public IReadOnlyList<ExportColumn> Columns { get; } = new[] { new ExportColumn("N", ExportValueKind.Integer) };

                public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    for (var i = 0; i < rows; i++)
                    {
                        yield return new object?[] { i };
                    }

                    await Task.CompletedTask;
                }
            }
        }
    }
}
