#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.CircleCi
{
    /// <summary>
    /// CircleCI (BL-229, n8n's CircleCI node: a project's pipelines read and triggered) over CircleCI's API v2
    /// (<c>https://circleci.com/api/v2</c>) by a personal API token in its <c>Circle-Token</c> header - never in the address. A
    /// test reads who the token belongs to. A CircleCI server installation is its own address.
    /// </summary>
    /// <remarks>
    /// <c>pipelines</c> (a project's, newest first, on a branch or all - page by page by CircleCI's page token, up to <c>max</c>),
    /// <c>pipeline</c> (one, by its number), <c>pipeline_trigger</c> (a pipeline started on a branch or at a tag, with its pipeline
    /// parameters as a JSON object from the step's Query box) and <c>pipeline_workflows</c> (a pipeline's workflows and how each
    /// went - what n8n's node could not say, so a run can wait on its build). A project as CircleCI's slug - gh/kafe/kassa - or as
    /// n8n's node took it, its provider and org/repo, from the step or the connection. Each pipeline a record, its branch, tag,
    /// revision and who started it beside CircleCI's own fields. Every project, number, branch, tag and parameter held to
    /// CircleCI's shape before anything is sent; CircleCI's message on a failure.
    /// </remarks>
    public sealed class CircleCiConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://circleci.com";

        public const int DefaultMax = 100;

        private static readonly string[] Known = { "pipelines", "pipeline", "pipeline_trigger", "pipeline_workflows" };

        private static readonly Dictionary<string, string> Providers = new(StringComparer.OrdinalIgnoreCase) { ["github"] = "gh", ["gh"] = "gh", ["bitbucket"] = "bb", ["bb"] = "bb", ["circleci"] = "circleci" };

        // An org's or a repository's name - never . or .., which would climb the path it is written into.
        private static readonly Regex Segment = new("^(?!\\.{1,2}$)[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);

        // A git ref's name as git allows it: no space, no control character, none of ~ ^ : ? * [ \, no "..", not ending in / or .lock.
        private static readonly Regex Ref = new("^(?!.*\\.\\.)(?!.*\\.lock$)(?!/)(?!.*/$)[^\\s~^:?*\\[\\\\\\x00-\\x1f\\x7f]{1,255}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "circleci";

        public string DisplayName => "CircleCI";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "Personal API token (User Settings > Personal API Tokens)" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://circleci.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://circleci.com/docs/api/v2/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: false, Description: "The project a step names none of - gh/kafe/kassa, or kafe/kassa with the provider below."),
            new ConnectorConfigField("vcs", "Provider", ConnectorConfigFieldType.Text, Required: false, Description: "github (the default), bitbucket or circleci - for a project given as org/repo."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField ProjectField = Field("project", "Project", "gh/kafe/kassa - or kafe/kassa with its provider. Empty: the connection's.");

        private static readonly ConnectorConfigField ProviderField = Field("vcs", "Provider", "github, bitbucket or circleci, for a project given as org/repo. Empty: the connection's, or github.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("pipelines", "List pipelines", "A project's pipelines, newest first - n8n's Get Many.")
            {
                Parameters = new(new[]
                {
                    ProjectField, ProviderField,
                    Field("branch", "Branch", "Only this branch's - main."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many pipelines, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("pipeline", "Get a pipeline", "One of the project's pipelines, by its number - n8n's Get.")
            {
                Parameters = new(new[] { ProjectField, ProviderField, Field("number", "Number", "The pipeline's number - 142.", required: true) }),
            },
            new ConnectorOperation("pipeline_trigger", "Trigger a pipeline", "A pipeline started - n8n's Trigger.")
            {
                Parameters = new(new[]
                {
                    ProjectField, ProviderField,
                    Field("branch", "Branch", "The branch it builds - main. Empty, and no tag: the project's default branch."),
                    Field("tag", "Tag", "The tag it builds instead - v1.4.0."),
                    Field("parameters", "Pipeline parameters", "A JSON object - {\"deploy\": true} - or over several lines in the step's Query box."),
                }),
            },
            new ConnectorOperation("pipeline_workflows", "List a pipeline's workflows", "A pipeline's workflows and how each went.")
            {
                Parameters = new(new[] { Field("pipeline", "Pipeline", "Its id - the id a trigger or a list answered.", required: true) }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var me = await session.SendAsync(HttpMethod.Get, "/me", null, cancellationToken);
                return Text(me["login"]) is { Length: > 0 } login
                    ? ConnectionTestResult.Ok($"Signed in to CircleCI as {login}.")
                    : ConnectionTestResult.Fail("CircleCI answered without who the token belongs to - check the address: a sign-in page or a proxy answers so.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "pipelines").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"CircleCI has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to CircleCI's shape before anything is sent.
            string? project = null;
            string? route = null;
            JsonObject? body = null;
            var max = DefaultMax;
            var query = new List<(string Name, string Value)>();
            switch (operation)
            {
                case "pipelines":
                    project = Project(p, context.Settings);
                    if (Values.Get(p, "branch") is { } only)
                    {
                        query.Add(("branch", Ref.IsMatch(only) ? only : throw new InvalidOperationException($"'{only}' is not a branch's name (branch).")));
                    }

                    max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
                    route = $"/project/{project}/pipeline";
                    break;
                case "pipeline":
                    {
                        project = Project(p, context.Settings);
                        var number = Values.Get(p, "number") ?? throw new InvalidOperationException("Give the pipeline's number - 142 (number).");
                        route = $"/project/{project}/pipeline/{(long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var at) && at > 0 ? at.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{number}' is not a pipeline's number - 142 (number)."))}";
                        break;
                    }

                case "pipeline_trigger":
                    {
                        project = Project(p, context.Settings);
                        body = new JsonObject();
                        var branch = Values.Get(p, "branch");
                        var tag = Values.Get(p, "tag");
                        if (branch is not null && tag is not null)
                        {
                            throw new InvalidOperationException("Give a branch or a tag, not both - CircleCI builds one (branch, tag).");
                        }

                        if (branch is not null)
                        {
                            body["branch"] = Ref.IsMatch(branch) ? branch : throw new InvalidOperationException($"'{branch}' is not a branch's name (branch).");
                        }

                        if (tag is not null)
                        {
                            body["tag"] = Ref.IsMatch(tag) ? tag : throw new InvalidOperationException($"'{tag}' is not a tag's name (tag).");
                        }

                        var given = Values.Get(p, "parameters") ?? (p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box : null);
                        if (given is not null)
                        {
                            JsonNode? parsed;
                            try
                            {
                                parsed = JsonNode.Parse(given);
                            }
                            catch (JsonException)
                            {
                                parsed = null;
                            }

                            var parameters = parsed as JsonObject ?? throw new InvalidOperationException("The pipeline parameters are a JSON object - {\"deploy\": true} (parameters).");
                            foreach (var (name, value) in parameters)
                            {
                                // CircleCI's pipeline parameters are strings, numbers and booleans - nothing nested.
                                if (value is not JsonValue plain || plain.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                                {
                                    throw new InvalidOperationException($"The pipeline parameter '{name}' is not a string, a number or true or false (parameters).");
                                }
                            }

                            body["parameters"] = parameters.DeepClone();
                        }

                        route = $"/project/{project}/pipeline";
                        break;
                    }

                default:
                    {
                        var pipeline = Values.Get(p, "pipeline") ?? throw new InvalidOperationException("Give the pipeline's id - the id a trigger or a list answered (pipeline).");
                        route = $"/pipeline/{(Guid.TryParseExact(pipeline, "D", out var id) ? id.ToString("D") : throw new InvalidOperationException($"'{pipeline}' is not a pipeline's id - 5034460f-c7c4-4c43-9457-de07e2029e7b (pipeline)."))}/workflow";
                        break;
                    }
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "pipeline":
                    yield return Pipeline(await session.SendAsync(HttpMethod.Get, route, null, cancellationToken));
                    break;
                case "pipeline_trigger":
                    yield return Pipeline(await session.SendAsync(HttpMethod.Post, route, body, cancellationToken));
                    break;
                default:
                    {
                        // A list page by page by CircleCI's page token - pipelines up to the most, a pipeline's workflows all.
                        string? token = null;
                        var read = 0;
                        do
                        {
                            var page = new List<(string Name, string Value)>(query);
                            if (token is not null)
                            {
                                page.Add(("page-token", token));
                            }

                            var answer = await session.SendAsync(HttpMethod.Get, route + (page.Count > 0 ? "?" + string.Join("&", page.Select(pair => pair.Name + "=" + Uri.EscapeDataString(pair.Value))) : string.Empty), null, cancellationToken);
                            var items = answer["items"] as JsonArray ?? throw new InvalidOperationException("CircleCI answered without its items - check the address: a sign-in page or a proxy answers so.");
                            foreach (var item in items.OfType<JsonObject>())
                            {
                                yield return operation == "pipelines" ? Pipeline(item) : Record(item);
                                if (operation == "pipelines" && ++read >= max)
                                {
                                    yield break;
                                }
                            }

                            token = Text(answer["next_page_token"]) is { Length: > 0 } next ? next : null;
                        }
                        while (token is not null);
                        break;
                    }
            }
        }

        // The project as CircleCI's slug: gh/kafe/kassa as it is, or org/repo under its provider - the step's, the connection's,
        // or GitHub - each part held to its shape, as it is written into the path.
        private static string Project(IReadOnlyDictionary<string, string> p, IReadOnlyDictionary<string, string> settings)
        {
            var given = Values.Get(p, "project") ?? Values.Get(settings, "project") ?? throw new InvalidOperationException("Give the project - gh/kafe/kassa (project) - or set the connection's.");
            var parts = given.Trim('/').Split('/');
            string provider;
            if (parts.Length == 3)
            {
                provider = Providers.TryGetValue(parts[0], out var named) ? named : throw new InvalidOperationException($"'{given}' is not a project - gh/kafe/kassa, bb/kafe/kassa or circleci/<organization id>/<project id> (project).");
                parts = parts[1..];
            }
            else if (parts.Length == 2)
            {
                var vcs = Values.Get(p, "vcs") ?? Values.Get(settings, "vcs") ?? "github";
                provider = Providers.TryGetValue(vcs, out var named) ? named : throw new InvalidOperationException($"'{vcs}' is not github, bitbucket or circleci (vcs).");
            }
            else
            {
                throw new InvalidOperationException($"'{given}' is not a project - gh/kafe/kassa, or kafe/kassa with its provider (project).");
            }

            if (!parts.All(Segment.IsMatch) || (provider == "circleci" && !parts.All(part => Guid.TryParseExact(part, "D", out _))))
            {
                throw new InvalidOperationException($"'{given}' is not a project - gh/kafe/kassa, bb/kafe/kassa or circleci/<organization id>/<project id> (project).");
            }

            return provider + "/" + parts[0] + "/" + parts[1];
        }

        // A pipeline as a record: CircleCI's fields, and its branch, tag, revision and who started it beside them.
        private static ConnectorRecord Pipeline(JsonObject pipeline)
        {
            var fields = new Dictionary<string, object?>(Record(pipeline).Fields, StringComparer.Ordinal)
            {
                ["branch"] = Text(pipeline["vcs"]?["branch"]),
                ["tag"] = Text(pipeline["vcs"]?["tag"]),
                ["revision"] = Text(pipeline["vcs"]?["revision"]),
                ["actor"] = Text(pipeline["trigger"]?["actor"]?["login"]),
            };
            return new ConnectorRecord(fields);
        }

        // An object as a record: its plain values as they are, a list or an object as JSON.
        private static ConnectorRecord Record(JsonObject data) => new(data.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(),
        }, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/api/v2", StringComparison.Ordinal))
                {
                    root = root[..^"/api/v2".Length]; // the API's own address given: its host
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not CircleCI's address - its own, {DefaultEndpoint}, or a CircleCI server's.");
                }

                var token = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The CircleCI connection has no API token.");
                return new Session(egress.CreateHttpClient(timeout), root + "/api/v2", token);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with the token in its header; CircleCI's JSON object, or its status and message.</summary>
            public async Task<JsonObject> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString();
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.Add("Circle-Token", token);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
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
                    throw new InvalidOperationException("CircleCI answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json as JsonObject ?? throw new InvalidOperationException("CircleCI answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                }

                var status = (int)response.StatusCode;
                var said = Text(json?["message"]);
                throw new InvalidOperationException($"CircleCI answered HTTP {status}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}"
                    + (status == 401 ? " - check the API token." : status == 404 ? " - check the project and that the token's user can see it." : string.Empty));
            }
        }
    }
}
