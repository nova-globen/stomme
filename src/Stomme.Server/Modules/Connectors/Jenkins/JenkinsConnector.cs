#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

namespace Stomme.Modules.Connectors.Jenkins
{
    /// <summary>
    /// Jenkins (n8n's Jenkins node: a job built, copied and created, the builds, the instance quieted and restarted):
    /// a Jenkins server's remote access API with a user's name and API token (Basic), over the host's egress. A test names
    /// whose the token is.
    /// </summary>
    /// <remarks>
    /// <c>jobs</c> (a folder's, or the top's), <c>job</c>, <c>build</c> (with parameters or without - queued),
    /// <c>builds</c> (newest first, up to <c>max</c>), <c>build_info</c> (a build by number or the last, the last
    /// successful, failed, completed or stable), <c>console</c> (a build's log, its end when long), <c>stop_build</c>,
    /// <c>copy_job</c> and <c>create_job</c> (from its config.xml) - each read back once made; <c>quiet_down</c>,
    /// <c>cancel_quiet_down</c>, <c>safe_restart</c>, <c>restart</c>, <c>safe_exit</c> and <c>exit</c>. A job in a folder is
    /// named by its path - shop/build. A record is its fields as Jenkins gives them, a list or an object as its JSON. Every
    /// job, number, name, parameter and config held to its shape before anything is sent. What Jenkins answers without JSON
    /// is taken for Jenkins's only when it carries Jenkins's own header - a sign-in page or a proxy's never does. A throttled
    /// call waits as Jenkins says; a failure is thrown with its status and Jenkins's word.
    /// </remarks>
    public sealed class JenkinsConnector : IConnector, IConnectorExtractor
    {
        public const string JobsOperation = "jobs";

        public const string JobOperation = "job";

        public const string BuildOperation = "build";

        public const string BuildsOperation = "builds";

        public const string BuildInfoOperation = "build_info";

        public const string ConsoleOperation = "console";

        public const string StopBuildOperation = "stop_build";

        public const string CopyJobOperation = "copy_job";

        public const string CreateJobOperation = "create_job";

        public const string QuietDownOperation = "quiet_down";

        public const string CancelQuietDownOperation = "cancel_quiet_down";

        public const string SafeRestartOperation = "safe_restart";

        public const string RestartOperation = "restart";

        public const string SafeExitOperation = "safe_exit";

        public const string ExitOperation = "exit";

        public const int DefaultMax = 1000;

        // A console log's end, at most - the end being where a build says how it went.
        public const int ConsoleChars = 100_000;

        // A job's or a folder's name: anything a path can carry but a slash, never a dot segment.
        private static readonly Regex Segment = new("^(?!\\.{1,2}$)[^/\\\\?#%\\u0000-\\u001f]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly Regex BuildNumber = new("^([1-9][0-9]{0,9}|lastBuild|lastSuccessfulBuild|lastFailedBuild|lastCompletedBuild|lastStableBuild|lastUnstableBuild|lastUnsuccessfulBuild)$", RegexOptions.CultureInvariant);

        private static readonly Regex Parameter = new("^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            JobsOperation, JobOperation, BuildOperation, BuildsOperation, BuildInfoOperation, ConsoleOperation, StopBuildOperation, CopyJobOperation, CreateJobOperation,
            QuietDownOperation, CancelQuietDownOperation, SafeRestartOperation, RestartOperation, SafeExitOperation, ExitOperation,
        };

        private const string JobTree = "name,fullName,url,description,color,buildable,inQueue,nextBuildNumber,lastBuild[number,url,result,timestamp,duration],lastCompletedBuild[number,result],lastSuccessfulBuild[number],lastFailedBuild[number],healthReport[score,description]";

        private const string BuildTree = "number,url,displayName,description,result,building,timestamp,duration";

        public string ConnectorType => "jenkins";

        public string DisplayName => "Jenkins";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://jenkins.example.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["password"] = "API token (your user > Security > API Token)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://www.jenkins.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://www.jenkins.io/doc/book/using/remote-access-api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField JobField = Field("job", "Job", "The job's name, or its path in folders - shop/build.", required: true);

        private static readonly ConnectorConfigField NumberField = Field("number", "Build", "Its number, or lastBuild, lastSuccessfulBuild, lastFailedBuild, lastCompletedBuild or lastStableBuild.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static ConnectorOperation Instance(string name, string label, string description) => new(name, label, description) { Parameters = Schema() };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(JobsOperation, "Get jobs", "A folder's jobs, or the top's.") { Parameters = Schema(Field("folder", "Folder", "A folder's path - shop; the top when left empty."), MaxField) },
            new ConnectorOperation(JobOperation, "Get a job", "A job with its last builds and health.") { Parameters = Schema(JobField) },
            new ConnectorOperation(BuildOperation, "Build a job", "A build queued, with parameters or without - n8n's Jenkins Job Trigger and Trigger with Parameters.")
            {
                Parameters = Schema(JobField, Field("parameters", "Parameters", "A JSON object of the build's parameters - {\"BRANCH\": \"main\", \"DRY_RUN\": true}.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(BuildsOperation, "Get builds", "A job's builds, newest first - n8n's Jenkins Build Get Many.") { Parameters = Schema(JobField, MaxField) },
            new ConnectorOperation(BuildInfoOperation, "Get a build", "A build by number, or the last of a kind.") { Parameters = Schema(JobField, NumberField) },
            new ConnectorOperation(ConsoleOperation, "Get a build's log", "Its console - the end, when longer than 100,000 characters.") { Parameters = Schema(JobField, NumberField) },
            new ConnectorOperation(StopBuildOperation, "Stop a build", "A running build stopped.") { Parameters = Schema(JobField, NumberField) },
            new ConnectorOperation(CopyJobOperation, "Copy a job", "A job copied beside it under a new name - n8n's Jenkins Job Copy.") { Parameters = Schema(JobField, Field("new_job", "New name", "The copy's name.", required: true)) },
            new ConnectorOperation(CreateJobOperation, "Create a job", "A job from its config.xml - n8n's Jenkins Job Create.")
            {
                Parameters = Schema(Field("new_job", "Name", "Its name, or its path in folders - shop/build.", required: true), Field("config", "Config", "Its config.xml - <project>...</project>.", ConnectorConfigFieldType.Multiline, required: true)),
            },
            new ConnectorOperation(QuietDownOperation, "Quiet down", "No new builds started - n8n's Jenkins Instance Quiet Down.") { Parameters = Schema(Field("reason", "Reason", "Why - shown on Jenkins's pages.")) },
            Instance(CancelQuietDownOperation, "Cancel quiet down", "New builds started again - n8n's Jenkins Instance Cancel Quiet Down."),
            Instance(SafeRestartOperation, "Restart safely", "Restarted once the running builds are done - n8n's Jenkins Instance Safely Restart."),
            Instance(RestartOperation, "Restart", "Restarted at once - n8n's Jenkins Instance Restart."),
            Instance(SafeExitOperation, "Shut down safely", "Shut down once the running builds are done - n8n's Jenkins Instance Safely Shutdown."),
            Instance(ExitOperation, "Shut down", "Shut down at once - n8n's Jenkins Instance Shutdown."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = (await session.SendAsync(HttpMethod.Get, "/me/api/json?tree=fullName,id", null, cancellationToken)).Json as JsonObject;
                return ConnectionTestResult.Ok($"Jenkins: the token is {(Text(me?["fullName"]) is { Length: > 0 } name ? name : "a user")}'s{(Text(me?["id"]) is { } id ? $" ({id})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? JobsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Jenkins has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string[] PathOf(string name, string given) =>
                given.Trim('/').Split('/') is { Length: > 0 } segments && segments.All(Segment.IsMatch) ? segments : throw new InvalidOperationException($"'{given}' is not a job's name or path - shop/build ({name}).");
            string[]? job = operation is JobOperation or BuildOperation or BuildsOperation or BuildInfoOperation or ConsoleOperation or StopBuildOperation or CopyJobOperation
                ? Values.Get(p, "job") is { } named ? PathOf("job", named) : throw new InvalidOperationException("Give the job - its name or path.")
                : null;
            var folder = operation == JobsOperation && Values.Get(p, "folder") is { } folderName ? PathOf("folder", folderName) : Array.Empty<string>();
            var number = operation is BuildInfoOperation or ConsoleOperation or StopBuildOperation
                ? Values.Get(p, "number") is { } given ? BuildNumber.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a build's number or lastBuild, lastSuccessfulBuild, lastFailedBuild, lastCompletedBuild or lastStableBuild (number).") : throw new InvalidOperationException("Give the build - its number or lastBuild.")
                : null;
            string[]? newJob = null;
            if (operation is CopyJobOperation or CreateJobOperation)
            {
                newJob = Values.Get(p, "new_job") is { } name ? PathOf("new_job", name) : throw new InvalidOperationException("Give the new job's name.");
                if (operation == CopyJobOperation && newJob.Length > 1)
                {
                    throw new InvalidOperationException($"'{Values.Get(p, "new_job")}' is a path - a copy is made beside its job: give its name alone (new_job).");
                }
            }

            var form = new List<KeyValuePair<string, string>>();
            if (operation == BuildOperation && Values.Get(p, "parameters") is { } parameters)
            {
                var parsed = Parse(parameters) as JsonObject ?? throw new InvalidOperationException("The parameters are not a JSON object - {\"BRANCH\": \"main\"}.");
                foreach (var (key, value) in parsed)
                {
                    if (!Parameter.IsMatch(key))
                    {
                        throw new InvalidOperationException($"'{key}' is not a parameter's name (parameters).");
                    }

                    form.Add(new(key, value switch
                    {
                        JsonValue text when text.TryGetValue<string>(out var s) => s,
                        JsonValue flag when flag.TryGetValue<bool>(out var b) => b ? "true" : "false",
                        JsonValue other => other.ToJsonString(Wire),
                        _ => throw new InvalidOperationException($"'{key}' is not a text, a number or true or false (parameters)."),
                    }));
                }
            }

            string? config = operation == CreateJobOperation
                ? Values.Get(p, "config") is { } xml ? xml.TrimStart().StartsWith('<') ? xml : throw new InvalidOperationException("The config is not XML - <project>...</project>.") : throw new InvalidOperationException("Give the job's config.xml.")
                : null;

            static string Path(IEnumerable<string> segments) => string.Concat(segments.Select(segment => "/job/" + Uri.EscapeDataString(segment)));
            using var client = session.Client;
            async Task<ConnectorRecord> JobRecord(string[] path) => Record((await session.SendAsync(HttpMethod.Get, Path(path) + "/api/json?tree=" + Uri.EscapeDataString(JobTree), null, cancellationToken)).Json as JsonObject ?? new JsonObject());
            async Task<ConnectorRecord> Instance() => Record((await session.SendAsync(HttpMethod.Get, "/api/json?tree=" + Uri.EscapeDataString("mode,nodeName,quietingDown,useSecurity"), null, cancellationToken)).Json as JsonObject ?? new JsonObject());
            switch (operation)
            {
                case JobsOperation:
                    foreach (var item in (((await session.SendAsync(HttpMethod.Get, Path(folder) + "/api/json?tree=" + Uri.EscapeDataString("jobs[name,fullName,url,color,buildable,description]"), null, cancellationToken)).Json?["jobs"]) as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                    {
                        yield return Record(item);
                    }

                    yield break;
                case JobOperation:
                    yield return await JobRecord(job!);
                    yield break;
                case BuildOperation:
                    {
                        // Jenkins answers a build queued with where in its queue: no queue item, no build.
                        var answer = await session.SendAsync(HttpMethod.Post, Path(job!) + (form.Count > 0 ? "/buildWithParameters" : "/build"), form.Count > 0 ? new FormUrlEncodedContent(form) : null, cancellationToken, json: false);
                        var item = answer.Location is { } location && Regex.Match(location, "/queue/item/(?<id>[0-9]+)/?$") is { Success: true } queued
                            ? long.Parse(queued.Groups["id"].Value, CultureInfo.InvariantCulture)
                            : throw new InvalidOperationException("Jenkins did not say where it queued the build.");
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["job"] = string.Join("/", job!), ["queued"] = true, ["queue_item"] = item });
                        yield break;
                    }

                case BuildsOperation:
                    foreach (var item in (((await session.SendAsync(HttpMethod.Get, Path(job!) + "/api/json?tree=" + Uri.EscapeDataString($"allBuilds[{BuildTree}]{{0,{max.ToString(CultureInfo.InvariantCulture)}}}"), null, cancellationToken)).Json?["allBuilds"]) as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                    {
                        yield return Record(item);
                    }

                    yield break;
                case BuildInfoOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Get, $"{Path(job!)}/{number}/api/json", null, cancellationToken)).Json as JsonObject ?? new JsonObject());
                    yield break;
                case ConsoleOperation:
                    {
                        var text = (await session.SendAsync(HttpMethod.Get, $"{Path(job!)}/{number}/consoleText", null, cancellationToken, json: false)).Text;
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["job"] = string.Join("/", job!), ["number"] = number, ["text"] = text.Length > ConsoleChars ? text[^ConsoleChars..] : text, ["truncated"] = text.Length > ConsoleChars,
                        });
                        yield break;
                    }

                case StopBuildOperation:
                    await session.SendAsync(HttpMethod.Post, $"{Path(job!)}/{number}/stop", null, cancellationToken, json: false);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["job"] = string.Join("/", job!), ["number"] = number, ["stopped"] = true });
                    yield break;
                case CopyJobOperation:
                    {
                        var parent = job![..^1];
                        await session.SendAsync(HttpMethod.Post, $"{Path(parent)}/createItem?name={Uri.EscapeDataString(newJob![0])}&mode=copy&from={Uri.EscapeDataString(string.Join("/", job))}", null, cancellationToken, json: false);
                        yield return await JobRecord(parent.Append(newJob[0]).ToArray());
                        yield break;
                    }

                case CreateJobOperation:
                    await session.SendAsync(HttpMethod.Post, $"{Path(newJob![..^1])}/createItem?name={Uri.EscapeDataString(newJob[^1])}", new StringContent(config!, Encoding.UTF8, "application/xml"), cancellationToken, json: false);
                    yield return await JobRecord(newJob);
                    yield break;
                case QuietDownOperation:
                    await session.SendAsync(HttpMethod.Post, "/quietDown" + (Values.Get(p, "reason") is { } reason ? "?reason=" + Uri.EscapeDataString(reason) : ""), null, cancellationToken, json: false);
                    yield return await Instance();
                    yield break;
                case CancelQuietDownOperation:
                    await session.SendAsync(HttpMethod.Post, "/cancelQuietDown", null, cancellationToken, json: false);
                    yield return await Instance();
                    yield break;
                default:
                    {
                        // A restart's or a shutdown's answer is often Jenkins going away: a 503 is taken for it.
                        var path = operation switch { SafeRestartOperation => "/safeRestart", RestartOperation => "/restart", SafeExitOperation => "/safeExit", _ => "/exit" };
                        await session.SendAsync(HttpMethod.Post, path, null, cancellationToken, json: false, goingAway: true);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["operation"] = operation, ["requested"] = true });
                        yield break;
                    }
            }
        }

        private static JsonNode? Parse(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A record: its fields as Jenkins gives them, a list or an object as its JSON.
        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => (object?)d,
            var other => other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the server's remote access API with the user's name and token; what is not JSON taken for
        // Jenkins's only with Jenkins's own header; a throttled call waiting as Jenkins says; a failure thrown with its status
        // and Jenkins's word.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly AuthenticationHeaderValue authorization;

            private Session(string baseUrl, AuthenticationHeaderValue authorization, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.authorization = authorization;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the server's address - https://jenkins.example.com.");
                }

                var user = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Jenkins connection has no user name.");
                var token = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Jenkins connection has no API token.");
                return new Session(Values.BaseUrl(endpoint, ""), new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + token))), egress.CreateHttpClient(timeout));
            }

            public async Task<(JsonNode? Json, string Text, string? Location)> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, bool json = true, bool goingAway = false)
            {
                var payload = content is null ? null : await content.ReadAsByteArrayAsync(cancellationToken);
                var type = content?.Headers.ContentType;
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path) { Headers = { Authorization = authorization } };
                    if (payload is not null)
                    {
                        request.Content = new ByteArrayContent(payload) { Headers = { ContentType = type } };
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                var jenkins = response.Headers.Contains("X-Jenkins");
                var status = (int)response.StatusCode;

                // An action Jenkins answers with a redirect to a page - never followed here - is done when Jenkins's own header
                // says the redirect is Jenkins's; a restart's or a shutdown's 503 is Jenkins going away.
                var away = goingAway && response.StatusCode == HttpStatusCode.ServiceUnavailable;
                if (!response.IsSuccessStatusCode && !(!json && jenkins && status is >= 300 and < 400) && !away)
                {
                    // Jenkins says what went wrong in a header, its page being HTML.
                    var said = response.Headers.TryGetValues("X-Error", out var errors) ? errors.FirstOrDefault() : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                JsonNode? node = null;
                if (json)
                {
                    try
                    {
                        node = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                    }
                    catch (JsonException) when (response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"Jenkins answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                    }
                    catch (JsonException)
                    {
                        // A page that is not Jenkins's - a sign-in page, a proxy's - read as no jobs would hide that it never was.
                        throw new InvalidOperationException($"Jenkins answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                    }
                }
                else if (!jenkins && !away)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode} came without Jenkins's own header - check the address: a sign-in page or a proxy answers so.");
                }

                return (node, answer, response.Headers.Location?.ToString());
            }
        }
    }
}
