#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Models;
using Stomme.Repository;

namespace Stomme.Modules.DataExport.Sources
{
    /// <summary>
    /// The one built-in export source: the site's event log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It pages, because the log is the largest table a site has.</b> The admin viewer's read is "newest N"; an
    /// export needs ALL of a range, and reading that in one query would materialise it. So rows are pulled through
    /// <see cref="ILogRepository.GetLogsPageAsync"/> a page at a time, keyed on <c>Id</c> - <c>LogDate</c> is not
    /// unique, and a cursor on it would skip or repeat every entry that shares an instant with a page boundary.
    /// </para>
    /// <para>
    /// <b>The site comes from the server-built context, never from the arguments.</b> The arguments carry a UTC
    /// range and an optional level and nothing else; an unknown member is REJECTED rather than ignored, so a request
    /// that tries <c>{"siteId": 2}</c> is refused instead of quietly exporting the caller's own site and looking
    /// like it worked.
    /// </para>
    /// <para>
    /// The level is a closed set mapped to the exact stored spelling before the query: a bare string <c>==</c> is
    /// case-sensitive on EF and case-insensitive on RavenDB, so a free-text level would return different rows
    /// per provider.
    /// </para>
    /// <para>
    /// Deliberately NOT exported: <c>Properties</c> and <c>Exception</c>. Entity log entries
    /// serialise whole object graphs into them; an export must not become the way that leaves the site.
    /// </para>
    /// </remarks>
    public sealed class EventLogExportSource : IExportSource
    {
        public const string SourceKey = "stomme.log";

        /// <summary>The widest range one export may cover.</summary>
        public static readonly TimeSpan MaximumRange = TimeSpan.FromDays(366);

        internal const int PageSize = 500;

        /// <summary>The closed set of levels, in the exact spelling the log stores them.</summary>
        private static readonly string[] Levels = { "Trace", "Debug", "Information", "Warning", "Error", "Critical" };

        private static readonly ExportColumn[] ColumnSet =
        {
            new("Id", ExportValueKind.Integer),
            new("LogDateUtc", ExportValueKind.DateTimeUtc),
            new("Level", ExportValueKind.Text),
            new("Function", ExportValueKind.Text),
            new("Category", ExportValueKind.Text),
            new("Feature", ExportValueKind.Text),
            new("Message", ExportValueKind.Text),
            new("UserId", ExportValueKind.Integer),
            new("PageId", ExportValueKind.Integer),
            new("ModuleId", ExportValueKind.Integer),
            new("Url", ExportValueKind.Text),
            new("Server", ExportValueKind.Text),
        };

        private readonly ILogRepository _logs;

        public EventLogExportSource(ILogRepository logs)
        {
            _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        }

        public string Key => SourceKey;

        public string RequiredPermission => ExportPermissions.Log;

        public TableCardinality TableCardinality => TableCardinality.Single;

        public string? Validate(ExportSourceContext context) => TryParse(context.ArgumentsJson, DateTime.UtcNow, out _, out var error) ? null : error;

        public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
        {
            if (!TryParse(context.ArgumentsJson, DateTime.UtcNow, out var arguments, out var error))
            {
                // Validated at request time; arguments that no longer parse when the job runs are a refusal, not a crash.
                throw new InvalidOperationException(error);
            }

            return Task.FromResult<ITabularDocument>(new LogDocument(_logs, context.SiteId, arguments));
        }

        /// <summary>The arguments as the source understands them.</summary>
        internal readonly record struct Arguments(DateTime FromUtc, DateTime ToUtc, string? Level);

        /// <summary>
        /// Parses <c>{"fromUtc": "...", "toUtc": "...", "level": "Error"}</c>. Every member is optional; with none,
        /// the last 30 days.
        /// </summary>
        internal static bool TryParse(string? json, DateTime nowUtc, out Arguments arguments, out string error)
        {
            var to = nowUtc;
            var from = nowUtc.AddDays(-30);
            string? level = null;
            arguments = default;
            error = string.Empty;

            if (!string.IsNullOrWhiteSpace(json))
            {
                JsonDocument parsed;
                try
                {
                    parsed = JsonDocument.Parse(json);
                }
                catch (JsonException)
                {
                    error = "The arguments are not valid JSON.";
                    return false;
                }

                using (parsed)
                {
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        error = "The arguments must be a JSON object.";
                        return false;
                    }

                    foreach (var member in parsed.RootElement.EnumerateObject())
                    {
                        switch (member.Name)
                        {
                            case "fromUtc":
                                if (!TryInstant(member.Value, out from))
                                {
                                    error = "fromUtc must be an ISO 8601 UTC instant.";
                                    return false;
                                }

                                break;

                            case "toUtc":
                                if (!TryInstant(member.Value, out to))
                                {
                                    error = "toUtc must be an ISO 8601 UTC instant.";
                                    return false;
                                }

                                break;

                            case "level":
                                if (member.Value.ValueKind == JsonValueKind.Null)
                                {
                                    break;
                                }

                                level = member.Value.ValueKind == JsonValueKind.String ? Canonical(member.Value.GetString()) : null;
                                if (level is null)
                                {
                                    error = "level must be one of: " + string.Join(", ", Levels) + ".";
                                    return false;
                                }

                                break;

                            default:
                                // REJECTED, not ignored: see the type remarks.
                                error = $"'{member.Name}' is not an argument of this source.";
                                return false;
                        }
                    }
                }
            }

            if (from >= to)
            {
                error = "fromUtc must be earlier than toUtc.";
                return false;
            }

            if (to - from > MaximumRange)
            {
                error = $"One export covers at most {MaximumRange.Days} days.";
                return false;
            }

            arguments = new Arguments(from, to, level);
            return true;
        }

        private static bool TryInstant(JsonElement value, out DateTime instant)
        {
            instant = default;
            if (value.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return false;
            }

            instant = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        /// <summary>The stored spelling of <paramref name="level"/>, or null when it is not a level at all.</summary>
        private static string? Canonical(string? level)
        {
            foreach (var known in Levels)
            {
                if (string.Equals(known, level, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return null;
        }

        private sealed class LogDocument(ILogRepository logs, int siteId, Arguments arguments) : ITabularDocument, ITabularTable
        {
            public string Name => "Event Log";

            public IReadOnlyList<ExportColumn> Columns => ColumnSet;

            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return this;
                await Task.CompletedTask;
            }

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                var afterId = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var page = await logs.GetLogsPageAsync(siteId, arguments.FromUtc, arguments.ToUtc, arguments.Level, afterId, PageSize);
                    if (page.Count == 0)
                    {
                        yield break;
                    }

                    foreach (var entry in page)
                    {
                        yield return new object?[]
                        {
                            entry.LogId,
                            // Stored as UTC and read back with no kind by a relational provider: stamped, never converted.
                            DateTime.SpecifyKind(entry.LogDate, DateTimeKind.Utc),
                            entry.Level,
                            entry.Function,
                            entry.Category,
                            entry.Feature,
                            entry.Message,
                            entry.UserId,
                            entry.PageId,
                            entry.ModuleId,
                            entry.Url,
                            entry.Server,
                        };
                    }

                    afterId = page[page.Count - 1].LogId;
                    if (page.Count < PageSize)
                    {
                        yield break;
                    }
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
