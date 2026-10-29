#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Stomme.Export
{
    /// <summary>The artifact formats the engine renders.</summary>
    public enum ExportFormat
    {
        Csv = 0,
        Xlsx = 1,
        Json = 2,
        Pdf = 3,
    }

    /// <summary>
    /// What a column's values are, declared by the SOURCE before any row is read.
    /// </summary>
    /// <remarks>
    /// This is a declaration, not a guarantee: a source can still hand back a value whose runtime type does not
    /// match. Renderers must treat the mismatch as TEXT rather than emit a mis-typed numeric cell — see
    /// <see cref="ExportRenderOptions.NeutraliseFormulas"/> and the renderer remarks.
    /// </remarks>
    public enum ExportValueKind
    {
        Text = 0,
        Integer = 1,
        Decimal = 2,
        Boolean = 3,
        DateTimeUtc = 4,
        Guid = 5,
    }

    /// <summary>Whether a source can ever produce more than one table.</summary>
    /// <remarks>
    /// Declared STATICALLY, because nothing is opened at request time and <see cref="ITabularDocument"/> is lazy —
    /// so "this export has two tables" cannot be discovered while validating a request. CSV holds one table, and
    /// request validation rejects it for <see cref="Multiple"/>; the CSV renderer ALSO fails the job if a second
    /// table turns up anyway, because a static declaration is a promise a source can break.
    /// </remarks>
    public enum TableCardinality
    {
        Single = 0,
        Multiple = 1,
    }

    /// <summary>One column of a tabular export.</summary>
    public sealed class ExportColumn
    {
        public ExportColumn(string name, ExportValueKind kind, string? format = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind;
            Format = format;
        }

        /// <summary>The header text. Never trusted as safe: renderers neutralise it like any other cell.</summary>
        public string Name { get; }

        public ExportValueKind Kind { get; }

        /// <summary>An optional source-supplied hint for the cell formatter; never interpreted by a renderer.</summary>
        public string? Format { get; }
    }

    /// <summary>
    /// One table: its columns, and its rows PULLED lazily.
    /// </summary>
    /// <remarks>
    /// <b>The rows are an <see cref="IAsyncEnumerable{T}"/> on purpose.</b> The engine's contract is that a
    /// million-row export never exists in memory at once — the renderer pulls a row, writes it, and lets it go.
    /// A source that materialises its rows into a list before returning them defeats that silently, and
    /// <c>ExportRendererStreamingTests</c> is what keeps it honest (peak heap bounded independently of row count).
    /// </remarks>
    public interface ITabularTable
    {
        /// <summary>A name for the table — the XLSX sheet name, ignored by CSV.</summary>
        string Name { get; }

        IReadOnlyList<ExportColumn> Columns { get; }

        /// <summary>
        /// The rows, pulled one at a time. Each row has one value per column, positionally; a value may be
        /// <c>null</c>, and may not match its column's <see cref="ExportColumn.Kind"/>.
        /// </summary>
        IAsyncEnumerable<object?[]> ReadRowsAsync(CancellationToken ct = default);
    }

    /// <summary>One or more tables, themselves pulled lazily.</summary>
    public interface ITabularDocument : IAsyncDisposable
    {
        IAsyncEnumerable<ITabularTable> ReadTablesAsync(CancellationToken ct = default);
    }

    /// <summary>The knobs a request may set. Server-validated; see the remarks on each.</summary>
    public sealed class ExportRenderOptions
    {
        /// <summary>The only delimiters a CSV request may ask for.</summary>
        /// <remarks>
        /// An allowlist rather than a free <c>char</c>: an arbitrary caller-supplied delimiter lets a requester
        /// choose one that appears in the data and change how a consumer parses the file.
        /// </remarks>
        public static readonly IReadOnlyList<char> AllowedCsvDelimiters = new[] { ',', ';', '\t', '|' };

        public char CsvDelimiter { get; init; } = ',';

        public bool IncludeHeader { get; init; } = true;

        public bool JsonIndented { get; init; }

        /// <summary>The page sizes a PDF request may ask for; the first is the default.</summary>
        public static readonly IReadOnlyList<string> AllowedPdfPageSizes = new[] { "A4", "A3", "Letter", "Legal" };

        /// <summary>The orientations a PDF request may ask for; the first is the default.</summary>
        public static readonly IReadOnlyList<string> AllowedPdfOrientations = new[] { "Portrait", "Landscape" };

        public string PdfPageSize { get; init; } = "A4";

        public string PdfOrientation { get; init; } = "Portrait";

        /// <summary>
        /// The canonical spelling of <paramref name="value"/> in <paramref name="allowed"/>, ignoring case - or null
        /// when it is not one of them. A REQUEST naming a size this engine does not have is refused rather than quietly
        /// given A4: the requester asked for something and would get something else with no sign of it.
        /// </summary>
        public static string? CanonicalOrNull(string? value, IReadOnlyList<string> allowed)
        {
            for (var i = 0; i < allowed.Count; i++)
            {
                if (string.Equals(allowed[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return allowed[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Whether a cell that a spreadsheet would treat as a formula is neutralised. ON by default.
        /// </summary>
        /// <remarks>
        /// <b>Turning it OFF is a privileged act, checked in the handler, not here.</b> A value beginning
        /// <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is executed by Excel and similar
        /// when the file is opened, so an untrusted row can become a command on someone else's machine. Any
        /// holder of the create permission could otherwise disable the guard for data they do not own, which is
        /// why the opt-out requires the manage permission (refutation 11).
        /// </remarks>
        public bool NeutraliseFormulas { get; init; } = true;

        public static ExportRenderOptions Default { get; } = new();

        /// <summary>Whether <paramref name="delimiter"/> is one this engine will accept.</summary>
        public static bool IsAllowedCsvDelimiter(char delimiter)
        {
            for (var i = 0; i < AllowedCsvDelimiters.Count; i++)
            {
                if (AllowedCsvDelimiters[i] == delimiter)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>What a render produced.</summary>
    public readonly struct RenderOutcome
    {
        public RenderOutcome(long rowCount) => RowCount = rowCount;

        /// <summary>Rows written across every table, excluding headers.</summary>
        public long RowCount { get; }
    }

    /// <summary>Turns one value into the text a renderer writes.</summary>
    /// <remarks>
    /// Its output is neutralised like any other cell — a formatter is as capable of returning <c>=cmd</c> as a
    /// source is.
    /// </remarks>
    public interface IExportCellFormatter
    {
        string? Format(object? value, ExportColumn column);
    }

    /// <summary>
    /// A formatter that needs a per-site lookup implements this beside <see cref="IExportCellFormatter"/>: the processor
    /// calls it ONCE per export, in the site's scope, before the first row - the per-cell call stays synchronous, and no
    /// renderer ever awaits a lookup (D108's correction of 2026-09-27). A renderer used on its own
    /// gets a formatter that was never prepared, which resolves what needs no site (the presets) and declines the rest.
    /// </summary>
    public interface IExportCellFormatterPreparation
    {
        System.Threading.Tasks.Task PrepareAsync(int siteId, CancellationToken ct = default);
    }

    /// <summary>Renders a document in one format.</summary>
    public interface ITabularRenderer
    {
        ExportFormat Format { get; }

        /// <summary>
        /// Writes <paramref name="document"/> into <paramref name="destination"/>, pulling rows as it goes.
        /// </summary>
        /// <remarks>
        /// Implementations must not buffer the whole artifact: the destination is the artifact, and the caller
        /// counts the bytes as they are written to enforce the size cap. <paramref name="ct"/> is honoured
        /// between rows, so a cancelled job stops promptly rather than at the end of a long table.
        /// </remarks>
        System.Threading.Tasks.Task<RenderOutcome> RenderAsync(
            ITabularDocument document,
            System.IO.Stream destination,
            ExportRenderOptions options,
            IExportCellFormatter formatter,
            CancellationToken ct = default);
    }

    /// <summary>What the server knows about a request; never built from client input.</summary>
    public sealed class ExportSourceContext
    {
        public ExportSourceContext(int siteId, int requesterUserId, string? argumentsJson = null)
        {
            SiteId = siteId;
            RequesterUserId = requesterUserId;
            ArgumentsJson = argumentsJson;
        }

        public int SiteId { get; }

        public int RequesterUserId { get; }

        /// <summary>The request's own arguments, opaque here and validated by the source.</summary>
        public string? ArgumentsJson { get; }
    }

    /// <summary>Something that can be exported.</summary>
    public interface IExportSource
    {
        /// <summary>The canonical key, e.g. <c>stomme.log</c>. See <see cref="ExportSourceKeys"/>.</summary>
        string Key { get; }

        /// <summary>The permission a requester must hold for THIS data, checked on top of export:create.</summary>
        string RequiredPermission { get; }

        TableCardinality TableCardinality { get; }

        /// <summary>Rejects arguments before a job is queued. Returns null when they are acceptable.</summary>
        string? Validate(ExportSourceContext context);

        /// <summary>Opens the document. Nothing is read until the renderer pulls.</summary>
        System.Threading.Tasks.Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default);
    }

    /// <summary>
    /// The canonical form of a source key, and the rule that a non-canonical one is REJECTED rather than fixed.
    /// </summary>
    /// <remarks>
    /// Rewriting a key silently would make two different spellings name the same source, so a permission granted
    /// for one would answer for the other. Canonical is lower-case ASCII letters, digits, dot, dash and
    /// underscore, non-empty, no leading or trailing dot.
    /// </remarks>
    public static class ExportSourceKeys
    {
        public const int MaximumLength = 100;

        public static bool IsCanonical(string? key)
        {
            if (string.IsNullOrEmpty(key) || key!.Length > MaximumLength)
            {
                return false;
            }

            if (key[0] == '.' || key[key.Length - 1] == '.')
            {
                return false;
            }

            foreach (var character in key)
            {
                var ok = (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '.' || character == '-' || character == '_';

                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
