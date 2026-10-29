#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Jira
{
    /// <summary>
    /// Jira (BL-229, n8n's Jira Software node: issues got, searched, created, updated, commented on and moved through their
    /// workflow): a Jira Cloud site's REST API (<c>https://your-site.atlassian.net</c>), an account's email and API token by the
    /// Basic scheme. A test asks Jira who the account is (<c>rest/api/3/myself</c>).
    /// </summary>
    /// <remarks>
    /// <c>search</c>: the issues a JQL query finds (<c>jql</c>), the <c>fields</c> named (or the usual ones), page by page by Jira's
    /// own token up to <c>max</c>; one record each: <c>id</c>, <c>key</c>, <c>url</c> (its page on the site) and each field as a plain
    /// value - a person their display name, a status, a priority or a type its name, an option its value, a description its text,
    /// a list or anything else as JSON. <c>create</c>: an issue in a <c>project</c> (its key) of an <c>issue_type</c> (Task by
    /// default) with a <c>summary</c>, a plain-text <c>description</c>, <c>labels</c> and any other <c>fields</c> as Jira's JSON - one
    /// record: its id, key and url. <c>update</c>: an <c>issue</c>'s summary, description, labels or other fields changed;
    /// <c>comment</c>: a comment added; <c>transition</c>: the issue moved by the transition named (<c>to</c>, the transition's name
    /// or the status it leads to - the issue's own transitions read first). An issue key and a project key are held to Jira's shape
    /// before they reach the path. A throttled call waits as Jira says; a failure is thrown with its status and Jira's first message.
    /// </remarks>
    public sealed class JiraConnector : IConnector, IConnectorExtractor
    {
        public const string SearchOperation = "search";

        public const string CreateOperation = "create";

        public const string UpdateOperation = "update";

        public const string CommentOperation = "comment";

        public const string TransitionOperation = "transition";

        public const int DefaultMax = 1000;

        public const string DefaultFields = "summary,status,assignee,reporter,priority,issuetype,labels,created,updated,description";

        private static readonly Regex IssueKey = new("^([A-Z][A-Z0-9_]{0,254}-[0-9]{1,12}|[0-9]{1,18})$", RegexOptions.CultureInvariant);

        private static readonly Regex ProjectKey = new("^[A-Z][A-Z0-9_]{0,254}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[A-Za-z0-9_.]{1,100}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "jira";

        public string DisplayName => "Jira";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        /// <summary>The Basic scheme as Atlassian names it: the account's email and an API token.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "Email", ["password"] = "API token" };

        public string EndpointExample => "https://your-site.atlassian.net";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.ItsmAssetManagement };

        public Uri WebsiteUrl { get; } = new("https://www.atlassian.com/software/jira");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.atlassian.com/cloud/jira/platform/rest/v3/intro/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField IssueField = new("issue", "Issue", ConnectorConfigFieldType.Text, Required: true, Description: "The issue's key (PROJ-123) or id.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SearchOperation, "Search issues", "The issues a JQL query finds - one record each: its id, key, page and fields - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("jql", "JQL", ConnectorConfigFieldType.Text, Required: true, Description: "Jira's query - project = OPS AND status != Done ORDER BY created DESC."),
                    new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Text, Required: false, Description: $"The fields to read, comma-separated. Empty: {DefaultFields}."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many issues, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(CreateOperation, "Create an issue", "Creates an issue - one record: its id, key and page - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: true, Description: "The project's key - OPS."),
                    new ConnectorConfigField("issue_type", "Issue type", ConnectorConfigFieldType.Text, Required: false, Description: "Task, Bug, Story...") { Default = "Task" },
                    new ConnectorConfigField("summary", "Summary", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Plain text."),
                    new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated, no spaces in a label."),
                    new ConnectorConfigField("fields", "Other fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "Jira's own JSON - {\"priority\": {\"name\": \"High\"}}."),
                }),
            },
            new ConnectorOperation(UpdateOperation, "Update an issue", "Changes an issue's summary, description, labels or other fields - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    IssueField,
                    new ConnectorConfigField("summary", "Summary", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Plain text."),
                    new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - they replace the issue's."),
                    new ConnectorConfigField("fields", "Other fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "Jira's own JSON."),
                }),
            },
            new ConnectorOperation(CommentOperation, "Comment on an issue", "Adds a comment to an issue - one record: its id - n8n's Add comment.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    IssueField,
                    new ConnectorConfigField("text", "Comment", ConnectorConfigFieldType.Multiline, Required: true, Description: "Plain text."),
                }),
            },
            new ConnectorOperation(TransitionOperation, "Move an issue", "Moves an issue through its workflow by a transition, named by itself or by the status it leads to.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    IssueField,
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: true, Description: "The transition's name, or the status it leads to - Done."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var site = Site(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, BearerClient.Basic(context.Credentials, "Jira"), HttpMethod.Get, site + "/rest/api/3/myself", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in as {me["displayName"]?.GetValue<string>() ?? me["emailAddress"]?.GetValue<string>() ?? "the account"}.");
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
            var site = Site(context.Endpoint);
            var operation = context.OperationName ?? SearchOperation;
            var authorization = BearerClient.Basic(context.Credentials, "Jira");

            if (string.Equals(operation, SearchOperation, StringComparison.OrdinalIgnoreCase))
            {
                await foreach (var record in SearchAsync(context, site, authorization, cancellationToken))
                {
                    yield return record;
                }

                yield break;
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (string.Equals(operation, CreateOperation, StringComparison.OrdinalIgnoreCase))
            {
                var project = Values.Get(context.Parameters, "project") is { } key && (ProjectKey.IsMatch(key) || key.All(char.IsAsciiDigit)) ? key
                    : throw new InvalidOperationException($"'{Values.Get(context.Parameters, "project")}' is not a Jira project key - OPS - or id.");
                var issueType = Values.Get(context.Parameters, "issue_type") ?? "Task";
                var fields = Fields(context.Parameters, requireSummary: true);
                fields["project"] = new JsonObject { [project.All(char.IsAsciiDigit) ? "id" : "key"] = project }; // n8n's pickers keep a project's id
                fields["issuetype"] = new JsonObject { [issueType.All(char.IsAsciiDigit) ? "id" : "name"] = issueType };
                var created = await SendAsync(client, authorization, HttpMethod.Post, site + "/rest/api/2/issue", new JsonObject { ["fields"] = fields }, cancellationToken);
                var createdKey = created["key"]?.GetValue<string>();
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = created["id"]?.GetValue<string>(),
                    ["key"] = createdKey,
                    ["url"] = createdKey is null ? null : $"{site}/browse/{createdKey}",
                });
                yield break;
            }

            var issue = Issue(Values.Get(context.Parameters, "issue"));
            if (string.Equals(operation, UpdateOperation, StringComparison.OrdinalIgnoreCase))
            {
                var fields = Fields(context.Parameters, requireSummary: false);
                if (fields.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - summary, description, labels or fields.");
                }

                await SendAsync(client, authorization, HttpMethod.Put, $"{site}/rest/api/2/issue/{issue}", new JsonObject { ["fields"] = fields }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = issue, ["updated"] = string.Join(", ", fields.Select(field => field.Key)) });
                yield break;
            }

            if (string.Equals(operation, CommentOperation, StringComparison.OrdinalIgnoreCase))
            {
                var text = Values.Get(context.Parameters, "text") ?? throw new InvalidOperationException("Give the comment's text.");
                var comment = await SendAsync(client, authorization, HttpMethod.Post, $"{site}/rest/api/2/issue/{issue}/comment", new JsonObject { ["body"] = text }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = issue, ["comment_id"] = comment["id"]?.GetValue<string>() });
                yield break;
            }

            if (string.Equals(operation, TransitionOperation, StringComparison.OrdinalIgnoreCase))
            {
                var to = Values.Get(context.Parameters, "to") ?? throw new InvalidOperationException("Say where it moves - the transition's name or its status.");
                var offered = await SendAsync(client, authorization, HttpMethod.Get, $"{site}/rest/api/2/issue/{issue}/transitions", null, cancellationToken);
                var transitions = (offered["transitions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                var chosen = transitions.FirstOrDefault(transition => string.Equals(transition["name"]?.GetValue<string>(), to, StringComparison.OrdinalIgnoreCase))
                    ?? transitions.FirstOrDefault(transition => string.Equals(transition["to"]?["name"]?.GetValue<string>(), to, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{issue} has no transition '{to}' - it can go: {string.Join(", ", transitions.Select(transition => transition["name"]?.GetValue<string>()))}.");
                await SendAsync(client, authorization, HttpMethod.Post, $"{site}/rest/api/2/issue/{issue}/transitions", new JsonObject { ["transition"] = new JsonObject { ["id"] = chosen["id"]?.GetValue<string>() } }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["key"] = issue,
                    ["transition"] = chosen["name"]?.GetValue<string>(),
                    ["status"] = chosen["to"]?["name"]?.GetValue<string>(),
                });
                yield break;
            }

            throw new InvalidOperationException($"Jira has no operation '{operation}' - search, create, update, comment or transition.");
        }

        private static async IAsyncEnumerable<ConnectorRecord> SearchAsync(ConnectorExtractContext context, string site, AuthenticationHeaderValue authorization, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var jql = Values.Get(context.Parameters, "jql") ?? throw new InvalidOperationException("Give the JQL query.");
            var fields = (Values.Get(context.Parameters, "fields") ?? DefaultFields).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => FieldName.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a Jira field's name."))
                .ToList();
            var max = int.TryParse(Values.Get(context.Parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var given) && given > 0 ? given : DefaultMax;
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            var read = 0;
            string? next = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = new JsonObject
                {
                    ["jql"] = jql,
                    ["fields"] = new JsonArray(fields.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
                    ["maxResults"] = Math.Min(100, max - read),
                };
                if (next is not null)
                {
                    body["nextPageToken"] = next;
                }

                var page = await SendAsync(client, authorization, HttpMethod.Post, site + "/rest/api/3/search/jql", body, cancellationToken);
                foreach (var issue in (page["issues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (read >= max)
                    {
                        break;
                    }

                    read++;
                    yield return Record(issue, site);
                }

                next = read < max && page["isLast"]?.GetValue<bool>() != true ? page["nextPageToken"]?.GetValue<string>() : null;
            }
            while (!string.IsNullOrEmpty(next));
        }

        // The fields a create or an update sends: its summary, description and labels given, and any other fields as Jira's JSON.
        private static JsonObject Fields(IReadOnlyDictionary<string, string> parameters, bool requireSummary)
        {
            var fields = new JsonObject();
            if (Values.Get(parameters, "fields") is { } extra)
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(extra);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("The other fields are not JSON - Jira's own, {\"priority\": {\"name\": \"High\"}}.");
                }

                foreach (var (name, value) in parsed as JsonObject ?? throw new InvalidOperationException("The other fields are not a JSON object."))
                {
                    if (!FieldName.IsMatch(name))
                    {
                        throw new InvalidOperationException($"'{name}' is not a Jira field's name.");
                    }

                    fields[name] = value?.DeepClone();
                }
            }

            if (Values.Get(parameters, "summary") is { } summary)
            {
                if (summary.Contains('\n', StringComparison.Ordinal) || summary.Contains('\r', StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The summary has a line break - one line only.");
                }

                fields["summary"] = summary;
            }
            else if (requireSummary)
            {
                throw new InvalidOperationException("Give the issue's summary.");
            }

            if (Values.Get(parameters, "description") is { } description)
            {
                fields["description"] = description;
            }

            if (Values.Get(parameters, "labels") is { } labels)
            {
                fields["labels"] = new JsonArray(labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(label => label.Contains(' ', StringComparison.Ordinal) ? throw new InvalidOperationException($"'{label}' has a space - Jira's labels have none.") : (JsonNode?)JsonValue.Create(label))
                    .ToArray());
            }

            return fields;
        }

        // An issue as a record: its id, key and page, then each field as a plain value.
        private static ConnectorRecord Record(JsonObject issue, string site)
        {
            var key = issue["key"]?.GetValue<string>();
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = issue["id"]?.GetValue<string>(),
                ["key"] = key,
                ["url"] = key is null ? null : $"{site}/browse/{key}",
            };
            foreach (var (name, value) in issue["fields"] as JsonObject ?? new JsonObject())
            {
                fields[name] = Plain(value);
            }

            return new ConnectorRecord(fields);
        }

        // A field's value, plain: a person their display name, a named thing its name, an option its value, a document its text.
        private static object? Plain(JsonNode? value) => value switch
        {
            null => null,
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
            JsonValue scalar when scalar.TryGetValue<long>(out var number) => number,
            JsonValue scalar when scalar.TryGetValue<double>(out var real) => real,
            JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
            JsonObject document when document["type"]?.GetValue<string>() == "doc" => DocumentText(document).Trim(),
            JsonObject person when person["displayName"] is JsonValue displayName => displayName.GetValue<string>(),
            JsonObject named when named["name"] is JsonValue name => name.GetValue<string>(),
            JsonObject option when option["value"] is JsonValue chosen => chosen.ToString(),
            _ => value.ToJsonString(),
        };

        // Atlassian's document format as its text: each text node's, each paragraph and hard break a line.
        private static string DocumentText(JsonNode? node)
        {
            if (node is not JsonObject block)
            {
                return "";
            }

            var type = block["type"]?.GetValue<string>();
            if (type == "text")
            {
                return block["text"]?.GetValue<string>() ?? "";
            }

            if (type == "hardBreak")
            {
                return "\n";
            }

            var inner = string.Concat((block["content"] as JsonArray ?? new JsonArray()).Select(DocumentText));
            return type is "paragraph" or "heading" or "listItem" or "codeBlock" ? inner + "\n" : inner;
        }

        private static string Issue(string? given) =>
            given is not null && IssueKey.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a Jira issue - its key (PROJ-123) or id.");

        // The site's address, its own: an https URL, its path dropped (a pasted issue's address forgiven down to the site).
        private static string Site(string endpoint)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var site) || (site.Scheme != Uri.UriSchemeHttps && site.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a Jira site's address - https://your-site.atlassian.net.");
            }

            return site.GetLeftPart(UriPartial.Authority);
        }

        // Jira's answer as JSON (an empty answer an empty object), a throttled call waiting as Jira says; a failure thrown with its
        // status and Jira's first message.
        private static async Task<JsonObject> SendAsync(HttpClient client, AuthenticationHeaderValue authorization, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = authorization;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Jira answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = (json?["errorMessages"] as JsonArray)?.FirstOrDefault()?.GetValue<string>()
                    ?? (json?["errors"] as JsonObject)?.Select(error => error.Key + ": " + error.Value?.ToString()).FirstOrDefault();
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json as JsonObject ?? new JsonObject();
        }
    }
}
