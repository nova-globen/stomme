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

namespace Stomme.Modules.Connectors.Sentry
{
    /// <summary>
    /// Sentry (BL-229, n8n's Sentry.io node: its events, issues, organizations, projects, releases and teams): Sentry's web API
    /// - sentry.io's, a region's or a self-hosted server's - with an auth token as a bearer token, over the host's egress. A
    /// test names the token's organizations.
    /// </summary>
    /// <remarks>
    /// <c>events</c> (a project's, in full or not), <c>event</c>; <c>issues</c> (a project's, by Sentry's search, a stats period
    /// and a short id), <c>issue</c>, <c>update_issue</c> (its status, assignee and flags) and <c>delete_issue</c>;
    /// <c>organizations</c> (the token's, as a member or an owner), <c>organization</c> and <c>update_organization</c>;
    /// <c>projects</c>, <c>project</c>, <c>create_project</c> (for a team), <c>update_project</c> and <c>delete_project</c>;
    /// <c>releases</c> (by a version's start), <c>release</c>, <c>create_release</c> (its projects, address, date, commits and
    /// refs), <c>update_release</c> and <c>delete_release</c>; <c>teams</c>, <c>team</c>, <c>create_team</c>,
    /// <c>update_team</c> and <c>delete_team</c>. A list is read page by page by the cursor Sentry's Link header names - never
    /// its address, so a page is never fetched from another host - up to <c>max</c>, as long as Sentry says there are results.
    /// A record is its fields as Sentry gives them, a list or an object as its JSON. Every slug, id, version, status, flag,
    /// number, date, commit and ref held to its shape before anything is sent. A throttled call waits as Sentry says; a failure
    /// is thrown with its status and Sentry's words.
    /// </remarks>
    public sealed class SentryConnector : IConnector, IConnectorExtractor
    {
        public const string EventsOperation = "events";

        public const string EventOperation = "event";

        public const string IssuesOperation = "issues";

        public const string IssueOperation = "issue";

        public const string UpdateIssueOperation = "update_issue";

        public const string DeleteIssueOperation = "delete_issue";

        public const string OrganizationsOperation = "organizations";

        public const string OrganizationOperation = "organization";

        public const string UpdateOrganizationOperation = "update_organization";

        public const string ProjectsOperation = "projects";

        public const string ProjectOperation = "project";

        public const string CreateProjectOperation = "create_project";

        public const string UpdateProjectOperation = "update_project";

        public const string DeleteProjectOperation = "delete_project";

        public const string ReleasesOperation = "releases";

        public const string ReleaseOperation = "release";

        public const string CreateReleaseOperation = "create_release";

        public const string UpdateReleaseOperation = "update_release";

        public const string DeleteReleaseOperation = "delete_release";

        public const string TeamsOperation = "teams";

        public const string TeamOperation = "team";

        public const string CreateTeamOperation = "create_team";

        public const string UpdateTeamOperation = "update_team";

        public const string DeleteTeamOperation = "delete_team";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private const string DefaultEndpoint = "https://sentry.io";

        private static readonly Regex Slug = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$", RegexOptions.CultureInvariant);

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex EventId = new("^[0-9A-Fa-f]{1,32}$|^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant);

        private static readonly Regex Link = new("<[^>]*>(?<attributes>(\\s*;\\s*[A-Za-z]+=\"[^\"]*\")*)", RegexOptions.CultureInvariant);

        private static readonly Regex Attribute = new("([A-Za-z]+)=\"([^\"]*)\"", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            EventsOperation, EventOperation, IssuesOperation, IssueOperation, UpdateIssueOperation, DeleteIssueOperation, OrganizationsOperation, OrganizationOperation,
            UpdateOrganizationOperation, ProjectsOperation, ProjectOperation, CreateProjectOperation, UpdateProjectOperation, DeleteProjectOperation, ReleasesOperation,
            ReleaseOperation, CreateReleaseOperation, UpdateReleaseOperation, DeleteReleaseOperation, TeamsOperation, TeamOperation, CreateTeamOperation, UpdateTeamOperation,
            DeleteTeamOperation,
        };

        private static readonly string[] Statuses = { "resolved", "resolvedInNextRelease", "unresolved", "ignored" };

        public string ConnectorType => "sentry";

        public string DisplayName => "Sentry";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Auth token (User Settings > Personal Tokens)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://sentry.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.sentry.io/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField OrganizationField = Field("organization", "Organization", "The organization's slug.", required: true);

        private static readonly ConnectorConfigField ProjectField = Field("project", "Project", "The project's slug.", required: true);

        private static readonly ConnectorConfigField TeamField = Field("team", "Team", "The team's slug.", required: true);

        private static readonly ConnectorConfigField IssueField = Field("issue", "Issue", "The issue's id - 1234.", required: true);

        private static readonly ConnectorConfigField VersionField = Field("version", "Version", "The release's version - my-app@1.0.0.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField Flag(string name, string label, string description) => Field(name, label, description, ConnectorConfigFieldType.Boolean);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(EventsOperation, "Get events", "A project's events - n8n's Sentry.io Event Get Many.") { Parameters = Schema(OrganizationField, ProjectField, Flag("full", "Full", "true: each with its full body and stack trace."), MaxField) },
            new ConnectorOperation(EventOperation, "Get an event", "n8n's Sentry.io Event Get.") { Parameters = Schema(OrganizationField, ProjectField, Field("event", "Event", "The event's id - its 32 hexadecimal digits.", required: true)) },
            new ConnectorOperation(IssuesOperation, "Get issues", "A project's issues - n8n's Sentry.io Issue Get Many.")
            {
                Parameters = Schema(OrganizationField, ProjectField, Field("query", "Query", "Sentry's search - is:unresolved."), Field("stats_period", "Stats period", "24h or 14d."), Flag("short_id_lookup", "Short id lookup", "true: a short id in the query finds its issue."), MaxField),
            },
            new ConnectorOperation(IssueOperation, "Get an issue", "n8n's Sentry.io Issue Get.") { Parameters = Schema(IssueField) },
            new ConnectorOperation(UpdateIssueOperation, "Update an issue", "n8n's Sentry.io Issue Update.")
            {
                Parameters = Schema(
                    IssueField,
                    Field("status", "Status", "resolved, resolvedInNextRelease, unresolved or ignored."),
                    Field("assigned_to", "Assigned to", "A user's name or e-mail, user:id or team:id - empty to unassign."),
                    Flag("has_seen", "Seen", "true or false."),
                    Flag("is_bookmarked", "Bookmarked", "true or false."),
                    Flag("is_subscribed", "Subscribed", "true or false."),
                    Flag("is_public", "Public", "true or false.")),
            },
            new ConnectorOperation(DeleteIssueOperation, "Delete an issue", "n8n's Sentry.io Issue Delete.") { Parameters = Schema(IssueField) },
            new ConnectorOperation(OrganizationsOperation, "Get organizations", "The token's organizations - n8n's Sentry.io Organization Get Many.")
            {
                Parameters = Schema(Flag("member", "Member", "true: those the token's user is a member of."), Flag("owner", "Owner", "true: those it owns."), MaxField),
            },
            new ConnectorOperation(OrganizationOperation, "Get an organization", "n8n's Sentry.io Organization Get.") { Parameters = Schema(OrganizationField) },
            new ConnectorOperation(UpdateOrganizationOperation, "Update an organization", "Its name or slug - n8n's Sentry.io Organization Update.")
            {
                Parameters = Schema(OrganizationField, Field("name", "Name", "Its new name."), Field("slug", "Slug", "Its new slug.")),
            },
            new ConnectorOperation(ProjectsOperation, "Get projects", "The token's projects - n8n's Sentry.io Project Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(ProjectOperation, "Get a project", "n8n's Sentry.io Project Get.") { Parameters = Schema(OrganizationField, ProjectField) },
            new ConnectorOperation(CreateProjectOperation, "Create a project", "For a team - n8n's Sentry.io Project Create.")
            {
                Parameters = Schema(OrganizationField, TeamField, Field("name", "Name", "Its name.", required: true), Field("slug", "Slug", "Its slug."), Field("platform", "Platform", "Its platform - javascript-react.")),
            },
            new ConnectorOperation(UpdateProjectOperation, "Update a project", "n8n's Sentry.io Project Update.")
            {
                Parameters = Schema(
                    OrganizationField,
                    ProjectField,
                    Field("name", "Name", "Its new name."),
                    Field("slug", "Slug", "Its new slug."),
                    Field("platform", "Platform", "Its platform."),
                    Flag("is_bookmarked", "Bookmarked", "true or false."),
                    Field("digests_min_delay", "Digests' least delay", "Seconds - 60.", ConnectorConfigFieldType.Integer),
                    Field("digests_max_delay", "Digests' most delay", "Seconds - 1800.", ConnectorConfigFieldType.Integer)),
            },
            new ConnectorOperation(DeleteProjectOperation, "Delete a project", "n8n's Sentry.io Project Delete.") { Parameters = Schema(OrganizationField, ProjectField) },
            new ConnectorOperation(ReleasesOperation, "Get releases", "An organization's releases - n8n's Sentry.io Release Get Many.")
            {
                Parameters = Schema(OrganizationField, Field("query", "Query", "The versions starting with it."), MaxField),
            },
            new ConnectorOperation(ReleaseOperation, "Get a release", "n8n's Sentry.io Release Get.") { Parameters = Schema(OrganizationField, VersionField) },
            new ConnectorOperation(CreateReleaseOperation, "Create a release", "n8n's Sentry.io Release Create.")
            {
                Parameters = Schema(
                    OrganizationField,
                    VersionField,
                    Field("projects", "Projects", "Comma-separated project slugs.", required: true),
                    Field("url", "Address", "Where it is - its tag on GitHub."),
                    Field("date_released", "Released", "When - 2026-10-13T10:00:00Z."),
                    Field("commits", "Commits", "A JSON list - [{\"id\": \"a1b2c3\", \"repository\": \"org/app\", \"message\": \"Fix\"}].", ConnectorConfigFieldType.Multiline),
                    Field("refs", "Refs", "A JSON list - [{\"repository\": \"org/app\", \"commit\": \"a1b2c3\"}].", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(UpdateReleaseOperation, "Update a release", "n8n's Sentry.io Release Update.")
            {
                Parameters = Schema(
                    OrganizationField,
                    VersionField,
                    Field("url", "Address", "Where it is."),
                    Field("date_released", "Released", "When - 2026-10-13T10:00:00Z."),
                    Field("ref", "Ref", "Its commit or tag."),
                    Field("commits", "Commits", "A JSON list of commits.", ConnectorConfigFieldType.Multiline),
                    Field("refs", "Refs", "A JSON list of refs.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(DeleteReleaseOperation, "Delete a release", "n8n's Sentry.io Release Delete.") { Parameters = Schema(OrganizationField, VersionField) },
            new ConnectorOperation(TeamsOperation, "Get teams", "An organization's teams - n8n's Sentry.io Team Get Many.") { Parameters = Schema(OrganizationField, MaxField) },
            new ConnectorOperation(TeamOperation, "Get a team", "n8n's Sentry.io Team Get.") { Parameters = Schema(OrganizationField, TeamField) },
            new ConnectorOperation(CreateTeamOperation, "Create a team", "n8n's Sentry.io Team Create.") { Parameters = Schema(OrganizationField, Field("name", "Name", "Its name.", required: true), Field("slug", "Slug", "Its slug.")) },
            new ConnectorOperation(UpdateTeamOperation, "Update a team", "n8n's Sentry.io Team Update.") { Parameters = Schema(OrganizationField, TeamField, Field("name", "Name", "Its new name."), Field("slug", "Slug", "Its new slug.")) },
            new ConnectorOperation(DeleteTeamOperation, "Delete a team", "n8n's Sentry.io Team Delete.") { Parameters = Schema(OrganizationField, TeamField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var (answer, _) = await session.SendAsync(HttpMethod.Get, "/organizations/?member=true", null, cancellationToken);
                var names = (answer as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(organization => Text(organization["slug"])).OfType<string>().ToList();
                return ConnectionTestResult.Ok(names.Count == 0 ? "Sentry: the token is in no organization." : $"Sentry: the token is in {string.Join(", ", names.Take(5))}{(names.Count > 5 ? $" and {names.Count - 5} more" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? IssuesOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Sentry has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string SlugOf(string name, string what) => Values.Get(p, name) is { } given ? Slug.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a slug (" + name + ").") : throw new InvalidOperationException($"Give the {what}'s slug.");
            string? Optional(string name) => Values.Get(p, name) is { } given ? Slug.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a slug ({name}).") : null;
            bool? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
            var organization = operation is OrganizationsOperation or ProjectsOperation or IssueOperation or UpdateIssueOperation or DeleteIssueOperation ? null : SlugOf("organization", "organization");
            var project = operation is EventsOperation or EventOperation or IssuesOperation or ProjectOperation or UpdateProjectOperation or DeleteProjectOperation ? SlugOf("project", "project") : null;
            var team = operation is CreateProjectOperation or TeamOperation or UpdateTeamOperation or DeleteTeamOperation ? SlugOf("team", "team") : null;
            var issue = operation is IssueOperation or UpdateIssueOperation or DeleteIssueOperation
                ? Values.Get(p, "issue") is { } id ? Id.IsMatch(id) ? id : throw new InvalidOperationException($"'{id}' is not an issue's id - 1234 (issue).") : throw new InvalidOperationException("Give the issue's id.")
                : null;
            var eventId = operation == EventOperation
                ? Values.Get(p, "event") is { } given ? EventId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an event's id - its 32 hexadecimal digits (event).") : throw new InvalidOperationException("Give the event's id.")
                : null;
            var version = operation is ReleaseOperation or CreateReleaseOperation or UpdateReleaseOperation or DeleteReleaseOperation ? VersionOf(Values.Get(p, "version")) : null;

            var query = new List<string>();
            void Add(string name, string? value)
            {
                if (value is not null)
                {
                    query.Add(name + "=" + Uri.EscapeDataString(value));
                }
            }

            JsonObject? body = null;
            switch (operation)
            {
                case EventsOperation:
                    Add("full", FlagOf("full") is { } full ? full ? "true" : "false" : null);
                    break;
                case IssuesOperation:
                    Add("query", Values.Get(p, "query"));
                    Add("statsPeriod", Values.Get(p, "stats_period") is { } period ? period is "24h" or "14d" ? period : throw new InvalidOperationException($"'{period}' is not 24h or 14d (stats_period).") : null);
                    Add("shortIdLookup", FlagOf("short_id_lookup") is { } lookup ? lookup ? "1" : "0" : null);
                    break;
                case OrganizationsOperation:
                    Add("member", FlagOf("member") is { } member ? member ? "true" : "false" : null);
                    Add("owner", FlagOf("owner") is { } owner ? owner ? "true" : "false" : null);
                    break;
                case ReleasesOperation:
                    Add("query", Values.Get(p, "query"));
                    break;
                case UpdateIssueOperation:
                    body = new JsonObject();
                    if (Values.Get(p, "status") is { } status)
                    {
                        body["status"] = Statuses.FirstOrDefault(known => string.Equals(known, status, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{status}' is not resolved, resolvedInNextRelease, unresolved or ignored (status).");
                    }

                    if (p.TryGetValue("assigned_to", out var assignee) && assignee is not null)
                    {
                        // An empty assignee unassigns, as Sentry reads it.
                        body["assignedTo"] = assignee.Trim();
                    }

                    foreach (var (name, wire) in new[] { ("has_seen", "hasSeen"), ("is_bookmarked", "isBookmarked"), ("is_subscribed", "isSubscribed"), ("is_public", "isPublic") })
                    {
                        if (FlagOf(name) is { } flag)
                        {
                            body[wire] = flag;
                        }
                    }

                    if (body.Count == 0)
                    {
                        throw new InvalidOperationException("Give what to change - its status, assignee or a flag.");
                    }

                    break;
                case UpdateOrganizationOperation or UpdateTeamOperation:
                    body = new JsonObject();
                    if (Values.Get(p, "name") is { } newName)
                    {
                        body["name"] = newName;
                    }

                    if (Optional("slug") is { } newSlug)
                    {
                        body["slug"] = newSlug;
                    }

                    if (body.Count == 0)
                    {
                        throw new InvalidOperationException("Give what to change - its name or slug.");
                    }

                    break;
                case CreateTeamOperation or CreateProjectOperation:
                    body = new JsonObject { ["name"] = Values.Get(p, "name") ?? throw new InvalidOperationException("Give its name.") };
                    if (Optional("slug") is { } slug)
                    {
                        body["slug"] = slug;
                    }

                    if (operation == CreateProjectOperation && Values.Get(p, "platform") is { } platform)
                    {
                        body["platform"] = PlatformOf(platform);
                    }

                    break;
                case UpdateProjectOperation:
                    body = new JsonObject();
                    if (Values.Get(p, "name") is { } projectName)
                    {
                        body["name"] = projectName;
                    }

                    if (Optional("slug") is { } projectSlug)
                    {
                        body["slug"] = projectSlug;
                    }

                    if (Values.Get(p, "platform") is { } projectPlatform)
                    {
                        body["platform"] = PlatformOf(projectPlatform);
                    }

                    if (FlagOf("is_bookmarked") is { } bookmarked)
                    {
                        body["isBookmarked"] = bookmarked;
                    }

                    foreach (var (name, wire) in new[] { ("digests_min_delay", "digestsMinDelay"), ("digests_max_delay", "digestsMaxDelay") })
                    {
                        if (Values.Get(p, name) is { } seconds)
                        {
                            body[wire] = int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var delay) && delay > 0 ? delay : throw new InvalidOperationException($"'{seconds}' is not a number of seconds above 0 ({name}).");
                        }
                    }

                    if (body.Count == 0)
                    {
                        throw new InvalidOperationException("Give what to change - its name, slug, platform, bookmark or digests' delays.");
                    }

                    break;
                case CreateReleaseOperation or UpdateReleaseOperation:
                    body = operation == CreateReleaseOperation ? new JsonObject { ["version"] = version } : new JsonObject();
                    if (operation == CreateReleaseOperation)
                    {
                        var projects = (Values.Get(p, "projects") ?? throw new InvalidOperationException("Give its projects - comma-separated slugs."))
                            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(slug => Slug.IsMatch(slug) ? slug : throw new InvalidOperationException($"'{slug}' is not a slug (projects)."))
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        body["projects"] = new JsonArray(projects.Select(slug => (JsonNode?)JsonValue.Create(slug)).ToArray());
                    }

                    if (Values.Get(p, "url") is { } url)
                    {
                        body["url"] = Uri.TryCreate(url, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps) ? url : throw new InvalidOperationException($"'{url}' is not an http(s) address (url).");
                    }

                    if (Values.Get(p, "date_released") is { } date)
                    {
                        body["dateReleased"] = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && date.Contains('T') ? when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{date}' is not a time - 2026-10-13T10:00:00Z (date_released).");
                    }

                    if (operation == UpdateReleaseOperation && Values.Get(p, "ref") is { } reference)
                    {
                        body["ref"] = reference;
                    }

                    if (Values.Get(p, "commits") is { } commits)
                    {
                        body["commits"] = ListOf(commits, "commits", "id");
                    }

                    if (Values.Get(p, "refs") is { } refs)
                    {
                        body["refs"] = ListOf(refs, "refs", "repository", "commit");
                    }

                    if (body.Count == 0)
                    {
                        throw new InvalidOperationException("Give what to change - its address, date, ref, commits or refs.");
                    }

                    break;
            }

            // A slug held to letters, digits, '-' and '_' needs no escaping in a path; a version does.
            var path = operation switch
            {
                EventsOperation => $"/projects/{organization}/{project}/events/",
                EventOperation => $"/projects/{organization}/{project}/events/{eventId}/",
                IssuesOperation => $"/projects/{organization}/{project}/issues/",
                IssueOperation or UpdateIssueOperation or DeleteIssueOperation => $"/issues/{issue}/",
                OrganizationsOperation => "/organizations/",
                OrganizationOperation or UpdateOrganizationOperation => $"/organizations/{organization}/",
                ProjectsOperation => "/projects/",
                ProjectOperation or UpdateProjectOperation or DeleteProjectOperation => $"/projects/{organization}/{project}/",
                CreateProjectOperation => $"/teams/{organization}/{team}/projects/",
                ReleasesOperation or CreateReleaseOperation => $"/organizations/{organization}/releases/",
                ReleaseOperation or UpdateReleaseOperation or DeleteReleaseOperation => $"/organizations/{organization}/releases/{Uri.EscapeDataString(version!)}/",
                TeamsOperation or CreateTeamOperation => $"/organizations/{organization}/teams/",
                _ => $"/teams/{organization}/{team}/",
            };

            using var client = session.Client;
            ConnectorRecord Done(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
            switch (operation)
            {
                case EventsOperation or IssuesOperation or OrganizationsOperation or ProjectsOperation or ReleasesOperation or TeamsOperation:
                    {
                        // Sentry pages by a cursor its Link header names: the size fixed at the start, Sentry's results="false" or an
                        // empty page the end - the cursor read, never the address beside it.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        string? cursor = null;
                        while (true)
                        {
                            var (answer, link) = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"per_page={size}").Concat(cursor is null ? Array.Empty<string>() : new[] { "cursor=" + Uri.EscapeDataString(cursor) })), null, cancellationToken);
                            var items = (answer as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            cursor = Next(link);
                            if (items.Count == 0 || cursor is null)
                            {
                                yield break;
                            }
                        }
                    }

                case DeleteIssueOperation or DeleteProjectOperation or DeleteReleaseOperation or DeleteTeamOperation:
                    await session.SendAsync(HttpMethod.Delete, path, null, cancellationToken);
                    yield return operation switch
                    {
                        DeleteIssueOperation => Done(("id", issue), ("deleted", true)),
                        DeleteProjectOperation => Done(("organization", organization), ("slug", project), ("deleted", true)),
                        DeleteReleaseOperation => Done(("organization", organization), ("version", version), ("deleted", true)),
                        _ => Done(("organization", organization), ("slug", team), ("deleted", true)),
                    };
                    yield break;
                default:
                    {
                        var method = operation switch
                        {
                            CreateProjectOperation or CreateReleaseOperation or CreateTeamOperation => HttpMethod.Post,
                            UpdateIssueOperation or UpdateOrganizationOperation or UpdateProjectOperation or UpdateReleaseOperation or UpdateTeamOperation => HttpMethod.Put,
                            _ => HttpMethod.Get,
                        };
                        var (answer, _) = await session.SendAsync(method, path, body, cancellationToken);
                        yield return Record(answer as JsonObject ?? new JsonObject());
                        yield break;
                    }
            }
        }

        // A release's version as Sentry keeps one: no slash, no line, not '.', '..' or 'latest', at most 200 characters.
        private static string VersionOf(string? version)
        {
            if (version is null)
            {
                throw new InvalidOperationException("Give the release's version - my-app@1.0.0.");
            }

            return version.Length <= 200 && !version.Any(c => c is '/' or '\\' || char.IsControl(c) || char.IsWhiteSpace(c)) && version is not ("." or ".." or "latest")
                ? version
                : throw new InvalidOperationException($"'{version}' is not a release's version - no slash, space or line, not '.', '..' or 'latest' (version).");
        }

        private static string PlatformOf(string platform) =>
            Regex.IsMatch(platform, "^[a-z0-9][a-z0-9-]{0,63}$") ? platform : throw new InvalidOperationException($"'{platform}' is not a platform - javascript-react (platform).");

        // A JSON list of objects, each naming what Sentry needs of it.
        private static JsonArray ListOf(string json, string name, params string[] required)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is not JsonArray list || list.Any(item => item is not JsonObject))
            {
                throw new InvalidOperationException($"The {name} are not a JSON list of objects - [{{\"{required[0]}\": \"...\"}}].");
            }

            foreach (var item in list.Cast<JsonObject>())
            {
                foreach (var field in required)
                {
                    if (Text(item[field]) is not { Length: > 0 })
                    {
                        throw new InvalidOperationException($"Each of the {name} needs its {field}.");
                    }
                }
            }

            return list;
        }

        // The cursor of the Link header's next page while Sentry says it has results; none otherwise.
        private static string? Next(string? link)
        {
            if (link is null)
            {
                return null;
            }

            foreach (Match match in Link.Matches(link))
            {
                var attributes = Attribute.Matches(match.Groups["attributes"].Value).ToDictionary(attribute => attribute.Groups[1].Value.ToLowerInvariant(), attribute => attribute.Groups[2].Value, StringComparer.Ordinal);
                if (attributes.GetValueOrDefault("rel") == "next")
                {
                    return attributes.GetValueOrDefault("results") == "true" && attributes.GetValueOrDefault("cursor") is { Length: > 0 } cursor ? cursor : null;
                }
            }

            return null;
        }

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

        // One connection's calls: Sentry's web API with the token as a bearer token; a throttled call waiting as Sentry says; a
        // failure thrown with its status and Sentry's words.
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
                var baseUrl = Values.BaseUrl(endpoint, DefaultEndpoint);
                baseUrl = baseUrl.EndsWith("/api/0", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^6] : baseUrl;
                return new Session(baseUrl + "/api/0", Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Sentry connection has no auth token."), egress.CreateHttpClient(timeout));
            }

            public async Task<(JsonNode? Answer, string? Link)> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
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
                    // A page that is not Sentry's - a sign-in page, a proxy's - read as no issues would hide that it never was.
                    throw new InvalidOperationException($"Sentry answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = json switch
                    {
                        JsonObject error when Text(error["detail"]) is { } detail => detail,
                        JsonObject fields => string.Join("; ", fields.Select(field => field.Key + ": " + (field.Value is JsonArray words ? string.Join(" ", words.Select(Text).OfType<string>()) : Text(field.Value) ?? field.Value?.ToJsonString(Wire)))),
                        _ => null,
                    };
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return (json, response.Headers.TryGetValues("Link", out var links) ? string.Join(", ", links) : null);
            }
        }
    }
}
