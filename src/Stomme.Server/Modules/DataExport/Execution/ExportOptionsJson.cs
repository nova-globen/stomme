#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Stomme.Export;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>
    /// The one spelling of <see cref="ExportRenderOptions"/> a job stores (bounded, canonical).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The options are stored at request time and read back by a job minutes later, possibly by another release.
    /// So <see cref="Parse"/> trusts NOTHING in the stored text: an unknown member is ignored, a delimiter outside
    /// the allowlist or an unknown page size falls back to the default, and unreadable JSON yields the defaults
    /// rather than a failed export. Crucially a missing or malformed <c>neutraliseFormulas</c> reads as TRUE -
    /// the guard fails closed.
    /// </para>
    /// <para>
    /// Whether a requester MAY turn neutralisation off is decided in the request handler (it needs the manage
    /// permission), never here: by the time a value reaches this type it has been authorised.
    /// </para>
    /// </remarks>
    public static class ExportOptionsJson
    {
        // ONE list, in the contract: what a request may ask for is exactly what a stored row may hold.
        private static readonly IReadOnlyList<string> PageSizes = ExportRenderOptions.AllowedPdfPageSizes;

        private static readonly IReadOnlyList<string> Orientations = ExportRenderOptions.AllowedPdfOrientations;

        public static string Serialize(ExportRenderOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!ExportRenderOptions.IsAllowedCsvDelimiter(options.CsvDelimiter))
            {
                throw new ArgumentException("The CSV delimiter is not one of: comma, semicolon, tab, pipe.", nameof(options));
            }

            using var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                // Fixed member order, no whitespace: two equal option sets always store the same text.
                writer.WriteStartObject();
                writer.WriteString("csvDelimiter", options.CsvDelimiter.ToString());
                writer.WriteBoolean("includeHeader", options.IncludeHeader);
                writer.WriteBoolean("jsonIndented", options.JsonIndented);
                writer.WriteString("pdfPageSize", Canonical(options.PdfPageSize, PageSizes));
                writer.WriteString("pdfOrientation", Canonical(options.PdfOrientation, Orientations));
                writer.WriteBoolean("neutraliseFormulas", options.NeutraliseFormulas);
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        public static ExportRenderOptions Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return ExportRenderOptions.Default;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return ExportRenderOptions.Default;
                }

                var defaults = ExportRenderOptions.Default;
                var delimiter = defaults.CsvDelimiter;
                if (root.TryGetProperty("csvDelimiter", out var d) && d.ValueKind == JsonValueKind.String
                    && d.GetString() is { Length: 1 } text && ExportRenderOptions.IsAllowedCsvDelimiter(text[0]))
                {
                    delimiter = text[0];
                }

                return new ExportRenderOptions
                {
                    CsvDelimiter = delimiter,
                    IncludeHeader = Bool(root, "includeHeader", defaults.IncludeHeader),
                    JsonIndented = Bool(root, "jsonIndented", defaults.JsonIndented),
                    PdfPageSize = Canonical(String(root, "pdfPageSize"), PageSizes),
                    PdfOrientation = Canonical(String(root, "pdfOrientation"), Orientations),
                    // Only an explicit JSON false turns the guard off. Anything else - absent, null, "false" as a
                    // string, a number - leaves it ON.
                    NeutraliseFormulas = !(root.TryGetProperty("neutraliseFormulas", out var n) && n.ValueKind == JsonValueKind.False),
                };
            }
            catch (JsonException)
            {
                return ExportRenderOptions.Default;
            }
        }

        private static bool Bool(JsonElement root, string name, bool fallback) =>
            root.TryGetProperty(name, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => fallback,
                }
                : fallback;

        private static string? String(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // STORED text is read back distrustfully: what is not on the list falls back to the default. (A REQUEST naming
        // something off the list is refused in the handler instead - the two are different moments.)
        private static string Canonical(string? value, IReadOnlyList<string> allowed) =>
            ExportRenderOptions.CanonicalOrNull(value, allowed) ?? allowed[0];
    }
}
