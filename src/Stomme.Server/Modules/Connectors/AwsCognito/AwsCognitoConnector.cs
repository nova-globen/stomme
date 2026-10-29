#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsCognito
{
    /// <summary>
    /// Amazon Cognito user pools (n8n's AWS Cognito node: a pool's users and groups administered) over the Cognito Identity
    /// Provider's JSON protocol (<c>https://cognito-idp.{region}.amazonaws.com</c>,
    /// <c>X-Amz-Target: AWSCognitoIdentityProviderService.*</c>) through the shared <see cref="AwsJsonSession"/>, every call signed
    /// with SigV4 by an IAM user's access key and secret, over the host's egress. A test describes the connection's user pool, or
    /// lists the pools the key sees.
    /// </summary>
    /// <remarks>
    /// Users: <c>users</c> (by an attribute's value - its start, as n8n's filter, or the whole of it - page by page up to
    /// <c>max</c>), <c>user</c>, <c>user_create</c> (its attributes, how its invitation goes - by email, SMS or not at all - never a
    /// password: Cognito makes the temporary one and sends it), <c>user_update</c> (attributes), <c>user_delete</c>,
    /// <c>user_add_to_group</c> and <c>user_remove_from_group</c>; a user's attributes its record's fields unless <c>simple</c> is
    /// false. Groups: <c>groups</c> and <c>group</c> (their users with them if asked), <c>group_create</c>, <c>group_update</c> and
    /// <c>group_delete</c> - a description, a precedence and an IAM role. Pools: <c>user_pool</c> (its settings without the long
    /// ones unless <c>simple</c> is false) and <c>user_pools</c>. The pool the step's, else the connection's. Every name, attribute,
    /// number and ARN held to Cognito's shape before anything is signed; AWS's error type and message on a failure.
    /// </remarks>
    public sealed class AwsCognitoConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 1000;

        private const int PageSize = 60; // Cognito's most a page, for users, groups and pools alike

        private static readonly string[] Known =
        {
            "users", "user", "user_create", "user_update", "user_delete", "user_add_to_group", "user_remove_from_group",
            "groups", "group", "group_create", "group_update", "group_delete", "user_pool", "user_pools",
        };

        // ListUsers filters on these alone (Cognito's own list).
        private static readonly string[] Filterable = { "username", "email", "phone_number", "name", "given_name", "family_name", "preferred_username", "cognito:user_status", "status", "sub" };

        // What n8n's Simplify leaves out of a pool: its long settings.
        private static readonly string[] LongPoolSettings =
        {
            "AccountRecoverySetting", "AdminCreateUserConfig", "EmailConfiguration", "LambdaConfig", "Policies", "SchemaAttributes",
            "UserAttributeUpdateSettings", "UserPoolTags", "UserPoolTier", "VerificationMessageTemplate",
        };

        private static readonly Regex Pool = new("^[\\w-]+_[0-9a-zA-Z]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Named = new("^[\\p{L}\\p{M}\\p{S}\\p{N}\\p{P}]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex Attribute = new("^[\\p{L}\\p{M}\\p{S}\\p{N}\\p{P}]{1,32}$", RegexOptions.CultureInvariant);

        private static readonly Regex RoleArn = new("^arn:aws[a-z-]*:iam::[0-9]{12}:role/[\\w+=,.@/-]{1,512}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awscognito";

        public string DisplayName => "Amazon Cognito";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://cognito-idp.eu-west-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/cognito/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/Welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region Cognito is called in - eu-west-1. The address is Cognito's in it when left empty."),
            new ConnectorConfigField("user_pool", "User pool", ConnectorConfigFieldType.Text, Required: false, Description: "The user pool a step names none of - eu-west-1_AbC123dEf."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField PoolField = Field("user_pool", "User pool", "Its id - eu-west-1_AbC123dEf. Empty: the connection's.");

        private static readonly ConnectorConfigField UserField = Field("user", "User", "The user's name, or their sub or an alias the pool signs in with.", required: true);

        private static readonly ConnectorConfigField GroupField = Field("group", "Group", "The group's name - Kassa.", required: true);

        private static readonly ConnectorConfigField SimpleField = Field("simple", "Simplify", "false: the answer as Cognito gives it.", ConnectorConfigFieldType.Boolean) with { Default = "true" };

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField UsersWithField = Field("include_users", "With its users", "true: each group with its users.", ConnectorConfigFieldType.Boolean);

        private static readonly ConnectorConfigField AttributesField = Field("attributes", "Attributes", "A JSON object of attributes - {\"email\": \"ada@kafe.example\", \"custom:team\": \"kassa\"}.");

        private static ConnectorConfigField[] GroupFields(bool create) => new[]
        {
            PoolField,
            GroupField,
            Field("description", "Description", "What the group is for."),
            Field("precedence", "Precedence", "Its rank among a user's groups, 0 the first.", ConnectorConfigFieldType.Integer),
            Field("role_arn", "IAM role", "The role its users take - arn:aws:iam::123456789012:role/Kassa." + (create ? string.Empty : " At least one of the three.")),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("users", "Get users", "A pool's users, by an attribute if asked - n8n's Get Many.")
            {
                Parameters = new(new[]
                {
                    PoolField,
                    Field("filter_attribute", "Filter on", "username, email, phone_number, name, given_name, family_name, preferred_username, cognito:user_status, status or sub."),
                    Field("filter_value", "Starting with", "The value it starts with - as n8n's filter."),
                    Field("exact", "Exactly", "true: the whole value, not its start.", ConnectorConfigFieldType.Boolean),
                    SimpleField,
                    MaxField,
                }),
            },
            new ConnectorOperation("user", "Get a user", "n8n's Get.") { Parameters = new(new[] { PoolField, UserField, SimpleField }) },
            new ConnectorOperation("user_create", "Create a user", "Invited as the pool says - n8n's Create. Never a password: Cognito makes the temporary one.")
            {
                Parameters = new(new[]
                {
                    PoolField,
                    Field("username", "Username", "Their name - or their email or phone number, if the pool signs in with them.", required: true),
                    AttributesField,
                    Field("delivery", "Invitation by", "email, sms, or email,sms."),
                    Field("message_action", "Invitation", "resend: to a user who exists, again; suppress: none sent."),
                    Field("force_alias", "Take the alias", "true: their email or phone number taken from a user who has it as an alias.", ConnectorConfigFieldType.Boolean),
                }),
            },
            new ConnectorOperation("user_update", "Update a user", "Their attributes - n8n's Update.") { Parameters = new(new[] { PoolField, UserField, AttributesField with { Required = true } }) },
            new ConnectorOperation("user_delete", "Delete a user", "n8n's Delete.") { Parameters = new(new[] { PoolField, UserField }) },
            new ConnectorOperation("user_add_to_group", "Add a user to a group", "n8n's Add to Group.") { Parameters = new(new[] { PoolField, UserField, GroupField }) },
            new ConnectorOperation("user_remove_from_group", "Remove a user from a group", "n8n's Remove From Group.") { Parameters = new(new[] { PoolField, UserField, GroupField }) },
            new ConnectorOperation("groups", "Get groups", "A pool's groups - n8n's Get Many.") { Parameters = new(new[] { PoolField, UsersWithField, MaxField }) },
            new ConnectorOperation("group", "Get a group", "n8n's Get.") { Parameters = new(new[] { PoolField, GroupField, UsersWithField }) },
            new ConnectorOperation("group_create", "Create a group", "n8n's Create.") { Parameters = new(GroupFields(create: true)) },
            new ConnectorOperation("group_update", "Update a group", "n8n's Update.") { Parameters = new(GroupFields(create: false)) },
            new ConnectorOperation("group_delete", "Delete a group", "n8n's Delete.") { Parameters = new(new[] { PoolField, GroupField }) },
            new ConnectorOperation("user_pool", "Get a user pool", "Its settings - n8n's Get.") { Parameters = new(new[] { PoolField, SimpleField }) },
            new ConnectorOperation("user_pools", "Get user pools", "The pools the key sees.") { Parameters = new(new[] { MaxField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var pool = Values.Get(context.Settings, "user_pool") is { } given ? PoolId(given) : null;
                var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
                if (pool is not null)
                {
                    var described = await session.CallAsync("DescribeUserPool", new JsonObject { ["UserPoolId"] = pool }, cancellationToken);
                    return ConnectionTestResult.Ok($"Amazon Cognito in {session.Region} answers: user pool {Text(described?["UserPool"]?["Name"]) ?? pool}.");
                }

                var answer = await session.CallAsync("ListUserPools", new JsonObject { ["MaxResults"] = 1 }, cancellationToken);
                return ConnectionTestResult.Ok($"Amazon Cognito in {session.Region} answers ({((answer?["UserPools"] as JsonArray)?.Count ?? 0) switch { 0 => "no user pools yet", _ => "its user pools listed" }}).");
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
                ?? throw new InvalidOperationException($"Amazon Cognito has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var simple = Flag(p, "simple") ?? true;
            var withUsers = Flag(p, "include_users") ?? false;
            var pool = operation == "user_pools" ? string.Empty
                : (Values.Get(p, "user_pool") ?? Values.Get(context.Settings, "user_pool")) is { } named ? PoolId(named)
                : throw new InvalidOperationException("Give the user pool - eu-west-1_AbC123dEf (user_pool), or the connection's.");
            var body = new JsonObject();
            if (pool.Length > 0)
            {
                body["UserPoolId"] = pool;
            }

            string user = string.Empty, group = string.Empty;
            if (operation is "user" or "user_update" or "user_delete" or "user_add_to_group" or "user_remove_from_group")
            {
                user = Name(p, "user", "a user's name");
                body["Username"] = user;
            }

            if (operation is "user_add_to_group" or "user_remove_from_group" or "group" or "group_create" or "group_update" or "group_delete")
            {
                group = Name(p, "group", "a group's name");
                body["GroupName"] = group;
            }

            switch (operation)
            {
                case "users":
                    if (Values.Get(p, "filter_attribute") is { } attribute)
                    {
                        var on = Filterable.FirstOrDefault(known => string.Equals(known, attribute, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException($"'{attribute}' is not an attribute Cognito filters users on - {string.Join(", ", Filterable)} (filter_attribute).");
                        var value = Values.Get(p, "filter_value") ?? throw new InvalidOperationException($"Give the value {on} is to start with (filter_value).");
                        body["Filter"] = $"{on} {((Flag(p, "exact") ?? false) ? "=" : "^=")} \"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
                    }
                    else if (Values.Get(p, "filter_value") is not null)
                    {
                        throw new InvalidOperationException("Give the attribute the value is of (filter_attribute).");
                    }

                    break;
                case "user_create":
                    {
                        var username = Name(p, "username", "a user's name");
                        body["Username"] = username;
                        var attributes = Attributes(p, required: false);
                        if (attributes.Count > 0)
                        {
                            body["UserAttributes"] = attributes;
                        }

                        var given = attributes.OfType<JsonObject>().Select(pair => Text(pair["Name"])).ToHashSet(StringComparer.Ordinal);
                        foreach (var (verified, of) in new[] { ("email_verified", "email"), ("phone_number_verified", "phone_number") })
                        {
                            if (given.Contains(verified) && !given.Contains(of))
                            {
                                throw new InvalidOperationException($"{verified} without {of}: give the {of} it verifies (attributes).");
                            }
                        }

                        if (Values.Get(p, "delivery") is { } delivery)
                        {
                            // Every medium named first, then each one's attribute: what is not email or sms is said before what is missing.
                            var asked = delivery.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(medium => medium.ToUpperInvariant()).Distinct()
                                .Select(medium => (Medium: medium, Needs: medium switch
                                {
                                    "EMAIL" => "email",
                                    "SMS" => "phone_number",
                                    _ => throw new InvalidOperationException($"'{medium.ToLowerInvariant()}' is not email or sms (delivery)."),
                                }))
                                .ToList();
                            var mediums = new JsonArray();
                            foreach (var (medium, needs) in asked)
                            {
                                mediums.Add(given.Contains(needs) ? medium : throw new InvalidOperationException($"An invitation by {medium.ToLowerInvariant()} needs the {needs} attribute (attributes)."));
                            }

                            body["DesiredDeliveryMediums"] = mediums;
                        }

                        if (Values.Get(p, "message_action") is { } action)
                        {
                            body["MessageAction"] = action.ToUpperInvariant() is ("RESEND" or "SUPPRESS") and var told ? told : throw new InvalidOperationException($"'{action}' is not resend or suppress (message_action).");
                        }

                        if (Flag(p, "force_alias") == true)
                        {
                            body["ForceAliasCreation"] = true;
                        }

                        break;
                    }

                case "user_update":
                    body["UserAttributes"] = Attributes(p, required: true);
                    break;
                case "group_create" or "group_update":
                    {
                        if (Values.Get(p, "description") is { } description)
                        {
                            body["Description"] = description.Length <= 2048 ? description : throw new InvalidOperationException("A group's description is at most 2,048 characters (description).");
                        }

                        if (Values.Get(p, "precedence") is { } precedence)
                        {
                            body["Precedence"] = int.TryParse(precedence, NumberStyles.None, CultureInfo.InvariantCulture, out var rank) ? rank : throw new InvalidOperationException($"'{precedence}' is not a number from 0 (precedence).");
                        }

                        if (Values.Get(p, "role_arn") is { } role)
                        {
                            body["RoleArn"] = RoleArn.IsMatch(role) ? role : throw new InvalidOperationException($"'{role}' is not an IAM role's ARN - arn:aws:iam::123456789012:role/Kassa (role_arn).");
                        }

                        if (operation == "group_update" && body.Count == 2)
                        {
                            throw new InvalidOperationException("Give what changes - a description, a precedence or an IAM role (description, precedence, role_arn).");
                        }

                        break;
                    }
            }

            var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "users":
                    await foreach (var found in PagesAsync(session, "ListUsers", body, "PaginationToken", "PaginationToken", "Users", "Limit", max, cancellationToken))
                    {
                        yield return Record(User(found, simple));
                    }

                    break;
                case "user":
                    {
                        var found = await session.CallAsync("AdminGetUser", body, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return Record(User(found, simple));
                        break;
                    }

                case "user_create":
                    yield return Record(User((await session.CallAsync("AdminCreateUser", body, cancellationToken))?["User"] as JsonObject ?? new JsonObject(), true));
                    break;
                case "user_update":
                    await session.CallAsync("AdminUpdateUserAttributes", body, cancellationToken);
                    yield return Fields(("user", user), ("updated", true));
                    break;
                case "user_delete":
                    await session.CallAsync("AdminDeleteUser", body, cancellationToken);
                    yield return Fields(("user", user), ("deleted", true));
                    break;
                case "user_add_to_group":
                    await session.CallAsync("AdminAddUserToGroup", body, cancellationToken);
                    yield return Fields(("user", user), ("group", group), ("added_to_group", true));
                    break;
                case "user_remove_from_group":
                    await session.CallAsync("AdminRemoveUserFromGroup", body, cancellationToken);
                    yield return Fields(("user", user), ("group", group), ("removed_from_group", true));
                    break;
                case "groups":
                    await foreach (var found in PagesAsync(session, "ListGroups", body, "NextToken", "NextToken", "Groups", "Limit", max, cancellationToken))
                    {
                        yield return Record(withUsers ? await WithUsersAsync(session, pool, found, cancellationToken) : found);
                    }

                    break;
                case "group":
                    {
                        var found = (await session.CallAsync("GetGroup", body, cancellationToken))?["Group"] as JsonObject ?? new JsonObject();
                        yield return Record(withUsers ? await WithUsersAsync(session, pool, found, cancellationToken) : found);
                        break;
                    }

                case "group_create":
                    yield return Record((await session.CallAsync("CreateGroup", body, cancellationToken))?["Group"] as JsonObject ?? new JsonObject());
                    break;
                case "group_update":
                    yield return Record((await session.CallAsync("UpdateGroup", body, cancellationToken))?["Group"] as JsonObject ?? new JsonObject());
                    break;
                case "group_delete":
                    await session.CallAsync("DeleteGroup", body, cancellationToken);
                    yield return Fields(("group", group), ("deleted", true));
                    break;
                case "user_pool":
                    {
                        var found = (await session.CallAsync("DescribeUserPool", body, cancellationToken))?["UserPool"] as JsonObject ?? new JsonObject();
                        if (simple)
                        {
                            foreach (var setting in LongPoolSettings)
                            {
                                found.Remove(setting);
                            }
                        }

                        yield return Record(found);
                        break;
                    }

                default:
                    await foreach (var found in PagesAsync(session, "ListUserPools", body, "NextToken", "NextToken", "UserPools", "MaxResults", max, cancellationToken))
                    {
                        yield return Record(found);
                    }

                    break;
            }
        }

        private AwsJsonSession Session(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout) =>
            AwsJsonSession.Of("Amazon Cognito", "cognito-idp", "AWSCognitoIdentityProviderService", endpoint, settings, credentials, egress, timeout, time);

        // A list page by page by Cognito's token, a page no larger than what is still wanted (at most 60).
        private static async IAsyncEnumerable<JsonObject> PagesAsync(AwsJsonSession session, string action, JsonObject body, string tokenIn, string tokenOut, string items, string size, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var read = 0;
            string? next = null;
            do
            {
                body[size] = Math.Min(PageSize, max - read);
                if (next is null)
                {
                    body.Remove(tokenIn);
                }
                else
                {
                    body[tokenIn] = next;
                }

                var page = await session.CallAsync(action, body, cancellationToken) as JsonObject;
                foreach (var item in (page?[items] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return item;
                    if (++read >= max)
                    {
                        yield break;
                    }
                }

                next = Text(page?[tokenOut]) is { Length: > 0 } token ? token : null;
            }
            while (next is not null);
        }

        // A group with its users (Cognito's ListUsersInGroup, every page), each as a user record is.
        private static async Task<JsonObject> WithUsersAsync(AwsJsonSession session, string pool, JsonObject group, CancellationToken cancellationToken)
        {
            var users = new JsonArray();
            await foreach (var user in PagesAsync(session, "ListUsersInGroup", new JsonObject { ["UserPoolId"] = pool, ["GroupName"] = Text(group["GroupName"]) }, "NextToken", "NextToken", "Users", "Limit", int.MaxValue, cancellationToken))
            {
                users.Add(User((JsonObject)user.DeepClone(), true)); // its page still holds the one it yielded
            }

            group["Users"] = users;
            return group;
        }

        // A user's attributes - Cognito's Name/Value pairs - as its own fields when simplified.
        private static JsonObject User(JsonObject user, bool simple)
        {
            if (!simple)
            {
                return user;
            }

            foreach (var key in new[] { "Attributes", "UserAttributes" })
            {
                if (user[key] is JsonArray pairs)
                {
                    user.Remove(key);
                    foreach (var pair in pairs.OfType<JsonObject>())
                    {
                        if (Text(pair["Name"]) is { Length: > 0 } name)
                        {
                            user[name] = Text(pair["Value"]) ?? string.Empty;
                        }
                    }
                }
            }

            return user;
        }

        private static JsonArray Attributes(IReadOnlyDictionary<string, string> p, bool required)
        {
            var pairs = new JsonArray();
            if (Values.Get(p, "attributes") is not { } given)
            {
                return required ? throw new InvalidOperationException("Give the attributes - a JSON object, {\"email\": \"ada@kafe.example\"} (attributes).") : pairs;
            }

            JsonObject? attributes;
            try
            {
                attributes = JsonNode.Parse(given) as JsonObject;
            }
            catch (JsonException)
            {
                attributes = null;
            }

            if (attributes is null || attributes.Count == 0)
            {
                throw new InvalidOperationException("The attributes are a JSON object of names and values - {\"email\": \"ada@kafe.example\"} (attributes).");
            }

            foreach (var (name, value) in attributes)
            {
                if (!Attribute.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{name}' is not an attribute's name - email, or custom:team (attributes).");
                }

                var text = value switch
                {
                    JsonValue plain when plain.TryGetValue<string>(out var s) => s,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b ? "true" : "false",
                    JsonValue number when number.GetValueKind() == JsonValueKind.Number => number.ToJsonString(),
                    _ => throw new InvalidOperationException($"{name}'s value is not a text, a number or true or false (attributes)."),
                };
                pairs.Add(new JsonObject { ["Name"] = name, ["Value"] = text.Length <= 2048 ? text : throw new InvalidOperationException($"{name}'s value is longer than Cognito's 2,048 characters (attributes).") });
            }

            return pairs;
        }

        private static string PoolId(string given) =>
            given.Length <= 55 && Pool.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a user pool's id - eu-west-1_AbC123dEf (user_pool).");

        private static string Name(IReadOnlyDictionary<string, string> p, string parameter, string what) =>
            Values.Get(p, parameter) is { } given
                ? Named.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what} Cognito takes - no spaces, at most 128 ({parameter}).")
                : throw new InvalidOperationException($"Give {what} ({parameter}).");

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) => Values.Get(p, name) switch
        {
            null => null,
            var given when string.Equals(given, "true", StringComparison.OrdinalIgnoreCase) => true,
            var given when string.Equals(given, "false", StringComparison.OrdinalIgnoreCase) => false,
            var given => throw new InvalidOperationException($"'{given}' is not true or false ({name})."),
        };

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

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
    }
}
