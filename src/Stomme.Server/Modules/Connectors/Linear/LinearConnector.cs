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

namespace Stomme.Modules.Connectors.Linear
{
    /// <summary>
    /// Linear (BL-229, n8n's Linear node: issues found, got, created, updated and deleted, a comment and a link added): Linear's
    /// GraphQL API (<c>https://api.linear.app/graphql</c>), a personal API key as Linear takes it (the key alone) or an OAuth
    /// token as a bearer. A test names the key's user.
    /// </summary>
    /// <remarks>
    /// An issue is named by its identifier (ENG-123) or its id; a team by its key (ENG) or its id, a workflow state by its name in
    /// the issue's team, an assignee by an address (or <c>me</c>), labels by their names - each looked up in Linear and refused when
    /// none is found, so a pipeline speaks as a person would. <c>issues</c>: the issues a team, a state, an assignee, a label, a
    /// priority and a change since a time keep, newest change first, page by page by Linear's cursor up to <c>max</c> (1,000); one
    /// record each: its identifier, title, description, address, priority, state, team, assignee, labels, estimate, due date and
    /// times. <c>get_issue</c> one. <c>create_issue</c>: a team and a title, a description (Markdown), an assignee, a priority
    /// (0 none, 1 urgent to 4 low), a state, labels, an estimate and a due date; <c>update_issue</c> changes those given (something,
    /// or refused); <c>delete_issue</c> moves one to the trash. <c>add_comment</c> comments on an issue (Markdown);
    /// <c>add_link</c> attaches a link. <c>teams</c>: the workspace's teams. A failure is thrown with Linear's message, its code
    /// beside it.
    /// </remarks>
    public sealed class LinearConnector : IConnector, IConnectorExtractor
    {
        public const string IssuesOperation = "issues";

        public const string GetIssueOperation = "get_issue";

        public const string CreateIssueOperation = "create_issue";

        public const string UpdateIssueOperation = "update_issue";

        public const string DeleteIssueOperation = "delete_issue";

        public const string AddCommentOperation = "add_comment";

        public const string AddLinkOperation = "add_link";

        public const string TeamsOperation = "teams";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private const string IssueFields = "fragment IssueFields on Issue { id identifier title description url priority priorityLabel estimate dueDate createdAt updatedAt completedAt state { name } team { key } assignee { email } labels { nodes { name } } }";

        private static readonly Regex IssueId = new("^([A-Za-z][A-Za-z0-9]{0,9}-[0-9]{1,9}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$", RegexOptions.CultureInvariant);

        private static readonly Regex Uuid = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        private static readonly Regex TeamKey = new("^[A-Za-z][A-Za-z0-9]{0,9}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "linear";

        public string DisplayName => "Linear";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        /// <summary>The key as Linear names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API key (lin_api_...) or an OAuth token" };

        public string EndpointExample => "https://api.linear.app";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://linear.app");

        public Uri ApiDocumentationUrl { get; } = new("https://linear.app/developers/graphql");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField IssueField = new("issue", "Issue", ConnectorConfigFieldType.Text, Required: true, Description: "Its identifier - ENG-123 - or its id.");

        private static ConnectorConfigField[] IssueInput(bool create) => new[]
        {
            new ConnectorConfigField("team", "Team", ConnectorConfigFieldType.Text, Required: create, Description: create ? "Its key - ENG - or its id." : "Moves it to this team - its key or its id."),
            new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown."),
            new ConnectorConfigField("assignee", "Assignee", ConnectorConfigFieldType.Text, Required: false, Description: "An address, me, or a user's id."),
            new ConnectorConfigField("priority", "Priority", ConnectorConfigFieldType.Integer, Required: false, Description: "0 none, 1 urgent, 2 high, 3 medium, 4 low."),
            new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "Its name in the team - Todo, In Progress, Done - or its id."),
            new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: create ? "Their names or ids, comma-separated." : "Their names or ids, comma-separated - replacing the issue's."),
            new ConnectorConfigField("estimate", "Estimate", ConnectorConfigFieldType.Integer, Required: false, Description: "Points."),
            new ConnectorConfigField("due_date", "Due date", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31"),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(IssuesOperation, "Get issues", "The issues a team, a state, an assignee and a label keep - one record each: its identifier, title, state and assignee - n8n's issue Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("team", "Team", ConnectorConfigFieldType.Text, Required: false, Description: "Only this team's - its key, ENG."),
                    new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "Only in this state - its name, In Progress."),
                    new ConnectorConfigField("assignee", "Assignee", ConnectorConfigFieldType.Text, Required: false, Description: "Only this person's - an address, or me."),
                    new ConnectorConfigField("label", "Label", ConnectorConfigFieldType.Text, Required: false, Description: "Only those with this label - its name."),
                    new ConnectorConfigField("priority", "Priority", ConnectorConfigFieldType.Integer, Required: false, Description: "Only this priority - 1 urgent to 4 low."),
                    new ConnectorConfigField("updated_since", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-01T00:00:00Z."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetIssueOperation, "Get an issue", "One issue - n8n's issue Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IssueField }),
            },
            new ConnectorOperation(CreateIssueOperation, "Create an issue", "Adds an issue - n8n's issue Create.")
            {
                Parameters = new ConnectorConfigSchema(IssueInput(create: true)),
            },
            new ConnectorOperation(UpdateIssueOperation, "Update an issue", "Changes an issue - n8n's issue Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IssueField }.Concat(IssueInput(create: false)).ToArray()),
            },
            new ConnectorOperation(DeleteIssueOperation, "Delete an issue", "Moves an issue to the trash - n8n's issue Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IssueField }),
            },
            new ConnectorOperation(AddCommentOperation, "Comment on an issue", "Adds a comment - n8n's comment Add.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IssueField, new ConnectorConfigField("body", "Comment", ConnectorConfigFieldType.Multiline, Required: true, Description: "Markdown.") }),
            },
            new ConnectorOperation(AddLinkOperation, "Link to an issue", "Attaches a link - n8n's issue Add Link.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    IssueField,
                    new ConnectorConfigField("url", "Address", ConnectorConfigFieldType.Text, Required: true, Description: "https://..."),
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: false, Description: "Empty: the address."),
                }),
            },
            new ConnectorOperation(TeamsOperation, "Get teams", "The workspace's teams - their keys, names and ids.")
            {
                Parameters = new ConnectorConfigSchema(Array.Empty<ConnectorConfigField>()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var data = await QueryAsync(client, Key(context.Credentials), api, "query { viewer { name email } }", new JsonObject(), cancellationToken);
                return Text(data?["viewer"]?["email"]) is { } email
                    ? ConnectionTestResult.Ok($"Signed in as {Text(data?["viewer"]?["name"]) ?? email} ({email}).")
                    : ConnectionTestResult.Fail("Linear answered without a user - check the API key.");
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
            var key = Key(context.Credentials);
            var operation = context.OperationName ?? IssuesOperation;
            var parameters = context.Parameters;
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;

            // What could be refused is refused before anything is sent: shapes first; names are looked up after.
            if (!new[] { IssuesOperation, GetIssueOperation, CreateIssueOperation, UpdateIssueOperation, DeleteIssueOperation, AddCommentOperation, AddLinkOperation, TeamsOperation }.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Linear has no operation '{operation}' - issues, get_issue, create_issue, update_issue, delete_issue, add_comment, add_link or teams.");
            }

            var issue = Is(operation, IssuesOperation) || Is(operation, CreateIssueOperation) || Is(operation, TeamsOperation) ? null : Issue(Values.Get(parameters, "issue"));
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (Is(operation, TeamsOperation))
            {
                var data = await QueryAsync(client, key, api, "query { teams(first: 250) { nodes { id key name } } }", new JsonObject(), cancellationToken);
                foreach (var team in (data?["teams"]?["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(team["id"]), ["key"] = Text(team["key"]), ["name"] = Text(team["name"]) });
                }

                yield break;
            }

            if (Is(operation, IssuesOperation))
            {
                var filter = new JsonObject();
                if (Values.Get(parameters, "team") is { } team)
                {
                    filter["team"] = TeamKey.IsMatch(team) ? Eq("key", team.ToUpperInvariant()) : Uuid.IsMatch(team) ? Eq("id", team) : throw new InvalidOperationException($"'{team}' is not a team's key (ENG) or id.");
                }

                if (Values.Get(parameters, "state") is { } state)
                {
                    filter["state"] = Uuid.IsMatch(state) ? Eq("id", state) : Eq("name", OneLine(state, "state"));
                }

                if (Values.Get(parameters, "assignee") is { } assignee)
                {
                    filter["assignee"] = assignee.Equals("me", StringComparison.OrdinalIgnoreCase) ? new JsonObject { ["isMe"] = new JsonObject { ["eq"] = true } } : Uuid.IsMatch(assignee) ? Eq("id", assignee) : Eq("email", Email(assignee));
                }

                if (Values.Get(parameters, "label") is { } label)
                {
                    filter["labels"] = new JsonObject { ["some"] = Eq("name", OneLine(label, "label")) };
                }

                if (Values.Get(parameters, "priority") is { } priority)
                {
                    filter["priority"] = new JsonObject { ["eq"] = Priority(priority) };
                }

                if (Values.Get(parameters, "updated_since") is { } since)
                {
                    filter["updatedAt"] = new JsonObject { ["gt"] = Time(since) };
                }

                var read = 0;
                string? after = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var variables = new JsonObject { ["first"] = Math.Min(PageSize, max - read), ["after"] = after, ["filter"] = filter.DeepClone() };
                    var data = await QueryAsync(client, key, api, "query Issues($first: Int!, $after: String, $filter: IssueFilter) { issues(first: $first, after: $after, filter: $filter, orderBy: updatedAt) { nodes { ...IssueFields } pageInfo { hasNextPage endCursor } } } " + IssueFields, variables, cancellationToken);
                    var nodes = (data?["issues"]?["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var node in nodes)
                    {
                        read++;
                        yield return Record(node);
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    var more = data?["issues"]?["pageInfo"]?["hasNextPage"] is JsonValue next && next.TryGetValue<bool>(out var hasNext) && hasNext;
                    after = Text(data?["issues"]?["pageInfo"]?["endCursor"]);
                    if (!more || nodes.Count == 0 || after is null)
                    {
                        yield break;
                    }
                }
            }

            if (Is(operation, GetIssueOperation))
            {
                var data = await QueryAsync(client, key, api, "query Issue($id: String!) { issue(id: $id) { ...IssueFields } } " + IssueFields, new JsonObject { ["id"] = issue }, cancellationToken);
                yield return Record(data?["issue"] as JsonObject ?? throw new InvalidOperationException($"Linear has no issue {issue}."));
                yield break;
            }

            if (Is(operation, DeleteIssueOperation))
            {
                var data = await QueryAsync(client, key, api, "mutation IssueDelete($id: String!) { issueDelete(id: $id) { success } }", new JsonObject { ["id"] = issue }, cancellationToken);
                Succeeded(data, "issueDelete");
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["issue"] = issue, ["deleted"] = true });
                yield break;
            }

            if (Is(operation, AddCommentOperation))
            {
                var body = Values.Get(parameters, "body") ?? throw new InvalidOperationException("Give the comment.");
                var data = await QueryAsync(client, key, api, "mutation CommentCreate($input: CommentCreateInput!) { commentCreate(input: $input) { success comment { id body url createdAt } } }",
                    new JsonObject { ["input"] = new JsonObject { ["issueId"] = issue, ["body"] = body } }, cancellationToken);
                var comment = Succeeded(data, "commentCreate")?["comment"];
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(comment?["id"]), ["issue"] = issue, ["body"] = Text(comment?["body"]), ["url"] = Text(comment?["url"]), ["created_at"] = Text(comment?["createdAt"]) });
                yield break;
            }

            if (Is(operation, AddLinkOperation))
            {
                var url = Values.Get(parameters, "url") is { } given && Uri.TryCreate(given, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
                    ? given : throw new InvalidOperationException($"'{Values.Get(parameters, "url")}' is not a link's address - https://...");
                var data = await QueryAsync(client, key, api, "mutation AttachmentLinkUrl($issueId: String!, $url: String!, $title: String) { attachmentLinkURL(issueId: $issueId, url: $url, title: $title) { success attachment { id url title } } }",
                    new JsonObject { ["issueId"] = issue, ["url"] = url, ["title"] = Values.Get(parameters, "title") is { } title ? OneLine(title, "title") : null }, cancellationToken);
                var attachment = Succeeded(data, "attachmentLinkURL")?["attachment"];
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(attachment?["id"]), ["issue"] = issue, ["url"] = Text(attachment?["url"]), ["title"] = Text(attachment?["title"]) });
                yield break;
            }

            // Create and update: the input's shapes checked first, then the names it gives looked up in Linear.
            var create = Is(operation, CreateIssueOperation);
            var input = new JsonObject();
            var teamGiven = Values.Get(parameters, "team");
            if (create && teamGiven is null)
            {
                throw new InvalidOperationException("Give the team - its key, ENG, or its id.");
            }

            if (teamGiven is not null && !TeamKey.IsMatch(teamGiven) && !Uuid.IsMatch(teamGiven))
            {
                throw new InvalidOperationException($"'{teamGiven}' is not a team's key (ENG) or id.");
            }

            if (create || Values.Get(parameters, "title") is not null)
            {
                input["title"] = OneLine(Values.Get(parameters, "title") ?? throw new InvalidOperationException("Give the issue's title."), "title");
            }

            if (Values.Get(parameters, "description") is { } description)
            {
                input["description"] = description;
            }

            if (Values.Get(parameters, "priority") is { } chosen)
            {
                input["priority"] = Priority(chosen);
            }

            if (Values.Get(parameters, "estimate") is { } estimate)
            {
                input["estimate"] = int.TryParse(estimate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var points) && points >= 0 ? points : throw new InvalidOperationException($"'{estimate}' is not an estimate - a whole number of points.");
            }

            if (Values.Get(parameters, "due_date") is { } due)
            {
                input["dueDate"] = DateOnly.TryParseExact(due.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{due}' is not a due date - 2026-10-31.");
            }

            var assigneeGiven = Values.Get(parameters, "assignee") is { } person ? person.Equals("me", StringComparison.OrdinalIgnoreCase) ? "me" : Uuid.IsMatch(person) ? person : Email(person) : null;
            var stateGiven = Values.Get(parameters, "state") is { } named ? OneLine(named, "state") : null;
            var labelsGiven = Values.Get(parameters, "labels") is { } names ? names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(name => OneLine(name, "label")).ToList() : null;
            if (!create && input.Count == 0 && teamGiven is null && assigneeGiven is null && stateGiven is null && labelsGiven is null)
            {
                throw new InvalidOperationException("Give something to change - a title, a description, an assignee, a priority, a state, labels, an estimate, a due date or a team.");
            }

            // The team: given, or the issue's own when a state or labels need one.
            string? teamId = null;
            if (teamGiven is not null)
            {
                teamId = Uuid.IsMatch(teamGiven) ? teamGiven : await LookUpAsync(client, key, api, "query Team($key: String!) { teams(filter: { key: { eq: $key } }) { nodes { id } } }", new JsonObject { ["key"] = teamGiven.ToUpperInvariant() }, "teams", $"Linear has no team {teamGiven.ToUpperInvariant()}.", cancellationToken);
                input["teamId"] = teamId;
            }
            else if ((stateGiven is not null && !Uuid.IsMatch(stateGiven)) || (labelsGiven is not null && !labelsGiven.All(Uuid.IsMatch)))
            {
                var data = await QueryAsync(client, key, api, "query IssueTeam($id: String!) { issue(id: $id) { team { id } } }", new JsonObject { ["id"] = issue }, cancellationToken);
                teamId = Text(data?["issue"]?["team"]?["id"]) ?? throw new InvalidOperationException($"Linear has no issue {issue}.");
            }

            if (assigneeGiven is not null)
            {
                input["assigneeId"] = assigneeGiven == "me"
                    ? Text((await QueryAsync(client, key, api, "query { viewer { id } }", new JsonObject(), cancellationToken))?["viewer"]?["id"]) ?? throw new InvalidOperationException("Linear answered without a user.")
                    : Uuid.IsMatch(assigneeGiven) ? assigneeGiven : await LookUpAsync(client, key, api, "query User($email: String!) { users(filter: { email: { eq: $email } }) { nodes { id } } }", new JsonObject { ["email"] = assigneeGiven }, "users", $"Linear has no user {assigneeGiven}.", cancellationToken);
            }

            if (stateGiven is not null)
            {
                input["stateId"] = Uuid.IsMatch(stateGiven) ? stateGiven : await LookUpAsync(client, key, api, "query State($team: ID!, $name: String!) { workflowStates(filter: { team: { id: { eq: $team } }, name: { eqIgnoreCase: $name } }) { nodes { id } } }",
                    new JsonObject { ["team"] = teamId, ["name"] = stateGiven }, "workflowStates", $"The team has no state '{stateGiven}'.", cancellationToken);
            }

            if (labelsGiven is not null && labelsGiven.All(Uuid.IsMatch))
            {
                input["labelIds"] = new JsonArray(labelsGiven.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()); // ids, as n8n gives them
            }
            else if (labelsGiven is not null)
            {
                // Every label once, matched here: Linear's own 'in' is case-sensitive, a person's 'bug' is its 'Bug'.
                var data = await QueryAsync(client, key, api, "query { issueLabels(first: 250) { nodes { id name team { id } } } }", new JsonObject(), cancellationToken);
                var found = (data?["issueLabels"]?["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Where(label => label["team"] is null || Text(label["team"]?["id"]) == teamId)
                    .OrderBy(label => label["team"] is null ? 1 : 0).ToList(); // the team's own first, then the workspace's
                var ids = new JsonArray();
                foreach (var name in labelsGiven)
                {
                    var match = found.FirstOrDefault(label => string.Equals(Text(label["name"]), name, StringComparison.OrdinalIgnoreCase) || string.Equals(Text(label["id"]), name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"The team has no label '{name}'.");
                    ids.Add(Text(match["id"]));
                }

                input["labelIds"] = ids;
            }

            if (create)
            {
                var data = await QueryAsync(client, key, api, "mutation IssueCreate($input: IssueCreateInput!) { issueCreate(input: $input) { success issue { ...IssueFields } } } " + IssueFields, new JsonObject { ["input"] = input }, cancellationToken);
                yield return Record(Succeeded(data, "issueCreate")?["issue"] as JsonObject ?? new JsonObject());
            }
            else
            {
                var data = await QueryAsync(client, key, api, "mutation IssueUpdate($id: String!, $input: IssueUpdateInput!) { issueUpdate(id: $id, input: $input) { success issue { ...IssueFields } } } " + IssueFields, new JsonObject { ["id"] = issue, ["input"] = input }, cancellationToken);
                yield return Record(Succeeded(data, "issueUpdate")?["issue"] as JsonObject ?? new JsonObject());
            }
        }

        private static JsonObject Eq(string field, string value) => new() { [field] = new JsonObject { ["eq"] = value } };

        // The one id a lookup finds, or Linear's word that there is none.
        private static async Task<string> LookUpAsync(HttpClient client, string key, string api, string query, JsonObject variables, string listed, string none, CancellationToken cancellationToken)
        {
            var data = await QueryAsync(client, key, api, query, variables, cancellationToken);
            return (data?[listed]?["nodes"] as JsonArray)?.OfType<JsonObject>().Select(node => Text(node["id"])).FirstOrDefault(id => id is not null) ?? throw new InvalidOperationException(none);
        }

        private static JsonObject? Succeeded(JsonNode? data, string mutation)
        {
            var payload = data?[mutation] as JsonObject;
            return payload?["success"] is JsonValue success && success.TryGetValue<bool>(out var done) && done ? payload : throw new InvalidOperationException($"Linear did not do it ({mutation}).");
        }

        private static ConnectorRecord Record(JsonObject issue) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(issue["id"]),
            ["identifier"] = Text(issue["identifier"]),
            ["title"] = Text(issue["title"]),
            ["description"] = Text(issue["description"]),
            ["url"] = Text(issue["url"]),
            ["priority"] = issue["priority"] is JsonValue value && value.TryGetValue<double>(out var level) ? (long)level : null,
            ["priority_label"] = Text(issue["priorityLabel"]),
            ["state"] = Text(issue["state"]?["name"]),
            ["team"] = Text(issue["team"]?["key"]),
            ["assignee"] = Text(issue["assignee"]?["email"]),
            ["labels"] = issue["labels"]?["nodes"] is JsonArray labels ? string.Join(", ", labels.OfType<JsonObject>().Select(label => Text(label["name"])).Where(name => name is not null)) : null,
            ["estimate"] = issue["estimate"] is JsonValue points && points.TryGetValue<double>(out var estimate) ? estimate : null,
            ["due_date"] = Text(issue["dueDate"]),
            ["created_at"] = Text(issue["createdAt"]),
            ["updated_at"] = Text(issue["updatedAt"]),
            ["completed_at"] = Text(issue["completedAt"]),
        });

        // Linear's GraphQL endpoint: its own address (a stand-in's as given), /graphql; a page of the app taken to the API.
        private static string Api(string endpoint)
        {
            if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not Linear's API address - https://api.linear.app.");
            }

            return address.Host.Equals("linear.app", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".linear.app", StringComparison.OrdinalIgnoreCase)
                ? "https://api.linear.app/graphql"
                : address.GetLeftPart(UriPartial.Authority) + "/graphql";
        }

        private static string Key(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("api-key", out var key) && !string.IsNullOrWhiteSpace(key) ? key.Trim() : throw new InvalidOperationException("The Linear connection has no API key.");

        private static string Issue(string? given) =>
            given is not null && IssueId.IsMatch(given.Trim()) ? (Uuid.IsMatch(given.Trim()) ? given.Trim() : given.Trim().ToUpperInvariant()) : throw new InvalidOperationException($"'{given}' is not an issue's identifier (ENG-123) or id.");

        private static int Priority(string given) =>
            int.TryParse(given.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level is >= 0 and <= 4 ? level : throw new InvalidOperationException($"'{given}' is not a priority - 0 none, 1 urgent, 2 high, 3 medium, 4 low.");

        private static string Email(string given) =>
            given.Contains('@', StringComparison.Ordinal) && !given.Trim().Any(c => char.IsWhiteSpace(c) || c == '"') ? given.Trim() : throw new InvalidOperationException($"'{given}' is not an address, or me.");

        private static string Time(string given) =>
            DateTimeOffset.TryParse(given.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-01T00:00:00Z.");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // A GraphQL query's data, the key as Linear takes it - a personal key alone, an OAuth token as a bearer; a failure thrown with
        // Linear's message and its code.
        private static async Task<JsonNode?> QueryAsync(HttpClient client, string key, string api, string query, JsonObject variables, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["query"] = query, ["variables"] = variables.DeepClone() }.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, api) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("Authorization", key.StartsWith("lin_api_", StringComparison.Ordinal) ? key : "Bearer " + key);
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
                throw new InvalidOperationException($"Linear answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            var error = (json?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var said = error is null ? null : string.Join(" ", new[] { Text(error["extensions"]?["userPresentableMessage"]) ?? Text(error["message"]), Text(error["extensions"]?["code"]) is { } code ? $"({code})" : null }.Where(part => part is not null));
            if (!response.IsSuccessStatusCode || said is not null)
            {
                var words = said ?? "";
                throw new InvalidOperationException($"{(response.IsSuccessStatusCode ? "Linear said" : $"HTTP {(int)response.StatusCode}")}{(words.Length == 0 ? "." : ": " + (words.Length > 300 ? words[..300] : words))}");
            }

            return json?["data"];
        }
    }
}
