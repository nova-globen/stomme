#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Splunk
{
    /// <summary>
    /// Splunk (BL-229, n8n's Splunk node: its searches, reports, fired alerts and users): a Splunk server's REST API on its
    /// management port with an authentication token as a bearer token, over the host's egress. A test names whose the token is.
    /// </summary>
    /// <remarks>
    /// <c>create_search</c> (a search job: its SPL - <c>search</c> put before it when it starts with neither a command nor a
    /// pipe - blocking unless it says normal, its times as Splunk's own modifiers, a moment with its offset or seconds since
    /// 1970, and the rest of Splunk's job settings; the job read back), <c>search_job</c>, <c>search_jobs</c> (sorted as
    /// asked), <c>search_results</c> (a field's value matched - quoted, never spliced - a job not finished a failure that
    /// says so) and <c>delete_search_job</c>; <c>reports</c>, <c>report</c>, <c>create_report</c> (from a job's search or a
    /// search given, scheduled by a cron when it says so) and <c>delete_report</c>; <c>fired_alerts</c> and
    /// <c>metric_alerts</c>; <c>users</c>, <c>user</c>, <c>update_user</c> (an e-mail, a full name, roles) and
    /// <c>delete_user</c>. A user is never created here: Splunk needs a password for one, and a password is never a step's
    /// parameter. A list is read page by page by count and offset, the size fixed and no bigger than asked, its total, a
    /// short page or an empty one the end. A record is an entry's own fields and its content beside them - its id the last
    /// part of its address, the address as <c>entryUrl</c> - or a result's fields; a list or an object as its JSON. Every
    /// id, name, role, time, flag and setting held to its shape before anything is sent. A throttled call waits as Splunk
    /// says; a failure is thrown with its status and Splunk's messages. A management port with a certificate the host does
    /// not trust is refused - there is no switch to skip the check.
    /// </remarks>
    public sealed class SplunkConnector : IConnector, IConnectorExtractor
    {
        public const string CreateSearchOperation = "create_search";

        public const string SearchJobOperation = "search_job";

        public const string SearchJobsOperation = "search_jobs";

        public const string SearchResultsOperation = "search_results";

        public const string DeleteSearchJobOperation = "delete_search_job";

        public const string ReportsOperation = "reports";

        public const string ReportOperation = "report";

        public const string CreateReportOperation = "create_report";

        public const string DeleteReportOperation = "delete_report";

        public const string FiredAlertsOperation = "fired_alerts";

        public const string MetricAlertsOperation = "metric_alerts";

        public const string UsersOperation = "users";

        public const string UserOperation = "user";

        public const string UpdateUserOperation = "update_user";

        public const string DeleteUserOperation = "delete_user";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        // A search job's id - Splunk's own or one given: its shape alone makes it safe in an address.
        private static readonly Regex Sid = new("^[A-Za-z0-9_.\\-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex UserName = new("^[^\\s:/\\\\]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly Regex Role = new("^[A-Za-z0-9_\\-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[A-Za-z_][A-Za-z0-9_.:\\-]{0,99}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldList = new("^[A-Za-z_*][A-Za-z0-9_.:*\\-]{0,99}(,[A-Za-z_*][A-Za-z0-9_.:*\\-]{0,99})*$", RegexOptions.CultureInvariant);

        private static readonly Regex AppName = new("^[A-Za-z0-9_.\\-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly Regex ServerList = new("^[A-Za-z0-9_.\\-*]{1,200}(,[A-Za-z0-9_.\\-*]{1,200})*$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s,]+@[^@\\s,]+\\.[^@\\s,]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Relative = new("^(rt)?(now|[+\\-@][A-Za-z0-9@+\\-]{1,40})$|^rt$", RegexOptions.CultureInvariant);

        private static readonly Regex Moment = new("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}(:\\d{2}(\\.\\d{1,7})?)?(Z|[+\\-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant);

        private static readonly Regex Epoch = new("^\\d{1,12}(\\.\\d{1,6})?$", RegexOptions.CultureInvariant);

        private static readonly Regex Cron = new("^[0-9*/,\\-]+( [0-9*/,\\-A-Za-z]+){4}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            CreateSearchOperation, SearchJobOperation, SearchJobsOperation, SearchResultsOperation, DeleteSearchJobOperation, ReportsOperation, ReportOperation,
            CreateReportOperation, DeleteReportOperation, FiredAlertsOperation, MetricAlertsOperation, UsersOperation, UserOperation, UpdateUserOperation, DeleteUserOperation,
        };

        // A search job's settings beyond its SPL and times, by Splunk's names: the numbers whole and not below 0, the kinds from
        // Splunk's own lists.
        private static readonly string[] JobNumbers = { "auto_cancel", "auto_finalize_ec", "auto_pause", "max_time", "reduce_freq", "reuse_max_seconds_ago", "status_buckets", "timeout", "indexedRealtimeOffset" };

        private static readonly (string Name, string[] Kinds)[] JobKinds = { ("adhoc_search_level", new[] { "fast", "smart", "verbose" }), ("search_mode", new[] { "normal", "realtime" }) };

        private static readonly string[] JobTimes = { "earliest_time", "latest_time", "index_earliest", "index_latest" };

        public string ConnectorType => "splunk";

        public string DisplayName => "Splunk";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => "https://splunk.example.com:8089";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "Authentication token (Settings > Tokens)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://www.splunk.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.splunk.com/Documentation/Splunk/latest/RESTREF/RESTprolog");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField JobField = Field("job", "Search job", "The search job's id (its sid).", required: true);

        private static readonly ConnectorConfigField ReportField = Field("report", "Report", "The report's name.", required: true);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "The user's name.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField Time(string name, string label) => Field(name, label, "Splunk's modifier (-24h@h, now), a moment with its offset (2026-10-13T08:00:00Z) or seconds since 1970.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(CreateSearchOperation, "Run a search", "A search job, read back when done - n8n's Splunk Search Create.")
            {
                Parameters = Schema(new[]
                {
                    Field("search", "Search", "Splunk's search language - index=main error | stats count by host.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("exec_mode", "Wait", "blocking (the default): read back done; normal: read back at once."),
                    Time("earliest_time", "Earliest time"), Time("latest_time", "Latest time"), Time("index_earliest", "Earliest index time"), Time("index_latest", "Latest index time"),
                    Field("adhoc_search_level", "Search level", "fast, smart or verbose."), Field("search_mode", "Search mode", "normal or realtime."),
                    Field("namespace", "App", "The app the search runs in."), Field("remote_server_list", "Search peers", "Comma-separated, wildcards allowed."),
                    Field("rf", "Required fields", "Comma-separated."), Field("workload_pool", "Workload pool", "Its name."),
                }.Concat(JobNumbers.Select(name => Field(name, name.Replace('_', ' '), "A whole number - Splunk's " + name + "."))).ToArray()),
            },
            new ConnectorOperation(SearchJobOperation, "Get a search job", "n8n's Splunk Search Get.") { Parameters = Schema(JobField) },
            new ConnectorOperation(SearchJobsOperation, "Get search jobs", "n8n's Splunk Search Get Many.")
            {
                Parameters = Schema(Field("sort_key", "Sort by", "A field - diskUsage."), Field("sort_dir", "Sort direction", "asc or desc."), Field("sort_mode", "Sort mode", "auto, alpha, alpha_case or num."), MaxField),
            },
            new ConnectorOperation(SearchResultsOperation, "Get search results", "A finished job's results - n8n's Splunk Search Get Result.")
            {
                Parameters = Schema(JobField, Field("field", "Where field", "A field to match - host."), Field("value", "Is", "Its value - web-01."), Field("summary", "Field summary", "true: Splunk's field summary in the metadata.", ConnectorConfigFieldType.Boolean), MaxField),
            },
            new ConnectorOperation(DeleteSearchJobOperation, "Delete a search job", "n8n's Splunk Search Delete.") { Parameters = Schema(JobField) },
            new ConnectorOperation(ReportsOperation, "Get reports", "Saved searches - n8n's Splunk Report Get Many.")
            {
                Parameters = Schema(Field("add_orphan_field", "Orphans", "true: say whether each is orphaned.", ConnectorConfigFieldType.Boolean), Field("list_default_action_args", "Default actions", "true: list the default actions' arguments.", ConnectorConfigFieldType.Boolean), MaxField),
            },
            new ConnectorOperation(ReportOperation, "Get a report", "n8n's Splunk Report Get.") { Parameters = Schema(ReportField) },
            new ConnectorOperation(CreateReportOperation, "Create a report", "From a search job's search or a search given - n8n's Splunk Report Create From Search.")
            {
                Parameters = Schema(
                    Field("name", "Name", "The report's name.", required: true), Field("job", "From search job", "A search job's id - its search and times taken."),
                    Field("search", "Search", "Or the search itself.", ConnectorConfigFieldType.Multiline), Field("description", "Description", "What it is for."),
                    Time("earliest_time", "Earliest time"), Time("latest_time", "Latest time"), Field("cron_schedule", "Schedule", "A cron - 0 6 * * 1: scheduled; none: run by hand.")),
            },
            new ConnectorOperation(DeleteReportOperation, "Delete a report", "n8n's Splunk Report Delete.") { Parameters = Schema(ReportField) },
            new ConnectorOperation(FiredAlertsOperation, "Get fired alerts", "n8n's Splunk Alert Get Fired Alerts.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(MetricAlertsOperation, "Get metric alerts", "n8n's Splunk Alert Get Metrics.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(UsersOperation, "Get users", "n8n's Splunk User Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(UserOperation, "Get a user", "n8n's Splunk User Get.") { Parameters = Schema(UserField) },
            new ConnectorOperation(UpdateUserOperation, "Update a user", "What is given, the rest kept - never a password - n8n's Splunk User Update.")
            {
                Parameters = Schema(UserField, Field("email", "E-mail", "Their e-mail."), Field("realname", "Full name", "Their full name."), Field("roles", "Roles", "Comma-separated - user, power: these and no others.")),
            },
            new ConnectorOperation(DeleteUserOperation, "Delete a user", "n8n's Splunk User Delete.") { Parameters = Schema(UserField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var (answer, _) = await session.SendAsync(HttpMethod.Get, "/authentication/current-context", null, null, cancellationToken);
                var name = Text(((answer?["entry"] as JsonArray)?.FirstOrDefault() as JsonObject)?["content"]?["username"]);
                return ConnectionTestResult.Ok($"Splunk: the token is {name ?? "a user"}'s.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SearchJobsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Splunk has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string JobOf() => Values.Get(p, "job") is { } given ? Sid.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a search job's id - letters, digits, '.', '_' and '-' (job).") : throw new InvalidOperationException("Give the search job's id (job).");
            string ReportOf() => Values.Get(p, "report") is { } given ? NameOf(given, "report") : throw new InvalidOperationException("Give the report's name (report).");
            string UserOf() => Values.Get(p, "user") is { } given ? UserName.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a user's name - no spaces, ':', '/' or '\\\\' (user).") : throw new InvalidOperationException("Give the user's name (user).");
            string? KindOf(string name, params string[] kinds) => Values.Get(p, name) is { } given ? kinds.FirstOrDefault(kind => string.Equals(kind, given, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{given}' is not {string.Join(", ", kinds[..^1])} or {kinds[^1]} ({name}).") : null;
            string? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
            string? TimeOf(string name) => Values.Get(p, name) is { } given
                ? Relative.IsMatch(given) || Epoch.IsMatch(given) ? given
                    : Moment.IsMatch(given) && DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment) ? moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"'{given}' is not a time - -24h@h, now, 2026-10-13T08:00:00Z or seconds since 1970 ({name}).")
                : null;
            string? ShapedOf(string name, Regex shape, string what) => Values.Get(p, name) is { } given ? shape.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what} ({name}).") : null;

            var query = new List<KeyValuePair<string, string>>();
            var form = new List<KeyValuePair<string, string>>();
            void Add(List<KeyValuePair<string, string>> to, string name, string? value)
            {
                if (value is not null)
                {
                    to.Add(new(name, value));
                }
            }

            string path;
            string? reportJob = null;
            switch (operation)
            {
                case CreateSearchOperation:
                    {
                        var search = Values.Get(p, "search") ?? throw new InvalidOperationException("Give the search (search).");
                        // Splunk runs what starts with a command; the search box puts search before the rest, and so does this.
                        Add(form, "search", search.StartsWith('|') || Regex.IsMatch(search, "^search\\s", RegexOptions.IgnoreCase) ? search : "search " + search);
                        Add(form, "exec_mode", KindOf("exec_mode", "blocking", "normal") ?? "blocking");
                        foreach (var name in JobTimes)
                        {
                            Add(form, name, TimeOf(name));
                        }

                        foreach (var (name, kinds) in JobKinds)
                        {
                            Add(form, name, KindOf(name, kinds));
                        }

                        foreach (var name in JobNumbers)
                        {
                            Add(form, name, Values.Get(p, name) is { } number ? int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? number : throw new InvalidOperationException($"'{number}' is not a whole number, 0 or more ({name}).") : null);
                        }

                        Add(form, "namespace", ShapedOf("namespace", AppName, "an app's name"));
                        Add(form, "remote_server_list", ShapedOf("remote_server_list", ServerList, "a comma-separated list of search peers"));
                        Add(form, "rf", ShapedOf("rf", FieldList, "a comma-separated list of fields"));
                        Add(form, "workload_pool", ShapedOf("workload_pool", AppName, "a workload pool's name"));
                        path = "/search/jobs";
                        break;
                    }

                case SearchJobOperation or DeleteSearchJobOperation:
                    path = "/search/jobs/" + JobOf();
                    break;
                case SearchJobsOperation:
                    Add(query, "sort_key", ShapedOf("sort_key", FieldName, "a field's name"));
                    Add(query, "sort_dir", KindOf("sort_dir", "asc", "desc"));
                    Add(query, "sort_mode", KindOf("sort_mode", "auto", "alpha", "alpha_case", "num"));
                    path = "/search/jobs";
                    break;
                case SearchResultsOperation:
                    {
                        path = $"/search/jobs/{JobOf()}/results";
                        var field = ShapedOf("field", FieldName, "a field's name");
                        var value = Values.Get(p, "value");
                        if ((field is null) != (value is null))
                        {
                            throw new InvalidOperationException("Give a field and its value together (field, value).");
                        }

                        // The value quoted, never spliced: a value cannot become more search.
                        Add(query, "search", field is null ? null : $"search {field}=\"{value!.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                        Add(query, "add_summary_to_metadata", FlagOf("summary"));
                        break;
                    }

                case ReportsOperation:
                    Add(query, "add_orphan_field", FlagOf("add_orphan_field"));
                    Add(query, "listDefaultActionArgs", FlagOf("list_default_action_args"));
                    path = "/saved/searches";
                    break;
                case ReportOperation or DeleteReportOperation:
                    path = "/saved/searches/" + Uri.EscapeDataString(ReportOf());
                    break;
                case CreateReportOperation:
                    {
                        Add(form, "name", Values.Get(p, "name") is { } name ? NameOf(name, "name") : throw new InvalidOperationException("Give the report's name (name)."));
                        var job = Values.Get(p, "job") is null ? null : JobOf();
                        var search = Values.Get(p, "search");
                        if ((job is null) == (search is null))
                        {
                            throw new InvalidOperationException("Give a search job or a search - one of them (job, search).");
                        }

                        Add(form, "search", search);
                        Add(form, "description", Values.Get(p, "description"));
                        Add(form, "dispatch.earliest_time", TimeOf("earliest_time"));
                        Add(form, "dispatch.latest_time", TimeOf("latest_time"));
                        if (ShapedOf("cron_schedule", Cron, "a cron - 0 6 * * 1") is { } cron)
                        {
                            Add(form, "cron_schedule", cron);
                            Add(form, "is_scheduled", "1");
                        }

                        reportJob = job;
                        path = "/saved/searches";
                        break;
                    }

                case FiredAlertsOperation:
                    path = "/alerts/fired_alerts";
                    break;
                case MetricAlertsOperation:
                    path = "/alerts/metric_alerts";
                    break;
                case UsersOperation:
                    path = "/authentication/users";
                    break;
                case UserOperation or DeleteUserOperation:
                    path = "/authentication/users/" + Uri.EscapeDataString(UserOf());
                    break;
                default:
                    {
                        path = "/authentication/users/" + Uri.EscapeDataString(UserOf());
                        Add(form, "email", ShapedOf("email", Email, "an e-mail"));
                        Add(form, "realname", Values.Get(p, "realname"));
                        if (Values.Get(p, "roles") is { } roles)
                        {
                            foreach (var role in roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                            {
                                Add(form, "roles", Role.IsMatch(role) ? role : throw new InvalidOperationException($"'{role}' is not a role's name - letters, digits, '_' and '-' (roles)."));
                            }
                        }

                        if (form.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - an e-mail, a full name or roles.");
                        }

                        break;
                    }
            }

            using var client = session.Client;
            switch (operation)
            {
                case SearchJobsOperation or SearchResultsOperation or ReportsOperation or FiredAlertsOperation or MetricAlertsOperation or UsersOperation:
                    {
                        // Splunk pages by count and offset: the size fixed at the start, its total, a short page or an empty one the end.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var offset = 0; ; offset += size)
                        {
                            var (answer, status) = await session.SendAsync(HttpMethod.Get, path, query.Append(new("count", size.ToString(CultureInfo.InvariantCulture))).Append(new("offset", offset.ToString(CultureInfo.InvariantCulture))), null, cancellationToken);
                            if (status == HttpStatusCode.NoContent && operation == SearchResultsOperation)
                            {
                                throw new InvalidOperationException("Splunk has no results yet - the search job has not finished: run it blocking (exec_mode), or read it again later.");
                            }

                            var results = operation == SearchResultsOperation;
                            var items = ((results ? answer?["results"] : answer?["entry"]) as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return results ? new ConnectorRecord(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal)) : Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            var total = answer?["paging"]?["total"] is JsonValue count && count.TryGetValue<long>(out var all) ? all : (long?)null;
                            if (items.Count < size || (total is not null && offset + items.Count >= total))
                            {
                                yield break;
                            }
                        }
                    }

                case DeleteSearchJobOperation or DeleteReportOperation or DeleteUserOperation:
                    {
                        await session.SendAsync(HttpMethod.Delete, path, null, null, cancellationToken);
                        var (name, given) = operation switch { DeleteSearchJobOperation => ("job", JobOf()), DeleteReportOperation => ("report", ReportOf()), _ => ("user", UserOf()) };
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [name] = given, ["deleted"] = true });
                        yield break;
                    }

                case CreateSearchOperation:
                    {
                        var (made, _) = await session.SendAsync(HttpMethod.Post, path, null, form, cancellationToken);
                        var sid = Text(made?["sid"]) is { } named && Sid.IsMatch(named) ? named : throw new InvalidOperationException("Splunk made no search job - it named none.");
                        var (job, _) = await session.SendAsync(HttpMethod.Get, "/search/jobs/" + sid, null, null, cancellationToken);
                        yield return Record(((job?["entry"] as JsonArray)?.FirstOrDefault() as JsonObject) ?? new JsonObject { ["sid"] = sid });
                        yield break;
                    }

                case CreateReportOperation when reportJob is not null:
                    {
                        // From a job: its search and its times, the times given winning.
                        var (answer, _) = await session.SendAsync(HttpMethod.Get, "/search/jobs/" + reportJob, null, null, cancellationToken);
                        var entry = ((answer?["entry"] as JsonArray)?.FirstOrDefault() as JsonObject) ?? throw new InvalidOperationException("Splunk has no search job with that id.");
                        var request = entry["content"]?["request"] as JsonObject;
                        form.Insert(1, new("search", Text(request?["search"]) ?? Text(entry["name"]) ?? throw new InvalidOperationException("Splunk named no search for that job.")));
                        foreach (var (name, asked) in new[] { ("dispatch.earliest_time", "earliest_time"), ("dispatch.latest_time", "latest_time") })
                        {
                            if (!form.Any(pair => pair.Key == name) && Text(request?[asked]) is { } time)
                            {
                                form.Add(new(name, time));
                            }
                        }

                        var (made, _) = await session.SendAsync(HttpMethod.Post, path, null, form, cancellationToken);
                        yield return Record(((made?["entry"] as JsonArray)?.FirstOrDefault() as JsonObject) ?? throw new InvalidOperationException("Splunk answered with no entry."));
                        yield break;
                    }

                default:
                    {
                        var (answer, _) = await session.SendAsync(operation is SearchJobOperation or ReportOperation or UserOperation ? HttpMethod.Get : HttpMethod.Post, path, null, form.Count == 0 ? null : form, cancellationToken);
                        yield return Record(((answer?["entry"] as JsonArray)?.FirstOrDefault() as JsonObject) ?? throw new InvalidOperationException("Splunk answered with no entry."));
                        yield break;
                    }
            }
        }

        // A report's name: any text but a control character, 1 to 255 of them.
        private static string NameOf(string given, string name) =>
            given.Length <= 255 && !given.Any(char.IsControl) ? given : throw new InvalidOperationException($"'{given}' is not a report's name - 255 characters at most, no control characters ({name}).");

        // An entry: its own fields - its id the last part of its address, the address as entryUrl - and its content beside them.
        private static ConnectorRecord Record(JsonObject entry)
        {
            var fields = entry.Where(pair => pair.Key is not "content" and not "links" and not "link").ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal);
            if (Text(entry["id"]) is { } address && Uri.TryCreate(address, UriKind.Absolute, out var url))
            {
                fields["entryUrl"] = address;
                fields["id"] = Uri.UnescapeDataString(url.Segments[^1].TrimEnd('/'));
            }

            if (entry["content"] is JsonObject content)
            {
                foreach (var (name, value) in content)
                {
                    fields.TryAdd(name, Value(value));
                }
            }

            return new ConnectorRecord(fields);
        }

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the server's REST API under /services, its answers JSON, the token as a bearer token; a
        // throttled call waiting as Splunk says; a failure thrown with its status and Splunk's messages.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string token;

            private Session(string baseUrl, string token, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the Splunk server's management address - https://splunk.example.com:8089.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                baseUrl = baseUrl.EndsWith("/services", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^9] : baseUrl;
                return new Session(baseUrl + "/services", Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Splunk connection has no authentication token."), egress.CreateHttpClient(timeout));
            }

            public async Task<(JsonNode? Answer, HttpStatusCode Status)> SendAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? query, IReadOnlyList<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
            {
                var address = baseUrl + path + "?" + string.Join("&", (query ?? Enumerable.Empty<KeyValuePair<string, string>>()).Prepend(new("output_mode", "json")).Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, address);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (form is not null)
                    {
                        request.Content = new FormUrlEncodedContent(form);
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    // A page that is not Splunk's - a sign-in page, a proxy's - read as nothing would hide that it never was.
                    throw new InvalidOperationException($"Splunk answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: its management port, not its web one; a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = string.Join("; ", ((json as JsonObject)?["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(message => Text(message["text"])).Where(text => !string.IsNullOrEmpty(text)));
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said.Length == 0 ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return (json, response.StatusCode);
            }
        }
    }
}
