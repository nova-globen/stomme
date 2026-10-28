#nullable enable
using System;
using System.Globalization;
using Stomme.Export;

namespace Stomme.Modules.DataExport.Rendering
{
    /// <summary>
    /// Turning a value into cell text, and making that text safe to open (BL-85, D108 refutation 11).
    /// </summary>
    internal static class ExportCellText
    {
        /// <summary>
        /// The leading characters a spreadsheet treats as the start of a formula.
        /// </summary>
        /// <remarks>
        /// Excel, LibreOffice and Google Sheets all execute a cell beginning <c>=</c>, <c>+</c>, <c>-</c> or
        /// <c>@</c> when the file is opened, and a leading TAB or CARRIAGE RETURN can smuggle one of those past a
        /// naive check because the spreadsheet strips it first. So an untrusted row in someone's export becomes a
        /// command on the machine of whoever opens it — the export does not have to be read back by this product
        /// to do harm.
        /// </remarks>
        private const string FormulaLeaders = "=+-@\t\r";

        /// <summary>
        /// The value as text. A value whose runtime type does not match its column is rendered as TEXT.
        /// </summary>
        /// <remarks>
        /// <b>Never coerce a mismatch into the declared kind.</b> <see cref="ExportColumn.Kind"/> is what the
        /// source PROMISED, not what it handed over, and a promise can be broken — by a bug, or by a caller who
        /// controls the data. Writing such a value into a numeric cell would either fail the document or, worse,
        /// silently change the number. Rendering it as text is lossless and visibly wrong, which is the right
        /// failure mode.
        /// </remarks>
        public static string Format(object? value, ExportColumn column, IExportCellFormatter formatter)
        {
            if (value is null)
            {
                return string.Empty;
            }

            // The formatter gets first refusal. Its output is text like any other, and is neutralised by the
            // caller exactly the same way - a formatter can return "=cmd" as easily as a source can.
            var formatted = formatter.Format(value, column);
            if (formatted != null)
            {
                return formatted;
            }

            // Kind is deliberately NOT consulted here: the text of a value is the text of the value, whatever the
            // column promised. Nothing is parsed, coerced or reformatted to fit the declared kind. Matches() is
            // for renderers that must choose a CELL TYPE (XLSX), where a mismatch has to become a text cell
            // rather than a broken numeric one.
            return Invariant(value);
        }

        /// <summary>
        /// The cell's text, made safe to open: neutralised UNLESS it is a genuine scalar that cannot carry a formula.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Found by exporting a real event log:</b> its <c>UserId</c> of <c>-1</c> came out as <c>'-1</c>. The
        /// first version neutralised every cell's TEXT, so a real negative number in a numeric column was
        /// corrupted - a consumer summing that column gets text. D108 refutation 11 never asked for that: it
        /// neutralises text, a formatter's output, and a value whose runtime type does NOT match its column.
        /// </para>
        /// <para>
        /// A value is exempt only when ALL of this holds: no formatter produced the text (a formatter returns a
        /// string and can return anything); its runtime type matches its declared kind; and it is a finite number,
        /// a boolean, an instant or a GUID - types whose invariant text is generated here and cannot be chosen by
        /// whoever controls the data. A STRING is never exempt, whatever it looks like: <c>"-1"</c> in an Integer
        /// column is a mismatch and is neutralised, because the same code path would carry <c>"-1+cmd|..."</c>.
        /// </para>
        /// </remarks>
        public static string SafeText(object? value, ExportColumn column, IExportCellFormatter formatter, bool neutralise)
        {
            if (value is null)
            {
                return string.Empty;
            }

            var formatted = formatter.Format(value, column);
            if (formatted != null)
            {
                return Neutralise(formatted, neutralise);
            }

            var text = Invariant(value);
            return IsInertScalar(value) && Matches(value, column.Kind) ? text : Neutralise(text, neutralise);
        }

        private static bool IsInertScalar(object value) => value switch
        {
            double number => double.IsFinite(number),
            float number => float.IsFinite(number),
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal => true,
            bool or DateTime or DateTimeOffset or Guid => true,
            _ => false,
        };

        /// <summary>Whether the runtime value actually is what its column claims.</summary>
        public static bool Matches(object? value, ExportValueKind kind) => value switch
        {
            null => true,
            _ => kind switch
            {
                ExportValueKind.Text => value is string,
                ExportValueKind.Integer => value is byte or sbyte or short or ushort or int or uint or long or ulong,
                ExportValueKind.Decimal => value is decimal or double or float,
                ExportValueKind.Boolean => value is bool,
                ExportValueKind.DateTimeUtc => value is DateTime or DateTimeOffset,
                ExportValueKind.Guid => value is Guid,
                _ => false,
            },
        };

        /// <summary>Culture-independent text, so an artifact reads the same wherever it was produced.</summary>
        private static string Invariant(object value) => value switch
        {
            string text => text,
            DateTime moment => moment.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            DateTimeOffset moment => moment.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        /// <summary>
        /// Makes <paramref name="text"/> safe to open, when neutralisation is on.
        /// </summary>
        /// <remarks>
        /// A leading apostrophe is the marker every major spreadsheet reads as "this cell is text"; it is shown
        /// in the formula bar but not in the cell, so the value a human sees is unchanged while the formula never
        /// runs. Prefixing is chosen over stripping because stripping silently alters data — an export of a
        /// column that legitimately begins with <c>-</c> (a negative amount held as text) would lose its sign.
        /// </remarks>
        public static string Neutralise(string text, bool enabled)
        {
            if (!enabled || string.IsNullOrEmpty(text))
            {
                return text;
            }

            return FormulaLeaders.IndexOf(text[0]) >= 0 ? "'" + text : text;
        }

        /// <summary>One CSV field: quoted only when it has to be, with embedded quotes doubled.</summary>
        public static string CsvField(string text, char delimiter)
        {
            var needsQuotes = text.IndexOf('"') >= 0
                || text.IndexOf('\n') >= 0
                || text.IndexOf('\r') >= 0
                || text.IndexOf(delimiter) >= 0;

            return needsQuotes ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }
    }
}
