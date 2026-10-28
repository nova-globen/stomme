#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;

namespace Stomme.Tests.Fixtures.ExportTestModule
{
    /// <summary>
    /// What a packaged module's export source looks like: it references <c>Stomme.Shared</c> and the BCL, and
    /// NOTHING else. <c>ExportContractDependencyTests</c> compiles this very file against those two alone, so a
    /// using directive that reaches into the engine breaks that test rather than quietly widening the contract.
    /// </summary>
    /// <remarks>
    /// The rows are GENERATED as they are pulled - one array alive at a time - so the same source serves the
    /// 1,000,000-row case without ever holding a table. The site comes from the server-built context; the only
    /// argument is a row count, and an unknown member is rejected rather than ignored.
    /// </remarks>
    public sealed class GeneratedRowsExportSource : IExportSource
    {
        public const string SourceKey = "tests.generated";
        public const string Permission = "tests:generated";
        public const int MaximumRows = 1_000_000;

        public string Key => SourceKey;

        public string RequiredPermission => Permission;

        public TableCardinality TableCardinality => TableCardinality.Single;

        public string? Validate(ExportSourceContext context) => TryReadRows(context.ArgumentsJson, out _, out var rejection) ? null : rejection;

        public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
        {
            if (!TryReadRows(context.ArgumentsJson, out var rows, out var rejection))
            {
                throw new InvalidOperationException(rejection);
            }

            return Task.FromResult<ITabularDocument>(new GeneratedDocument(context.SiteId, rows));
        }

        private static bool TryReadRows(string? argumentsJson, out int rows, out string? rejection)
        {
            rows = 10;
            rejection = null;
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return true;
            }

            try
            {
                using var parsed = JsonDocument.Parse(argumentsJson);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                {
                    rejection = "The arguments must be a JSON object.";
                    return false;
                }

                foreach (var member in parsed.RootElement.EnumerateObject())
                {
                    if (member.Name != "rows" || !member.Value.TryGetInt32(out rows) || rows < 0 || rows > MaximumRows)
                    {
                        rejection = $"The only argument is \"rows\", a whole number from 0 to {MaximumRows}.";
                        return false;
                    }
                }

                return true;
            }
            catch (JsonException)
            {
                rejection = "The arguments are not valid JSON.";
                return false;
            }
        }

        private sealed class GeneratedDocument : ITabularDocument
        {
            private readonly int _siteId;
            private readonly int _rows;

            public GeneratedDocument(int siteId, int rows)
            {
                _siteId = siteId;
                _rows = rows;
            }

            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                yield return new GeneratedTable(_siteId, _rows);
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => default;
        }

        private sealed class GeneratedTable : ITabularTable
        {
            private readonly int _siteId;
            private readonly int _rows;

            public GeneratedTable(int siteId, int rows)
            {
                _siteId = siteId;
                _rows = rows;
            }

            public string Name => "generated";

            public IReadOnlyList<ExportColumn> Columns { get; } = new[]
            {
                new ExportColumn("Site", ExportValueKind.Integer),
                new ExportColumn("Number", ExportValueKind.Integer),
                new ExportColumn("Label", ExportValueKind.Text),
            };

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                for (var index = 0; index < _rows; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new object?[] { _siteId, index, "row " + index };
                }

                await Task.CompletedTask;
            }
        }
    }
}
