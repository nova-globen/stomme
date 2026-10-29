#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>A data table as it is kept: its id, name and columns.</summary>
    public sealed class StoredSiteTable
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public List<PipelineTableColumn> Columns { get; set; } = new();

        public DateTime CreatedOnUtc { get; set; }

        public DateTime UpdatedOnUtc { get; set; }

        public PipelineSiteTableView ToView() => new()
        {
            Id = Id,
            Name = Name,
            Columns = Columns.Select(column => new PipelineTableColumn { Name = column.Name, Type = column.Type }).ToList(),
            CreatedOnUtc = CreatedOnUtc,
            UpdatedOnUtc = UpdatedOnUtc,
        };

        /// <summary>The table's column of the name, whatever its case.</summary>
        public PipelineTableColumn? Column(string name) =>
            Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The site's data tables (<see cref="PipelineSiteTables"/>) in the engine's key-value store, under the SITE (a
    /// key-value key is one row per installation - the schedules' lesson): one key per table, holding its name and columns, and
    /// one key per ROW, so two runs writing different rows - or an author adding a table while a run writes - never overwrite
    /// each other. A row is a JSON object of its id, createdAt, updatedAt and its columns' values; read in full for a table
    /// (at most <see cref="PipelineSiteTables.MaxRows"/>), oldest first - a row's id sorts in the order rows were made
    /// (<see cref="NewRowId"/>). Two runs updating the SAME row: the last write wins.
    /// </summary>
    public static class PipelineSiteTableStore
    {
        public const string TableKeyPrefix = "pipelines:datatable:";

        public const string RowKeyPrefix = "pipelines:datarow:";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static string TablesPrefix(string? tenantId) => TableKeyPrefix + tenantId + ":";

        public static string TableKey(string? tenantId, string tableId) => TablesPrefix(tenantId) + tableId;

        public static string RowsPrefix(string? tenantId, string tableId) => RowKeyPrefix + tenantId + ":" + tableId + ":";

        public static string RowKey(string? tenantId, string tableId, string rowId) => RowsPrefix(tenantId, tableId) + rowId;

        /// <summary>The site's tables, by name.</summary>
        public static async Task<List<StoredSiteTable>> ListAsync(IKeyValueStore keyValues, string? tenantId, CancellationToken ct)
        {
            var prefix = TablesPrefix(tenantId);
            var tables = new List<StoredSiteTable>();
            foreach (var pair in await keyValues.FindManyAsync(new KeyValueFilter { Key = prefix, StartsWith = true }, ct))
            {
                // Case-blind providers match the prefix case-blindly; the site's own keys match it exactly.
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && Read<StoredSiteTable>(pair.SerializedValue) is { } table)
                {
                    tables.Add(table);
                }
            }

            return tables.OrderBy(table => table.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(table => table.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>The table with the id, or else the one with the name (whatever its case); refused when two share the name.</summary>
        public static async Task<StoredSiteTable?> FindAsync(IKeyValueStore keyValues, string? tenantId, string? idOrName, CancellationToken ct)
        {
            var wanted = (idOrName ?? "").Trim();
            if (wanted.Length == 0)
            {
                return null;
            }

            var tables = await ListAsync(keyValues, tenantId, ct);
            if (tables.FirstOrDefault(table => table.Id == wanted) is { } byId)
            {
                return byId;
            }

            var named = tables.Where(table => string.Equals(table.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            return named.Count switch
            {
                0 => null,
                1 => named[0],
                _ => throw new ArgumentException($"{named.Count} data tables are named '{wanted}' - rename all but one."),
            };
        }

        public static async Task<StoredSiteTable> CreateAsync(IKeyValueStore keyValues, string? tenantId, PipelineSiteTableRequest request, string id, DateTimeOffset now, CancellationToken ct)
        {
            var (name, columns) = Checked(request);
            var tables = await ListAsync(keyValues, tenantId, ct);
            if (tables.Count >= PipelineSiteTables.MaxTables)
            {
                throw new ArgumentException($"A site keeps at most {PipelineSiteTables.MaxTables} data tables.");
            }

            if (tables.Any(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"There is already a data table named '{name}'.");
            }

            var created = new StoredSiteTable { Id = id, Name = name, Columns = columns, CreatedOnUtc = now.UtcDateTime, UpdatedOnUtc = now.UtcDateTime };
            await SaveAsync(keyValues, tenantId, created, ct);
            return created;
        }

        /// <summary>
        /// Renames a table, adds columns, renames them and removes them: a column named with the name it <c>Was</c> keeps its values
        /// under its new name (every row rewritten); a removed column's values go with it; a column added again starts empty. A
        /// column's type does not change - remove it and add it again. Null when there is no such table.
        /// </summary>
        public static async Task<StoredSiteTable?> UpdateAsync(IKeyValueStore keyValues, string? tenantId, string id, PipelineSiteTableRequest request, DateTimeOffset now, CancellationToken ct)
        {
            var (name, columns) = Checked(request);
            var tables = await ListAsync(keyValues, tenantId, ct);
            if (tables.FirstOrDefault(table => table.Id == id) is not { } table)
            {
                return null;
            }

            if (tables.Any(other => other.Id != id && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"There is already a data table named '{name}'.");
            }

            if (columns.FirstOrDefault(column => column.Was is { Length: > 0 } was && table.Column(was) is null) is { } unknown)
            {
                throw new ArgumentException($"'{table.Name}' has no column '{unknown.Was}' to rename.");
            }

            if (columns.Where(column => column.Was is { Length: > 0 }).GroupBy(column => column.Was!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } twice)
            {
                throw new ArgumentException($"The column '{twice.Key}' is renamed twice.");
            }

            if (columns.FirstOrDefault(column => table.Column(column.Was is { Length: > 0 } was ? was : column.Name) is { } kept && kept.Type != column.Type) is { } changed)
            {
                var kept = table.Column(changed.Was is { Length: > 0 } was ? was : changed.Name)!;
                throw new ArgumentException($"'{kept.Name}' holds {PipelineSiteTables.Describe(kept.Type)}; a column's type does not change - remove the column and add it again.");
            }

            // A kept column renamed takes its values along: every row rewritten at once from what it held, so two columns that
            // swap names swap their values rather than one overwriting the other.
            var renamed = columns.Any(column => column.Was is { Length: > 0 } was && !string.Equals(was, column.Name, StringComparison.Ordinal));
            var before = table.Columns.ToList();
            table.Name = name;
            table.Columns = columns.Select(column => new PipelineTableColumn { Name = column.Name, Type = column.Type }).ToList();
            table.UpdatedOnUtc = now.UtcDateTime;
            await SaveAsync(keyValues, tenantId, table, ct);
            if (renamed)
            {
                var source = before.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var row in await ReadRowsAsync(keyValues, tenantId, table.Id, ct))
                {
                    var moved = NewRow(IdOf(row), now);
                    moved[PipelineSiteTables.CreatedColumn] = row[PipelineSiteTables.CreatedColumn]?.DeepClone();
                    moved[PipelineSiteTables.UpdatedColumn] = row[PipelineSiteTables.UpdatedColumn]?.DeepClone();
                    foreach (var column in columns)
                    {
                        var from = column.Was is { Length: > 0 } was ? was : (source.ContainsKey(column.Name) ? column.Name : null);
                        if (from is not null)
                        {
                            moved[column.Name] = row.FirstOrDefault(property => string.Equals(property.Key, from, StringComparison.OrdinalIgnoreCase)).Value?.DeepClone();
                        }
                    }

                    await SaveRowAsync(keyValues, tenantId, table, moved, ct);
                }
            }

            return table;
        }

        /// <summary>Deletes the table and every row of it; false when there is no such table.</summary>
        public static async Task<bool> DeleteAsync(IKeyValueStore keyValues, string? tenantId, string id, CancellationToken ct)
        {
            if ((await ListAsync(keyValues, tenantId, ct)).All(table => table.Id != id))
            {
                return false;
            }

            foreach (var row in await ReadRowsAsync(keyValues, tenantId, id, ct))
            {
                await keyValues.DeleteAsync(RowKey(tenantId, id, IdOf(row)), ct);
            }

            await keyValues.DeleteAsync(TableKey(tenantId, id), ct);
            return true;
        }

        /// <summary>Every row of the table, oldest first.</summary>
        public static async Task<List<JsonObject>> ReadRowsAsync(IKeyValueStore keyValues, string? tenantId, string tableId, CancellationToken ct)
        {
            var prefix = RowsPrefix(tenantId, tableId);
            var rows = new List<JsonObject>();
            foreach (var pair in await keyValues.FindManyAsync(new KeyValueFilter { Key = prefix, StartsWith = true }, ct))
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(pair.SerializedValue ?? "") is JsonObject row)
                    {
                        row[PipelineSiteTables.IdColumn] = pair.Key[prefix.Length..]; // the key is the truth
                        rows.Add(row);
                    }
                }
                catch (JsonException)
                {
                    // Written by this class only; an unreadable row is skipped rather than failing every run that reads the table.
                }
            }

            return rows.OrderBy(IdOf, StringComparer.Ordinal).ToList();
        }

        public static Task SaveRowAsync(IKeyValueStore keyValues, string? tenantId, StoredSiteTable table, JsonObject row, CancellationToken ct) =>
            keyValues.SaveAsync(new SerializedKeyValuePair { Key = RowKey(tenantId, table.Id, IdOf(row)), SerializedValue = Project(table, row).ToJsonString() }, ct);

        public static Task DeleteRowAsync(IKeyValueStore keyValues, string? tenantId, string tableId, string rowId, CancellationToken ct) =>
            keyValues.DeleteAsync(RowKey(tenantId, tableId, rowId), ct);

        /// <summary>
        /// A new row's id: the moment it is made, in ticks, plus its place among the rows one step makes at that moment - so ids
        /// sort in the order rows were made (createdAt keeps milliseconds; many rows share one) - then five random hex digits, so
        /// two runs making a row in the same tick never take one id.
        /// </summary>
        public static string NewRowId(DateTimeOffset now, int index) =>
            (now.UtcTicks + index).ToString("x16", CultureInfo.InvariantCulture) + Random.Shared.Next(0x100000).ToString("x5", CultureInfo.InvariantCulture);

        /// <summary>A new, empty row.</summary>
        public static JsonObject NewRow(string id, DateTimeOffset now) => new()
        {
            [PipelineSiteTables.IdColumn] = id,
            [PipelineSiteTables.CreatedColumn] = Stamp(now),
            [PipelineSiteTables.UpdatedColumn] = Stamp(now),
        };

        /// <summary>
        /// Sets the row's columns from the values - each converted to what its column holds, an empty one cleared - and stamps it
        /// updated. A name that is no column of the table is refused, or, given <paramref name="ignored"/>, passed over and named
        /// there (records carry fields a table does not keep). False, with the reason, for a value its column cannot hold.
        /// </summary>
        public static bool TrySet(StoredSiteTable table, JsonObject row, IEnumerable<KeyValuePair<string, JsonNode?>> values, DateTimeOffset now, out string? error, ISet<string>? ignored = null)
        {
            error = null;
            foreach (var (name, value) in values)
            {
                if (PipelineSiteTables.SystemColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue; // a row read back and written again carries them; they are the store's
                }

                if (table.Column(name) is not { } column)
                {
                    if (ignored is null)
                    {
                        error = $"'{table.Name}' has no column '{name}'.";
                        return false;
                    }

                    ignored.Add(name);
                    continue;
                }

                if (!PipelineSiteTables.TryConvert(value, column.Type, out var converted, out var why))
                {
                    error = $"{column.Name}: {why}.";
                    return false;
                }

                row[column.Name] = converted;
            }

            row[PipelineSiteTables.UpdatedColumn] = Stamp(now);
            return true;
        }

        /// <summary>The row as a run and the page read it: its id, createdAt and updatedAt, then every column in the table's order (null when empty).</summary>
        public static JsonObject Project(StoredSiteTable table, JsonObject row)
        {
            var projected = new JsonObject
            {
                [PipelineSiteTables.IdColumn] = IdOf(row),
                [PipelineSiteTables.CreatedColumn] = row[PipelineSiteTables.CreatedColumn]?.DeepClone(),
                [PipelineSiteTables.UpdatedColumn] = row[PipelineSiteTables.UpdatedColumn]?.DeepClone(),
            };
            foreach (var column in table.Columns)
            {
                var value = row.FirstOrDefault(property => string.Equals(property.Key, column.Name, StringComparison.OrdinalIgnoreCase)).Value;
                projected[column.Name] = value?.DeepClone();
            }

            return projected;
        }

        /// <summary>The row's values as text, for the page.</summary>
        public static Dictionary<string, string> Text(StoredSiteTable table, JsonObject row) =>
            Project(table, row).ToDictionary(property => property.Key, property => PipelineSiteTables.Text(property.Value), StringComparer.Ordinal);

        public static string IdOf(JsonObject row) => PipelineSiteTables.Text(row[PipelineSiteTables.IdColumn]);

        private static string Stamp(DateTimeOffset now) =>
            now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        private static (string Name, List<PipelineTableColumn> Columns) Checked(PipelineSiteTableRequest? request)
        {
            var name = (request?.Name ?? "").Trim();
            var columns = (request?.Columns ?? new List<PipelineTableColumn>())
                .Select(column => new PipelineTableColumn { Name = (column?.Name ?? "").Trim(), Type = column?.Type ?? PipelineTableColumnType.Text, Was = string.IsNullOrWhiteSpace(column?.Was) ? null : column!.Was!.Trim() })
                .ToList();
            if (!PipelineSiteTables.Check(name, columns, out var error))
            {
                throw new ArgumentException(error);
            }

            return (name, columns);
        }

        private static async Task SaveAsync(IKeyValueStore keyValues, string? tenantId, StoredSiteTable table, CancellationToken ct) =>
            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = TableKey(tenantId, table.Id), SerializedValue = JsonSerializer.Serialize(table, Json) }, ct);

        private static T? Read<T>(string? json)
            where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json ?? "", Json);
            }
            catch (JsonException)
            {
                return null; // written by this class only; unreadable is absent rather than a broken page
            }
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListSiteTablesQuery : IQuery<List<PipelineSiteTableView>>;

    public sealed class ListSiteTablesQueryHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : IQueryHandler<ListSiteTablesQuery, List<PipelineSiteTableView>>
    {
        public async Task<List<PipelineSiteTableView>> Handle(ListSiteTablesQuery request, CancellationToken ct) =>
            (await PipelineSiteTableStore.ListAsync(keyValues, tenants.TenantId, ct)).Select(table => table.ToView()).ToList();
    }

    /// <summary>Makes a data table; a name or a column that cannot be one is refused with the reason.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record CreateSiteTableCommand(PipelineSiteTableRequest? Request) : ICommand<PipelineSiteTableView>;

    public sealed class CreateSiteTableCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants, IIdentityGenerator identities, ISystemClock clock)
        : ICommandHandler<CreateSiteTableCommand, PipelineSiteTableView>
    {
        public async Task<PipelineSiteTableView> Handle(CreateSiteTableCommand request, CancellationToken ct) =>
            (await PipelineSiteTableStore.CreateAsync(keyValues, tenants.TenantId, request.Request ?? new PipelineSiteTableRequest(), identities.GenerateId(), clock.UtcNow, ct)).ToView();
    }

    /// <summary>Renames a data table or changes its columns; null when there is no such table.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record UpdateSiteTableCommand(string Id, PipelineSiteTableRequest? Request) : ICommand<PipelineSiteTableView?>;

    public sealed class UpdateSiteTableCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants, ISystemClock clock)
        : ICommandHandler<UpdateSiteTableCommand, PipelineSiteTableView?>
    {
        public async Task<PipelineSiteTableView?> Handle(UpdateSiteTableCommand request, CancellationToken ct) =>
            (await PipelineSiteTableStore.UpdateAsync(keyValues, tenants.TenantId, request.Id, request.Request ?? new PipelineSiteTableRequest(), clock.UtcNow, ct))?.ToView();
    }

    /// <summary>Deletes a data table with its rows.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record DeleteSiteTableCommand(string Id) : ICommand<bool>;

    public sealed class DeleteSiteTableCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : ICommandHandler<DeleteSiteTableCommand, bool>
    {
        public Task<bool> Handle(DeleteSiteTableCommand request, CancellationToken ct) =>
            PipelineSiteTableStore.DeleteAsync(keyValues, tenants.TenantId, request.Id, ct);
    }

    /// <summary>
    /// A page of a table's rows - oldest first, or sorted by a column (or createdAt, updatedAt) as it holds its values: numbers as
    /// numbers, dates as dates, text whatever its case, empty last - and only the rows with the searched text in a value, when
    /// searched; null when there is no such table.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetTableRowsQuery(string Id, int Skip, int Take, string? Search = null, string? Sort = null, bool Descending = false) : IQuery<PipelineTableRowsView?>;

    public sealed class GetTableRowsQueryHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : IQueryHandler<GetTableRowsQuery, PipelineTableRowsView?>
    {
        public const int MaxTake = 500;

        public async Task<PipelineTableRowsView?> Handle(GetTableRowsQuery request, CancellationToken ct)
        {
            if (await PipelineSiteTableStore.FindAsync(keyValues, tenants.TenantId, request.Id, ct) is not { } table || table.Id != request.Id)
            {
                return null;
            }

            var rows = (await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenants.TenantId, table.Id, ct))
                .Select(row => (Row: row, Text: PipelineSiteTableStore.Text(table, row)))
                .ToList();
            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var search = request.Search.Trim();
                rows = rows.Where(row => row.Text.Values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
            }

            if (!string.IsNullOrWhiteSpace(request.Sort))
            {
                rows = Sorted(table, rows, request.Sort.Trim(), request.Descending);
            }

            var skip = Math.Max(0, request.Skip);
            return new PipelineTableRowsView
            {
                Table = table.ToView(),
                Total = rows.Count,
                Skip = skip,
                Rows = rows.Skip(skip).Take(Math.Clamp(request.Take, 1, MaxTake)).Select(row => row.Text).ToList(),
            };
        }

        // Sorted by the column as it holds its values; an empty value last either way; rows that tie keep their order.
        private static List<(JsonObject Row, Dictionary<string, string> Text)> Sorted(StoredSiteTable table, List<(JsonObject Row, Dictionary<string, string> Text)> rows, string sort, bool descending)
        {
            var type = table.Column(sort)?.Type
                ?? (sort.Equals(PipelineSiteTables.CreatedColumn, StringComparison.OrdinalIgnoreCase) || sort.Equals(PipelineSiteTables.UpdatedColumn, StringComparison.OrdinalIgnoreCase) ? PipelineTableColumnType.Date
                    : sort.Equals(PipelineSiteTables.IdColumn, StringComparison.OrdinalIgnoreCase) ? PipelineTableColumnType.Text : (PipelineTableColumnType?)null);
            if (type is null || rows.Count == 0)
            {
                return rows;
            }

            var key = rows.First().Text.Keys.FirstOrDefault(name => string.Equals(name, sort, StringComparison.OrdinalIgnoreCase)) ?? sort;
            int Compare(string left, string right) => type switch
            {
                PipelineTableColumnType.Number when decimal.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) => a.CompareTo(b),
                PipelineTableColumnType.Date when DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a) && DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var b) => a.CompareTo(b),
                _ => StringComparer.OrdinalIgnoreCase.Compare(left, right),
            };

            var present = rows.Where(row => row.Text.TryGetValue(key, out var value) && value.Length > 0).ToList();
            var empty = rows.Where(row => !row.Text.TryGetValue(key, out var value) || value.Length == 0);
            var ordered = present.Select((row, index) => (row, index)).ToList();
            ordered.Sort((x, y) =>
            {
                var compared = Compare(x.row.Text[key], y.row.Text[key]);
                return compared != 0 ? (descending ? -compared : compared) : x.index.CompareTo(y.index);
            });
            return ordered.Select(pair => pair.row).Concat(empty).ToList();
        }
    }

    /// <summary>Adds a row by hand; null when there is no such table, refused when a value does not fit its column or the table is full.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record AddTableRowCommand(string Id, PipelineTableRowRequest? Request) : ICommand<Dictionary<string, string>?>;

    public sealed class AddTableRowCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants, ISystemClock clock)
        : ICommandHandler<AddTableRowCommand, Dictionary<string, string>?>
    {
        public async Task<Dictionary<string, string>?> Handle(AddTableRowCommand request, CancellationToken ct)
        {
            if (await PipelineSiteTableStore.FindAsync(keyValues, tenants.TenantId, request.Id, ct) is not { } table || table.Id != request.Id)
            {
                return null;
            }

            if ((await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenants.TenantId, table.Id, ct)).Count >= PipelineSiteTables.MaxRows)
            {
                throw new ArgumentException($"'{table.Name}' holds {PipelineSiteTables.MaxRows} rows, as many as a table may.");
            }

            var now = clock.UtcNow;
            var row = PipelineSiteTableStore.NewRow(PipelineSiteTableStore.NewRowId(now, 0), now);
            if (!PipelineSiteTableStore.TrySet(table, row, Values(request.Request), now, out var error))
            {
                throw new ArgumentException(error);
            }

            await PipelineSiteTableStore.SaveRowAsync(keyValues, tenants.TenantId, table, row, ct);
            return PipelineSiteTableStore.Text(table, row);
        }

        internal static IEnumerable<KeyValuePair<string, JsonNode?>> Values(PipelineTableRowRequest? request) =>
            (request?.Values ?? new Dictionary<string, string>()).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value ?? "")));
    }

    /// <summary>Changes a row's values by hand (a column left out is left as it is); null when there is no such table or row.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record UpdateTableRowCommand(string Id, string RowId, PipelineTableRowRequest? Request) : ICommand<Dictionary<string, string>?>;

    public sealed class UpdateTableRowCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants, ISystemClock clock)
        : ICommandHandler<UpdateTableRowCommand, Dictionary<string, string>?>
    {
        public async Task<Dictionary<string, string>?> Handle(UpdateTableRowCommand request, CancellationToken ct)
        {
            if (await PipelineSiteTableStore.FindAsync(keyValues, tenants.TenantId, request.Id, ct) is not { } table || table.Id != request.Id)
            {
                return null;
            }

            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = PipelineSiteTableStore.RowKey(tenants.TenantId, table.Id, request.RowId) }, ct);
            if (stored is null || JsonNode.Parse(stored.SerializedValue ?? "{}") is not JsonObject row)
            {
                return null;
            }

            row[PipelineSiteTables.IdColumn] = request.RowId;
            if (!PipelineSiteTableStore.TrySet(table, row, AddTableRowCommandHandler.Values(request.Request), clock.UtcNow, out var error))
            {
                throw new ArgumentException(error);
            }

            await PipelineSiteTableStore.SaveRowAsync(keyValues, tenants.TenantId, table, row, ct);
            return PipelineSiteTableStore.Text(table, row);
        }
    }

    /// <summary>A table's rows as CSV - id, createdAt, updatedAt, then its columns, oldest first; null when there is no such table.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ExportTableCsvQuery(string Id) : IQuery<PipelineTableCsv?>;

    public sealed class ExportTableCsvQueryHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : IQueryHandler<ExportTableCsvQuery, PipelineTableCsv?>
    {
        public async Task<PipelineTableCsv?> Handle(ExportTableCsvQuery request, CancellationToken ct)
        {
            if (await PipelineSiteTableStore.FindAsync(keyValues, tenants.TenantId, request.Id, ct) is not { } table || table.Id != request.Id)
            {
                return null;
            }

            var rows = await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenants.TenantId, table.Id, ct);
            var columns = PipelineSiteTables.SystemColumns.Concat(table.Columns.Select(column => column.Name)).ToList();
            var csv = Steps.Csv.Write(columns, rows.Select(row => (IReadOnlyDictionary<string, string?>)PipelineSiteTableStore.Text(table, row).ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal)), ',');
            var name = new string(table.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
            return new PipelineTableCsv { FileName = (name.Length == 0 ? "table" : name) + ".csv", Csv = csv };
        }
    }

    /// <summary>
    /// Adds a CSV's rows to a table: the header names the columns (whatever their case; id, createdAt and updatedAt are the
    /// table's own and passed over, any other name that is no column is named back as ignored), each value converted as its column
    /// holds it. All or nothing: a value that does not fit refuses the whole file, naming its row and column, before a row is
    /// written. The delimiter is the header's - comma, semicolon or tab. Null when there is no such table.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record ImportTableCsvCommand(string Id, string? Csv) : ICommand<PipelineTableImportResult?>;

    public sealed class ImportTableCsvCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants, ISystemClock clock)
        : ICommandHandler<ImportTableCsvCommand, PipelineTableImportResult?>
    {
        public const int MaxCharacters = 10_000_000;

        public async Task<PipelineTableImportResult?> Handle(ImportTableCsvCommand request, CancellationToken ct)
        {
            if (await PipelineSiteTableStore.FindAsync(keyValues, tenants.TenantId, request.Id, ct) is not { } table || table.Id != request.Id)
            {
                return null;
            }

            var text = (request.Csv ?? "").TrimStart('\uFEFF');
            if (text.Length > MaxCharacters)
            {
                throw new ArgumentException($"The CSV is over {MaxCharacters:N0} characters.");
            }

            var firstLine = text.Split('\n')[0];
            var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(candidate => firstLine.Count(c => c == candidate)).First();
            var records = Steps.Csv.Parse(text, delimiter);
            if (records.Count == 0)
            {
                throw new ArgumentException("The CSV is empty: it needs a header row naming the columns.");
            }

            var header = records[0].Select(name => name.Trim()).ToList();
            var ignored = new List<string>();
            var mapped = new List<(int Index, string Column)>();
            for (var index = 0; index < header.Count; index++)
            {
                if (PipelineSiteTables.SystemColumns.Contains(header[index], StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (table.Column(header[index]) is { } column)
                {
                    mapped.Add((index, column.Name));
                }
                else if (header[index].Length > 0)
                {
                    ignored.Add(header[index]);
                }
            }

            if (mapped.Count == 0)
            {
                throw new ArgumentException($"None of the CSV's columns ({string.Join(", ", header)}) is a column of '{table.Name}' ({string.Join(", ", table.Columns.Select(column => column.Name))}).");
            }

            var existing = (await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenants.TenantId, table.Id, ct)).Count;
            if (existing + records.Count - 1 > PipelineSiteTables.MaxRows)
            {
                throw new ArgumentException($"'{table.Name}' holds {existing} rows; {records.Count - 1} more would pass the {PipelineSiteTables.MaxRows} a table may hold.");
            }

            var now = clock.UtcNow;
            var rows = new List<JsonObject>();
            for (var line = 1; line < records.Count; line++)
            {
                var cells = records[line];
                var row = PipelineSiteTableStore.NewRow(PipelineSiteTableStore.NewRowId(now, line - 1), now);
                var values = mapped.Select(map => KeyValuePair.Create(map.Column, (JsonNode?)JsonValue.Create(map.Index < cells.Count ? cells[map.Index] : "")));
                if (!PipelineSiteTableStore.TrySet(table, row, values, now, out var error))
                {
                    throw new ArgumentException($"Row {line + 1}, {error} Nothing was imported.");
                }

                rows.Add(row);
            }

            foreach (var row in rows)
            {
                await PipelineSiteTableStore.SaveRowAsync(keyValues, tenants.TenantId, table, row, ct);
            }

            return new PipelineTableImportResult { Imported = rows.Count, Ignored = ignored };
        }
    }

    /// <summary>Deletes a row by hand.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record DeleteTableRowCommand(string Id, string RowId) : ICommand<bool>;

    public sealed class DeleteTableRowCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : ICommandHandler<DeleteTableRowCommand, bool>
    {
        public async Task<bool> Handle(DeleteTableRowCommand request, CancellationToken ct)
        {
            var key = PipelineSiteTableStore.RowKey(tenants.TenantId, request.Id, request.RowId);
            if (await keyValues.FindAsync(new KeyValueFilter { Key = key }, ct) is null)
            {
                return false;
            }

            await keyValues.DeleteAsync(key, ct);
            return true;
        }
    }
}
