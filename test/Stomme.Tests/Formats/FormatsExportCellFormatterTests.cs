#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Formats;
using Stomme.Formats.Persistence;
using Stomme.Modules.Formats.Application;
using Stomme.Modules.Formats.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.Formats
{
    /// <summary>
    /// The D108 cell formatter over Formats: a key resolves for the prepared site (a definition
    /// by id or name, a preset by id or name); an unknown key declines, so the renderer writes invariant text; a key
    /// naming ANOTHER site's definition is not this site's and declines the same way - refused without a cross-site read;
    /// a column without a key declines; a value the format cannot take renders through the descriptor's never-throw path.
    /// </summary>
    public sealed class FormatsExportCellFormatterTests
    {
        private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private static FormatDefinitionAggregate Definition(int siteId, string name, FormatKind kind, string pattern, string culture) =>
            FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, name, kind, pattern, culture, null, Now);

        private static async Task<FormatsExportCellFormatter> PreparedAsync(int siteId, params FormatDefinitionAggregate[] definitions)
        {
            var formatter = new FormatsExportCellFormatter(new InMemoryFormatDefinitions(definitions), NullLogger<FormatsExportCellFormatter>.Instance);
            await formatter.PrepareAsync(siteId);
            return formatter;
        }

        [Fact]
        public async Task A_site_definition_resolves_by_id_and_by_name_for_the_prepared_site()
        {
            var euro = Definition(1, "Euro amount", FormatKind.Currency, "C2", "de-DE");
            var formatter = await PreparedAsync(1, euro);

            Assert.Equal("1.234,50 €", formatter.Format(1234.5m, new ExportColumn("Amount", ExportValueKind.Decimal, euro.Id.ToString())));
            Assert.Equal("1.234,50 €", formatter.Format(1234.5m, new ExportColumn("Amount", ExportValueKind.Decimal, "euro AMOUNT")));
        }

        [Fact]
        public async Task A_preset_resolves_by_id_and_by_name_with_or_without_a_prepared_site()
        {
            var iso = SystemFormats.All.First(preset => preset.Name == "ISO 8601 date");
            var cold = new FormatsExportCellFormatter(new InMemoryFormatDefinitions(), NullLogger<FormatsExportCellFormatter>.Instance);
            var when = new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

            Assert.Equal("2026-06-23", cold.Format(when, new ExportColumn("When", ExportValueKind.DateTimeUtc, iso.Id.ToString())));
            Assert.Equal("2026-06-23", (await PreparedAsync(1)).Format(when, new ExportColumn("When", ExportValueKind.DateTimeUtc, "iso 8601 DATE")));
        }

        [Fact]
        public async Task An_unknown_key_a_blank_key_and_a_null_value_decline_so_the_renderer_writes_invariant_text()
        {
            var formatter = await PreparedAsync(1, Definition(1, "Euro amount", FormatKind.Currency, "C2", "de-DE"));

            Assert.Null(formatter.Format(5m, new ExportColumn("Amount", ExportValueKind.Decimal, Guid.NewGuid().ToString())));
            Assert.Null(formatter.Format(5m, new ExportColumn("Amount", ExportValueKind.Decimal, "no such format")));
            Assert.Null(formatter.Format(5m, new ExportColumn("Amount", ExportValueKind.Decimal)));
            Assert.Null(formatter.Format(5m, new ExportColumn("Amount", ExportValueKind.Decimal, "  ")));
            Assert.Null(formatter.Format(null, new ExportColumn("Amount", ExportValueKind.Decimal, "Euro amount")));
        }

        [Fact]
        public async Task Another_sites_definition_is_refused_by_id_and_by_name_without_a_cross_site_read()
        {
            var theirs = Definition(2, "Euro amount", FormatKind.Currency, "C2", "de-DE");
            var store = new InMemoryFormatDefinitions(theirs);
            var formatter = new FormatsExportCellFormatter(store, NullLogger<FormatsExportCellFormatter>.Instance);
            await formatter.PrepareAsync(1);

            Assert.Null(formatter.Format(5m, new ExportColumn("Amount", ExportValueKind.Decimal, theirs.Id.ToString())));
            Assert.Null(formatter.Format(5m, new ExportColumn("Amount", ExportValueKind.Decimal, "Euro amount")));
            Assert.Equal(new[] { 1 }, store.SitesListed); // the other site's rows were never asked for
            Assert.Equal(0, store.ByIdReads);

            // Prepared for site 2, the same key applies.
            await formatter.PrepareAsync(2);
            Assert.Equal("5,00 €", formatter.Format(5m, new ExportColumn("Amount", ExportValueKind.Decimal, theirs.Id.ToString())));
        }

        [Fact]
        public async Task A_value_the_format_cannot_take_renders_through_the_never_throw_path()
        {
            var formatter = await PreparedAsync(1, Definition(1, "Short date", FormatKind.Date, "yyyy-MM-dd", "en-US"));

            var text = formatter.Format("not a date", new ExportColumn("When", ExportValueKind.Text, "Short date"));

            Assert.NotNull(text);
            Assert.Contains("not a date", text);
        }

        /// <summary>A dictionary repository that records which sites were listed and whether an id was read.</summary>
        internal sealed class InMemoryFormatDefinitions(params FormatDefinitionAggregate[] seed) : IFormatDefinitionRepository
        {
            private readonly List<FormatDefinitionAggregate> _rows = seed.ToList();

            public List<int> SitesListed { get; } = new();

            public int ByIdReads { get; private set; }

            public Task<FormatDefinitionAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default)
            {
                ByIdReads++;
                return Task.FromResult(_rows.FirstOrDefault(row => row.Id == id));
            }

            public Task<IReadOnlyList<FormatDefinitionAggregate>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FormatDefinitionAggregate>>(_rows.ToList());

            public Task AddAsync(FormatDefinitionAggregate aggregate, CancellationToken ct = default)
            {
                _rows.Add(aggregate);
                return Task.CompletedTask;
            }

            public Task UpdateAsync(FormatDefinitionAggregate aggregate, CancellationToken ct = default) => Task.CompletedTask;

            public Task DeleteAsync(FormatDefinitionAggregate aggregate, CancellationToken ct = default)
            {
                _rows.Remove(aggregate);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<FormatDefinitionAggregate>> FindAsync(Expression<Func<FormatDefinitionAggregate, bool>> predicate, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<FormatDefinitionAggregate>>(_rows.Where(predicate.Compile()).ToList());

            public Task<FormatDefinitionAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default) =>
                Task.FromResult(_rows.FirstOrDefault(row => row.SiteId == siteId && row.NameNormalized == FormatDefinitionAggregate.NormalizeName(name)));

            public Task<IReadOnlyList<FormatDefinitionAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default)
            {
                SitesListed.Add(siteId);
                return Task.FromResult<IReadOnlyList<FormatDefinitionAggregate>>(_rows.Where(row => row.SiteId == siteId).ToList());
            }
        }
    }
}
