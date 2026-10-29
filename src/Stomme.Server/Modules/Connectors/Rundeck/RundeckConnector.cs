#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Rundeck
{
    /// <summary>
    /// Rundeck (n8n's Rundeck node: a job run, a job's metadata) over Rundeck's API (<c>{address}/api/44</c>) by an API
    /// token in the <c>X-Rundeck-Auth-Token</c> header - never in the address. A test reads the token's user's projects.
    /// </summary>
    /// <remarks>
    /// <c>run</c> (a job run with its options - each an <c>option.name</c> parameter - and a node filter; optionally waited for,
    /// its output's last lines with it), <c>job</c> (n8n's Get Metadata), <c>jobs</c> (a project's, by group), <c>executions</c>
    /// (a job's, by status), <c>execution</c>, <c>execution_output</c> and <c>abort</c>. API 44, Rundeck 4.12 and later: n8n's node
    /// runs a job through API 14, which Rundeck 6 refuses ("Minimum supported version: 18", measured), and Rundeck 6 raises the
    /// minimum per endpoint (44 for a job import). The options go as Rundeck's options object, never n8n's argString, which
    /// Rundeck splits on its own dashes: a value "fisk -day söndag" set the day option to söndag (measured). Rundeck runs a job
    /// with an option it does not have, silently, and answers an executions filter it does not know with nothing: so an option
    /// is checked against the job's own definition before the run, and a status against Rundeck's. Every id held to Rundeck's
    /// shape; Rundeck's message on a failure. Measured against Rundeck 6.2.1 (<c>scripts/rundeck-test-server.ps1</c>).
    /// </remarks>
    public sealed class RundeckConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 20; // Rundeck's own page

        public const int MostWait = 3600;

        private const int OutputLines = 20; // a waited run's output: its last lines

        private const string Api = "/api/44";

        private static readonly string[] Known = { "run", "job", "jobs", "executions", "execution", "execution_output", "abort" };

        private static readonly string[] Statuses = { "running", "succeeded", "failed", "aborted" };

        // A job's uuid - or the id an imported job was given; a project's name as Rundeck allows one; an option's name.
        private static readonly Regex JobPattern = new("^[0-9A-Za-z][0-9A-Za-z_.-]{0,254}$", RegexOptions.CultureInvariant);

        private static readonly Regex ProjectPattern = new("^[-_a-zA-Z0-9+][-._a-zA-Z0-9+]{0,254}$", RegexOptions.CultureInvariant);

        private static readonly Regex OptionPattern = new("^[a-zA-Z_0-9.-]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "rundeck";

        public string DisplayName => "Rundeck";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API token (your profile - User API Tokens)" };

        public string EndpointExample => "https://rundeck.kafe.example";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://www.rundeck.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.rundeck.com/docs/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField JobField = Field("job", "Job", "Its id - from its address in Rundeck, ff50cae3-0ccc-4113-8b9e-f51f0642b3c4.", required: true);

        private static readonly ConnectorConfigField ExecutionField = new("execution", "Execution", ConnectorConfigFieldType.Integer, Required: true, Description: "Its number - 42.");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("run", "Run a job", "n8n's Execute: each of the job's options an option.name parameter - option.day = tisdag.")
            {
                Parameters = new(new[]
                {
                    JobField, Field("filter", "Nodes", "A node filter - name: kassa1 tags: kassa. Empty: the job's own."),
                    new ConnectorConfigField("wait", "Wait up to", ConnectorConfigFieldType.Integer, Required: false, Description: $"Seconds to wait for it to finish, its output's last lines with it - at most {MostWait}. Empty: not waited for."),
                }),
            },
            new ConnectorOperation("job", "Get a job", "Its metadata - n8n's Get Metadata.") { Parameters = new(new[] { JobField }) },
            new ConnectorOperation("jobs", "Get jobs", "A project's jobs.") { Parameters = new(new[] { Field("project", "Project", "Its name - kafe.", required: true), Field("group", "Group", "Jobs in this group - kassa."), Field("name", "Name", "The job of this name, exactly - Dagens meny."), MaxField }) },
            new ConnectorOperation("executions", "Get a job's executions", "Newest first.") { Parameters = new(new[] { JobField, Field("status", "Status", "running, succeeded, failed or aborted."), MaxField }) },
            new ConnectorOperation("execution", "Get an execution", "Its status, dates and options.") { Parameters = new(new[] { ExecutionField }) },
            new ConnectorOperation("execution_output", "Get an execution's output", "Each log line a record.") { Parameters = new(new[] { ExecutionField, new ConnectorConfigField("lines", "Last lines", ConnectorConfigFieldType.Integer, Required: false, Description: "Its last lines only - 50. Empty: all of it.") }) },
            new ConnectorOperation("abort", "Abort an execution", "Rundeck's answer says whether it was.") { Parameters = new(new[] { ExecutionField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var projects = await session.SendAsync(HttpMethod.Get, "/projects", null, cancellationToken) as JsonArray
                    ?? throw new InvalidOperationException("Rundeck answered with what is not its projects - check the address: a sign-in page or a proxy answers so.");
                return ConnectionTestResult.Ok($"Rundeck answers: {projects.Count} project(s) the token may see.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "run").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Rundeck has no operation '{context.OperationName}' - run, job, jobs, executions, execution, execution_output or abort.");
            var p = context.Parameters;

            // Everything held to Rundeck's shape before anything is sent.
            var max = Number(p, "max", 1, int.MaxValue) ?? DefaultMax;
            switch (operation)
            {
                case "run":
                    {
                        var job = Job(p);
                        var options = new JsonObject();
                        foreach (var (key, value) in p.Where(pair => pair.Key.StartsWith("option.", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
                        {
                            var name = key["option.".Length..];
                            options[OptionPattern.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not an option's name - letters, digits, _ . and - (option.{name}).")] = value;
                        }

                        var filter = Values.Get(p, "filter") is { } given ? given.Length <= 4096 && !given.Any(char.IsControl) ? given : throw new InvalidOperationException("A node filter is one line (filter).") : null;
                        var wait = Number(p, "wait", 0, MostWait);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

                        // Rundeck runs a job with an option it does not have, silently: each one checked against the job's own.
                        var definition = (await session.SendAsync(HttpMethod.Get, $"/job/{job}?format=json", null, cancellationToken) as JsonArray)?.OfType<JsonObject>().FirstOrDefault()
                            ?? throw new InvalidOperationException("Rundeck answered without the job's definition - check the address: a sign-in page or a proxy answers so.");
                        var known = (definition["options"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(option => Text(option["name"])).Where(name => name is not null).ToList();
                        if (options.Select(option => option.Key).FirstOrDefault(name => !known.Contains(name)) is { } unknown)
                        {
                            throw new InvalidOperationException($"The job has no option '{unknown}' - {(known.Count == 0 ? "it takes none" : "it takes " + string.Join(", ", known))} (option.{unknown}).");
                        }

                        var body = new JsonObject { ["options"] = options };
                        if (filter is not null)
                        {
                            body["filter"] = filter;
                        }

                        var started = await session.SendAsync(HttpMethod.Post, $"/job/{job}/run", body, cancellationToken) as JsonObject
                            ?? throw new InvalidOperationException("Rundeck answered without the execution - check the address: a sign-in page or a proxy answers so.");
                        if (wait is not { } seconds || seconds == 0)
                        {
                            yield return Execution(started);
                            break;
                        }

                        var id = Text(started["id"]) ?? throw new InvalidOperationException("Rundeck answered without the execution's number.");
                        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
                        var execution = started;
                        while (Text(execution["status"]) is "running" or "scheduled" && DateTimeOffset.UtcNow < until)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                            execution = await session.SendAsync(HttpMethod.Get, $"/execution/{id}", null, cancellationToken) as JsonObject ?? execution;
                        }

                        var fields = new Dictionary<string, object?>(Execution(execution).Fields, StringComparer.Ordinal);
                        var output = await session.SendAsync(HttpMethod.Get, $"/execution/{id}/output?lastlines={OutputLines}", null, cancellationToken) as JsonObject;
                        fields["output"] = string.Join("\n", Entries(output).Select(entry => Text(entry["log"])));
                        fields["finished"] = Text(execution["status"]) is not ("running" or "scheduled");
                        yield return new ConnectorRecord(fields);
                        break;
                    }

                case "job":
                    {
                        var job = Job(p);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        yield return Record(await session.SendAsync(HttpMethod.Get, $"/job/{job}/info", null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Rundeck answered without the job - check the address."));
                        break;
                    }

                case "jobs":
                    {
                        var project = Values.Get(p, "project") ?? throw new InvalidOperationException("Give the project - its name, kafe (project).");
                        if (!ProjectPattern.IsMatch(project))
                        {
                            throw new InvalidOperationException($"'{project}' is not a project's name - letters, digits, _ - . and + (project).");
                        }

                        var group = Values.Get(p, "group") is { } path ? path.Length <= 1024 && !path.Any(char.IsControl) ? path : throw new InvalidOperationException("A group is one line - kassa/dagligt (group).") : null;
                        var named = Values.Get(p, "name") is { } exact ? exact.Length <= 1024 && !exact.Any(char.IsControl) ? exact : throw new InvalidOperationException("A job's name is one line - Dagens meny (name).") : null;
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var query = new[] { ("groupPath", group), ("jobExactFilter", named) }.Where(pair => pair.Item2 is not null).Select(pair => pair.Item1 + "=" + Uri.EscapeDataString(pair.Item2!)).ToArray();
                        var route = $"/project/{project}/jobs" + (query.Length == 0 ? string.Empty : "?" + string.Join("&", query));
                        foreach (var item in List(await session.SendAsync(HttpMethod.Get, route, null, cancellationToken)).Take(max))
                        {
                            yield return Record(item);
                        }

                        break;
                    }

                case "executions":
                    {
                        var job = Job(p);
                        var status = Values.Get(p, "status") is { } given
                            ? Statuses.FirstOrDefault(known => string.Equals(known, given, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{given}' is not a status - running, succeeded, failed or aborted (status).")
                            : null;
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var route = $"/job/{job}/executions?max={max}" + (status is null ? string.Empty : "&status=" + status);
                        var answer = await session.SendAsync(HttpMethod.Get, route, null, cancellationToken) as JsonObject;
                        foreach (var item in List(answer?["executions"]).Take(max))
                        {
                            yield return Execution(item);
                        }

                        break;
                    }

                case "execution":
                    {
                        var id = ExecutionId(p);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        yield return Execution(await session.SendAsync(HttpMethod.Get, $"/execution/{id}", null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Rundeck answered without the execution - check the address."));
                        break;
                    }

                case "execution_output":
                    {
                        var id = ExecutionId(p);
                        var lines = Number(p, "lines", 1, 100000);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var output = await session.SendAsync(HttpMethod.Get, $"/execution/{id}/output" + (lines is { } last ? "?lastlines=" + last : string.Empty), null, cancellationToken) as JsonObject
                            ?? throw new InvalidOperationException("Rundeck answered without the output - check the address.");
                        foreach (var entry in Entries(output))
                        {
                            yield return Fields(("execution", id), ("time", Text(entry["absolute_time"])), ("level", Text(entry["level"])), ("node", Text(entry["node"])), ("step", Text(entry["stepctx"])), ("log", Text(entry["log"])), ("state", Text(output["execState"])));
                        }

                        break;
                    }

                default: // abort
                    {
                        var id = ExecutionId(p);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var answer = await session.SendAsync(HttpMethod.Post, $"/execution/{id}/abort", null, cancellationToken) as JsonObject
                            ?? throw new InvalidOperationException("Rundeck answered without the abort - check the address.");

                        // Rundeck answers 200 whether or not it aborted: its abort status says - "failed", "Job is not running".
                        yield return Fields(("execution", id), ("abort", Text(answer["abort"]?["status"])), ("reason", Text(answer["abort"]?["reason"])), ("status", Text(answer["execution"]?["status"])));
                        break;
                    }
            }
        }

        private static string Job(IReadOnlyDictionary<string, string> p)
        {
            var given = Values.Get(p, "job") ?? throw new InvalidOperationException("Give the job - its id, ff50cae3-0ccc-4113-8b9e-f51f0642b3c4 (job).");
            return JobPattern.IsMatch(given) ? given : throw new InvalidOperationException($"'{(given.Length > 40 ? given[..40] + "..." : given)}' is not a job's id - ff50cae3-0ccc-4113-8b9e-f51f0642b3c4 (job).");
        }

        private static long ExecutionId(IReadOnlyDictionary<string, string> p)
        {
            var given = Values.Get(p, "execution") ?? throw new InvalidOperationException("Give the execution - its number, 42 (execution).");
            return long.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new InvalidOperationException($"'{given}' is not an execution's number - 42 (execution).");
        }

        private static int? Number(IReadOnlyDictionary<string, string> p, string name, int least, int most) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= least && n <= most ? n : throw new InvalidOperationException($"'{given}' is not a number from {least} to {most} ({name}).")
                : null;

        private static IEnumerable<JsonObject> List(JsonNode? answer) =>
            answer is JsonArray items ? items.OfType<JsonObject>() : throw new InvalidOperationException("Rundeck answered with what is not a list - check the address: a sign-in page or a proxy answers so.");

        private static IEnumerable<JsonObject> Entries(JsonObject? output) => (output?["entries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

        // An execution as a record: its number, status, job and dates flat; its options as JSON.
        private static ConnectorRecord Execution(JsonObject execution)
        {
            var job = execution["job"] as JsonObject;
            return Fields(
                ("id", Value(execution["id"])), ("status", Text(execution["status"])), ("project", Text(execution["project"])), ("user", Text(execution["user"])),
                ("job", Text(job?["id"])), ("job_name", Text(job?["name"])), ("group", Text(job?["group"])), ("options", job?["options"]?.ToJsonString(Wire)),
                ("started", Text(execution["date-started"]?["date"])), ("ended", Text(execution["date-ended"]?["date"])), ("aborted_by", Text(execution["abortedby"])),
                ("argstring", Text(execution["argstring"])), ("permalink", Text(execution["permalink"])));
        }

        private static ConnectorRecord Record(JsonObject data) =>
            new(data.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null
            : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string token;

            private Session(HttpClient client, string api, string token)
            {
                this.client = client;
                this.api = api;
                this.token = token;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give Rundeck's address - https://rundeck.kafe.example.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                var version = Regex.Match(root, "/api(/[0-9]+)?$", RegexOptions.CultureInvariant);
                if (version.Success)
                {
                    root = root[..version.Index]; // the API's own address given: Rundeck's
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Rundeck's address - https://rundeck.kafe.example, with no query.");
                }

                var token = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Rundeck connection has no API token.");
                return new Session(egress.CreateHttpClient(timeout), root + Api, token);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with the token in its header; Rundeck's JSON, or its status, message and what it supports.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.TryAddWithoutValidation("X-Rundeck-Auth-Token", token);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json ?? throw new InvalidOperationException("Rundeck answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }

                var status = (int)response.StatusCode;
                // Rundeck names a refused token by its first characters, "(Token:abcde****)": never into a run's log.
                var said = Regex.Replace(Text(json?["message"]) ?? string.Empty, @"\(Token:[^)]*\)", "(the token)", RegexOptions.CultureInvariant);
                said = said.Length > 300 ? said[..300] : said;
                var code = Text(json?["errorCode"]);
                throw new InvalidOperationException($"Rundeck answered HTTP {status}{(said.Length == 0 ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + (code == "api.error.api-version.unsupported" ? $" - this connector speaks Rundeck's API 44 (Rundeck 4.12 and later); this Rundeck answers up to {Text(json?["apiversion"])}."
                        : status is 401 or 403 ? " - check the API token and what its user may do." : string.Empty));
            }
        }
    }
}
