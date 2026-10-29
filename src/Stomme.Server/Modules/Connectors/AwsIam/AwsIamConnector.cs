#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsIam
{
    /// <summary>
    /// AWS IAM (n8n's AWS IAM node: an account's users and groups) over IAM's Query protocol at its global address
    /// (<c>https://iam.amazonaws.com</c>, signed for <c>iam</c> in us-east-1) through the shared <see cref="AwsQuerySession"/>,
    /// every call signed with SigV4 by an IAM user's access key and secret, over the host's egress. A test names the account's
    /// alias, or that it has none.
    /// </summary>
    /// <remarks>
    /// Users: <c>users</c> (by a path prefix, page by page up to <c>max</c>), <c>user</c>, <c>user_create</c> (its path, a
    /// permissions boundary and tags), <c>user_update</c> (a new name or path), <c>user_delete</c> (taken out of its groups first,
    /// as n8n's node does), <c>user_add_to_group</c> and <c>user_remove_from_group</c>. Groups: <c>groups</c> and <c>group</c>
    /// (their users with them if asked), <c>group_create</c>, <c>group_update</c> and <c>group_delete</c> (its users taken out
    /// first, as n8n's node does). Never a password or an access key: what signs a user in is set in IAM. Every name, path, ARN
    /// and tag held to IAM's shape before anything is signed; AWS's error code and message on a failure.
    /// </remarks>
    public sealed class AwsIamConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://iam.amazonaws.com";

        public const int DefaultMax = 1000;

        private const string Version = "2010-05-08";

        private const int PageSize = 1000; // IAM's most a page

        private static readonly string[] Known =
        {
            "users", "user", "user_create", "user_update", "user_delete", "user_add_to_group", "user_remove_from_group",
            "groups", "group", "group_create", "group_update", "group_delete",
        };

        private static readonly Regex UserName = new("^[\\w+=,.@-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex GroupName = new("^[\\w+=,.@-]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex Path = new("^/([\\u0021-\\u007E]*/)?$", RegexOptions.CultureInvariant);

        private static readonly Regex PolicyArn = new("^arn:aws[a-z-]*:iam::([0-9]{12}|aws):policy/[\\w+=,.@/-]{1,512}$", RegexOptions.CultureInvariant);

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awsiam";

        public string DisplayName => "AWS IAM";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/iam/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/IAM/latest/APIReference/welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Signing region", ConnectorConfigFieldType.Text, Required: false, Description: "The region IAM's calls are signed for - us-east-1, IAM's own for the global address.") { Default = "us-east-1" },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "The user's name.", required: true);

        private static readonly ConnectorConfigField GroupField = Field("group", "Group", "The group's name.", required: true);

        private static readonly ConnectorConfigField PathField = Field("path", "Path", "Where it sits - /kassa/, starting and ending with /. Empty: /.");

        private static readonly ConnectorConfigField PrefixField = Field("path_prefix", "Path prefix", "Only those under it - /kassa/.");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField UsersWithField = Field("include_users", "With its users", "true: each group with its users.", ConnectorConfigFieldType.Boolean);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("users", "Get users", "The account's users - n8n's Get Many.") { Parameters = new(new[] { PrefixField, MaxField }) },
            new ConnectorOperation("user", "Get a user", "n8n's Get.") { Parameters = new(new[] { UserField }) },
            new ConnectorOperation("user_create", "Create a user", "Never a password or a key: those are set in IAM - n8n's Create.")
            {
                Parameters = new(new[]
                {
                    Field("user", "User", "The new user's name - kassa-ada.", required: true),
                    PathField,
                    Field("permissions_boundary", "Permissions boundary", "A managed policy's ARN - arn:aws:iam::123456789012:policy/KassaBoundary."),
                    Field("tags", "Tags", "A JSON object - {\"team\": \"kassa\"}."),
                }),
            },
            new ConnectorOperation("user_update", "Update a user", "Its name or path - n8n's Update.")
            {
                Parameters = new(new[] { UserField, Field("new_name", "New name", "Its new name."), Field("new_path", "New path", "Its new path - /kassa/.") }),
            },
            new ConnectorOperation("user_delete", "Delete a user", "Taken out of its groups first - n8n's Delete.") { Parameters = new(new[] { UserField }) },
            new ConnectorOperation("user_add_to_group", "Add a user to a group", "n8n's Add to Group.") { Parameters = new(new[] { UserField, GroupField }) },
            new ConnectorOperation("user_remove_from_group", "Remove a user from a group", "n8n's Remove From Group.") { Parameters = new(new[] { UserField, GroupField }) },
            new ConnectorOperation("groups", "Get groups", "The account's groups - n8n's Get Many.") { Parameters = new(new[] { PrefixField, UsersWithField, MaxField }) },
            new ConnectorOperation("group", "Get a group", "n8n's Get.") { Parameters = new(new[] { GroupField, UsersWithField }) },
            new ConnectorOperation("group_create", "Create a group", "n8n's Create.") { Parameters = new(new[] { Field("group", "Group", "The new group's name - Kassa.", required: true), PathField }) },
            new ConnectorOperation("group_update", "Update a group", "Its name or path - n8n's Update.")
            {
                Parameters = new(new[] { GroupField, Field("new_name", "New name", "Its new name."), Field("new_path", "New path", "Its new path - /kassa/.") }),
            },
            new ConnectorOperation("group_delete", "Delete a group", "Its users taken out first - n8n's Delete.") { Parameters = new(new[] { GroupField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.CallAsync("ListAccountAliases", Array.Empty<(string, string)>(), cancellationToken);
                var alias = AwsQuerySession.Members(answer, "AccountAliases").Select(member => member.Value).FirstOrDefault();
                return ConnectionTestResult.Ok(alias is null ? "AWS IAM answers (the account has no alias)." : $"AWS IAM answers: the account {alias}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "users").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"AWS IAM has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var withUsers = Flag(p, "include_users") ?? false;
            var user = operation.StartsWith("user_", StringComparison.Ordinal) || operation == "user" ? Name(p, "user", UserName, "a user's name", 64) : string.Empty;
            var group = operation is "user_add_to_group" or "user_remove_from_group" || operation.StartsWith("group", StringComparison.Ordinal) && operation != "groups" ? Name(p, "group", GroupName, "a group's name", 128) : string.Empty;
            var parameters = new List<(string Name, string Value)>();
            switch (operation)
            {
                case "users" or "groups":
                    if (Values.Get(p, "path_prefix") is { } prefix)
                    {
                        parameters.Add(("PathPrefix", PathOf(prefix, "path_prefix")));
                    }

                    break;
                case "user_create":
                    {
                        parameters.Add(("UserName", user));
                        if (Values.Get(p, "path") is { } path)
                        {
                            parameters.Add(("Path", PathOf(path, "path")));
                        }

                        if (Values.Get(p, "permissions_boundary") is { } boundary)
                        {
                            parameters.Add(("PermissionsBoundary", PolicyArn.IsMatch(boundary) ? boundary : throw new InvalidOperationException($"'{boundary}' is not a managed policy's ARN - arn:aws:iam::123456789012:policy/KassaBoundary (permissions_boundary).")));
                        }

                        var index = 0;
                        foreach (var (key, value) in Tags(p))
                        {
                            index++;
                            parameters.Add(($"Tags.member.{index}.Key", key));
                            parameters.Add(($"Tags.member.{index}.Value", value));
                        }

                        break;
                    }

                case "user_update" or "group_update":
                    {
                        var isUser = operation == "user_update";
                        parameters.Add((isUser ? "UserName" : "GroupName", isUser ? user : group));
                        if (Values.Get(p, "new_name") is { } renamed)
                        {
                            parameters.Add((isUser ? "NewUserName" : "NewGroupName", Name(p, "new_name", isUser ? UserName : GroupName, isUser ? "a user's name" : "a group's name", isUser ? 64 : 128)));
                        }

                        if (Values.Get(p, "new_path") is { } moved)
                        {
                            parameters.Add(("NewPath", PathOf(moved, "new_path")));
                        }

                        if (parameters.Count == 1)
                        {
                            throw new InvalidOperationException("Give what changes - a new name or a new path (new_name, new_path).");
                        }

                        break;
                    }

                case "group_create":
                    parameters.Add(("GroupName", group));
                    if (Values.Get(p, "path") is { } groupPath)
                    {
                        parameters.Add(("Path", PathOf(groupPath, "path")));
                    }

                    break;
            }

            var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "users":
                    await foreach (var found in PagesAsync(session, "ListUsers", parameters, "Users", max, cancellationToken))
                    {
                        yield return Record(found);
                    }

                    break;
                case "user":
                    yield return Record(Item(await session.CallAsync("GetUser", new[] { ("UserName", user) }, cancellationToken), "User"));
                    break;
                case "user_create":
                    yield return Record(Item(await session.CallAsync("CreateUser", parameters, cancellationToken), "User"));
                    break;
                case "user_update":
                    await session.CallAsync("UpdateUser", parameters, cancellationToken);
                    yield return Fields(("user", user), ("updated", true));
                    break;
                case "user_delete":
                    {
                        // n8n's Delete takes the user out of its groups first, which IAM refuses to delete it in.
                        var left = new List<string>();
                        await foreach (var member in PagesAsync(session, "ListGroupsForUser", new List<(string, string)> { ("UserName", user) }, "Groups", int.MaxValue, cancellationToken))
                        {
                            var name = AwsQuerySession.Child(member, "GroupName")!;
                            await session.CallAsync("RemoveUserFromGroup", new[] { ("GroupName", name), ("UserName", user) }, cancellationToken);
                            left.Add(name);
                        }

                        await session.CallAsync("DeleteUser", new[] { ("UserName", user) }, cancellationToken);
                        yield return Fields(("user", user), ("deleted", true), ("left_groups", JsonSerializer.Serialize(left)));
                        break;
                    }

                case "user_add_to_group":
                    await session.CallAsync("AddUserToGroup", new[] { ("GroupName", group), ("UserName", user) }, cancellationToken);
                    yield return Fields(("user", user), ("group", group), ("added_to_group", true));
                    break;
                case "user_remove_from_group":
                    await session.CallAsync("RemoveUserFromGroup", new[] { ("GroupName", group), ("UserName", user) }, cancellationToken);
                    yield return Fields(("user", user), ("group", group), ("removed_from_group", true));
                    break;
                case "groups":
                    await foreach (var found in PagesAsync(session, "ListGroups", parameters, "Groups", max, cancellationToken))
                    {
                        yield return withUsers ? await WithUsersAsync(session, found, cancellationToken) : Record(found);
                    }

                    break;
                case "group":
                    {
                        var answer = await session.CallAsync("GetGroup", new[] { ("GroupName", group) }, cancellationToken);
                        var found = Item(answer, "Group");
                        yield return withUsers ? await WithUsersAsync(session, found, cancellationToken) : Record(found);
                        break;
                    }

                case "group_create":
                    yield return Record(Item(await session.CallAsync("CreateGroup", parameters, cancellationToken), "Group"));
                    break;
                case "group_update":
                    await session.CallAsync("UpdateGroup", parameters, cancellationToken);
                    yield return Fields(("group", group), ("updated", true));
                    break;
                default:
                    {
                        // n8n's Delete takes the group's users out first, which IAM refuses to delete it with.
                        var left = new List<string>();
                        foreach (var member in await GroupUsersAsync(session, group, cancellationToken))
                        {
                            var name = AwsQuerySession.Child(member, "UserName")!;
                            await session.CallAsync("RemoveUserFromGroup", new[] { ("GroupName", group), ("UserName", name) }, cancellationToken);
                            left.Add(name);
                        }

                        await session.CallAsync("DeleteGroup", new[] { ("GroupName", group) }, cancellationToken);
                        yield return Fields(("group", group), ("deleted", true), ("removed_users", JsonSerializer.Serialize(left)));
                        break;
                    }
            }
        }

        private AwsQuerySession Session(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
        {
            var region = Values.Get(settings, "region") ?? "us-east-1";
            if (!Regex.IsMatch(region, "^[a-z]{2}(-gov|-iso[a-z]?)?-[a-z]+-[0-9]$", RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException($"'{region}' is not an AWS region - us-east-1 (region).");
            }

            return AwsQuerySession.Of("AWS IAM", "iam", Version, DefaultEndpoint, region, endpoint, credentials, egress, timeout, time);
        }

        // A list page by page by IAM's Marker while it says it is truncated, a page no larger than what is still wanted.
        private static async IAsyncEnumerable<XElement> PagesAsync(AwsQuerySession session, string action, List<(string Name, string Value)> parameters, string list, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var read = 0;
            string? marker = null;
            do
            {
                var page = new List<(string, string)>(parameters) { ("MaxItems", Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)) };
                if (marker is not null)
                {
                    page.Add(("Marker", marker));
                }

                var answer = await session.CallAsync(action, page, cancellationToken);
                foreach (var member in AwsQuerySession.Members(answer, list))
                {
                    yield return member;
                    if (++read >= max)
                    {
                        yield break;
                    }
                }

                marker = AwsQuerySession.Child(answer, "IsTruncated") == "true" && AwsQuerySession.Child(answer, "Marker") is { Length: > 0 } next ? next : null;
            }
            while (marker is not null);
        }

        private static async Task<List<XElement>> GroupUsersAsync(AwsQuerySession session, string group, CancellationToken cancellationToken)
        {
            var users = new List<XElement>();
            string? marker = null;
            do
            {
                var page = new List<(string, string)> { ("GroupName", group), ("MaxItems", PageSize.ToString(CultureInfo.InvariantCulture)) };
                if (marker is not null)
                {
                    page.Add(("Marker", marker));
                }

                var answer = await session.CallAsync("GetGroup", page, cancellationToken);
                users.AddRange(AwsQuerySession.Members(answer, "Users"));
                marker = AwsQuerySession.Child(answer, "IsTruncated") == "true" && AwsQuerySession.Child(answer, "Marker") is { Length: > 0 } next ? next : null;
            }
            while (marker is not null);
            return users;
        }

        private static async Task<ConnectorRecord> WithUsersAsync(AwsQuerySession session, XElement group, CancellationToken cancellationToken)
        {
            var record = Record(group);
            var users = await GroupUsersAsync(session, AwsQuerySession.Child(group, "GroupName")!, cancellationToken);
            var fields = new Dictionary<string, object?>(record.Fields, StringComparer.Ordinal)
            {
                ["Users"] = new JsonArray(users.Select(user => (JsonNode?)Json(user)).ToArray()).ToJsonString(),
            };
            return new ConnectorRecord(fields);
        }

        // The one user or group a result holds - empty when IAM answered none.
        private static XElement Item(XElement result, string localName) =>
            result.Elements().FirstOrDefault(element => element.Name.LocalName == localName) ?? new XElement(localName);

        private static IEnumerable<(string Key, string Value)> Tags(IReadOnlyDictionary<string, string> p)
        {
            if (Values.Get(p, "tags") is not { } given)
            {
                yield break;
            }

            JsonObject? tags;
            try
            {
                tags = JsonNode.Parse(given) as JsonObject;
            }
            catch (JsonException)
            {
                tags = null;
            }

            if (tags is null || tags.Count is 0 or > 50)
            {
                throw new InvalidOperationException("The tags are a JSON object of up to 50 keys and their values - {\"team\": \"kassa\"} (tags).");
            }

            foreach (var (key, value) in tags)
            {
                var text = value is JsonValue plain && plain.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? string.Empty;
                yield return key.Length is > 0 and <= 128 && text.Length <= 256 ? (key, text) : throw new InvalidOperationException($"A tag's key is 1 to 128 characters and its value at most 256 ('{key}', tags).");
            }
        }

        private static string Name(IReadOnlyDictionary<string, string> p, string parameter, Regex shape, string what, int most) =>
            Values.Get(p, parameter) is { } given
                ? shape.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what} - letters, digits and + = , . @ _ -, at most {most} ({parameter}).")
                : throw new InvalidOperationException($"Give {what} ({parameter}).");

        private static string PathOf(string given, string parameter) =>
            given.Length <= 512 && Path.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a path - /kassa/, starting and ending with / ({parameter}).");

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) => Values.Get(p, name) switch
        {
            null => null,
            var given when string.Equals(given, "true", StringComparison.OrdinalIgnoreCase) => true,
            var given when string.Equals(given, "false", StringComparison.OrdinalIgnoreCase) => false,
            var given => throw new InvalidOperationException($"'{given}' is not true or false ({name})."),
        };

        // A user's or a group's element as a record: its leaves as fields, a list (its tags) as JSON.
        private static ConnectorRecord Record(XElement element) => new(Json(element).ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => (object?)text,
            null => null,
            var other => other.ToJsonString(),
        }, StringComparer.Ordinal));

        private static JsonObject Json(XElement element)
        {
            var json = new JsonObject();
            foreach (var child in element.Elements())
            {
                json[child.Name.LocalName] = child.HasElements
                    ? child.Elements().All(member => member.Name.LocalName == "member")
                        ? new JsonArray(child.Elements().Select(member => member.HasElements ? (JsonNode)Json(member) : JsonValue.Create(member.Value)).ToArray())
                        : Json(child)
                    : JsonValue.Create(child.Value);
            }

            return json;
        }

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }
}
