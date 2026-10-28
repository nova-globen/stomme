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

namespace Stomme.Modules.Connectors.Messaging
{
    /// <summary>
    /// The rest of n8n's Mattermost node (BL-229) beside its message posted - channels made, archived, restored, searched, counted
    /// and joined, their members; a message to one user only and a post deleted; reactions added, removed and listed; users made,
    /// deactivated, listed, found by e-mail or id, invited - over Mattermost's API v4 with the connection's token.
    /// </summary>
    /// <remarks>
    /// Measured against Mattermost Team Edition 10.11.25 (<c>scripts/mattermost-test-server.ps1</c>): a reaction is saved only as
    /// the token's own user - another's is a 403, "you cannot save reaction for the other user" - so a reaction's user is the
    /// token's unless given, and another refused before anything is sent; a reaction removed that is not there is answered "OK"
    /// all the same, so the post's reactions are read first and a delete says whether there was one; a channel's name must be
    /// lowercase letters and digits (a 400), an id 26 of them (a malformed one a 400, not a 404), a sort one the list it sorts
    /// knows (a 400): each held here before anything is sent. n8n's User - Create sends an e-mail user's password as a node
    /// parameter; a password is never a step's parameter here, so a user signing in by e-mail is invited instead (User - Invite),
    /// and one signing in through a service (GitLab, Google, LDAP, Office 365, SAML, OpenID) is made with that service's id.
    /// A team by its id or its name, a user by its id or its username.
    /// </remarks>
    internal static class MattermostApi
    {
        public const int DefaultMax = 100; // n8n's limit

        private const int PageSize = 200; // Mattermost's own most

        private static readonly Regex IdPattern = new("^[a-z0-9]{26}$", RegexOptions.CultureInvariant);

        private static readonly Regex ChannelName = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex UserName = new("^[a-z][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex Emoji = new("^[a-z0-9_+-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s]{1,64}@[^@\\s]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly string[] Services = { "gitlab", "google", "ldap", "office365", "saml", "openid" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static ConnectorConfigField Text(string name, string label, bool required, string description) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField ChannelField = Text("channel", "Channel", true, "The channel's id - 26 letters and digits, from its View Info.");

        private static readonly ConnectorConfigField TeamField = Text("team", "Team", true, "The team's id or its name - kafe.");

        private static readonly ConnectorConfigField PostField = Text("post", "Post", true, "The post's id - 26 letters and digits.");

        private static readonly ConnectorConfigField UserField = Text("user", "User", true, "The user's id or username.");

        private static ConnectorOperation Op(string name, string label, string description, params ConnectorConfigField[] fields) => new(name, label, description) { Parameters = new(fields) };

        public static IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            Op("message_ephemeral", "Send a message only one user sees", "n8n's Message - Post Ephemeral.", ChannelField, UserField, new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: true, Description: "The message.")),
            Op("message_delete", "Delete a post", "n8n's Message - Delete.", PostField),
            Op("channel_create", "Create a channel", "n8n's Channel - Create.", TeamField, Text("name", "Name", true, "Its name in its address - lowercase letters, digits, - and _: bestallningar."),
                Text("display_name", "Display name", true, "Beställningar."), Text("type", "Type", false, "public or private - public by default."), Text("purpose", "Purpose", false, "What it is for."), Text("header", "Header", false, "Its header.")),
            Op("channel_delete", "Archive a channel", "n8n's Channel - Delete: Mattermost archives it, and it can be restored.", ChannelField),
            Op("channel_restore", "Restore a channel", "n8n's Channel - Restore.", ChannelField),
            Op("channel_add_user", "Add a user to a channel", "n8n's Channel - Add User.", ChannelField, UserField),
            Op("channel_members", "Get a channel's members", "n8n's Channel - Members.", ChannelField, new ConnectorConfigField("resolve", "As users", ConnectorConfigFieldType.Boolean, Required: false, Description: "Each member as its user, true or false - false by default."), MaxField),
            Op("channel_search", "Search a team's channels", "n8n's Channel - Search.", TeamField, Text("term", "Search", true, "What a channel's name holds."), MaxField),
            Op("channel_statistics", "Get a channel's statistics", "n8n's Channel - Statistics.", ChannelField),
            Op("reaction_create", "Add a reaction", "n8n's Reaction - Create: as the token's own user, the only one Mattermost saves one as.", PostField, Text("emoji", "Emoji", true, "Its name - coffee, or :coffee:."), Text("user", "User", false, "The token's own user - Mattermost refuses another's.")),
            Op("reaction_delete", "Remove a reaction", "n8n's Reaction - Delete: says whether there was one.", PostField, Text("emoji", "Emoji", true, "Its name - coffee."), Text("user", "User", false, "The token's own user unless an administrator removes another's.")),
            Op("reactions", "Get a post's reactions", "n8n's Reaction - Get Many.", PostField, MaxField),
            Op("user_create", "Create a user signing in through a service", "n8n's User - Create, for a user of GitLab, Google, LDAP, Office 365, SAML or OpenID; one signing in by e-mail is invited (user_invite): a password is never a step's parameter.",
                Text("username", "Username", true, "ada - lowercase, starting with a letter."), Text("email", "E-mail", true, "ada@kafe.example."), Text("auth_service", "Sign-in service", true, "gitlab, google, ldap, office365, saml or openid."),
                Text("auth_data", "Its id there", true, "The user's id with that service."), Text("first_name", "First name", false, "Ada."), Text("last_name", "Last name", false, "Lund."), Text("nickname", "Nickname", false, "Adis."), Text("locale", "Language", false, "sv, en.")),
            Op("user_deactivate", "Deactivate a user", "n8n's User - Deactivate.", UserField),
            Op("users", "Get users", "n8n's User - Get Many.", Text("in_team", "In team", false, "A team's id or name."), Text("not_in_team", "Not in team", false, "A team's id or name."),
                Text("in_channel", "In channel", false, "A channel's id."), Text("not_in_channel", "Not in channel", false, "A channel's id - with in_team."),
                Text("sort", "Sort", false, "username; created_at or last_activity_at with in_team; status with in_channel."), MaxField),
            Op("user_by_email", "Get a user by e-mail", "n8n's User - Get by Email.", Text("email", "E-mail", true, "ada@kafe.example.")),
            Op("users_by_id", "Get users by id", "n8n's User - Get by ID.", Text("users", "Users", true, "Their ids, by commas."), Text("since", "Changed since", false, "Only those changed since - 2026-10-01T08:00.")),
            Op("user_invite", "Invite users to a team", "n8n's User - Invite: Mattermost e-mails them, when its server sends e-mail.", TeamField, Text("emails", "E-mails", true, "By commas - ada@kafe.example, bo@kafe.example.")),
        };

        public static async IAsyncEnumerable<ConnectorRecord> RunAsync(ConnectorExtractContext context, string name, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var p = context.Parameters;
            var max = Count(p, "max", DefaultMax);
            switch (name)
            {
                case "message_ephemeral":
                    {
                        var channel = Id(p, "channel");
                        var text = Values.Get(p, "text") ?? throw new InvalidOperationException("Give the text - the message (text).");
                        using var session = Session.Open(context);
                        var user = await session.UserAsync(Required(p, "user"), cancellationToken);
                        yield return Record(await session.ObjectAsync(HttpMethod.Post, "posts/ephemeral", new JsonObject { ["user_id"] = user, ["post"] = new JsonObject { ["channel_id"] = channel, ["message"] = text } }, cancellationToken));
                        break;
                    }

                case "message_delete":
                case "channel_delete":
                    {
                        var (key, route) = name == "message_delete" ? ("post", "posts/") : ("channel", "channels/");
                        var id = Id(p, key);
                        using var session = Session.Open(context);
                        await session.SendAsync(HttpMethod.Delete, route + id, null, cancellationToken);
                        yield return Done(("id", id), (name == "message_delete" ? "deleted" : "archived", true));
                        break;
                    }

                case "channel_create":
                    {
                        var channelName = Required(p, "name");
                        if (!ChannelName.IsMatch(channelName))
                        {
                            throw new InvalidOperationException($"'{channelName}' is not a channel's name - lowercase letters, digits, - and _, at most 64: bestallningar (name).");
                        }

                        var display = OneLine(Required(p, "display_name"), "display_name", 64);
                        var type = (Values.Get(p, "type") ?? "public").Trim().ToLowerInvariant() switch
                        {
                            "public" or "o" => "O",
                            "private" or "p" => "P",
                            var other => throw new InvalidOperationException($"'{other}' is not public or private (type)."),
                        };
                        using var session = Session.Open(context);
                        var body = new JsonObject { ["team_id"] = await session.TeamAsync(Required(p, "team"), cancellationToken), ["name"] = channelName, ["display_name"] = display, ["type"] = type };
                        foreach (var (key, most) in new[] { ("purpose", 250), ("header", 1024) })
                        {
                            if (Values.Get(p, key) is { } given)
                            {
                                body[key] = OneLine(given, key, most);
                            }
                        }

                        yield return Record(await session.ObjectAsync(HttpMethod.Post, "channels", body, cancellationToken));
                        break;
                    }

                case "channel_restore":
                    {
                        var channel = Id(p, "channel");
                        using var session = Session.Open(context);
                        yield return Record(await session.ObjectAsync(HttpMethod.Post, $"channels/{channel}/restore", null, cancellationToken));
                        break;
                    }

                case "channel_add_user":
                    {
                        var channel = Id(p, "channel");
                        using var session = Session.Open(context);
                        var user = await session.UserAsync(Required(p, "user"), cancellationToken);
                        yield return Record(await session.ObjectAsync(HttpMethod.Post, $"channels/{channel}/members", new JsonObject { ["user_id"] = user }, cancellationToken));
                        break;
                    }

                case "channel_members":
                    {
                        var channel = Id(p, "channel");
                        var resolve = Flag(p, "resolve") ?? false;
                        using var session = Session.Open(context);
                        var found = 0;
                        for (var page = 0; found < max; page++)
                        {
                            var members = (await session.ListAsync($"channels/{channel}/members?page={page}&per_page={PageSize}", cancellationToken)).Take(max - found).ToList();
                            var items = resolve && members.Count > 0
                                ? await session.ListAsync("users/ids", cancellationToken, new JsonArray(members.Select(member => (JsonNode?)JsonValue.Create(Text(member["user_id"]))).ToArray()))
                                : members;
                            foreach (var item in items)
                            {
                                found++;
                                yield return Record(item);
                            }

                            if (members.Count < PageSize)
                            {
                                break;
                            }
                        }

                        break;
                    }

                case "channel_search":
                    {
                        var term = OneLine(Required(p, "term"), "term", 64);
                        using var session = Session.Open(context);
                        var team = await session.TeamAsync(Required(p, "team"), cancellationToken);
                        foreach (var item in (await session.ListAsync($"teams/{team}/channels/search", cancellationToken, new JsonObject { ["term"] = term })).Take(max))
                        {
                            yield return Record(item);
                        }

                        break;
                    }

                case "channel_statistics":
                    {
                        var channel = Id(p, "channel");
                        using var session = Session.Open(context);
                        yield return Record(await session.ObjectAsync(HttpMethod.Get, $"channels/{channel}/stats", null, cancellationToken));
                        break;
                    }

                case "reaction_create":
                case "reaction_delete":
                    {
                        var post = Id(p, "post");
                        var emoji = Required(p, "emoji").Trim().Trim(':').ToLowerInvariant();
                        if (!Emoji.IsMatch(emoji))
                        {
                            throw new InvalidOperationException($"'{Values.Get(p, "emoji")}' is not an emoji's name - coffee, or :coffee: (emoji).");
                        }

                        using var session = Session.Open(context);
                        var me = await session.MeAsync(cancellationToken);
                        var user = Values.Get(p, "user") is { } given ? await session.UserAsync(given, cancellationToken) : me;
                        if (name == "reaction_create")
                        {
                            if (user != me)
                            {
                                // Mattermost saves a reaction only as the token's own user: another's is a 403.
                                throw new InvalidOperationException("Mattermost saves a reaction only as the token's own user - leave the user out (user).");
                            }

                            yield return Record(await session.ObjectAsync(HttpMethod.Post, "reactions", new JsonObject { ["user_id"] = user, ["post_id"] = post, ["emoji_name"] = emoji }, cancellationToken));
                            break;
                        }

                        // Mattermost answers "OK" to removing a reaction that is not there: read first, and say whether there was one.
                        var had = (await session.ListAsync($"posts/{post}/reactions", cancellationToken)).Any(item => Text(item["user_id"]) == user && Text(item["emoji_name"]) == emoji);
                        if (had)
                        {
                            await session.SendAsync(HttpMethod.Delete, $"users/{user}/posts/{post}/reactions/{Uri.EscapeDataString(emoji)}", null, cancellationToken);
                        }

                        yield return Done(("post_id", post), ("user_id", user), ("emoji_name", emoji), ("deleted", had));
                        break;
                    }

                case "reactions":
                    {
                        var post = Id(p, "post");
                        using var session = Session.Open(context);
                        foreach (var item in (await session.ListAsync($"posts/{post}/reactions", cancellationToken)).Take(max))
                        {
                            yield return Record(item);
                        }

                        break;
                    }

                case "user_create":
                    {
                        var service = Required(p, "auth_service").Trim().ToLowerInvariant();
                        if (service == "email")
                        {
                            throw new InvalidOperationException("A user signing in by e-mail needs a password, and a password is never a step's parameter here - invite them with user_invite, or make them with their sign-in service (auth_service).");
                        }

                        var username = Required(p, "username").Trim();
                        var body = new JsonObject
                        {
                            ["username"] = UserName.IsMatch(username) ? username : throw new InvalidOperationException($"'{username}' is not a username - a lowercase letter, then up to 63 letters, digits, . - or _ (username)."),
                            ["email"] = Address(Required(p, "email"), "email"),
                            ["auth_service"] = Services.Contains(service) ? service : throw new InvalidOperationException($"'{service}' is not gitlab, google, ldap, office365, saml or openid (auth_service)."),
                            ["auth_data"] = OneLine(Required(p, "auth_data"), "auth_data", 128),
                        };
                        foreach (var (key, most) in new[] { ("first_name", 64), ("last_name", 64), ("nickname", 64), ("locale", 5) })
                        {
                            if (Values.Get(p, key) is { } given)
                            {
                                body[key] = OneLine(given, key, most);
                            }
                        }

                        using var session = Session.Open(context);
                        yield return Record(await session.ObjectAsync(HttpMethod.Post, "users", body, cancellationToken));
                        break;
                    }

                case "user_deactivate":
                    {
                        using var session = Session.Open(context);
                        var user = await session.UserAsync(Required(p, "user"), cancellationToken);
                        await session.SendAsync(HttpMethod.Delete, "users/" + user, null, cancellationToken);
                        yield return Done(("id", user), ("deactivated", true));
                        break;
                    }

                case "users":
                    {
                        var sort = Values.Get(p, "sort") is { } given ? Regex.Replace(given.Trim(), "(?<=[a-z])(?=[A-Z])", "_").ToLowerInvariant() : null;
                        var inChannel = Values.Get(p, "in_channel") is { } inside ? IdOf(inside, "in_channel") : null;
                        var notInChannel = Values.Get(p, "not_in_channel") is { } outside ? IdOf(outside, "not_in_channel") : null;
                        var hasTeam = Values.Get(p, "in_team") is not null;
                        if (sort is not null and not "username" && !(sort is "created_at" or "last_activity_at" && hasTeam) && !(sort == "status" && inChannel is not null))
                        {
                            throw new InvalidOperationException($"'{Values.Get(p, "sort")}' sorts no list here - username; created_at or last_activity_at with in_team; status with in_channel (sort).");
                        }

                        if (notInChannel is not null && !hasTeam)
                        {
                            throw new InvalidOperationException("Mattermost lists those not in a channel only within a team - give in_team too (not_in_channel).");
                        }

                        using var session = Session.Open(context);
                        var query = new List<string>();
                        foreach (var key in new[] { "in_team", "not_in_team" })
                        {
                            if (Values.Get(p, key) is { } team)
                            {
                                query.Add(key + "=" + await session.TeamAsync(team, cancellationToken));
                            }
                        }

                        if (inChannel is not null)
                        {
                            query.Add("in_channel=" + inChannel);
                        }

                        if (notInChannel is not null)
                        {
                            query.Add("not_in_channel=" + notInChannel);
                        }

                        if (sort is not null and not "username")
                        {
                            query.Add("sort=" + sort); // username is Mattermost's own order
                        }

                        var found = 0;
                        for (var page = 0; found < max; page++)
                        {
                            var items = await session.ListAsync($"users?{string.Join("&", query.Append($"page={page}").Append($"per_page={PageSize}"))}", cancellationToken);
                            foreach (var item in items.Take(max - found))
                            {
                                found++;
                                yield return Record(item);
                            }

                            if (items.Count < PageSize)
                            {
                                break;
                            }
                        }

                        break;
                    }

                case "user_by_email":
                    {
                        var email = Address(Required(p, "email"), "email");
                        using var session = Session.Open(context);
                        yield return Record(await session.ObjectAsync(HttpMethod.Get, "users/email/" + Uri.EscapeDataString(email), null, cancellationToken));
                        break;
                    }

                case "users_by_id":
                    {
                        var ids = Required(p, "users").Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).Select(part => IdOf(part, "users")).Distinct().ToArray();
                        if (ids.Length is 0 or > 200)
                        {
                            throw new InvalidOperationException("Give 1 to 200 users' ids, by commas (users).");
                        }

                        var since = Values.Get(p, "since") is { } changed ? "?since=" + Moment(changed, "since").ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : string.Empty;
                        using var session = Session.Open(context);
                        foreach (var item in await session.ListAsync("users/ids" + since, cancellationToken, new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())))
                        {
                            yield return Record(item);
                        }

                        break;
                    }

                case "user_invite":
                    {
                        var emails = Required(p, "emails").Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).Select(part => Address(part, "emails")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (emails.Length is 0 or > 20)
                        {
                            throw new InvalidOperationException("Give 1 to 20 e-mail addresses, by commas (emails).");
                        }

                        using var session = Session.Open(context);
                        var team = await session.TeamAsync(Required(p, "team"), cancellationToken);
                        await session.SendAsync(HttpMethod.Post, $"teams/{team}/invite/email", new JsonArray(emails.Select(email => (JsonNode?)JsonValue.Create(email)).ToArray()), cancellationToken);
                        yield return Done(("team_id", team), ("invited", string.Join(", ", emails)));
                        break;
                    }

                default:
                    throw new InvalidOperationException($"Mattermost has no operation '{name}' - message, and {string.Join(", ", Operations.Select(operation => operation.Name))}.");
            }
        }

        private static string Required(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) ?? throw new InvalidOperationException($"Give the {name.Replace('_', ' ')} ({name}).");

        private static string Id(IReadOnlyDictionary<string, string> p, string name) => IdOf(Required(p, name), name);

        private static string IdOf(string given, string name) =>
            IdPattern.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a Mattermost id - 26 lowercase letters and digits ({name}).");

        private static string OneLine(string value, string name, int most) =>
            value.Length <= most && !value.Any(char.IsControl) ? value : throw new InvalidOperationException($"A {name.Replace('_', ' ')} is one line of at most {most} characters ({name}).");

        private static string Address(string value, string name) =>
            Email.IsMatch(value.Trim()) ? value.Trim() : throw new InvalidOperationException($"'{value}' is not an e-mail address ({name}).");

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? bool.TryParse(given, out var flag) ? flag : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

        private static int Count(IReadOnlyDictionary<string, string> p, string name, int fallback) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{given}' is not a number above 0 ({name}).")
                : fallback;

        private static readonly string[] Moments = { "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.fffK" };

        private static DateTimeOffset Moment(string value, string name) =>
            DateTimeOffset.TryParseExact(value.Trim(), Moments, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment
                : throw new InvalidOperationException($"'{value}' is not a moment - 2026-10-21T08:00, or a day ({name}).");

        private static ConnectorRecord Done(params (string Key, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal));

        private static ConnectorRecord Record(JsonObject data) => new(data.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string token;
            private string? me;

            private Session(HttpClient client, string api, string token)
            {
                this.client = client;
                this.api = api;
                this.token = token;
            }

            public static Session Open(ConnectorExtractContext context)
            {
                var root = (context.Endpoint ?? string.Empty).Trim().TrimEnd('/');
                if (root.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase))
                {
                    root = root[..^"/api/v4".Length]; // the API's own address given: the server's
                }

                if (!ChatMessages.TryUnder(root, "api/v4", out var api))
                {
                    throw new InvalidOperationException("Give the server's address - https://chat.kafe.example, with no query.");
                }

                var token = Values.Get(context.Credentials, "token") ?? throw new InvalidOperationException("The Mattermost connection has no access token.");
                return new Session(context.Egress.CreateHttpClient(context.Timeout), api.AbsoluteUri.TrimEnd('/') + "/", token);
            }

            public void Dispose() => client.Dispose();

            public async Task<string> MeAsync(CancellationToken cancellationToken) =>
                me ??= Text((await ObjectAsync(HttpMethod.Get, "users/me", null, cancellationToken))["id"]) ?? throw new InvalidOperationException("Mattermost answered without the token's user - check the address.");

            /// <summary>A team's id, from its id or its name.</summary>
            public async Task<string> TeamAsync(string given, CancellationToken cancellationToken) =>
                IdPattern.IsMatch(given.Trim()) ? given.Trim()
                    : Text((await ObjectAsync(HttpMethod.Get, "teams/name/" + Uri.EscapeDataString(ChannelName.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a team's id or name - kafe (team).")), null, cancellationToken))["id"])
                        ?? throw new InvalidOperationException($"Mattermost has no team '{given}' (team).");

            /// <summary>A user's id, from its id or its username.</summary>
            public async Task<string> UserAsync(string given, CancellationToken cancellationToken) =>
                IdPattern.IsMatch(given.Trim()) ? given.Trim()
                    : Text((await ObjectAsync(HttpMethod.Get, "users/username/" + Uri.EscapeDataString(UserName.IsMatch(given.Trim().TrimStart('@')) ? given.Trim().TrimStart('@') : throw new InvalidOperationException($"'{given}' is not a user's id or username - ada (user).")), null, cancellationToken))["id"])
                        ?? throw new InvalidOperationException($"Mattermost has no user '{given}' (user).");

            public async Task<JsonObject> ObjectAsync(HttpMethod method, string route, JsonNode? body, CancellationToken cancellationToken) =>
                await SendAsync(method, route, body, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Mattermost answered without the item - check the address: a sign-in page or a proxy answers so.");

            public async Task<List<JsonObject>> ListAsync(string route, CancellationToken cancellationToken, JsonNode? body = null) =>
                (await SendAsync(body is null ? HttpMethod.Get : HttpMethod.Post, route, body, cancellationToken)) switch
                {
                    JsonArray list => list.OfType<JsonObject>().ToList(),
                    null => new List<JsonObject>(), // a post without reactions answers null
                    _ => throw new InvalidOperationException("Mattermost answered with what is not a list - check the address: a sign-in page or a proxy answers so."),
                };

            /// <summary>A call with the token as a bearer; Mattermost's JSON, or its status and its message.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("Mattermost answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                    }

                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json;
                }

                var said = Text(json?["message"]) ?? string.Empty;
                said = said.Length > 300 ? said[..300] : said;
                var status = (int)response.StatusCode;
                throw new InvalidOperationException($"Mattermost answered HTTP {status}{(said.Length == 0 ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + (status == 401 ? " - check the access token." : string.Empty));
            }
        }
    }
}
