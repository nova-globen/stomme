#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Dashboards;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Dashboards.Domain
{
    // ============================================================================================================
    // The Dashboard's value objects. Every one is an
    // IStringValueObject with a public static Create(string): the module store persists a value object as ONE scalar
    // through that pair - a plain ValueObject property is refused at model build - and the widget list rides the
    // JSON collection column, where the same pair reconstructs each nested value object. The scalar is the value
    // object's canonical text; a value that fails its own rule on the way back in fails loudly, never as an empty row.
    // ============================================================================================================

    internal static class DashboardJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T? Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
    }

    /// <summary>The grid a dashboard lays its widgets on: columns and a row height; the scalar is <c>{columns}x{rowHeightPx}</c>.</summary>
    public sealed class LayoutGrid : ValueObject, IStringValueObject
    {
        public LayoutGrid(int columns, int rowHeightPx)
        {
            if (columns is < DashboardLimits.MinColumns or > DashboardLimits.MaxColumns)
            {
                throw new ArgumentOutOfRangeException(nameof(columns), columns, $"A layout grid must have between {DashboardLimits.MinColumns} and {DashboardLimits.MaxColumns} columns.");
            }

            if (rowHeightPx is < DashboardLimits.MinRowHeightPx or > DashboardLimits.MaxRowHeightPx)
            {
                throw new ArgumentOutOfRangeException(nameof(rowHeightPx), rowHeightPx, $"Row height must be between {DashboardLimits.MinRowHeightPx} and {DashboardLimits.MaxRowHeightPx} px.");
            }

            Columns = columns;
            RowHeightPx = rowHeightPx;
        }

        public int Columns { get; }

        public int RowHeightPx { get; }

        public string Value => $"{Columns}x{RowHeightPx}";

        public static LayoutGrid Default => new(12, 40);

        public static LayoutGrid Create(string value)
        {
            var parts = (value ?? string.Empty).Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var columns) || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowHeight))
            {
                throw new ArgumentException($"'{value}' is not a layout grid ({{columns}}x{{rowHeightPx}}).", nameof(value));
            }

            return new LayoutGrid(columns, rowHeight);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Columns;
            yield return RowHeightPx;
        }

        public override string ToString() => Value;
    }

    /// <summary>Where a widget sits on the grid and how many cells it spans; the scalar is <c>column,row,columnSpan,rowSpan</c>.</summary>
    public sealed class GridPosition : ValueObject, IStringValueObject
    {
        public GridPosition(int column, int row, int columnSpan, int rowSpan)
        {
            if (column < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be non-negative.");
            }

            if (row < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(row), row, "Row must be non-negative.");
            }

            if (columnSpan < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(columnSpan), columnSpan, "Column span must be at least 1.");
            }

            if (rowSpan < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rowSpan), rowSpan, "Row span must be at least 1.");
            }

            Column = column;
            Row = row;
            ColumnSpan = columnSpan;
            RowSpan = rowSpan;
        }

        public int Column { get; }

        public int Row { get; }

        public int ColumnSpan { get; }

        public int RowSpan { get; }

        public string Value => string.Join(',', new[] { Column, Row, ColumnSpan, RowSpan }.Select(n => n.ToString(CultureInfo.InvariantCulture)));

        public bool FitsWithin(int columns) => Column + ColumnSpan <= columns;

        public bool Overlaps(GridPosition other)
        {
            ArgumentNullException.ThrowIfNull(other);
            var separated = Column + ColumnSpan <= other.Column || other.Column + other.ColumnSpan <= Column || Row + RowSpan <= other.Row || other.Row + other.RowSpan <= Row;
            return !separated;
        }

        public static GridPosition Create(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            var numbers = new int[4];
            if (parts.Length != 4 || parts.Where((part, index) => !int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[index])).Any())
            {
                throw new ArgumentException($"'{value}' is not a grid position (column,row,columnSpan,rowSpan).", nameof(value));
            }

            return new GridPosition(numbers[0], numbers[1], numbers[2], numbers[3]);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Column;
            yield return Row;
            yield return ColumnSpan;
            yield return RowSpan;
        }

        public override string ToString() => Value;
    }

    /// <summary>
    /// A chart widget's ECharts option document, theme-free: colours and fonts are the theme's and the renderer injects
    /// them, so a spec carrying any is refused. The scalar is the JSON itself.
    /// </summary>
    public sealed class ChartSpec : ValueObject, IStringValueObject
    {
        private static readonly HashSet<string> ThemeProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "color", "colors", "backgroundColor", "borderColor", "textColor", "fontFamily", "fontSize", "fontWeight", "fontStyle",
        };

        public ChartSpec(string echartsJson)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(echartsJson);
            if (echartsJson.Length > DashboardLimits.MaxChartSpecLength)
            {
                throw new ArgumentException($"A chart spec must be at most {DashboardLimits.MaxChartSpecLength} characters.", nameof(echartsJson));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(echartsJson);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("A chart spec must be valid JSON.", nameof(echartsJson), ex);
            }

            using (document)
            {
                var offending = FindThemeProperty(document.RootElement);
                if (offending is not null)
                {
                    throw new InvalidOperationException($"A chart spec must be theme-free; the property '{offending}' carries a color/font value and is injected by the renderer.");
                }
            }

            EchartsJson = echartsJson;
        }

        public string EchartsJson { get; }

        public string Value => EchartsJson;

        public static ChartSpec Create(string value) => new(value);

        private static string? FindThemeProperty(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (ThemeProperties.Contains(property.Name))
                        {
                            return property.Name;
                        }

                        if (FindThemeProperty(property.Value) is { } nested)
                        {
                            return nested;
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        if (FindThemeProperty(item) is { } nested)
                        {
                            return nested;
                        }
                    }

                    break;
            }

            return null;
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return EchartsJson;
        }

        public override string ToString() => Value;
    }

    /// <summary>The dataset a widget reads and the columns it binds; the scalar is a small JSON document.</summary>
    public sealed class DataBinding : ValueObject, IStringValueObject
    {
        private sealed record Document(Guid DatasetId, IReadOnlyList<string> Columns);

        public DataBinding(Guid datasetId, IReadOnlyList<string> columns)
        {
            if (datasetId == Guid.Empty)
            {
                throw new ArgumentException("A data binding must reference a dataset.", nameof(datasetId));
            }

            ArgumentNullException.ThrowIfNull(columns);
            Columns = columns.Select(column => column?.Trim() ?? string.Empty).ToList();
            if (Columns.Any(string.IsNullOrEmpty))
            {
                throw new ArgumentException("Bound column names must be non-empty.", nameof(columns));
            }

            DatasetId = datasetId;
        }

        public Guid DatasetId { get; }

        public IReadOnlyList<string> Columns { get; }

        public string Value => DashboardJson.Write(new Document(DatasetId, Columns));

        public static DataBinding Create(string value)
        {
            var document = DashboardJson.Read<Document>(value) ?? throw new ArgumentException("A data binding is a JSON document.", nameof(value));
            return new DataBinding(document.DatasetId, document.Columns ?? Array.Empty<string>());
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return DatasetId;
            foreach (var column in Columns)
            {
                yield return column;
            }
        }

        public override string ToString() => Value;
    }

    /// <summary>What a widget does when clicked; the scalar is <c>Kind</c> or <c>Kind|target</c>.</summary>
    public sealed class Interaction : ValueObject, IStringValueObject
    {
        public Interaction(InteractionKind kind, string? target = null)
        {
            if (kind != InteractionKind.None && string.IsNullOrWhiteSpace(target))
            {
                throw new ArgumentException("An interactive widget must declare a target.", nameof(target));
            }

            Kind = kind;
            Target = kind == InteractionKind.None ? null : target!.Trim();
        }

        public InteractionKind Kind { get; }

        public string? Target { get; }

        public string Value => Target is null ? Kind.ToString() : $"{Kind}|{Target}";

        public static Interaction None => new(InteractionKind.None);

        public static Interaction Create(string value)
        {
            var separator = (value ?? string.Empty).IndexOf('|');
            var kindText = separator < 0 ? value ?? string.Empty : value![..separator];
            if (!Enum.TryParse<InteractionKind>(kindText, ignoreCase: true, out var kind))
            {
                throw new ArgumentException($"'{value}' is not an interaction.", nameof(value));
            }

            return new Interaction(kind, separator < 0 ? null : value![(separator + 1)..]);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Kind;
            yield return Target;
        }

        public override string ToString() => Value;
    }

    public sealed record ColumnWidth(string Column, int WidthPx);

    /// <summary>A grid widget's view state - column widths, sort, filter; the scalar is a small JSON document.</summary>
    public sealed class GridState : ValueObject, IStringValueObject
    {
        private sealed record Document(IReadOnlyList<ColumnWidth> ColumnWidths, string? SortColumn, bool SortDescending, string? FilterExpression);

        public GridState(IReadOnlyList<ColumnWidth> columnWidths, string? sortColumn, bool sortDescending, string? filterExpression)
        {
            ArgumentNullException.ThrowIfNull(columnWidths);
            if (columnWidths.Any(column => string.IsNullOrWhiteSpace(column.Column)))
            {
                throw new ArgumentException("Grid-state column names must be non-empty.", nameof(columnWidths));
            }

            if (columnWidths.Any(column => column.WidthPx < 1))
            {
                throw new ArgumentOutOfRangeException(nameof(columnWidths), "Column widths must be positive.");
            }

            ColumnWidths = columnWidths.ToList();
            SortColumn = string.IsNullOrWhiteSpace(sortColumn) ? null : sortColumn.Trim();
            SortDescending = sortDescending;
            FilterExpression = string.IsNullOrWhiteSpace(filterExpression) ? null : filterExpression.Trim();
        }

        public IReadOnlyList<ColumnWidth> ColumnWidths { get; }

        public string? SortColumn { get; }

        public bool SortDescending { get; }

        public string? FilterExpression { get; }

        public string Value => DashboardJson.Write(new Document(ColumnWidths, SortColumn, SortDescending, FilterExpression));

        public static GridState Empty => new(Array.Empty<ColumnWidth>(), null, false, null);

        public static GridState Create(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Empty;
            }

            var document = DashboardJson.Read<Document>(value) ?? throw new ArgumentException("A grid state is a JSON document.", nameof(value));
            return new GridState(document.ColumnWidths ?? Array.Empty<ColumnWidth>(), document.SortColumn, document.SortDescending, document.FilterExpression);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            foreach (var column in ColumnWidths)
            {
                yield return column;
            }

            yield return SortColumn;
            yield return SortDescending;
            yield return FilterExpression;
        }

        public override string ToString() => Value;
    }

    /// <summary>How a widget refreshes: by hand, on an interval of at least thirty seconds, or when its dataset changes; the scalar is <c>Manual</c>, <c>Interval:seconds</c> or <c>EventDriven</c>.</summary>
    public sealed class RefreshMode : ValueObject, IStringValueObject
    {
        public const int MinimumIntervalSeconds = DashboardLimits.MinimumRefreshIntervalSeconds;

        private RefreshMode(RefreshKind kind, int? intervalSeconds)
        {
            Kind = kind;
            IntervalSeconds = intervalSeconds;
        }

        public RefreshKind Kind { get; }

        public int? IntervalSeconds { get; }

        public string Value => IntervalSeconds is { } seconds ? $"{Kind}:{seconds.ToString(CultureInfo.InvariantCulture)}" : Kind.ToString();

        public static RefreshMode Manual => new(RefreshKind.Manual, null);

        public static RefreshMode EventDriven => new(RefreshKind.EventDriven, null);

        public static RefreshMode Interval(int seconds)
        {
            if (seconds < MinimumIntervalSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"An interval refresh must be at least {MinimumIntervalSeconds} seconds.");
            }

            return new RefreshMode(RefreshKind.Interval, seconds);
        }

        public static RefreshMode Create(string value)
        {
            var separator = (value ?? string.Empty).IndexOf(':');
            var kindText = separator < 0 ? value ?? string.Empty : value![..separator];
            if (!Enum.TryParse<RefreshKind>(kindText, ignoreCase: true, out var kind))
            {
                throw new ArgumentException($"'{value}' is not a refresh mode.", nameof(value));
            }

            return kind switch
            {
                RefreshKind.Manual => Manual,
                RefreshKind.EventDriven => EventDriven,
                RefreshKind.Interval when separator > 0 && int.TryParse(value![(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) => Interval(seconds),
                _ => throw new ArgumentException($"'{value}' is not a refresh mode (an interval names its seconds).", nameof(value)),
            };
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Kind;
            yield return IntervalSeconds;
        }

        public override string ToString() => Value;
    }
}
