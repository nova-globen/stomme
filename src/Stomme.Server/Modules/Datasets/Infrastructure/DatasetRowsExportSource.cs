#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Export;
using Stomme.Modules.DataExport.Sources;

namespace Stomme.Modules.Datasets.Infrastructure
{
    /// <summary>
    /// A dataset's rows as an export (the predecessor platform's <c>DatasetExportSourceResolver</c> on D108's contract):
    /// key <c>datasets.rows</c>, one table named after the dataset, its columns the published schema in order or the
    /// request's projection, each typed from the schema and carrying the column's format for the D108 cell formatter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The data permission is <c>datasets:preview</c></b>, the same raw-row key the Datasets API asks - the engine checks
    /// it for the requester when the request is made and again when the job runs (the predecessor platform checked it inside the resolver).
    /// </para>
    /// <para>
    /// <b>Everything goes through the catalogue</b> (<see cref="IDatasetCatalog"/>): the site is the job's resolved alias, so
    /// another site's dataset "does not exist", and the rows come only when the classification allows them - a Confidential
    /// or Restricted dataset exports its schema with NO rows, never past the gate.
    /// </para>
    /// <para>
    /// Arguments: <c>{"datasetId": "...", "columns": ["b", "a"]}</c> - the id required, the projection optional (it
    /// selects and orders; an empty list means the whole schema); any other member is refused. A projected column the
    /// schema does not have is refused when the job opens the source, naming it.
    /// </para>
    /// </remarks>
    public sealed class DatasetRowsExportSource(IDatasetCatalog catalog) : IExportSource
    {
        public const string SourceKey = "datasets.rows";

        private static readonly string[] Members = { "datasetId", "columns" };

        public string Key => SourceKey;

        public string RequiredPermission => DatasetPermissions.Preview;

        public TableCardinality TableCardinality => TableCardinality.Single;

        public string? Validate(ExportSourceContext context) => TryParse(context?.ArgumentsJson, out _, out _, out var error) ? null : error;

        public async Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryParse(context.ArgumentsJson, out var datasetId, out var projection, out var error))
            {
                throw new InvalidOperationException(error);
            }

            var descriptor = await catalog.GetDescriptorAsync(datasetId, ct)
                ?? throw new InvalidOperationException($"Dataset '{datasetId}' does not exist.");

            var schema = descriptor.Schema.Columns;
            IReadOnlyList<DatasetColumnDescriptor> columns = schema;
            if (projection.Count > 0)
            {
                var selected = new List<DatasetColumnDescriptor>(projection.Count);
                foreach (var name in projection)
                {
                    var column = schema.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"The export projection requests column '{name}', which is not in the dataset schema.");
                    selected.Add(column);
                }

                columns = selected;
            }

            var rows = await catalog.GetPreviewRowsAsync(datasetId, ct);
            var table = new JsonRowsTable(
                descriptor.Name,
                columns.Select(column => new ExportColumn(column.Name, JsonRowsTable.KindOf(column.DataType), column.FormatKey)).ToList(),
                rows.Allowed ? rows.RowsJson : "[]"); // a sensitive dataset exports its schema only
            return new ExportTablesDocument(new[] { table });
        }

        internal static bool TryParse(string? json, out Guid datasetId, out IReadOnlyList<string> projection, out string error)
        {
            datasetId = Guid.Empty;
            projection = Array.Empty<string>();
            if (!ExportArguments.TryReadObject(json, Members, out var members, out error))
            {
                return false;
            }

            if (!ExportArguments.TryGuid(members, "datasetId", out datasetId))
            {
                error = "The arguments need a 'datasetId' that is a GUID.";
                return false;
            }

            if (members.TryGetValue("columns", out var columns) && columns.ValueKind != JsonValueKind.Null)
            {
                if (columns.ValueKind != JsonValueKind.Array)
                {
                    error = "'columns' must be an array of column names.";
                    return false;
                }

                var names = new List<string>();
                foreach (var column in columns.EnumerateArray())
                {
                    if (column.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(column.GetString()))
                    {
                        error = "'columns' must be an array of column names.";
                        return false;
                    }

                    names.Add(column.GetString()!.Trim());
                }

                projection = names;
            }

            return true;
        }
    }
}
