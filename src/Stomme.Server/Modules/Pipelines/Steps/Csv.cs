#nullable enable
using System.Collections.Generic;
using System.Text;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// RFC 4180 reading and writing for the CSV steps: a quoted field may hold the delimiter, a doubled quote and a
    /// line break; CR, LF and CRLF all end a record; an empty line is skipped. Small on purpose - the predecessor platform's CsvHelper
    /// dependency is not carried for two steps.
    /// </summary>
    internal static class Csv
    {
        public static List<List<string>> Parse(string text, char delimiter)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            var fieldStarted = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }

                    continue;
                }

                if (c == '"' && !fieldStarted)
                {
                    quoted = true;
                    fieldStarted = true;
                }
                else if (c == delimiter)
                {
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    EndRecord();
                }
                else
                {
                    field.Append(c);
                    fieldStarted = true;
                }
            }

            if (field.Length > 0 || row.Count > 0 || fieldStarted)
            {
                EndRecord();
            }

            return rows;

            void EndRecord()
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                if (row.Count == 1 && row[0].Length == 0)
                {
                    row.Clear();
                    return;
                }

                rows.Add(row);
                row = new List<string>();
            }
        }

        public static string Write(IReadOnlyList<string> columns, IEnumerable<IReadOnlyDictionary<string, string?>> rows, char delimiter)
        {
            var text = new StringBuilder();
            AppendRecord(columns);
            foreach (var row in rows)
            {
                var values = new List<string>(columns.Count);
                foreach (var column in columns)
                {
                    values.Add(row.TryGetValue(column, out var value) ? value ?? "" : "");
                }

                AppendRecord(values);
            }

            return text.ToString();

            void AppendRecord(IReadOnlyList<string> values)
            {
                for (var i = 0; i < values.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(delimiter);
                    }

                    var value = values[i];
                    if (value.IndexOfAny(new[] { delimiter, '"', '\n', '\r' }) >= 0)
                    {
                        text.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                    }
                    else
                    {
                        text.Append(value);
                    }
                }

                text.Append("\r\n");
            }
        }
    }
}
