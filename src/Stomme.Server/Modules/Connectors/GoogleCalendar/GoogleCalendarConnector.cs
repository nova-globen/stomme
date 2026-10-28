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

namespace Stomme.Modules.Connectors.GoogleCalendar
{
    /// <summary>
    /// Google Calendar (BL-229, n8n's Google Calendar node: events got, listed, created, updated and deleted, a calendar's
    /// availability): the Calendar API (<c>https://www.googleapis.com/calendar/v3</c>) over the OAuth2 REFRESH-TOKEN scheme - a
    /// Google Cloud OAuth client's id and secret and a refresh token granted the calendar scope - exchanged at Google's token
    /// endpoint (the <c>token_url</c> setting) through the shared <see cref="OAuth2TokenProvider"/>, as Gmail's is. A test reads
    /// the primary calendar.
    /// </summary>
    /// <remarks>
    /// Every operation names its <c>calendar</c> - primary (the default), an address or a calendar's id - and an event's id, both
    /// held to their shapes and escaped before they reach the path. <c>events</c>: the events between <c>time_min</c> and
    /// <c>time_max</c> matching <c>query</c>, a recurring event's each occurrence, in the order they start, page by page by
    /// Google's token up to <c>max</c>; one record each: id, summary, description, location, status, url, start, end, all_day,
    /// time_zone, organizer, attendees (comma-separated), meet, recurring_event_id, created, updated. <c>get_event</c> the same for
    /// one. <c>create_event</c>: a summary, a start and an end - a date for a whole day (its end the next day when none is given),
    /// a time with an offset, or a time in <c>time_zone</c> (an hour long when no end is given) - a description, a location and
    /// attendees; <c>update_event</c> changes those given; <c>delete_event</c> removes one. Each tells its attendees as
    /// <c>notify</c> says (none by default). <c>availability</c>: whether the calendar is free between two times, its busy spans
    /// beside it (freeBusy). A throttled call waits as Google says; a failure is thrown with its status, Google's status word and
    /// its message.
    /// </remarks>
    public sealed class GoogleCalendarConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://www.googleapis.com/calendar/v3";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string EventsOperation = "events";

        public const string GetEventOperation = "get_event";

        public const string CreateEventOperation = "create_event";

        public const string UpdateEventOperation = "update_event";

        public const string DeleteEventOperation = "delete_event";

        public const string AvailabilityOperation = "availability";

        public const int DefaultMax = 1000;

        private static readonly Regex CalendarId = new("^[A-Za-z0-9._%+@#-]{1,254}$", RegexOptions.CultureInvariant);

        private static readonly Regex EventId = new("^[A-Za-z0-9_-]{1,1024}$", RegexOptions.CultureInvariant);

        private static readonly Regex WholeDate = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);

        private static readonly Regex Offset = new("(Z|[+-][0-9]{2}:?[0-9]{2})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googlecalendar";

        public string DisplayName => "Google Calendar";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://calendar.google.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/calendar/api/v3/reference");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField CalendarField = new("calendar", "Calendar", ConnectorConfigFieldType.Text, Required: false, Description: "primary, an address, or a calendar's id.") { Default = "primary" };

        private static readonly ConnectorConfigField EventField = new("event", "Event", ConnectorConfigFieldType.Text, Required: true, Description: "The event's id.");

        private static readonly ConnectorConfigField NotifyField = new("notify", "Tell the attendees", ConnectorConfigFieldType.Text, Required: false, Description: "all, externalOnly or none.") { Default = "none" };

        private static ConnectorConfigField[] EventFields(bool create) => new[]
        {
            new ConnectorConfigField("summary", "Title", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("start", "Start", ConnectorConfigFieldType.Text, Required: create, Description: "2026-10-12T10:00:00+02:00, 2026-10-12 10:00 with a time zone, or 2026-10-12 for the whole day."),
            new ConnectorConfigField("end", "End", ConnectorConfigFieldType.Text, Required: false, Description: "As the start. Empty: an hour later, or the next day for a whole day."),
            new ConnectorConfigField("time_zone", "Time zone", ConnectorConfigFieldType.Text, Required: false, Description: "Europe/Stockholm - for a time without an offset."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Text."),
            new ConnectorConfigField("location", "Location", ConnectorConfigFieldType.Text, Required: false, Description: "An address or a room."),
            new ConnectorConfigField("attendees", "Attendees", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(EventsOperation, "Get events", "A calendar's events between two times - one record each: its id, title, start, end, place, attendees and page - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    CalendarField,
                    new ConnectorConfigField("time_min", "From", ConnectorConfigFieldType.Text, Required: false, Description: "Events ending after it - 2026-10-12, or a time. Empty: any."),
                    new ConnectorConfigField("time_max", "To", ConnectorConfigFieldType.Text, Required: false, Description: "Events starting before it. Empty: any."),
                    new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words in an event's title, description, place or attendees."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many events, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetEventOperation, "Get an event", "One event - n8n's Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CalendarField, EventField }),
            },
            new ConnectorOperation(CreateEventOperation, "Create an event", "Adds an event - one record: its id and page - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CalendarField }.Concat(EventFields(create: true)).Append(NotifyField).ToArray()),
            },
            new ConnectorOperation(UpdateEventOperation, "Update an event", "Changes an event's title, times, description, place or attendees - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CalendarField, EventField }.Concat(EventFields(create: false)).Append(NotifyField).ToArray()),
            },
            new ConnectorOperation(DeleteEventOperation, "Delete an event", "Removes an event - n8n's Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CalendarField, EventField, NotifyField }),
            },
            new ConnectorOperation(AvailabilityOperation, "Availability", "Whether a calendar is free between two times, and its busy spans - n8n's calendar Availability.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    CalendarField,
                    new ConnectorConfigField("time_min", "From", ConnectorConfigFieldType.Text, Required: true, Description: "2026-10-12T09:00:00+02:00"),
                    new ConnectorConfigField("time_max", "To", ConnectorConfigFieldType.Text, Required: true, Description: "2026-10-12T17:00:00+02:00"),
                }),
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
                var calendar = await SendAsync(client, token, HttpMethod.Get, api + "/calendars/primary", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in to {Text(calendar?["summary"]) ?? "the calendar"}.");
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
            var operation = context.OperationName ?? EventsOperation;
            var parameters = context.Parameters;
            var calendar = Calendar(parameters);
            var events = $"{api}/calendars/{calendar}/events";

            // What could be refused is refused before a token is asked for.
            string? eventPath = null;
            JsonObject? body = null;
            string? notify = null;
            if (Is(operation, GetEventOperation) || Is(operation, UpdateEventOperation) || Is(operation, DeleteEventOperation))
            {
                eventPath = events + "/" + Event(parameters);
            }

            if (Is(operation, CreateEventOperation) || Is(operation, UpdateEventOperation))
            {
                body = EventBody(parameters, create: Is(operation, CreateEventOperation));
                if (body.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - summary, start, end, description, location or attendees.");
                }
            }

            if (Is(operation, CreateEventOperation) || Is(operation, UpdateEventOperation) || Is(operation, DeleteEventOperation))
            {
                notify = (Values.Get(parameters, "notify") ?? "none") switch
                {
                    var all when all.Equals("all", StringComparison.OrdinalIgnoreCase) => "all",
                    var external when external.Equals("externalOnly", StringComparison.OrdinalIgnoreCase) => "externalOnly",
                    var none when none.Equals("none", StringComparison.OrdinalIgnoreCase) => "none",
                    var other => throw new InvalidOperationException($"'{other}' is not who to tell - all, externalOnly or none."),
                };
            }

            if (!Is(operation, EventsOperation) && !Is(operation, AvailabilityOperation) && eventPath is null && body is null)
            {
                throw new InvalidOperationException($"Google Calendar has no operation '{operation}' - events, get_event, create_event, update_event, delete_event or availability.");
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, EventsOperation))
            {
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var query = new List<string> { "singleEvents=true", "orderBy=startTime" };
                foreach (var (parameter, name) in new[] { ("time_min", "timeMin"), ("time_max", "timeMax") })
                {
                    if (Values.Get(parameters, parameter) is { } moment)
                    {
                        query.Add(name + "=" + Uri.EscapeDataString(Instant(moment)));
                    }
                }

                if (Values.Get(parameters, "query") is { } words)
                {
                    query.Add("q=" + Uri.EscapeDataString(words));
                }

                var read = 0;
                string? page = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = $"{events}?{string.Join("&", query)}&maxResults={Math.Min(250, max - read).ToString(CultureInfo.InvariantCulture)}{(page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page))}";
                    var answer = await SendAsync(client, token, HttpMethod.Get, url, null, cancellationToken);
                    foreach (var item in (answer?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        if (read >= max)
                        {
                            yield break;
                        }

                        read++;
                        yield return EventRecord(item);
                    }

                    page = read < max ? Text(answer?["nextPageToken"]) : null;
                }
                while (!string.IsNullOrEmpty(page));

                yield break;
            }

            if (Is(operation, AvailabilityOperation))
            {
                var from = Instant(Values.Get(parameters, "time_min") ?? throw new InvalidOperationException("Give the time it starts from (time_min)."));
                var to = Instant(Values.Get(parameters, "time_max") ?? throw new InvalidOperationException("Give the time it ends (time_max)."));
                var id = Uri.UnescapeDataString(calendar);
                var answer = await SendAsync(client, token, HttpMethod.Post, api + "/freeBusy",
                    new JsonObject { ["timeMin"] = from, ["timeMax"] = to, ["items"] = new JsonArray(new JsonObject { ["id"] = id }) }, cancellationToken);
                var calendars = answer?["calendars"] as JsonObject;
                var entry = (calendars?[id] ?? calendars?.FirstOrDefault().Value) as JsonObject;
                if (entry?["errors"] is JsonArray errors && errors.Count > 0)
                {
                    throw new InvalidOperationException($"Google Calendar could not read '{id}': {Text(errors[0]?["reason"]) ?? "no reason given"}.");
                }

                var busy = entry?["busy"] as JsonArray ?? new JsonArray();
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["calendar"] = id,
                    ["time_min"] = from,
                    ["time_max"] = to,
                    ["available"] = busy.Count == 0,
                    ["busy"] = busy.ToJsonString(),
                });
                yield break;
            }

            var sendUpdates = notify is null ? "" : "?sendUpdates=" + notify;
            if (Is(operation, GetEventOperation))
            {
                var found = await SendAsync(client, token, HttpMethod.Get, eventPath!, null, cancellationToken);
                yield return EventRecord(found as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, CreateEventOperation))
            {
                var made = await SendAsync(client, token, HttpMethod.Post, events + sendUpdates, body, cancellationToken);
                yield return EventRecord(made as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdateEventOperation))
            {
                var changed = await SendAsync(client, token, HttpMethod.Patch, eventPath + sendUpdates, body, cancellationToken);
                yield return EventRecord(changed as JsonObject ?? new JsonObject());
                yield break;
            }

            await SendAsync(client, token, HttpMethod.Delete, eventPath + sendUpdates, null, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Values.Get(parameters, "event"), ["deleted"] = true });
        }

        // An event's fields as Google's: the title, its times, the text, the place and the attendees given.
        private static JsonObject EventBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var body = new JsonObject();
            if (Values.Get(parameters, "summary") is { } summary)
            {
                body["summary"] = summary.Contains('\n', StringComparison.Ordinal) || summary.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException("The title has a line break - one line only.") : summary;
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the event's title (summary).");
            }

            var zone = Values.Get(parameters, "time_zone");
            if (zone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _) is false)
            {
                throw new InvalidOperationException($"'{zone}' is not a time zone - Europe/Stockholm.");
            }

            var start = Values.Get(parameters, "start");
            var end = Values.Get(parameters, "end");
            if (start is null && create)
            {
                throw new InvalidOperationException("Give the event's start.");
            }

            if (start is not null)
            {
                var (startTime, wholeDay) = When(start, zone);
                body["start"] = startTime;
                body["end"] = end is not null ? When(end, zone).Time : wholeDay
                    ? new JsonObject { ["date"] = WholeDate.IsMatch(start) ? DateTime.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null }
                    : Later(startTime, TimeSpan.FromHours(1));
            }
            else if (end is not null)
            {
                body["end"] = When(end, zone).Time;
            }

            if (Values.Get(parameters, "description") is { } description)
            {
                body["description"] = description;
            }

            if (Values.Get(parameters, "location") is { } location)
            {
                body["location"] = location;
            }

            if (Values.Get(parameters, "attendees") is { } attendees)
            {
                body["attendees"] = new JsonArray(attendees.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(address => address.Contains('@', StringComparison.Ordinal) && !address.Any(char.IsWhiteSpace) ? (JsonNode)new JsonObject { ["email"] = address } : throw new InvalidOperationException($"'{address}' is not an address."))
                    .ToArray());
            }

            return body;
        }

        // A time as Google's: a date for a whole day; a time with its offset as it is; a time without one in the time zone given.
        private static (JsonObject Time, bool WholeDay) When(string given, string? zone)
        {
            if (WholeDate.IsMatch(given))
            {
                return DateTime.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? (new JsonObject { ["date"] = given }, true)
                    : throw new InvalidOperationException($"'{given}' is not a date.");
            }

            var normal = given.Replace(' ', 'T');
            if (Offset.IsMatch(normal))
            {
                return DateTimeOffset.TryParse(normal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
                    ? (new JsonObject { ["dateTime"] = moment.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) }, false)
                    : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-12T10:00:00+02:00.");
            }

            if (!DateTime.TryParse(normal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                throw new InvalidOperationException($"'{given}' is not a time - 2026-10-12T10:00:00+02:00.");
            }

            return zone is null
                ? throw new InvalidOperationException($"'{given}' has no offset - give one (+02:00) or the time zone (time_zone).")
                : (new JsonObject { ["dateTime"] = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), ["timeZone"] = zone }, false);
        }

        // A timed start moved on by a span, its offset or time zone kept.
        private static JsonObject Later(JsonObject start, TimeSpan span)
        {
            var text = Text(start["dateTime"])!;
            if (start["timeZone"] is { } zone)
            {
                return new JsonObject { ["dateTime"] = DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture).Add(span).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), ["timeZone"] = zone.DeepClone() };
            }

            return new JsonObject { ["dateTime"] = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).Add(span).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) };
        }

        // A moment for a query, in UTC: a date its midnight UTC, a time without an offset taken as UTC.
        private static string Instant(string given) =>
            DateTimeOffset.TryParse(given.Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-12, or 2026-10-12T10:00:00+02:00.");

        // An event as a record - its times as Google gives them, a person their address, the attendees comma-separated.
        private static ConnectorRecord EventRecord(JsonObject item) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(item["id"]),
            ["summary"] = Text(item["summary"]) ?? "",
            ["description"] = Text(item["description"]) ?? "",
            ["location"] = Text(item["location"]) ?? "",
            ["status"] = Text(item["status"]),
            ["url"] = Text(item["htmlLink"]),
            ["start"] = Text(item["start"]?["dateTime"]) ?? Text(item["start"]?["date"]),
            ["end"] = Text(item["end"]?["dateTime"]) ?? Text(item["end"]?["date"]),
            ["all_day"] = item["start"]?["date"] is not null,
            ["time_zone"] = Text(item["start"]?["timeZone"]),
            ["organizer"] = Text(item["organizer"]?["email"]),
            ["attendees"] = string.Join(", ", (item["attendees"] as JsonArray ?? new JsonArray()).Select(person => Text(person?["email"])).Where(address => !string.IsNullOrEmpty(address))),
            ["meet"] = Text(item["hangoutLink"]),
            ["recurring_event_id"] = Text(item["recurringEventId"]),
            ["created"] = Text(item["created"]),
            ["updated"] = Text(item["updated"]),
        });

        // The calendar's id, held to the shapes Google gives (primary, an address, a group calendar's id) and escaped for the path.
        private static string Calendar(IReadOnlyDictionary<string, string> parameters)
        {
            var given = Values.Get(parameters, "calendar") ?? "primary";
            return CalendarId.IsMatch(given) && !given.Contains("..", StringComparison.Ordinal)
                ? Uri.EscapeDataString(given)
                : throw new InvalidOperationException($"'{given}' is not a calendar - primary, an address, or a calendar's id.");
        }

        private static string Event(IReadOnlyDictionary<string, string> parameters) =>
            Values.Get(parameters, "event") is { } given && EventId.IsMatch(given) ? given : throw new InvalidOperationException($"'{Values.Get(parameters, "event")}' is not an event's id.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // Google's answer as JSON (an empty answer null), a throttled call waiting as Google says; a failure thrown with its status,
        // Google's status word and its message.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                throw new InvalidOperationException($"Google Calendar answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = Text(json?["error"]?["status"]);
                var said = Text(json?["error"]?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(status is null ? "" : " " + status)}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json;
        }
    }
}
