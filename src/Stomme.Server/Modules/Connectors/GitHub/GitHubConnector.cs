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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.GitHub
{
    /// <summary>
    /// GitHub (n8n's GitHub node: a repository's issues got, an issue got, created, edited and commented on, a release
    /// created, a file got and committed, a workflow dispatched): GitHub's REST API (<c>https://api.github.com</c>, or a GitHub
    /// Enterprise Server's <c>https://github.example.com/api/v3</c>), a personal access token as the Bearer token. A test asks
    /// GitHub who the token is (<c>user</c>).
    /// </summary>
    /// <remarks>
    /// Every operation names its repository - <c>owner</c> and <c>repository</c>, or <c>owner/name</c>, or its address - held to
    /// GitHub's shapes before they reach the path, as are an issue's number, a workflow (its file name or id) and a file's path
    /// (each segment escaped; no empty, <c>.</c> or <c>..</c> segment). <c>issues</c>: a repository's issues by <c>state</c>,
    /// <c>labels</c>, <c>assignee</c> and <c>since</c>, page by page by GitHub's Link header - followed only while it stays under
    /// the endpoint, since the token goes with it - up to <c>max</c>; a pull request is an issue to GitHub, and says so. One
    /// record each: number, title, state, url, author, labels, assignees, body, comments, pull_request and its times;
    /// <c>get_issue</c> the same for one. <c>create_issue</c> and <c>update_issue</c>: a title, a body, labels and assignees
    /// (comma-separated), a state (open or closed) - one record, its number and url. <c>comment</c>: its id and url.
    /// <c>release</c>: a tag's release, its name, notes, draft, prerelease and target - its id and url. <c>file</c>: a text file's
    /// content at a ref, its path, sha and size (a file that is not UTF-8 text, a folder, or one over GitHub's 1 MB refused).
    /// <c>commit_file</c>: a text file written on a branch with a commit message - created, or updated over the sha read first -
    /// its commit's sha and url. <c>dispatch</c>: a workflow run started at a ref, its inputs a JSON object. A throttled call
    /// waits as GitHub says; a failure is thrown with its status and GitHub's message.
    /// </remarks>
    public sealed class GitHubConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.github.com";

        public const string IssuesOperation = "issues";

        public const string GetIssueOperation = "get_issue";

        public const string CreateIssueOperation = "create_issue";

        public const string UpdateIssueOperation = "update_issue";

        public const string CommentOperation = "comment";

        public const string ReleaseOperation = "release";

        public const string FileOperation = "file";

        public const string CommitFileOperation = "commit_file";

        public const string DispatchOperation = "dispatch";

        public const int DefaultMax = 1000;

        private static readonly Regex Owner = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.CultureInvariant);

        private static readonly Regex Name = new("^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly Regex IssueNumber = new("^[0-9]{1,10}$", RegexOptions.CultureInvariant);

        private static readonly Regex Workflow = new("^([0-9]{1,18}|[A-Za-z0-9._-]{1,200}\\.ya?ml)$", RegexOptions.CultureInvariant);

        public string ConnectorType => "github";

        public string DisplayName => "GitHub";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as GitHub names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Personal access token" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://github.com");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.github.com/en/rest");

        private static readonly ConnectorConfigField OwnerField = new("owner", "Owner", ConnectorConfigFieldType.Text, Required: false, Description: "The repository's owner - a user or an organisation. Empty when Repository is owner/name.");

        private static readonly ConnectorConfigField RepositoryField = new("repository", "Repository", ConnectorConfigFieldType.Text, Required: true, Description: "Its name, owner/name, or its address.");

        private static readonly ConnectorConfigField IssueField = new("issue", "Issue", ConnectorConfigFieldType.Integer, Required: true, Description: "The issue's (or pull request's) number.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(IssuesOperation, "Get issues", "A repository's issues - one record each: its number, title, state, page, author, labels, assignees and body - n8n's Get Issues.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OwnerField, RepositoryField,
                    new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "open, closed or all.") { Default = "open" },
                    new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Only issues with every one of these labels, comma-separated."),
                    new ConnectorConfigField("assignee", "Assignee", ConnectorConfigFieldType.Text, Required: false, Description: "A login, none or *."),
                    new ConnectorConfigField("since", "Updated since", ConnectorConfigFieldType.Text, Required: false, Description: "Only issues updated since then - 2026-10-01T00:00:00Z."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many issues, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetIssueOperation, "Get an issue", "One issue - its number, title, state, page, author, labels, assignees and body - n8n's Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { OwnerField, RepositoryField, IssueField }),
            },
            new ConnectorOperation(CreateIssueOperation, "Create an issue", "Opens an issue - one record: its number and page - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OwnerField, RepositoryField,
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                    new ConnectorConfigField("body", "Body", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown."),
                    new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
                    new ConnectorConfigField("assignees", "Assignees", ConnectorConfigFieldType.Text, Required: false, Description: "Logins, comma-separated."),
                }),
            },
            new ConnectorOperation(UpdateIssueOperation, "Update an issue", "Changes an issue's title, body, state, labels or assignees - n8n's Edit.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OwnerField, RepositoryField, IssueField,
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
                    new ConnectorConfigField("body", "Body", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown."),
                    new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "open or closed."),
                    new ConnectorConfigField("state_reason", "Why", ConnectorConfigFieldType.Text, Required: false, Description: "completed, not_planned or reopened."),
                    new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - they replace the issue's."),
                    new ConnectorConfigField("assignees", "Assignees", ConnectorConfigFieldType.Text, Required: false, Description: "Logins, comma-separated - they replace the issue's."),
                }),
            },
            new ConnectorOperation(CommentOperation, "Comment on an issue", "Adds a comment to an issue or a pull request - one record: its id and page - n8n's Create Comment.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OwnerField, RepositoryField, IssueField,
                    new ConnectorConfigField("body", "Comment", ConnectorConfigFieldType.Multiline, Required: true, Description: "Markdown."),
                }),
            },
            new ConnectorOperation(ReleaseOperation, "Create a release", "Publishes a release of a tag - one record: its id and page - n8n's release Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OwnerField, RepositoryField,
                    new ConnectorConfigField("tag", "Tag", ConnectorConfigFieldType.Text, Required: true, Description: "v1.2.3 - made from Target when it does not exist yet."),
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Empty: the tag."),
                    new ConnectorConfigField("body", "Notes", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown."),
                    new ConnectorConfigField("draft", "Draft", ConnectorConfigFieldType.Boolean, Required: false, Description: "Kept unpublished.") { Default = "false" },
                    new ConnectorConfigField("prerelease", "Pre-release", ConnectorConfigFieldType.Boolean, Required: false, Description: "Marked not ready for production.") { Default = "false" },
                    new ConnectorConfigField("target", "Target", ConnectorConfigFieldType.Text, Required: false, Description: "A branch or commit the tag is made from. Empty: the default branch."),
                }),
            },
            new ConnectorOperation(FileOperation, "Get a file", "A text file's content - one record: its path, sha, size and content - n8n's file Get.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OwnerField, RepositoryField,
                    new ConnectorConfigField("path", "Path", ConnectorConfigFieldType.Text, Required: true, Description: "docs/README.md"),
                    new ConnectorConfigField("ref", "Branch, tag or commit", ConnectorConfigFieldType.Text, Required: false, Description: "Empty: the default branch."),
                }),
            },
            new ConnectorOperation(CommitFileOperation, "Commit a file", "Writes a text file with a commit - created, or replacing the file there - one record: the commit's sha and page - n8n's file Create and Edit.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OwnerField, RepositoryField,
                    new ConnectorConfigField("path", "Path", ConnectorConfigFieldType.Text, Required: true, Description: "docs/status.md"),
                    new ConnectorConfigField("content", "Content", ConnectorConfigFieldType.Multiline, Required: true, Description: "The file's text."),
                    new ConnectorConfigField("message", "Commit message", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                    new ConnectorConfigField("branch", "Branch", ConnectorConfigFieldType.Text, Required: false, Description: "Empty: the default branch."),
                }),
            },
            new ConnectorOperation(DispatchOperation, "Run a workflow", "Starts a GitHub Actions workflow that has workflow_dispatch - n8n's workflow Dispatch.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OwnerField, RepositoryField,
                    new ConnectorConfigField("workflow", "Workflow", ConnectorConfigFieldType.Text, Required: true, Description: "Its file name - deploy.yml - or its id."),
                    new ConnectorConfigField("ref", "Branch or tag", ConnectorConfigFieldType.Text, Required: true, Description: "main"),
                    new ConnectorConfigField("inputs", "Inputs", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object - {\"environment\": \"uat\"}."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var (me, _) = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, api + "/user", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in as {Text(me?["login"]) ?? "the token's account"}.");
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
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var token = Token(context.Credentials);
            var operation = context.OperationName ?? IssuesOperation;
            var parameters = context.Parameters;
            var repo = Repository(api, parameters);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, IssuesOperation))
            {
                await foreach (var issue in IssuesAsync(client, token, api, repo, parameters, cancellationToken))
                {
                    yield return issue;
                }

                yield break;
            }

            if (Is(operation, GetIssueOperation))
            {
                var (issue, _) = await SendAsync(client, token, HttpMethod.Get, $"{repo}/issues/{Issue(parameters)}", null, cancellationToken);
                yield return IssueRecord(issue as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, CreateIssueOperation))
            {
                var body = IssueFields(parameters);
                if (!body.ContainsKey("title"))
                {
                    throw new InvalidOperationException("Give the issue's title.");
                }

                var (created, _) = await SendAsync(client, token, HttpMethod.Post, repo + "/issues", body, cancellationToken);
                yield return Made(("number", Number(created?["number"])), ("url", Text(created?["html_url"])));
                yield break;
            }

            if (Is(operation, UpdateIssueOperation))
            {
                var number = Issue(parameters);
                var body = IssueFields(parameters);
                if (Values.Get(parameters, "state") is { } state)
                {
                    body["state"] = state.ToLowerInvariant() is "open" or "closed" ? state.ToLowerInvariant() : throw new InvalidOperationException($"'{state}' is not a state - open or closed.");
                }

                if (Values.Get(parameters, "state_reason") is { } reason)
                {
                    body["state_reason"] = reason.ToLowerInvariant() is "completed" or "not_planned" or "reopened" ? reason.ToLowerInvariant() : throw new InvalidOperationException($"'{reason}' is not a reason - completed, not_planned or reopened.");
                }

                if (body.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - title, body, state, labels or assignees.");
                }

                var (updated, _) = await SendAsync(client, token, HttpMethod.Patch, $"{repo}/issues/{number}", body, cancellationToken);
                yield return Made(("number", Number(updated?["number"])), ("url", Text(updated?["html_url"])), ("state", Text(updated?["state"])), ("updated", string.Join(", ", body.Select(field => field.Key))));
                yield break;
            }

            if (Is(operation, CommentOperation))
            {
                var number = Issue(parameters);
                var text = Values.Get(parameters, "body") ?? throw new InvalidOperationException("Give the comment's text.");
                var (comment, _) = await SendAsync(client, token, HttpMethod.Post, $"{repo}/issues/{number}/comments", new JsonObject { ["body"] = text }, cancellationToken);
                yield return Made(("issue", long.Parse(number, CultureInfo.InvariantCulture)), ("comment_id", Number(comment?["id"])), ("url", Text(comment?["html_url"])));
                yield break;
            }

            if (Is(operation, ReleaseOperation))
            {
                var tag = Values.Get(parameters, "tag") ?? throw new InvalidOperationException("Give the release's tag.");
                var body = new JsonObject { ["tag_name"] = tag, ["draft"] = Values.GetBool(parameters, "draft", false), ["prerelease"] = Values.GetBool(parameters, "prerelease", false) };
                foreach (var (parameter, field) in new[] { ("name", "name"), ("body", "body"), ("target", "target_commitish") })
                {
                    if (Values.Get(parameters, parameter) is { } value)
                    {
                        body[field] = value;
                    }
                }

                var (release, _) = await SendAsync(client, token, HttpMethod.Post, repo + "/releases", body, cancellationToken);
                yield return Made(("release_id", Number(release?["id"])), ("tag", Text(release?["tag_name"]) ?? tag), ("url", Text(release?["html_url"])));
                yield break;
            }

            if (Is(operation, FileOperation))
            {
                var path = FilePath(parameters);
                var at = Values.Get(parameters, "ref") is { } reference ? "?ref=" + Uri.EscapeDataString(reference) : "";
                var (file, _) = await SendAsync(client, token, HttpMethod.Get, $"{repo}/contents/{path}{at}", null, cancellationToken);
                yield return FileRecord(file as JsonObject ?? throw new InvalidOperationException($"'{Values.Get(parameters, "path")}' is a folder - give a file's path."));
                yield break;
            }

            if (Is(operation, CommitFileOperation))
            {
                var path = FilePath(parameters);
                var content = parameters.TryGetValue("content", out var given) ? given : throw new InvalidOperationException("Give the file's content.");
                var message = Values.Get(parameters, "message") ?? throw new InvalidOperationException("Give the commit's message.");
                var branch = Values.Get(parameters, "branch");
                var (existing, _) = await SendAsync(client, token, HttpMethod.Get, $"{repo}/contents/{path}{(branch is null ? "" : "?ref=" + Uri.EscapeDataString(branch))}", null, cancellationToken, notFound: true);
                if (existing is JsonArray)
                {
                    throw new InvalidOperationException($"'{Values.Get(parameters, "path")}' is a folder - give a file's path.");
                }

                var body = new JsonObject { ["message"] = message, ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) };
                if (branch is not null)
                {
                    body["branch"] = branch;
                }

                if (Text(existing?["sha"]) is { } sha)
                {
                    body["sha"] = sha;
                }

                var (written, _) = await SendAsync(client, token, HttpMethod.Put, $"{repo}/contents/{path}", body, cancellationToken);
                yield return Made(("path", Text(written?["content"]?["path"])), ("sha", Text(written?["content"]?["sha"])), ("commit", Text(written?["commit"]?["sha"])),
                    ("url", Text(written?["commit"]?["html_url"])), ("created", existing is null));
                yield break;
            }

            if (Is(operation, DispatchOperation))
            {
                var workflow = Values.Get(parameters, "workflow") is { } named && Workflow.IsMatch(named) ? named
                    : throw new InvalidOperationException($"'{Values.Get(parameters, "workflow")}' is not a workflow - its file name (deploy.yml) or its id.");
                var reference = Values.Get(parameters, "ref") ?? throw new InvalidOperationException("Give the branch or tag the workflow runs on.");
                var body = new JsonObject { ["ref"] = reference };
                if (Values.Get(parameters, "inputs") is { } inputs)
                {
                    JsonNode? parsed;
                    try
                    {
                        parsed = JsonNode.Parse(inputs);
                    }
                    catch (JsonException)
                    {
                        throw new InvalidOperationException("The inputs are not JSON - {\"environment\": \"uat\"}.");
                    }

                    var given = parsed as JsonObject ?? throw new InvalidOperationException("The inputs are not a JSON object.");
                    var asText = new JsonObject();
                    foreach (var (name, value) in given)
                    {
                        asText[name] = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString(); // GitHub takes each input as text
                    }

                    body["inputs"] = asText;
                }

                await SendAsync(client, token, HttpMethod.Post, $"{repo}/actions/workflows/{workflow}/dispatches", body, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["workflow"] = workflow, ["ref"] = reference, ["dispatched"] = true });
                yield break;
            }

            throw new InvalidOperationException($"GitHub has no operation '{operation}' - issues, get_issue, create_issue, update_issue, comment, release, file, commit_file or dispatch.");
        }

        private static async IAsyncEnumerable<ConnectorRecord> IssuesAsync(HttpClient client, string token, string api, string repo, IReadOnlyDictionary<string, string> parameters, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
            var state = (Values.Get(parameters, "state") ?? "open").ToLowerInvariant();
            if (state is not ("open" or "closed" or "all"))
            {
                throw new InvalidOperationException($"'{state}' is not a state - open, closed or all.");
            }

            var query = new List<string> { "state=" + state, "per_page=" + Math.Min(100, max).ToString(CultureInfo.InvariantCulture) };
            if (Values.Get(parameters, "labels") is { } labels)
            {
                query.Add("labels=" + Uri.EscapeDataString(string.Join(",", labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));
            }

            if (Values.Get(parameters, "assignee") is { } assignee)
            {
                query.Add("assignee=" + Uri.EscapeDataString(assignee));
            }

            if (Values.Get(parameters, "since") is { } since)
            {
                query.Add("since=" + (DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                    ? Uri.EscapeDataString(moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                    : throw new InvalidOperationException($"'{since}' is not a time - 2026-10-01T00:00:00Z.")));
            }

            var read = 0;
            string? next = $"{repo}/issues?{string.Join("&", query)}";
            while (next is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (page, link) = await SendAsync(client, token, HttpMethod.Get, next, null, cancellationToken);
                foreach (var issue in (page as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (read >= max)
                    {
                        yield break;
                    }

                    read++;
                    yield return IssueRecord(issue);
                }

                // GitHub's next page, followed only while it stays under the endpoint - the token goes with it.
                next = read < max && link is not null && link.StartsWith(api + "/", StringComparison.OrdinalIgnoreCase) ? link : null;
            }
        }

        // The title, body, labels and assignees given, as GitHub's issue fields.
        private static JsonObject IssueFields(IReadOnlyDictionary<string, string> parameters)
        {
            var fields = new JsonObject();
            if (Values.Get(parameters, "title") is { } title)
            {
                fields["title"] = title.Contains('\n', StringComparison.Ordinal) || title.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException("The title has a line break - one line only.") : title;
            }

            if (parameters.TryGetValue("body", out var body) && !string.IsNullOrWhiteSpace(body))
            {
                fields["body"] = body;
            }

            foreach (var list in new[] { "labels", "assignees" })
            {
                if (Values.Get(parameters, list) is { } given)
                {
                    fields[list] = new JsonArray(given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
                }
            }

            return fields;
        }

        // An issue as a record - a person their login, a label its name, the lists comma-separated.
        private static ConnectorRecord IssueRecord(JsonObject issue) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["number"] = Number(issue["number"]),
            ["title"] = Text(issue["title"]),
            ["state"] = Text(issue["state"]),
            ["url"] = Text(issue["html_url"]),
            ["author"] = Text(issue["user"]?["login"]),
            ["labels"] = string.Join(", ", (issue["labels"] as JsonArray ?? new JsonArray()).Select(label => label is JsonObject named ? Text(named["name"]) : Text(label)).Where(name => !string.IsNullOrEmpty(name))),
            ["assignees"] = string.Join(", ", (issue["assignees"] as JsonArray ?? new JsonArray()).Select(person => Text(person?["login"])).Where(login => !string.IsNullOrEmpty(login))),
            ["body"] = Text(issue["body"]) ?? "",
            ["comments"] = Number(issue["comments"]),
            ["pull_request"] = issue["pull_request"] is not null,
            ["created_at"] = Text(issue["created_at"]),
            ["updated_at"] = Text(issue["updated_at"]),
            ["closed_at"] = Text(issue["closed_at"]),
        });

        // A file's answer as a record, its content decoded - text only, as GitHub answers it up to 1 MB.
        private static ConnectorRecord FileRecord(JsonObject file)
        {
            if (Text(file["type"]) != "file")
            {
                throw new InvalidOperationException($"'{Text(file["path"])}' is a {Text(file["type"]) ?? "thing"}, not a file.");
            }

            var size = Number(file["size"]) ?? 0;
            var encoded = Text(file["content"]) ?? "";
            if (encoded.Length == 0 && size > 0 || Text(file["encoding"]) is { } encoding && encoding != "base64")
            {
                throw new InvalidOperationException($"'{Text(file["path"])}' is over the 1 MB GitHub answers a file's content up to.");
            }

            string content;
            try
            {
                content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(encoded.Replace("\n", "", StringComparison.Ordinal)));
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                throw new InvalidOperationException($"'{Text(file["path"])}' is not a text file.");
            }

            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = Text(file["path"]),
                ["sha"] = Text(file["sha"]),
                ["size"] = size,
                ["url"] = Text(file["html_url"]),
                ["content"] = content,
            });
        }

        private static ConnectorRecord Made(params (string Name, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        // The repository's own address under the endpoint: owner and name held to GitHub's shapes, owner/name or the repository's
        // address forgiven down to them.
        private static string Repository(string api, IReadOnlyDictionary<string, string> parameters)
        {
            var owner = Values.Get(parameters, "owner");
            var name = Values.Get(parameters, "repository") ?? throw new InvalidOperationException("Name the repository.");
            if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(name, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
            {
                var segments = address.AbsolutePath.Trim('/').Split('/');
                (owner, name) = segments.Length >= 2 ? (segments[0], segments[1]) : (owner, name);
            }
            else if (name.Contains('/', StringComparison.Ordinal))
            {
                var parts = name.Split('/');
                (owner, name) = parts.Length == 2 ? (parts[0], parts[1]) : (owner, name);
            }

            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            if (owner is null || !Owner.IsMatch(owner))
            {
                throw new InvalidOperationException($"'{owner}' is not a GitHub owner - a user's or an organisation's login.");
            }

            if (!Name.IsMatch(name) || name is "." or "..")
            {
                throw new InvalidOperationException($"'{name}' is not a GitHub repository's name.");
            }

            return $"{api}/repos/{owner}/{name}";
        }

        private static string Issue(IReadOnlyDictionary<string, string> parameters) =>
            Values.Get(parameters, "issue") is { } given && IssueNumber.IsMatch(given.TrimStart('#')) ? given.TrimStart('#') : throw new InvalidOperationException($"'{Values.Get(parameters, "issue")}' is not an issue's number.");

        // A file's path, each segment escaped; an empty, . or .. segment refused - it would leave the repository's contents.
        private static string FilePath(IReadOnlyDictionary<string, string> parameters)
        {
            var given = Values.Get(parameters, "path") ?? throw new InvalidOperationException("Give the file's path.");
            var segments = given.Trim('/').Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains('\\', StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"'{given}' is not a file's path in the repository.");
            }

            return string.Join("/", segments.Select(Uri.EscapeDataString));
        }

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The GitHub connection has no token - a personal access token.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        // GitHub's answer as JSON and its next page's address (the Link header's rel="next"); a throttled call waits as GitHub says;
        // a failure thrown with its status and GitHub's message - a 404 answered as nothing when asked for.
        private static async Task<(JsonNode? Json, string? Next)> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken, bool notFound = false)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Stomme", null)); // GitHub refuses a call without one
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            if (notFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, null);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GitHub answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = Text(json?["message"]);
                if (said is not null && json?["errors"] is JsonArray errors && errors.FirstOrDefault() is JsonObject first)
                {
                    said += " (" + (Text(first["message"]) ?? string.Join(" ", new[] { Text(first["resource"]), Text(first["field"]), Text(first["code"]) }.Where(part => part is not null))) + ")";
                }

                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            string? next = null;
            if (response.Headers.TryGetValues("Link", out var links))
            {
                foreach (var part in string.Join(",", links).Split(','))
                {
                    var pieces = part.Split(';');
                    if (pieces.Length >= 2 && pieces.Skip(1).Any(piece => piece.Trim() == "rel=\"next\""))
                    {
                        next = pieces[0].Trim().TrimStart('<').TrimEnd('>');
                    }
                }
            }

            return (json, next);
        }
    }
}
