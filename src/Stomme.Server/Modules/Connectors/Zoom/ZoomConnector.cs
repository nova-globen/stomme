#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

namespace Stomme.Modules.Connectors.Zoom
{
    /// <summary>
    /// Zoom (BL-229, n8n's Zoom node: its meetings created, got, read, updated and deleted): an account over Zoom's API
    /// (<c>https://api.zoom.us/v2</c>) through a server-to-server OAuth app - its account id (the <c>account_id</c> setting) and
    /// client, exchanged at Zoom's token endpoint (<c>https://zoom.us/oauth/token</c>, the client as HTTP Basic) through the shared
    /// <see cref="OAuth2TokenProvider"/>, the access token sent as a bearer. A test names the user.
    /// </summary>
    /// <remarks>
    /// A user is <c>me</c>, an id or an e-mail address; a meeting its id - each held to its shape before anything is sent.
    /// <c>meetings</c>: a user's meetings of a kind (scheduled, live, upcoming, upcoming_meetings or previous_meetings), page by
    /// page by Zoom's token up to <c>max</c> (1,000); <c>get_meeting</c> (an occurrence, or the previous ones), <c>create_meeting</c>,
    /// <c>update_meeting</c> - a topic, a kind (instant, scheduled, recurring or recurring_fixed, or Zoom's numbers), a start (in
    /// UTC, or the meeting's own time zone's clock when one is given), a duration in minutes, a time zone, a passcode, an agenda,
    /// whom it is scheduled for and Zoom's settings as a JSON object - <c>delete_meeting</c> (an occurrence, the reminder mail)
    /// and <c>end_meeting</c>. <c>registrants</c> (by their status) and <c>add_registrant</c>; <c>recordings</c> (a user's cloud
    /// recordings between two days); <c>users</c> (by their status). Each meeting a record of its id, topic, kind, status, start,
    /// duration, time zone, agenda, host and join link - never its start link, which starts the meeting as its host. A throttled
    /// call waits as Zoom says; a failure is thrown with its status, Zoom's code and its message.
    /// </remarks>
    public sealed class ZoomConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.zoom.us/v2";

        public const string DefaultTokenUrl = "https://zoom.us/oauth/token";

        public const string MeetingsOperation = "meetings";

        public const string GetMeetingOperation = "get_meeting";

        public const string CreateMeetingOperation = "create_meeting";

        public const string UpdateMeetingOperation = "update_meeting";

        public const string DeleteMeetingOperation = "delete_meeting";

        public const string EndMeetingOperation = "end_meeting";

        public const string RegistrantsOperation = "registrants";

        public const string AddRegistrantOperation = "add_registrant";

        public const string RecordingsOperation = "recordings";

        public const string UsersOperation = "users";

        public const int DefaultMax = 1000;

        private const int PageSize = 300;

        private static readonly Regex MeetingId = new("^[0-9]{9,12}$", RegexOptions.CultureInvariant);

        private static readonly Regex UserId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Occurrence = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly string[] MeetingKinds = { "scheduled", "live", "upcoming", "upcoming_meetings", "previous_meetings" };

        private static readonly (string Name, int Value)[] Types = { ("instant", 1), ("scheduled", 2), ("recurring", 3), ("recurring_fixed", 8) };

        // JSON as written: topics, agendas and their accents, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { MeetingsOperation, GetMeetingOperation, CreateMeetingOperation, UpdateMeetingOperation, DeleteMeetingOperation, EndMeetingOperation, RegistrantsOperation, AddRegistrantOperation, RecordingsOperation, UsersOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "zoom";

        public string DisplayName => "Zoom";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://zoom.us");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.zoom.us/docs/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("account_id", "Account id", ConnectorConfigFieldType.Text, Required: true,
                Description: "Your server-to-server OAuth app's account id - under the app's App Credentials. The token URL is Zoom's, https://zoom.us/oauth/token."),
        });

        private static readonly ConnectorConfigField MeetingField = new("meeting", "Meeting", ConnectorConfigFieldType.Text, Required: true, Description: "The meeting's id - 85746065432.");

        private static readonly ConnectorConfigField UserField = new("user", "User", ConnectorConfigFieldType.Text, Required: false, Description: "me, a user's id or e-mail address.") { Default = "me" };

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] MeetingWrite(bool create) => new[]
        {
            new ConnectorConfigField("topic", "Topic", ConnectorConfigFieldType.Text, Required: create, Description: "Weekly review."),
            new ConnectorConfigField("type", "Kind", ConnectorConfigFieldType.Text, Required: false, Description: "instant, scheduled, recurring or recurring_fixed.") { Default = create ? "scheduled" : null },
            new ConnectorConfigField("start_time", "Start", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-20T09:00:00+02:00 - or the time zone's clock, 2026-10-20T09:00:00, with a time zone."),
            new ConnectorConfigField("duration", "Minutes", ConnectorConfigFieldType.Text, Required: false, Description: "60."),
            new ConnectorConfigField("timezone", "Time zone", ConnectorConfigFieldType.Text, Required: false, Description: "Europe/Stockholm."),
            new ConnectorConfigField("password", "Passcode", ConnectorConfigFieldType.Text, Required: false, Description: "Up to ten letters, digits and @-_*."),
            new ConnectorConfigField("agenda", "Agenda", ConnectorConfigFieldType.Multiline, Required: false, Description: "What it is for."),
            new ConnectorConfigField("schedule_for", "Scheduled for", ConnectorConfigFieldType.Text, Required: false, Description: "The e-mail address of the user it is scheduled for."),
            new ConnectorConfigField("settings", "Settings", ConnectorConfigFieldType.Multiline, Required: false, Description: "Zoom's settings, a JSON object - {\"host_video\": true, \"waiting_room\": true, \"auto_recording\": \"cloud\"}."),
        };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(MeetingsOperation, "Get meetings", "A user's meetings of a kind - n8n's meeting Get Many.")
            {
                Parameters = Schema(UserField, new ConnectorConfigField("type", "Kind", ConnectorConfigFieldType.Text, Required: false, Description: "scheduled, live, upcoming, upcoming_meetings or previous_meetings.") { Default = "scheduled" }, Max()),
            },
            new ConnectorOperation(GetMeetingOperation, "Get a meeting", "One meeting - n8n's meeting Get.")
            {
                Parameters = Schema(
                    MeetingField,
                    new ConnectorConfigField("occurrence_id", "Occurrence", ConnectorConfigFieldType.Text, Required: false, Description: "One occurrence of a recurring meeting - its id."),
                    new ConnectorConfigField("show_previous_occurrences", "Previous occurrences", ConnectorConfigFieldType.Text, Required: false, Description: "true to see them too.")),
            },
            new ConnectorOperation(CreateMeetingOperation, "Create a meeting", "Schedules a meeting for a user - n8n's meeting Create.") { Parameters = Schema(new[] { UserField }.Concat(MeetingWrite(create: true)).ToArray()) },
            new ConnectorOperation(UpdateMeetingOperation, "Update a meeting", "Changes a meeting - n8n's meeting Update.") { Parameters = Schema(new[] { MeetingField }.Concat(MeetingWrite(create: false)).ToArray()) },
            new ConnectorOperation(DeleteMeetingOperation, "Delete a meeting", "Deletes a meeting, or one occurrence - n8n's meeting Delete.")
            {
                Parameters = Schema(
                    MeetingField,
                    new ConnectorConfigField("occurrence_id", "Occurrence", ConnectorConfigFieldType.Text, Required: false, Description: "Only this occurrence - its id."),
                    new ConnectorConfigField("schedule_for_reminder", "Tell the host", ConnectorConfigFieldType.Text, Required: false, Description: "true to mail the host and its alternatives.")),
            },
            new ConnectorOperation(EndMeetingOperation, "End a meeting", "Ends a meeting that is running.") { Parameters = Schema(MeetingField) },
            new ConnectorOperation(RegistrantsOperation, "Get registrants", "A meeting's registrants.")
            {
                Parameters = Schema(MeetingField, new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "approved, pending or denied.") { Default = "approved" }, Max()),
            },
            new ConnectorOperation(AddRegistrantOperation, "Add a registrant", "Registers someone for a meeting - its join link said.")
            {
                Parameters = Schema(
                    MeetingField,
                    new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: true, Description: "ada@example.com."),
                    new ConnectorConfigField("first_name", "First name", ConnectorConfigFieldType.Text, Required: true, Description: "Ada."),
                    new ConnectorConfigField("last_name", "Last name", ConnectorConfigFieldType.Text, Required: false, Description: "Lovelace.")),
            },
            new ConnectorOperation(RecordingsOperation, "Get recordings", "A user's cloud recordings between two days.")
            {
                Parameters = Schema(
                    UserField,
                    new ConnectorConfigField("from", "From", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01 - Zoom's default a day ago."),
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-12."),
                    Max()),
            },
            new ConnectorOperation(UsersOperation, "Get users", "The account's users.")
            {
                Parameters = Schema(new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "active, inactive or pending.") { Default = "active" }, Max()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, token, HttpMethod.Get, api + "/users/me", null, cancellationToken) as JsonObject ?? new JsonObject();
                var name = string.Join(" ", new[] { Text(me["first_name"]), Text(me["last_name"]) }.Where(part => !string.IsNullOrWhiteSpace(part)));
                return ConnectionTestResult.Ok($"Signed in as {(name.Length > 0 ? name : Text(me["email"]) ?? "the app's account")}{(name.Length > 0 && Text(me["email"]) is { } email ? $" ({email})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? MeetingsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Zoom has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var user = operation is MeetingsOperation or CreateMeetingOperation or RecordingsOperation
                ? Values.Get(p, "user") is { } who ? who == "me" || UserId.IsMatch(who) || IsAddress(who) ? Uri.EscapeDataString(who) : throw new InvalidOperationException($"'{who}' is not a user - me, an id or an e-mail address.") : "me"
                : null;
            var meeting = operation is GetMeetingOperation or UpdateMeetingOperation or DeleteMeetingOperation or EndMeetingOperation or RegistrantsOperation or AddRegistrantOperation
                ? Values.Get(p, "meeting") is { } id ? MeetingId.IsMatch(id.Replace(" ", "", StringComparison.Ordinal)) ? id.Replace(" ", "", StringComparison.Ordinal) : throw new InvalidOperationException($"'{id}' is not a meeting's id - 85746065432.") : throw new InvalidOperationException("Give the meeting - its id.")
                : null;
            var query = new List<string>();
            JsonObject? body = null;
            switch (operation)
            {
                case MeetingsOperation:
                    query.Add("type=" + Choice(Values.Get(p, "type") ?? "scheduled", "a kind of meetings", MeetingKinds));
                    break;
                case GetMeetingOperation:
                case DeleteMeetingOperation:
                    if (Values.Get(p, "occurrence_id") is { } occurrence)
                    {
                        query.Add("occurrence_id=" + (Occurrence.IsMatch(occurrence) ? occurrence : throw new InvalidOperationException($"'{occurrence}' is not an occurrence's id.")));
                    }

                    var flag = operation == GetMeetingOperation ? "show_previous_occurrences" : "schedule_for_reminder";
                    if (Values.Get(p, flag) is { } shown)
                    {
                        query.Add(flag + "=" + Flag(shown, flag.Replace('_', ' ')).ToString().ToLowerInvariant());
                    }

                    break;
                case CreateMeetingOperation:
                case UpdateMeetingOperation:
                    {
                        body = new JsonObject();
                        if (Values.Get(p, "topic") is { } topic)
                        {
                            body["topic"] = OneLine(topic, "topic");
                        }
                        else if (operation == CreateMeetingOperation)
                        {
                            throw new InvalidOperationException("Give the meeting's topic.");
                        }

                        if (Values.Get(p, "type") is not null || operation == CreateMeetingOperation)
                        {
                            var kind = Values.Get(p, "type") ?? "scheduled";
                            body["type"] = Types.Where(choice => string.Equals(choice.Name, kind, StringComparison.OrdinalIgnoreCase) || choice.Value.ToString(CultureInfo.InvariantCulture) == kind).Select(choice => (int?)choice.Value).FirstOrDefault()
                                ?? throw new InvalidOperationException($"'{kind}' is not a kind of meeting - instant, scheduled, recurring or recurring_fixed.");
                        }

                        var timezone = Values.Get(p, "timezone") is { } zone ? Zone(zone) : null;
                        if (Values.Get(p, "start_time") is { } start)
                        {
                            body["start_time"] = Start(start, timezone is not null);
                        }

                        if (Values.Get(p, "duration") is { } duration)
                        {
                            body["duration"] = int.TryParse(duration, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is > 0 and <= 1440 ? minutes : throw new InvalidOperationException($"'{duration}' is not a number of minutes - 1 to 1440.");
                        }

                        if (timezone is not null)
                        {
                            body["timezone"] = timezone;
                        }

                        if (Values.Get(p, "password") is { } password)
                        {
                            body["password"] = Regex.IsMatch(password, "^[A-Za-z0-9@_*-]{1,10}$", RegexOptions.CultureInvariant) ? password : throw new InvalidOperationException("The passcode is up to ten letters, digits and @-_* only.");
                        }

                        if (Values.Get(p, "agenda") is { } agenda)
                        {
                            body["agenda"] = agenda;
                        }

                        if (Values.Get(p, "schedule_for") is { } scheduleFor)
                        {
                            body["schedule_for"] = IsAddress(scheduleFor) ? scheduleFor : throw new InvalidOperationException($"'{scheduleFor}' is not an e-mail address (scheduled for).");
                        }

                        if (Values.Get(p, "settings") is { } settings)
                        {
                            body["settings"] = ObjectOf(settings, "settings are", "{\"host_video\": true}");
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - a topic, a kind, a start, a duration, a time zone, a passcode, an agenda, whom it is for or settings.");
                        }

                        break;
                    }

                case RegistrantsOperation:
                    query.Add("status=" + Choice(Values.Get(p, "status") ?? "approved", "a registrant's status", "approved", "pending", "denied"));
                    break;
                case AddRegistrantOperation:
                    body = new JsonObject
                    {
                        ["email"] = Values.Get(p, "email") is { } email ? IsAddress(email) ? email : throw new InvalidOperationException($"'{email}' is not an e-mail address.") : throw new InvalidOperationException("Give the registrant's e-mail address."),
                        ["first_name"] = OneLine(Values.Get(p, "first_name") ?? throw new InvalidOperationException("Give the registrant's first name."), "first name"),
                    };
                    if (Values.Get(p, "last_name") is { } last)
                    {
                        body["last_name"] = OneLine(last, "last name");
                    }

                    break;
                case RecordingsOperation:
                    foreach (var name in new[] { "from", "to" })
                    {
                        if (Values.Get(p, name) is { } day)
                        {
                            query.Add(name + "=" + (DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? day : throw new InvalidOperationException($"'{day}' is not a day ({name}) - 2026-10-01.")));
                        }
                    }

                    break;
                case UsersOperation:
                    query.Add("status=" + Choice(Values.Get(p, "status") ?? "active", "a user's status", "active", "inactive", "pending"));
                    break;
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (operation is MeetingsOperation or RegistrantsOperation or RecordingsOperation or UsersOperation)
            {
                var (path, root) = operation switch
                {
                    MeetingsOperation => ($"/users/{user}/meetings", "meetings"),
                    RegistrantsOperation => ($"/meetings/{meeting}/registrants", "registrants"),
                    RecordingsOperation => ($"/users/{user}/recordings", "meetings"),
                    _ => ("/users", "users"),
                };
                var read = 0;
                string? next = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(PageSize, max - read);
                    var page = await SendAsync(client, token, HttpMethod.Get, $"{api}{path}?{string.Join("&", query.Append("page_size=" + size.ToString(CultureInfo.InvariantCulture)))}{(next is null ? "" : "&next_page_token=" + Uri.EscapeDataString(next))}", null, cancellationToken);
                    foreach (var item in (page?[root] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return operation switch
                        {
                            MeetingsOperation => MeetingRecord(item),
                            RecordingsOperation => RecordingRecord(item),
                            _ => Record(item),
                        };
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    next = Text(page?["next_page_token"]) is { Length: > 0 } more ? more : null;
                }
                while (next is not null);

                yield break;
            }

            switch (operation)
            {
                case GetMeetingOperation:
                    yield return MeetingRecord(await SendAsync(client, token, HttpMethod.Get, $"{api}/meetings/{meeting}{(query.Count == 0 ? "" : "?" + string.Join("&", query))}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case CreateMeetingOperation:
                    yield return MeetingRecord(await SendAsync(client, token, HttpMethod.Post, $"{api}/users/{user}/meetings", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case AddRegistrantOperation:
                    {
                        var added = await SendAsync(client, token, HttpMethod.Post, $"{api}/meetings/{meeting}/registrants", body, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["registrant_id"] = Text(added["registrant_id"]),
                            ["meeting"] = meeting,
                            ["topic"] = Text(added["topic"]),
                            ["start_time"] = Text(added["start_time"]),
                            ["join_url"] = Text(added["join_url"]),
                        });
                        yield break;
                    }

                default:
                    {
                        var (method, url, sent) = operation switch
                        {
                            UpdateMeetingOperation => (HttpMethod.Patch, $"{api}/meetings/{meeting}", body),
                            EndMeetingOperation => (HttpMethod.Put, $"{api}/meetings/{meeting}/status", new JsonObject { ["action"] = "end" }),
                            _ => (HttpMethod.Delete, $"{api}/meetings/{meeting}{(query.Count == 0 ? "" : "?" + string.Join("&", query))}", (JsonObject?)null),
                        };
                        await SendAsync(client, token, method, url, sent, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["meeting"] = meeting, ["done"] = true });
                        yield break;
                    }
            }
        }

        // A meeting: its id, topic, kind, status, start, duration, time zone, agenda, host and join link - never its start link,
        // which starts the meeting as its host.
        private static ConnectorRecord MeetingRecord(JsonObject meeting) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(meeting["id"]),
            ["uuid"] = Text(meeting["uuid"]),
            ["topic"] = Text(meeting["topic"]),
            ["type"] = Types.Where(choice => Text(meeting["type"]) == choice.Value.ToString(CultureInfo.InvariantCulture)).Select(choice => choice.Name).FirstOrDefault() ?? Text(meeting["type"]),
            ["status"] = Text(meeting["status"]),
            ["start_time"] = Text(meeting["start_time"]),
            ["duration"] = Long(meeting["duration"]),
            ["timezone"] = Text(meeting["timezone"]),
            ["agenda"] = Text(meeting["agenda"]),
            ["host_id"] = Text(meeting["host_id"]),
            ["host_email"] = Text(meeting["host_email"]),
            ["join_url"] = Text(meeting["join_url"]),
            ["created_at"] = Text(meeting["created_at"]),
        });

        private static ConnectorRecord RecordingRecord(JsonObject meeting)
        {
            var files = meeting["recording_files"] as JsonArray ?? new JsonArray();
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(meeting["id"]),
                ["uuid"] = Text(meeting["uuid"]),
                ["topic"] = Text(meeting["topic"]),
                ["start_time"] = Text(meeting["start_time"]),
                ["duration"] = Long(meeting["duration"]),
                ["total_size"] = Long(meeting["total_size"]),
                ["recording_count"] = Long(meeting["recording_count"]) ?? files.Count,
                ["share_url"] = Text(meeting["share_url"]),
                ["file_types"] = string.Join(", ", files.Select(file => Text(file?["file_type"])).Where(type => type is not null).Distinct(StringComparer.Ordinal)),
            });
        }

        // A registrant or a user: its single values as Zoom names them; what is not one value as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item)
            {
                fields[name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        // A start: in UTC - or, with a time zone, that zone's clock as given, the way Zoom reads a start beside a time zone.
        private static string Start(string given, bool zoned)
        {
            if (zoned)
            {
                return DateTime.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) && !given.EndsWith('Z') && !Regex.IsMatch(given, "[+-][0-9]{2}:?[0-9]{2}$", RegexOptions.CultureInvariant)
                    ? local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"'{given}' is not the time zone's clock (start) - 2026-10-20T09:00:00, with no offset beside a time zone.");
            }

            return DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time (start) - 2026-10-20T09:00:00+02:00.");
        }

        // A time zone by its IANA name, as Zoom takes it - Europe/Stockholm, or UTC.
        private static string Zone(string given)
        {
            var zone = given.Trim();
            try
            {
                return (zone == "UTC" || zone.Contains('/', StringComparison.Ordinal)) && TimeZoneInfo.FindSystemTimeZoneById(zone) is not null ? zone : throw new TimeZoneNotFoundException();
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new InvalidOperationException($"'{given}' is not a time zone - Europe/Stockholm.");
            }
        }

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        private static bool IsAddress(string given) =>
            !given.Any(char.IsWhiteSpace) && given.IndexOf('@') > 0 && given.IndexOf('@') == given.LastIndexOf('@') && given.IndexOf('@') < given.Length - 1;

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static JsonObject ObjectOf(string given, string what, string example)
        {
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
            }

            return parsed as JsonObject ?? throw new InvalidOperationException($"The {what} not a JSON object - {example}.");
        }

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static string? Text(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null
            : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2AccountCredentialsRequest(
                Values.Get(credentials, "token-url") ?? DefaultTokenUrl,
                Values.Get(settings, "account_id") ?? string.Empty,
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty)
            {
                Timeout = timeout,
            }, cancellationToken);

        // Zoom's answer as JSON (an empty answer - its 204 - nothing), the token as a bearer, a throttled call waiting as Zoom says;
        // a failure thrown with its status, Zoom's code and its message.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("application/json");
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
                throw new InvalidOperationException($"Zoom answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Text(json?["code"]);
                var said = Text(json?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            return json;
        }
    }
}
