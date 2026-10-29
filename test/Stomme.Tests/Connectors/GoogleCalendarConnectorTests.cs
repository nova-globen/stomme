#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.GoogleCalendar;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Google Calendar (n8n's Google Calendar node: an event got, listed, created, updated and deleted, a calendar's
    /// availability): the refresh token exchanged at the token endpoint, the access token sent as a bearer; the events between
    /// two times read page by page by Google's token up to the most asked for, each occurrence in the order they start, each a
    /// plain record; an event made at a time with its offset, in a time zone or for a whole day - an hour long, or to the next
    /// day, when no end is given - changed and removed, the attendees told as asked; whether a calendar is free and its busy
    /// spans; a calendar, an event, a title, a time, a time zone, an attendee and who to tell refused before anything is sent -
    /// not even a token asked for; Google's refusal its status word and message; a test naming the calendar.
    /// </summary>
    public sealed class GoogleCalendarConnectorTests
    {
        private const string Api = "https://calendar.example.test/calendar/v3";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Calendar { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                if (url.StartsWith("https://oauth2.example.test/token", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""", Encoding.UTF8, "application/json") };
                }

                var (status, answer) = Calendar(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> CalendarRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://oauth2.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static ConnectorExtractContext Context(Google google, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(google), Api, operation, parameters,
                settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials);

        private static GoogleCalendarConnector Connector() => new(new OAuth2TokenProvider());

        // A body as text with nothing escaped that need not be - a + stays a + (JSON may write it +; Google reads either).
        private static string? Plain(string? json) => json is null ? null : JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        private static string Event(string id, string summary, string start, string end, bool wholeDay = false) =>
            """{"id":"@ID","summary":"@S","description":"Twelve, please.","location":"Room 2","status":"confirmed","htmlLink":"https://calendar.example/event?eid=@ID",@WHEN"organizer":{"email":"ops@example.com"},"attendees":[{"email":"ada@example.com","responseStatus":"accepted"},{"email":"grace@example.com"}],"hangoutLink":"https://meet.example/abc","created":"2026-10-01T10:00:00.000Z","updated":"2026-10-02T10:00:00.000Z"}"""
                .Replace("@ID", id, StringComparison.Ordinal).Replace("@S", summary, StringComparison.Ordinal)
                .Replace("@WHEN", wholeDay
                    ? "\"start\":{\"date\":\"" + start + "\"},\"end\":{\"date\":\"" + end + "\"},"
                    : "\"start\":{\"dateTime\":\"" + start + "\",\"timeZone\":\"Europe/Stockholm\"},\"end\":{\"dateTime\":\"" + end + "\",\"timeZone\":\"Europe/Stockholm\"},", StringComparison.Ordinal);

        [Fact]
        public async Task The_events_between_two_times_are_read_page_by_page_up_to_the_most_asked_for()
        {
            var google = new Google
            {
                Calendar = (_, url, _) => url.Contains("pageToken=p2", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"items":[""" + Event("e3", "Planning", "2026-10-12", "2026-10-13", wholeDay: true) + "," + Event("e4", "Retro", "2026-10-12T15:00:00+02:00", "2026-10-12T16:00:00+02:00") + """],"nextPageToken":"p3"}""")
                    : (HttpStatusCode.OK, """{"items":[""" + Event("e1", "Standup", "2026-10-12T09:00:00+02:00", "2026-10-12T09:15:00+02:00") + "," + Event("e2", "Review", "2026-10-12T11:00:00+02:00", "2026-10-12T12:00:00+02:00") + """],"nextPageToken":"p2"}"""),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(google, GoogleCalendarConnector.EventsOperation,
                new() { ["time_min"] = "2026-10-12", ["time_max"] = "2026-10-12T23:59:59+02:00", ["query"] = "order 42", ["max"] = "3" }));

            Assert.Equal(new[] { "e1", "e2", "e3" }, records.Select(record => (string?)record.Fields["id"])); // the most asked for: no page more
            var calls = google.CalendarRequests;
            Assert.Equal(2, calls.Count);
            Assert.Equal($"{Api}/calendars/primary/events?singleEvents=true&orderBy=startTime&timeMin=2026-10-12T00%3A00%3A00Z&timeMax=2026-10-12T21%3A59%3A59Z&q=order%2042&maxResults=3", calls[0].Url);
            Assert.Equal($"{Api}/calendars/primary/events?singleEvents=true&orderBy=startTime&timeMin=2026-10-12T00%3A00%3A00Z&timeMax=2026-10-12T21%3A59%3A59Z&q=order%2042&maxResults=1&pageToken=p2", calls[1].Url);
            Assert.All(calls, call => Assert.Equal("Bearer access-1", call.Authorization)); // the refresh token exchanged first
            var standup = records[0].Fields;
            Assert.Equal(("Standup", "2026-10-12T09:00:00+02:00", "2026-10-12T09:15:00+02:00", false, "Europe/Stockholm", "ops@example.com", "ada@example.com, grace@example.com", "https://meet.example/abc", "Room 2"),
                ((string?)standup["summary"], (string?)standup["start"], (string?)standup["end"], (bool?)standup["all_day"], (string?)standup["time_zone"], (string?)standup["organizer"], (string?)standup["attendees"], (string?)standup["meet"], (string?)standup["location"]));
            Assert.Equal(("2026-10-12", true), ((string?)records[2].Fields["start"], (bool?)records[2].Fields["all_day"])); // a whole day: its date
        }

        [Fact]
        public async Task An_event_is_made_in_a_time_zone_with_an_offset_or_for_a_whole_day_changed_and_removed()
        {
            var google = new Google
            {
                Calendar = (method, url, body) => method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "")
                    : (HttpStatusCode.OK, Event("evt1", "Standup", "2026-10-12T10:00:00", "2026-10-12T11:00:00")),
            };

            var made = await Contexts.DrainAsync(Connector(), Context(google, GoogleCalendarConnector.CreateEventOperation, new()
            {
                ["calendar"] = "team@example.com", ["summary"] = "Standup", ["start"] = "2026-10-12 10:00", ["time_zone"] = "Europe/Stockholm",
                ["description"] = "Twelve, please.", ["location"] = "Room 2", ["attendees"] = "ada@example.com, grace@example.com", ["notify"] = "all",
            }));
            var call = google.CalendarRequests[0];
            Assert.Equal((HttpMethod.Post, $"{Api}/calendars/team%40example.com/events?sendUpdates=all"), (call.Method, call.Url));
            Assert.Equal("""{"summary":"Standup","start":{"dateTime":"2026-10-12T10:00:00","timeZone":"Europe/Stockholm"},"end":{"dateTime":"2026-10-12T11:00:00","timeZone":"Europe/Stockholm"},"description":"Twelve, please.","location":"Room 2","attendees":[{"email":"ada@example.com"},{"email":"grace@example.com"}]}""", call.Body); // an hour long
            Assert.Equal(("evt1", "https://calendar.example/event?eid=evt1"), ((string?)Assert.Single(made).Fields["id"], (string?)made[0].Fields["url"]));

            await Contexts.DrainAsync(Connector(), Context(google, GoogleCalendarConnector.CreateEventOperation, new() { ["summary"] = "Call", ["start"] = "2026-10-12T10:30:00+02:00" }));
            Assert.Equal("""{"summary":"Call","start":{"dateTime":"2026-10-12T10:30:00+02:00"},"end":{"dateTime":"2026-10-12T11:30:00+02:00"}}""", Plain(google.CalendarRequests[1].Body)); // its offset kept
            Assert.EndsWith("/calendars/primary/events?sendUpdates=none", google.CalendarRequests[1].Url); // no one told unless asked
            await Contexts.DrainAsync(Connector(), Context(google, GoogleCalendarConnector.CreateEventOperation, new() { ["summary"] = "Offsite", ["start"] = "2026-10-12" }));
            Assert.Equal("""{"summary":"Offsite","start":{"date":"2026-10-12"},"end":{"date":"2026-10-13"}}""", google.CalendarRequests[2].Body); // a whole day, to the next

            var changed = await Contexts.DrainAsync(Connector(), Context(google, GoogleCalendarConnector.UpdateEventOperation, new() { ["event"] = "evt1", ["location"] = "Room 3", ["end"] = "2026-10-12T11:30:00+02:00" }));
            Assert.Equal((HttpMethod.Patch, $"{Api}/calendars/primary/events/evt1?sendUpdates=none", """{"end":{"dateTime":"2026-10-12T11:30:00+02:00"},"location":"Room 3"}"""),
                (google.CalendarRequests[3].Method, google.CalendarRequests[3].Url, Plain(google.CalendarRequests[3].Body)));
            Assert.Single(changed);

            var read = await Contexts.DrainAsync(Connector(), Context(google, GoogleCalendarConnector.GetEventOperation, new() { ["event"] = "evt1" }));
            Assert.Equal(($"{Api}/calendars/primary/events/evt1", "Standup"), (google.CalendarRequests[4].Url, (string?)Assert.Single(read).Fields["summary"]));

            var removed = await Contexts.DrainAsync(Connector(), Context(google, GoogleCalendarConnector.DeleteEventOperation, new() { ["event"] = "evt1", ["notify"] = "externalOnly" }));
            Assert.Equal((HttpMethod.Delete, $"{Api}/calendars/primary/events/evt1?sendUpdates=externalOnly"), (google.CalendarRequests[5].Method, google.CalendarRequests[5].Url));
            Assert.Equal(("evt1", true), ((string?)Assert.Single(removed).Fields["id"], (bool?)removed[0].Fields["deleted"]));
        }

        [Fact]
        public async Task A_calendars_availability_is_whether_it_is_free_and_its_busy_spans()
        {
            var busy = new Google { Calendar = (_, _, _) => (HttpStatusCode.OK, """{"calendars":{"ops@example.com":{"busy":[{"start":"2026-10-12T08:00:00Z","end":"2026-10-12T09:00:00Z"}]}}}""") };
            var answer = await Contexts.DrainAsync(Connector(), Context(busy, GoogleCalendarConnector.AvailabilityOperation,
                new() { ["calendar"] = "ops@example.com", ["time_min"] = "2026-10-12T09:00:00+02:00", ["time_max"] = "2026-10-12T17:00:00+02:00" }));
            var call = busy.CalendarRequests.Single();
            Assert.Equal((HttpMethod.Post, $"{Api}/freeBusy", """{"timeMin":"2026-10-12T07:00:00Z","timeMax":"2026-10-12T15:00:00Z","items":[{"id":"ops@example.com"}]}"""), (call.Method, call.Url, call.Body));
            var fields = Assert.Single(answer).Fields;
            Assert.Equal((false, """[{"start":"2026-10-12T08:00:00Z","end":"2026-10-12T09:00:00Z"}]"""), ((bool?)fields["available"], (string?)fields["busy"]));

            var free = new Google { Calendar = (_, _, _) => (HttpStatusCode.OK, """{"calendars":{"primary":{"busy":[]}}}""") };
            Assert.True((bool?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(free, GoogleCalendarConnector.AvailabilityOperation, new() { ["time_min"] = "2026-10-12", ["time_max"] = "2026-10-13" }))).Fields["available"]);

            var unreadable = new Google { Calendar = (_, _, _) => (HttpStatusCode.OK, """{"calendars":{"x@example.com":{"errors":[{"domain":"global","reason":"notFound"}],"busy":[]}}}""") };
            Assert.Equal("Google Calendar could not read 'x@example.com': notFound.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(unreadable, GoogleCalendarConnector.AvailabilityOperation,
                new() { ["calendar"] = "x@example.com", ["time_min"] = "2026-10-12", ["time_max"] = "2026-10-13" })))).Message); // not "free"
        }

        [Fact]
        public async Task What_could_reach_the_path_or_mislead_the_calendar_is_refused_before_a_token_is_asked_for_and_googles_refusal_is_its_word()
        {
            var google = new Google();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, operation, parameters)))).Message;

            Assert.StartsWith("'../x' is not a calendar", await Refused(GoogleCalendarConnector.EventsOperation, new() { ["calendar"] = "../x" }));
            Assert.StartsWith("'evt1/../x' is not an event's id", await Refused(GoogleCalendarConnector.GetEventOperation, new() { ["event"] = "evt1/../x" }));
            Assert.Equal("The title has a line break - one line only.", await Refused(GoogleCalendarConnector.CreateEventOperation, new() { ["summary"] = "Standup\nBcc", ["start"] = "2026-10-12" }));
            Assert.StartsWith("'noon' is not a time", await Refused(GoogleCalendarConnector.CreateEventOperation, new() { ["summary"] = "Standup", ["start"] = "noon" }));
            Assert.Equal("'2026-10-12 10:00' has no offset - give one (+02:00) or the time zone (time_zone).", await Refused(GoogleCalendarConnector.CreateEventOperation, new() { ["summary"] = "Standup", ["start"] = "2026-10-12 10:00" }));
            Assert.StartsWith("'Mars/Olympus' is not a time zone", await Refused(GoogleCalendarConnector.CreateEventOperation, new() { ["summary"] = "Standup", ["start"] = "2026-10-12 10:00", ["time_zone"] = "Mars/Olympus" }));
            Assert.Equal("'ada at example' is not an address.", await Refused(GoogleCalendarConnector.CreateEventOperation, new() { ["summary"] = "Standup", ["start"] = "2026-10-12", ["attendees"] = "ada at example" }));
            Assert.StartsWith("'everyone' is not who to tell", await Refused(GoogleCalendarConnector.DeleteEventOperation, new() { ["event"] = "evt1", ["notify"] = "everyone" }));
            Assert.StartsWith("Give something to change", await Refused(GoogleCalendarConnector.UpdateEventOperation, new() { ["event"] = "evt1" }));
            Assert.Empty(google.Requests); // not even a token asked for

            var refusing = new Google { Calendar = (_, _, _) => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Not Found","status":"NOT_FOUND"}}""") };
            Assert.Equal("HTTP 404 NOT_FOUND: Not Found", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, GoogleCalendarConnector.GetEventOperation, new() { ["event"] = "evt9" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_calendar()
        {
            var google = new Google { Calendar = (_, _, _) => (HttpStatusCode.OK, """{"id":"ops@example.com","summary":"ops@example.com","timeZone":"Europe/Stockholm"}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(google), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials));
            Assert.Equal((true, "Signed in to ops@example.com."), (tested.Success, tested.Message));
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Google { Calendar = (_, _, _) => (HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"Request had invalid authentication credentials.","status":"UNAUTHENTICATED"}}""") }),
                Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials));
            Assert.Equal((false, "HTTP 401 UNAUTHENTICATED: Request had invalid authentication credentials."), (refused.Success, refused.Message));
            Assert.Equal(($"{Api}/calendars/primary", "Bearer access-1"), (google.CalendarRequests[0].Url, google.CalendarRequests[0].Authorization));
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "googlecalendar", ConnectorCategory.Productivity), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "events", "get_event", "create_event", "update_event", "delete_event", "availability" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
