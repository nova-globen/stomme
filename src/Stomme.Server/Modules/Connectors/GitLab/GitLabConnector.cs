#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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

namespace Stomme.Modules.Connectors.GitLab
{
    /// <summary>
    /// GitLab (BL-229, n8n's GitLab node: a project's issues got, an issue got, created, edited and commented on, a release created,
    /// a file got and committed - and its merge requests read and a pipeline run): GitLab's REST API (<c>https://gitlab.com/api/v4</c>,
    /// or a self-managed instance's), a personal, project or group access token in GitLab's <c>PRIVATE-TOKEN</c> header. A test asks
    /// GitLab who the token is (<c>user</c>).
    /// </summary>
    /// <remarks>
    /// Every operation names its project - its path (<c>group/project</c>, subgroups too), its id or its address - held to GitLab's
    /// shapes before it reaches the path, as are an issue's number (its iid), a branch, a tag and a file's path (each segment checked,
    /// no empty, <c>.</c> or <c>..</c> one). <c>issues</c>: a project's issues by state, labels, an assignee, a search and a change
    /// since a time, newest change first, page by page by GitLab's <c>X-Next-Page</c> up to <c>max</c> (1,000); one record each: its
    /// iid, title, state, address, author, labels, assignees, description, due date and times. <c>get_issue</c> one.
    /// <c>create_issue</c>: a title, a description (Markdown), labels, assignees (usernames, each looked up), a due date and
    /// confidential or not; <c>update_issue</c> changes those given, closes or reopens, adds or removes labels (something, or
    /// refused). <c>comment</c>: a note on an issue. <c>merge_requests</c>: by state, target branch and labels. <c>release</c>: a
    /// tag's release, its name and notes (the tag made from a ref when it does not exist yet). <c>file</c>: a text file's content at
    /// a ref (one that is not UTF-8 text refused). <c>commit_file</c>: a text file written on a branch with a commit message -
    /// created, or updated when it is there. <c>pipeline</c>: a pipeline run on a ref, its variables a JSON object. A throttled call
    /// waits as GitLab says; a failure is thrown with its status and GitLab's message.
    /// </remarks>
    public sealed class GitLabConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://gitlab.com";

        public const string IssuesOperation = "issues";

        public const string GetIssueOperation = "get_issue";

        public const string CreateIssueOperation = "create_issue";

        public const string UpdateIssueOperation = "update_issue";

        public const string CommentOperation = "comment";

        public const string MergeRequestsOperation = "merge_requests";

        public const string ReleaseOperation = "release";

        public const string FileOperation = "file";

        public const string CommitFileOperation = "commit_file";

        public const string PipelineOperation = "pipeline";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex ProjectPath = new("^[A-Za-z0-9_.][A-Za-z0-9_.-]*(/[A-Za-z0-9_.][A-Za-z0-9_.-]*){1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex Digits = new("^[0-9]{1,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Username = new("^[A-Za-z0-9_.][A-Za-z0-9_.-]{0,254}$", RegexOptions.CultureInvariant);

        private static readonly Regex RefName = new("^(?!/)(?!.*//)(?!.*\\.\\.)[A-Za-z0-9._/-]{1,255}(?<!/)(?<!\\.lock)$", RegexOptions.CultureInvariant);

        private static readonly Regex VariableKey = new("^[A-Za-z0-9_]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> IssueStates = new(StringComparer.Ordinal) { "opened", "closed", "all" };

        private static readonly HashSet<string> MergeRequestStates = new(StringComparer.Ordinal) { "opened", "closed", "merged", "locked", "all" };

        public string ConnectorType => "gitlab";

        public string DisplayName => "GitLab";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The token as GitLab names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Access token (glpat-... - personal, project or group)" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://gitlab.com");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.gitlab.com/api/rest/");

        private static readonly ConnectorConfigField ProjectField = new("project", "Project", ConnectorConfigFieldType.Text, Required: true, Description: "Its path - group/project - its id, or its address.");

        private static readonly ConnectorConfigField IssueField = new("issue", "Issue", ConnectorConfigFieldType.Integer, Required: true, Description: "The issue's number in the project (#12).");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] IssueInput(bool create) => new[]
        {
            new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown."),
            new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: create ? "Comma-separated." : "Comma-separated - they replace the issue's."),
            new ConnectorConfigField("assignees", "Assignees", ConnectorConfigFieldType.Text, Required: false, Description: "Usernames or user ids, comma-separated."),
            new ConnectorConfigField("due_date", "Due date", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31"),
            new ConnectorConfigField("confidential", "Confidential", ConnectorConfigFieldType.Text, Required: false, Description: "true or false."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(IssuesOperation, "Get issues", "A project's issues - one record each: its number, title, state, page, author, labels and assignees - n8n's repository Get Issues.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ProjectField,
                    new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "opened, closed or all.") { Default = "opened" },
                    new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Only those with every one of these, comma-separated."),
                    new ConnectorConfigField("assignee", "Assignee", ConnectorConfigFieldType.Text, Required: false, Description: "A username."),
                    new ConnectorConfigField("search", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words in a title or description."),
                    new ConnectorConfigField("updated_since", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-01T00:00:00Z."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetIssueOperation, "Get an issue", "One issue - n8n's issue Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ProjectField, IssueField }),
            },
            new ConnectorOperation(CreateIssueOperation, "Create an issue", "Opens an issue - n8n's issue Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ProjectField }.Concat(IssueInput(create: true)).ToArray()),
            },
            new ConnectorOperation(UpdateIssueOperation, "Update an issue", "Changes an issue - its fields, closed or reopened, labels added or removed - n8n's issue Edit.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ProjectField, IssueField }.Concat(IssueInput(create: false)).Concat(new[]
                {
                    new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "close or reopen."),
                    new ConnectorConfigField("add_labels", "Add labels", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
                    new ConnectorConfigField("remove_labels", "Remove labels", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
                }).ToArray()),
            },
            new ConnectorOperation(CommentOperation, "Comment on an issue", "Adds a note to an issue - n8n's issue Create Comment.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ProjectField, IssueField, new ConnectorConfigField("body", "Comment", ConnectorConfigFieldType.Multiline, Required: true, Description: "Markdown.") }),
            },
            new ConnectorOperation(MergeRequestsOperation, "Get merge requests", "A project's merge requests - one record each: its number, title, state, branches, author and page.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ProjectField,
                    new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "opened, closed, merged, locked or all.") { Default = "opened" },
                    new ConnectorConfigField("target_branch", "Target branch", ConnectorConfigFieldType.Text, Required: false, Description: "Only those into this branch."),
                    new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Only those with every one of these, comma-separated."),
                    MaxField,
                }),
            },
            new ConnectorOperation(ReleaseOperation, "Create a release", "Publishes a release of a tag - n8n's release Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ProjectField,
                    new ConnectorConfigField("tag", "Tag", ConnectorConfigFieldType.Text, Required: true, Description: "v1.2.3 - made from Ref when it does not exist yet."),
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Empty: the tag."),
                    new ConnectorConfigField("description", "Notes", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown."),
                    new ConnectorConfigField("ref", "Ref", ConnectorConfigFieldType.Text, Required: false, Description: "A branch or commit the tag is made from."),
                }),
            },
            new ConnectorOperation(FileOperation, "Get a file", "A text file's content - its path, size, last commit and content - n8n's file Get.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ProjectField,
                    new ConnectorConfigField("path", "Path", ConnectorConfigFieldType.Text, Required: true, Description: "docs/README.md"),
                    new ConnectorConfigField("ref", "Ref", ConnectorConfigFieldType.Text, Required: false, Description: "A branch, tag or commit.") { Default = "main" },
                }),
            },
            new ConnectorOperation(CommitFileOperation, "Commit a file", "Writes a text file on a branch - created, or updated when it is there - n8n's file Create and Edit.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ProjectField,
                    new ConnectorConfigField("path", "Path", ConnectorConfigFieldType.Text, Required: true, Description: "docs/README.md"),
                    new ConnectorConfigField("branch", "Branch", ConnectorConfigFieldType.Text, Required: true, Description: "main"),
                    new ConnectorConfigField("content", "Content", ConnectorConfigFieldType.Multiline, Required: true, Description: "The file's text."),
                    new ConnectorConfigField("message", "Commit message", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                }),
            },
            new ConnectorOperation(PipelineOperation, "Run a pipeline", "Starts a CI/CD pipeline on a ref - its id, status and page.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ProjectField,
                    new ConnectorConfigField("ref", "Ref", ConnectorConfigFieldType.Text, Required: true, Description: "A branch or tag - main."),
                    new ConnectorConfigField("variables", "Variables", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - {\"DEPLOY\": \"true\"}."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var (user, _) = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, api + "/user", null, cancellationToken);
                return Text(user?["username"]) is { } username
                    ? ConnectionTestResult.Ok($"Signed in as {username}{(Text(user?["name"]) is { Length: > 0 } name ? $" ({name})" : "")}.")
                    : ConnectionTestResult.Fail("GitLab answered without a user - check the address and the token.");
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
            var api = Api(context.Endpoint);
            var token = Token(context.Credentials);
            var operation = context.OperationName ?? IssuesOperation;
            var parameters = context.Parameters;
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
            if (!new[] { IssuesOperation, GetIssueOperation, CreateIssueOperation, UpdateIssueOperation, CommentOperation, MergeRequestsOperation, ReleaseOperation, FileOperation, CommitFileOperation, PipelineOperation }.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"GitLab has no operation '{operation}' - issues, get_issue, create_issue, update_issue, comment, merge_requests, release, file, commit_file or pipeline.");
            }

            // What could be refused is refused before anything is sent: shapes first; usernames are looked up after.
            var project = $"{api}/projects/{Project(Values.Get(parameters, "project"))}";
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (Is(operation, IssuesOperation) || Is(operation, MergeRequestsOperation))
            {
                var issues = Is(operation, IssuesOperation);
                var query = new List<string>
                {
                    "state=" + Word(Values.Get(parameters, "state") ?? "opened", issues ? IssueStates : MergeRequestStates, "state"),
                };
                if (Values.Get(parameters, "labels") is { } labels)
                {
                    query.Add("labels=" + Uri.EscapeDataString(string.Join(",", List(labels, "label"))));
                }

                if (issues && Values.Get(parameters, "assignee") is { } assignee)
                {
                    query.Add("assignee_username=" + User(assignee));
                }

                if (issues && Values.Get(parameters, "search") is { } search)
                {
                    query.Add("search=" + Uri.EscapeDataString(OneLine(search, "search")));
                }

                if (issues && Values.Get(parameters, "updated_since") is { } since)
                {
                    query.Add("updated_after=" + Uri.EscapeDataString(Time(since)));
                }

                if (!issues && Values.Get(parameters, "target_branch") is { } target)
                {
                    query.Add("target_branch=" + Uri.EscapeDataString(Ref(target, "branch")));
                }

                query.Add("order_by=updated_at");
                query.Add("sort=desc");
                var url = $"{project}/{(issues ? "issues" : "merge_requests")}?{string.Join("&", query)}";
                var read = 0;
                for (var page = "1"; page is not null;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(PageSize, max - read);
                    var (items, next) = await SendAsync(client, token, HttpMethod.Get, $"{url}&per_page={size.ToString(CultureInfo.InvariantCulture)}&page={page}", null, cancellationToken);
                    var found = (items as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in found)
                    {
                        read++;
                        yield return issues ? Issue(item) : MergeRequest(item);
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    page = found.Count < size || next is null || !Digits.IsMatch(next) ? null : next;
                }

                yield break;
            }

            if (Is(operation, GetIssueOperation) || Is(operation, CommentOperation))
            {
                var issue = Number(Values.Get(parameters, "issue"));
                if (Is(operation, GetIssueOperation))
                {
                    var (found, _) = await SendAsync(client, token, HttpMethod.Get, $"{project}/issues/{issue}", null, cancellationToken);
                    yield return Issue(found as JsonObject ?? new JsonObject());
                    yield break;
                }

                var body = Values.Get(parameters, "body") ?? throw new InvalidOperationException("Give the comment.");
                var (note, _) = await SendAsync(client, token, HttpMethod.Post, $"{project}/issues/{issue}/notes", new JsonObject { ["body"] = body }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Long(note?["id"]), ["issue"] = long.Parse(issue, CultureInfo.InvariantCulture), ["body"] = Text(note?["body"]), ["author"] = Text(note?["author"]?["username"]), ["created_at"] = Text(note?["created_at"]),
                });
                yield break;
            }

            if (Is(operation, CreateIssueOperation) || Is(operation, UpdateIssueOperation))
            {
                var create = Is(operation, CreateIssueOperation);
                var issue = create ? null : Number(Values.Get(parameters, "issue"));
                var input = new JsonObject();
                if (create || Values.Get(parameters, "title") is not null)
                {
                    input["title"] = OneLine(Values.Get(parameters, "title") ?? throw new InvalidOperationException("Give the issue's title."), "title");
                }

                if (Values.Get(parameters, "description") is { } description)
                {
                    input["description"] = description;
                }

                foreach (var name in new[] { "labels", "add_labels", "remove_labels" })
                {
                    if ((create && name != "labels") || Values.Get(parameters, name) is not { } given)
                    {
                        continue;
                    }

                    input[name] = string.Join(",", List(given, "label"));
                }

                if (Values.Get(parameters, "due_date") is { } due)
                {
                    input["due_date"] = DateOnly.TryParseExact(due.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{due}' is not a due date - 2026-10-31.");
                }

                if (Values.Get(parameters, "confidential") is { } confidential)
                {
                    input["confidential"] = Flag(confidential, "confidential");
                }

                if (!create && Values.Get(parameters, "state") is { } state)
                {
                    input["state_event"] = Word(state, new HashSet<string>(StringComparer.Ordinal) { "close", "reopen" }, "state change");
                }

                // Assignees by username - each looked up - or by id, as n8n gives them.
            var assignees = Values.Get(parameters, "assignees") is { } names ? List(names, "assignee").Select(name => Digits.IsMatch(name) ? name : User(name)).ToList() : null;
                if (!create && input.Count == 0 && assignees is null)
                {
                    throw new InvalidOperationException("Give something to change - a title, a description, labels, assignees, a due date, confidential or a state.");
                }

                if (assignees is not null)
                {
                    var ids = new JsonArray();
                    foreach (var username in assignees)
                    {
                        if (Digits.IsMatch(username))
                        {
                            ids.Add(long.Parse(username, CultureInfo.InvariantCulture));
                            continue;
                        }

                        var (users, _) = await SendAsync(client, token, HttpMethod.Get, $"{api}/users?username={Uri.EscapeDataString(username)}", null, cancellationToken);
                        ids.Add(Long((users as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["id"]) ?? throw new InvalidOperationException($"GitLab has no user {username}."));
                    }

                    input["assignee_ids"] = ids;
                }

                var (made, _) = await SendAsync(client, token, create ? HttpMethod.Post : HttpMethod.Put, create ? $"{project}/issues" : $"{project}/issues/{issue}", input, cancellationToken);
                yield return Issue(made as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, ReleaseOperation))
            {
                var tag = Ref(Values.Get(parameters, "tag") ?? throw new InvalidOperationException("Give the release's tag."), "tag");
                var input = new JsonObject { ["tag_name"] = tag, ["name"] = Values.Get(parameters, "name") is { } name ? OneLine(name, "name") : tag };
                if (Values.Get(parameters, "description") is { } notes)
                {
                    input["description"] = notes;
                }

                if (Values.Get(parameters, "ref") is { } from)
                {
                    input["ref"] = Ref(from, "ref");
                }

                var (release, _) = await SendAsync(client, token, HttpMethod.Post, $"{project}/releases", input, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tag"] = Text(release?["tag_name"]), ["name"] = Text(release?["name"]), ["url"] = Text(release?["_links"]?["self"]), ["commit"] = Text(release?["commit"]?["id"]), ["created_at"] = Text(release?["created_at"]),
                });
                yield break;
            }

            if (Is(operation, FileOperation) || Is(operation, CommitFileOperation))
            {
                var path = FilePath(Values.Get(parameters, "path"));
                var file = $"{project}/repository/files/{Uri.EscapeDataString(path)}";
                if (Is(operation, FileOperation))
                {
                    var at = Ref(Values.Get(parameters, "ref") ?? "main", "ref");
                    var (found, _) = await SendAsync(client, token, HttpMethod.Get, $"{file}?ref={Uri.EscapeDataString(at)}", null, cancellationToken);
                    string content;
                    try
                    {
                        content = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(Text(found?["content"]) ?? ""));
                    }
                    catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
                    {
                        throw new InvalidOperationException($"{path} is not a UTF-8 text file.");
                    }

                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["path"] = Text(found?["file_path"]), ["ref"] = Text(found?["ref"]), ["size"] = Long(found?["size"]), ["last_commit"] = Text(found?["last_commit_id"]), ["content"] = content,
                    });
                    yield break;
                }

                var branch = Ref(Values.Get(parameters, "branch") ?? throw new InvalidOperationException("Give the branch to commit on."), "branch");
                var text = Values.Get(parameters, "content") ?? throw new InvalidOperationException("Give the file's content.");
                var message = OneLine(Values.Get(parameters, "message") ?? throw new InvalidOperationException("Give the commit message."), "commit message");
                var exists = await ExistsAsync(client, token, $"{file}?ref={Uri.EscapeDataString(branch)}", cancellationToken);
                var input = new JsonObject { ["branch"] = branch, ["content"] = text, ["commit_message"] = message, ["encoding"] = "text" };
                var (written, _) = await SendAsync(client, token, exists ? HttpMethod.Put : HttpMethod.Post, file, input, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = Text(written?["file_path"]), ["branch"] = Text(written?["branch"]), ["created"] = !exists });
                yield break;
            }

            // A pipeline on a ref, its variables as GitLab's key and value pairs.
            var pipelineRef = Ref(Values.Get(parameters, "ref") ?? throw new InvalidOperationException("Give the ref to run on - a branch or a tag."), "ref");
            var run = new JsonObject { ["ref"] = pipelineRef };
            if (Values.Get(parameters, "variables") is { } variables)
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(variables);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("The variables are not JSON - {\"DEPLOY\": \"true\"}.");
                }

                if (parsed is not JsonObject pairs)
                {
                    throw new InvalidOperationException("The variables are a JSON object - {\"DEPLOY\": \"true\"}.");
                }

                var list = new JsonArray();
                foreach (var (name, value) in pairs)
                {
                    list.Add(new JsonObject
                    {
                        ["key"] = VariableKey.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a variable's name (letters, digits and _)."),
                        ["value"] = value is JsonValue scalar && scalar.TryGetValue<string>(out var words) ? words : value?.ToJsonString() ?? "",
                    });
                }

                run["variables"] = list;
            }

            var (pipeline, _) = await SendAsync(client, token, HttpMethod.Post, $"{project}/pipeline", run, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Long(pipeline?["id"]), ["status"] = Text(pipeline?["status"]), ["ref"] = Text(pipeline?["ref"]), ["sha"] = Text(pipeline?["sha"]), ["url"] = Text(pipeline?["web_url"]),
            });
        }

        private static ConnectorRecord Issue(JsonObject issue) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Long(issue["id"]),
            ["iid"] = Long(issue["iid"]),
            ["title"] = Text(issue["title"]),
            ["state"] = Text(issue["state"]),
            ["url"] = Text(issue["web_url"]),
            ["author"] = Text(issue["author"]?["username"]),
            ["labels"] = issue["labels"] is JsonArray labels ? string.Join(", ", labels.Select(Text).Where(label => label is not null)) : null,
            ["assignees"] = issue["assignees"] is JsonArray assignees ? string.Join(", ", assignees.OfType<JsonObject>().Select(user => Text(user["username"])).Where(name => name is not null)) : null,
            ["description"] = Text(issue["description"]),
            ["due_date"] = Text(issue["due_date"]),
            ["confidential"] = issue["confidential"] is JsonValue secret && secret.TryGetValue<bool>(out var hidden) && hidden,
            ["created_at"] = Text(issue["created_at"]),
            ["updated_at"] = Text(issue["updated_at"]),
            ["closed_at"] = Text(issue["closed_at"]),
        });

        private static ConnectorRecord MergeRequest(JsonObject request) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Long(request["id"]),
            ["iid"] = Long(request["iid"]),
            ["title"] = Text(request["title"]),
            ["state"] = Text(request["state"]),
            ["url"] = Text(request["web_url"]),
            ["author"] = Text(request["author"]?["username"]),
            ["source_branch"] = Text(request["source_branch"]),
            ["target_branch"] = Text(request["target_branch"]),
            ["labels"] = request["labels"] is JsonArray labels ? string.Join(", ", labels.Select(Text).Where(label => label is not null)) : null,
            ["draft"] = request["draft"] is JsonValue draft && draft.TryGetValue<bool>(out var isDraft) && isDraft,
            ["merged_at"] = Text(request["merged_at"]),
            ["created_at"] = Text(request["created_at"]),
            ["updated_at"] = Text(request["updated_at"]),
        });

        // GitLab's REST API: the instance's address (a project's or any page's address cut back to it), /api/v4.
        private static string Api(string endpoint) => Instance(endpoint) + "/api/v4";

        private static string Instance(string? endpoint)
        {
            var given = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a GitLab instance's address - https://gitlab.com.");
            }

            return address.GetLeftPart(UriPartial.Authority);
        }

        // A project as the path takes it: an id, or its path URL-encoded - from group/project or from its address.
        private static string Project(string? given)
        {
            var project = given?.Trim() ?? throw new InvalidOperationException("Give the project - group/project, its id or its address.");
            if (Digits.IsMatch(project))
            {
                return project;
            }

            if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(project, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
            {
                var path = address.AbsolutePath.Trim('/');
                var cut = path.IndexOf("/-/", StringComparison.Ordinal);
                project = (cut >= 0 ? path[..cut] : path).TrimEnd('/');
                if (project.EndsWith(".git", StringComparison.Ordinal))
                {
                    project = project[..^4];
                }
            }

            return ProjectPath.IsMatch(project) && !project.Split('/').Any(segment => segment is "." or "..")
                ? Uri.EscapeDataString(project)
                : throw new InvalidOperationException($"'{given}' is not a GitLab project - group/project, its id or its address.");
        }

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The GitLab connection has no token.");

        private static string Number(string? given) =>
            given is not null && Digits.IsMatch(given.Trim().TrimStart('#')) ? given.Trim().TrimStart('#') : throw new InvalidOperationException($"'{given}' is not an issue's number (#12).");

        private static string User(string given) =>
            Username.IsMatch(given.Trim().TrimStart('@')) ? given.Trim().TrimStart('@') : throw new InvalidOperationException($"'{given}' is not a GitLab username.");

        private static string Ref(string given, string what) =>
            RefName.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a {what}'s name.");

        private static string FilePath(string? given)
        {
            var path = given?.Trim().TrimStart('/') ?? throw new InvalidOperationException("Give the file's path - docs/README.md.");
            return path.Length > 0 && path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") && !segment.Any(char.IsControl))
                ? path
                : throw new InvalidOperationException($"'{given}' is not a file's path - docs/README.md.");
        }

        private static List<string> List(string given, string what) =>
            given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => OneLine(item, what)).ToList();

        private static string Word(string given, HashSet<string> words, string what) =>
            words.Contains(given.Trim().ToLowerInvariant()) ? given.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not a {what} - {string.Join(", ", words)}.");

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        private static string Time(string given) =>
            DateTimeOffset.TryParse(given.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-01T00:00:00Z.");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Whether GitLab has the file on the branch: 200 yes, 404 no, anything else its refusal.
        private static async Task<bool> ExistsAsync(HttpClient client, string token, string url, CancellationToken cancellationToken)
        {
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.Add("PRIVATE-TOKEN", token);
                return request;
            }, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => true,
                HttpStatusCode.NotFound => false,
                var status => throw new InvalidOperationException($"HTTP {(int)status}."),
            };
        }

        // GitLab's answer as JSON with its next page; a throttled call waits as GitLab says; a failure thrown with its status and
        // GitLab's message - a field's errors named.
        private static async Task<(JsonNode? Json, string? NextPage)> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Add("PRIVATE-TOKEN", token);
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
                throw new InvalidOperationException($"GitLab answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = json?["message"] switch
                {
                    JsonValue text when text.TryGetValue<string>(out var words) => words,
                    JsonObject fields => string.Join("; ", fields.Select(field => field.Key + " " + (field.Value is JsonArray list ? string.Join(", ", list.Select(Text)) : Text(field.Value)))),
                    JsonArray list => string.Join("; ", list.Select(Text)),
                    _ => Text(json?["error_description"]) ?? Text(json?["error"]),
                };
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(message) ? "." : ": " + (message.Length > 300 ? message[..300] : message))}");
            }

            var next = response.Headers.TryGetValues("X-Next-Page", out var pages) ? pages.FirstOrDefault() : null;
            return (json, string.IsNullOrWhiteSpace(next) ? null : next.Trim());
        }
    }
}
