#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

namespace Stomme.Modules.Connectors.Netlify
{
    /// <summary>
    /// Netlify (n8n's Netlify node: sites and deploys): Netlify's API with a personal access token as a bearer token,
    /// over the host's egress. A test names whose the token is.
    /// </summary>
    /// <remarks>
    /// <c>sites</c>, <c>site</c> and <c>delete_site</c>; <c>deploys</c> (a site's, by state, branch or production only),
    /// <c>deploy</c>, <c>create_deploy</c>, <c>cancel_deploy</c> and <c>restore_deploy</c> (an earlier deploy published
    /// again); <c>build</c> (a site built from its repository, its cache cleared or not); <c>forms</c>, <c>submissions</c>
    /// (a form's - each field it was sent with its own too) and <c>delete_submission</c>. Lists page by page up to
    /// <c>max</c> as long as Netlify says there is a next page. A record is its fields as Netlify gives them, a list or an
    /// object as its JSON. Every site, deploy, form, submission, state and branch held to its shape before anything is
    /// sent. A throttled call waits as Netlify says; a failure is thrown with its status and Netlify's word.
    /// </remarks>
    public sealed class NetlifyConnector : IConnector, IConnectorExtractor
    {
        public const string SitesOperation = "sites";

        public const string SiteOperation = "site";

        public const string DeleteSiteOperation = "delete_site";

        public const string DeploysOperation = "deploys";

        public const string DeployOperation = "deploy";

        public const string CreateDeployOperation = "create_deploy";

        public const string CancelDeployOperation = "cancel_deploy";

        public const string RestoreDeployOperation = "restore_deploy";

        public const string BuildOperation = "build";

        public const string FormsOperation = "forms";

        public const string SubmissionsOperation = "submissions";

        public const string DeleteSubmissionOperation = "delete_submission";

        public const string DefaultEndpoint = "https://api.netlify.com/api/v1";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        // A site: its id, or its name or domain - kafe.netlify.app.
        private static readonly Regex Site = new("^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant);

        // A deploy's, a form's, a submission's id - 24 hex digits.
        private static readonly Regex ObjectId = new("^[0-9a-f]{24}$", RegexOptions.CultureInvariant);

        private static readonly Regex Branch = new("^[A-Za-z0-9._/-]{1,250}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SitesOperation, SiteOperation, DeleteSiteOperation, DeploysOperation, DeployOperation, CreateDeployOperation, CancelDeployOperation, RestoreDeployOperation, BuildOperation, FormsOperation, SubmissionsOperation, DeleteSubmissionOperation };

        private static readonly string[] States = { "new", "pending_review", "accepted", "rejected", "enqueued", "building", "uploading", "uploaded", "preparing", "prepared", "processing", "processed", "ready", "error", "retrying" };

        public string ConnectorType => "netlify";

        public string DisplayName => "Netlify";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Personal access token (User settings > Applications)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://www.netlify.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://open-api.netlify.com/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField SiteField = Field("site", "Site", "The site's id, or its name or domain - kafe.netlify.app.", required: true);

        private static readonly ConnectorConfigField DeployField = Field("deploy", "Deploy", "The deploy's id.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SitesOperation, "Get sites", "Every site the token reaches - n8n's Netlify Site Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(SiteOperation, "Get a site", "n8n's Netlify Site Get.") { Parameters = Schema(SiteField) },
            new ConnectorOperation(DeleteSiteOperation, "Delete a site", "n8n's Netlify Site Delete.") { Parameters = Schema(SiteField) },
            new ConnectorOperation(DeploysOperation, "Get deploys", "A site's deploys, newest first - n8n's Netlify Deploy Get Many.")
            {
                Parameters = Schema(
                    SiteField,
                    Field("state", "State", "ready, error, building, enqueued and the rest of Netlify's states."),
                    Field("branch", "Branch", "A branch's deploys only."),
                    Field("production", "Production", "true: production deploys only.", ConnectorConfigFieldType.Boolean),
                    MaxField),
            },
            new ConnectorOperation(DeployOperation, "Get a deploy", "n8n's Netlify Deploy Get.") { Parameters = Schema(DeployField) },
            new ConnectorOperation(CreateDeployOperation, "Create a deploy", "A deploy made - n8n's Netlify Deploy Create.")
            {
                Parameters = Schema(SiteField, Field("title", "Title", "Its title."), Field("branch", "Branch", "Its branch."), Field("draft", "Draft", "true: a draft, not published.", ConnectorConfigFieldType.Boolean)),
            },
            new ConnectorOperation(CancelDeployOperation, "Cancel a deploy", "n8n's Netlify Deploy Cancel.") { Parameters = Schema(DeployField) },
            new ConnectorOperation(RestoreDeployOperation, "Publish an earlier deploy", "A site rolled back to one of its deploys.") { Parameters = Schema(SiteField, DeployField) },
            new ConnectorOperation(BuildOperation, "Build a site", "A site built from its repository.") { Parameters = Schema(SiteField, Field("clear_cache", "Clear the cache", "true: built with its cache cleared.", ConnectorConfigFieldType.Boolean)) },
            new ConnectorOperation(FormsOperation, "Get forms", "A site's forms.") { Parameters = Schema(SiteField) },
            new ConnectorOperation(SubmissionsOperation, "Get submissions", "A form's submissions, newest first - each field it was sent with its own too.") { Parameters = Schema(Field("form", "Form", "The form's id.", required: true), MaxField) },
            new ConnectorOperation(DeleteSubmissionOperation, "Delete a submission", "A form's submission deleted.") { Parameters = Schema(Field("submission", "Submission", "The submission's id.", required: true)) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var user = (await session.SendAsync(HttpMethod.Get, "/user", null, cancellationToken)).Json as JsonObject;
                return ConnectionTestResult.Ok($"Netlify: the token is {(Text(user?["full_name"]) is { Length: > 0 } name ? name : "a user")}'s{(Text(user?["email"]) is { } email ? $" ({email})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? DeploysOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Netlify has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string Of(string name, Regex shape, string what) => Values.Get(p, name) is { } given ? shape.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what} ({name}).") : throw new InvalidOperationException($"Give the {name}.");
            string? Flag(string name) => Values.Get(p, name) is { } flag ? flag.ToLowerInvariant() is "true" or "false" ? flag.ToLowerInvariant() : throw new InvalidOperationException($"'{flag}' is not true or false ({name}).") : null;
            var site = operation is SiteOperation or DeleteSiteOperation or DeploysOperation or CreateDeployOperation or RestoreDeployOperation or BuildOperation or FormsOperation ? Of("site", Site, "a site's id, name or domain") : null;
            var deploy = operation is DeployOperation or CancelDeployOperation or RestoreDeployOperation ? Of("deploy", ObjectId, "a deploy's id") : null;
            var form = operation == SubmissionsOperation ? Of("form", ObjectId, "a form's id") : null;
            var submission = operation == DeleteSubmissionOperation ? Of("submission", ObjectId, "a submission's id") : null;
            var branch = Values.Get(p, "branch") is { } named ? Branch.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a branch (branch).") : null;

            var query = new List<string>();
            if (operation == SitesOperation)
            {
                query.Add("filter=all");
            }

            if (operation == DeploysOperation)
            {
                if (Values.Get(p, "state") is { } state)
                {
                    query.Add("state=" + (States.Contains(state.ToLowerInvariant()) ? state.ToLowerInvariant() : throw new InvalidOperationException($"'{state}' is not a deploy's state - ready, error, building, enqueued, new, uploading, uploaded, processing, processed, prepared, preparing, accepted, rejected, pending_review or retrying (state).")));
                }

                if (branch is not null)
                {
                    query.Add("branch=" + Uri.EscapeDataString(branch));
                }

                if (Flag("production") == "true")
                {
                    query.Add("production=true");
                }
            }

            JsonObject? body = null;
            if (operation == CreateDeployOperation)
            {
                body = new JsonObject();
                if (branch is not null)
                {
                    body["branch"] = branch;
                }

                if (Flag("draft") is { } draft)
                {
                    body["draft"] = draft == "true";
                }

                if (Values.Get(p, "title") is { } title)
                {
                    query.Add("title=" + Uri.EscapeDataString(title));
                }
            }

            if (operation == BuildOperation)
            {
                body = new JsonObject();
                if (Flag("clear_cache") is { } clear)
                {
                    body["clear_cache"] = clear == "true";
                }
            }

            string Query(params string[] more) => query.Concat(more).ToList() is { Count: > 0 } all ? "?" + string.Join("&", all) : "";
            using var client = session.Client;
            switch (operation)
            {
                case SiteOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Get, $"/sites/{site}", null, cancellationToken)).Json as JsonObject ?? new JsonObject());
                    yield break;
                case DeleteSiteOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/sites/{site}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["site"] = site, ["deleted"] = true });
                    yield break;
                case DeployOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Get, $"/deploys/{deploy}", null, cancellationToken)).Json as JsonObject ?? new JsonObject());
                    yield break;
                case CreateDeployOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Post, $"/sites/{site}/deploys{Query()}", body, cancellationToken)).Json as JsonObject ?? new JsonObject());
                    yield break;
                case CancelDeployOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Post, $"/deploys/{deploy}/cancel", null, cancellationToken)).Json as JsonObject ?? new JsonObject());
                    yield break;
                case RestoreDeployOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Post, $"/sites/{site}/deploys/{deploy}/restore", null, cancellationToken)).Json as JsonObject ?? new JsonObject());
                    yield break;
                case BuildOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Post, $"/sites/{site}/builds", body, cancellationToken)).Json as JsonObject ?? new JsonObject());
                    yield break;
                case DeleteSubmissionOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/submissions/{submission}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = submission, ["deleted"] = true });
                    yield break;
                case FormsOperation:
                    foreach (var item in ((await session.SendAsync(HttpMethod.Get, $"/sites/{site}/forms", null, cancellationToken)).Json as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(item);
                    }

                    yield break;
                default:
                    {
                        // Netlify pages by number and says when there is a next page: the size fixed at the start, no next
                        // page or an empty one the end.
                        var path = operation switch
                        {
                            SitesOperation => "/sites",
                            DeploysOperation => $"/sites/{site}/deploys",
                            _ => $"/forms/{form}/submissions",
                        };
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var (json, more) = await session.SendAsync(HttpMethod.Get, path + Query($"page={page}", $"per_page={size}"), null, cancellationToken);
                            var items = (json as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return operation == SubmissionsOperation ? Submission(item) : Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count == 0 || !more)
                            {
                                yield break;
                            }
                        }
                    }
            }
        }

        // A submission: its fields as Netlify gives them, and each field it was sent with its own when Netlify has none of
        // that name.
        private static ConnectorRecord Submission(JsonObject item)
        {
            var fields = item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal);
            foreach (var (name, value) in item["data"] as JsonObject ?? new JsonObject())
            {
                fields.TryAdd(name, Value(value));
            }

            return new ConnectorRecord(fields);
        }

        // A record: its fields as Netlify gives them, a list or an object as its JSON.
        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

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

        // One connection's calls: Netlify's API with the token as a bearer token; whether Netlify says there is a next page;
        // a throttled call waiting as Netlify says; a failure thrown with its status and Netlify's word.
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

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout) =>
                new(Values.BaseUrl(endpoint, DefaultEndpoint), Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Netlify connection has no access token."), egress.CreateHttpClient(timeout));

            public async Task<(JsonNode? Json, bool More)> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                    // A page that is not Netlify's - a sign-in page, a proxy's - read as no sites would hide that it never was.
                    throw new InvalidOperationException($"Netlify answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = Text((json as JsonObject)?["message"]) ?? Text((json as JsonObject)?["error"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                // Netlify's next page is a link it names next.
                var more = response.Headers.TryGetValues("Link", out var links) && links.Any(link => link.Split(',').Any(part => part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)));
                return (json, more);
            }
        }
    }
}
