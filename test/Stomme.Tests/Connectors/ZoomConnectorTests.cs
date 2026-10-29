#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Connectors.Zoom;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Zoom (n8n's Zoom node: its meetings created, got, read, updated and deleted): the server-to-server grant - the
    /// account in the form, the client as HTTP Basic at Zoom's token endpoint - the access token a bearer; a user's meetings page
    /// by page by Zoom's token up to the most asked for; each meeting its id, topic, kind by name, start, duration, host and join
    /// link, never its start link; a meeting made (a start in UTC, or the time zone's clock beside a time zone), got with an
    /// occurrence, changed, ended and deleted; registrants read and added, recordings and users read; what could go wrong refused
    /// before anything is sent - not even the token asked for; Zoom's refusal its code and message; a test naming the user.
    /// </summary>
    public sealed class ZoomConnectorTests
    {
        private const string Api = "https://api.zoom.example.test/v2";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1" };

        private static readonly Dictionary<string, string> Settings = new() { ["account_id"] = "acct-1" };

        private sealed class Zoom : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.NoContent, "");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.StartsWith(ZoomConnector.DefaultTokenUrl, StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"access_token":"access-1","token_type":"bearer","expires_in":3599,"scope":"meeting:read:admin"}""")
                    : Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Zoom zoom, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(zoom), Api, operation, parameters, settings: Settings, credentials: Credentials);

        private static ZoomConnector Connector() => new(new OAuth2TokenProvider());

        private static string Meeting(long id) =>
            "{\"uuid\":\"aDYlohsHRtCd4ii1uC2+hA==\",\"id\":" + id + ",\"host_id\":\"KDcuGIm1QgePTO8WbOqwIQ\",\"host_email\":\"ada@example.com\",\"topic\":\"Café <b>review</b>\",\"type\":2,\"status\":\"waiting\","
            + "\"start_time\":\"2026-10-20T07:00:00Z\",\"duration\":60,\"timezone\":\"Europe/Stockholm\",\"agenda\":\"Plans\",\"created_at\":\"2026-10-12T10:00:00Z\",\"start_url\":\"https://zoom.us/s/" + id + "?zak=host-secret\","
            + "\"join_url\":\"https://zoom.us/j/" + id + "?pwd=abc\"}";

        private static string Meetings(long from, int count, string? next) =>
            "{\"page_size\":300,\"total_records\":350,\"next_page_token\":\"" + (next ?? "") + "\",\"meetings\":[" + string.Join(",", Enumerable.Range(0, count).Select(n => Meeting(from + n))) + "]}";

        [Fact]
        public async Task Meetings_are_read_page_by_page_by_zooms_token_each_without_its_start_link()
        {
            var zoom = new Zoom { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("next_page_token", StringComparison.Ordinal) ? Meetings(85746065732, 50, null) : Meetings(85746065432, 300, "tok+1")) };
            var meetings = await Contexts.DrainAsync(Connector(), Context(zoom, ZoomConnector.MeetingsOperation, new() { ["type"] = "Upcoming" }));

            Assert.Equal(350, meetings.Count); // no token on the last page: the end
            var (method, tokenUrl, basic, form) = zoom.Requests[0];
            Assert.Equal(("POST", ZoomConnector.DefaultTokenUrl, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-1:secret-1")), "grant_type=account_credentials&account_id=acct-1"), (method.Method, tokenUrl, basic, form));
            Assert.All(zoom.Requests.Skip(1), request => Assert.Equal("Bearer access-1", request.Authorization));
            Assert.Equal(new[] { Api + "/users/me/meetings?type=upcoming&page_size=300", Api + "/users/me/meetings?type=upcoming&page_size=300&next_page_token=tok%2B1" }, zoom.Requests.Skip(1).Select(request => request.Url));
            var first = meetings[0].Fields;
            Assert.Equal(("85746065432", "Café <b>review</b>", "scheduled", "waiting", "2026-10-20T07:00:00Z", 60L, "Europe/Stockholm", "ada@example.com", "https://zoom.us/j/85746065432?pwd=abc"),
                ((string?)first["id"], (string?)first["topic"], (string?)first["type"], (string?)first["status"], (string?)first["start_time"], (long?)first["duration"], (string?)first["timezone"], (string?)first["host_email"], (string?)first["join_url"]));
            Assert.False(first.ContainsKey("start_url")); // it starts the meeting as its host
            Assert.DoesNotContain(first.Values, value => value is string text && text.Contains("host-secret", StringComparison.Ordinal));

            var few = new Zoom { Answer = (_, _) => (HttpStatusCode.OK, Meetings(85746065432, 300, "more")) };
            Assert.Equal(310, (await Contexts.DrainAsync(Connector(), Context(few, ZoomConnector.MeetingsOperation, new() { ["user"] = "ada@example.com", ["max"] = "310" }))).Count);
            Assert.Equal(new[] { Api + "/users/ada%40example.com/meetings?type=scheduled&page_size=300", Api + "/users/ada%40example.com/meetings?type=scheduled&page_size=10&next_page_token=more" }, few.Requests.Skip(1).Select(request => request.Url)); // scheduled by default; a page no bigger than what is left

            var lists = new Zoom
            {
                Answer = (_, url) => url.Contains("/registrants", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"next_page_token":"","registrants":[{"id":"r1","email":"ada@example.com","first_name":"Ada","status":"approved","custom_questions":[{"title":"Team","value":"Ops"}]}]}""")
                    : url.Contains("/recordings", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"next_page_token":"","meetings":[{"uuid":"u1","id":85746065432,"topic":"Review","start_time":"2026-10-05T07:00:00Z","duration":58,"total_size":1048576,"recording_count":5,"share_url":"https://zoom.us/rec/share/abc","recording_files":[{"file_type":"MP4"},{"file_type":"M4A"},{"file_type":"MP4"}]}]}""")
                    : (HttpStatusCode.OK, """{"next_page_token":"","users":[{"id":"KDcuGIm1QgePTO8WbOqwIQ","email":"ada@example.com","type":2,"status":"pending","verified":0}]}"""),
            };
            var registrant = Assert.Single(await Contexts.DrainAsync(Connector(), Context(lists, ZoomConnector.RegistrantsOperation, new() { ["meeting"] = "857 4606 5432" })));
            Assert.Equal(Api + "/meetings/85746065432/registrants?status=approved&page_size=300", lists.Requests[^1].Url); // approved by default; the id's spaces dropped
            Assert.Equal(("r1", "Ada", "[{\"title\":\"Team\",\"value\":\"Ops\"}]"), ((string?)registrant.Fields["id"], (string?)registrant.Fields["first_name"], (string?)registrant.Fields["custom_questions"]));
            var recording = Assert.Single(await Contexts.DrainAsync(Connector(), Context(lists, ZoomConnector.RecordingsOperation, new() { ["from"] = "2026-10-01", ["to"] = "2026-10-12" })));
            Assert.Equal(Api + "/users/me/recordings?from=2026-10-01&to=2026-10-12&page_size=300", lists.Requests[^1].Url);
            Assert.Equal(("85746065432", 58L, 1048576L, 5L, "MP4, M4A", "https://zoom.us/rec/share/abc"),
                ((string?)recording.Fields["id"], (long?)recording.Fields["duration"], (long?)recording.Fields["total_size"], (long?)recording.Fields["recording_count"], (string?)recording.Fields["file_types"], (string?)recording.Fields["share_url"]));
            var user = Assert.Single(await Contexts.DrainAsync(Connector(), Context(lists, ZoomConnector.UsersOperation, new() { ["status"] = "Pending" })));
            Assert.Equal(Api + "/users?status=pending&page_size=300", lists.Requests[^1].Url);
            Assert.Equal(("ada@example.com", 2L), ((string?)user.Fields["email"], (long?)user.Fields["type"]));
            await Contexts.DrainAsync(Connector(), Context(lists, ZoomConnector.UsersOperation, new()));
            Assert.Equal(Api + "/users?status=active&page_size=300", lists.Requests[^1].Url); // active by default
        }

        [Fact]
        public async Task A_meeting_is_made_got_changed_ended_and_deleted_and_registrants_added()
        {
            var zoom = new Zoom
            {
                Answer = (method, url) => url.EndsWith("/registrants", StringComparison.Ordinal) ? (HttpStatusCode.Created, """{"id":85746065432,"registrant_id":"r9","topic":"Review","start_time":"2026-10-20T07:00:00Z","join_url":"https://zoom.us/w/85746065432?tk=r9"}""")
                    : method == HttpMethod.Post || method == HttpMethod.Get ? (HttpStatusCode.Created, Meeting(85746065432).Replace("\"type\":2", "\"type\":3", StringComparison.Ordinal))
                    : (HttpStatusCode.NoContent, ""),
            };
            (string, string, string?) Last() => (zoom.Requests[^1].Method.Method, zoom.Requests[^1].Url, zoom.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(zoom, operation, parameters));

            var made = Assert.Single(await Run(ZoomConnector.CreateMeetingOperation, new()
            {
                ["topic"] = "Café <b>review</b>", ["type"] = "Recurring", ["start_time"] = "2026-10-20T09:00:00+02:00", ["duration"] = "60", ["password"] = "ab-12", ["agenda"] = "Plans\nand more",
                ["schedule_for"] = "lin@example.com", ["settings"] = "{\"host_video\": true, \"auto_recording\": \"cloud\"}",
            }));
            Assert.Equal(("POST", Api + "/users/me/meetings", "{\"topic\":\"Café <b>review</b>\",\"type\":3,\"start_time\":\"2026-10-20T07:00:00Z\",\"duration\":60,\"password\":\"ab-12\",\"agenda\":\"Plans\\nand more\","
                + "\"schedule_for\":\"lin@example.com\",\"settings\":{\"host_video\":true,\"auto_recording\":\"cloud\"}}"), Last()); // the start in UTC
            Assert.Equal(("85746065432", "recurring"), ((string?)made.Fields["id"], (string?)made.Fields["type"]));
            Assert.False(made.Fields.ContainsKey("start_url"));
            await Run(ZoomConnector.CreateMeetingOperation, new() { ["user"] = "KDcuGIm1QgePTO8WbOqwIQ", ["topic"] = "Standup", ["start_time"] = "2026-10-20T09:00:00", ["timezone"] = "Europe/Stockholm" });
            Assert.Equal(("POST", Api + "/users/KDcuGIm1QgePTO8WbOqwIQ/meetings", "{\"topic\":\"Standup\",\"type\":2,\"start_time\":\"2026-10-20T09:00:00\",\"timezone\":\"Europe/Stockholm\"}"), Last()); // scheduled by default; the zone's own clock
            await Run(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "Now", ["type"] = "8" });
            Assert.Equal("{\"topic\":\"Now\",\"type\":8}", Last().Item3); // Zoom's own number too

            await Run(ZoomConnector.GetMeetingOperation, new() { ["meeting"] = "85746065432", ["occurrence_id"] = "1648194360000", ["show_previous_occurrences"] = "TRUE" });
            Assert.Equal(("GET", Api + "/meetings/85746065432?occurrence_id=1648194360000&show_previous_occurrences=true", (string?)null), Last());
            var changed = Assert.Single(await Run(ZoomConnector.UpdateMeetingOperation, new() { ["meeting"] = "85746065432", ["duration"] = "90", ["topic"] = "Review, longer" }));
            Assert.Equal(("PATCH", Api + "/meetings/85746065432", "{\"topic\":\"Review, longer\",\"duration\":90}"), Last());
            Assert.Equal(("85746065432", true), ((string?)changed.Fields["meeting"], (bool?)changed.Fields["done"]));
            await Run(ZoomConnector.EndMeetingOperation, new() { ["meeting"] = "85746065432" });
            Assert.Equal(("PUT", Api + "/meetings/85746065432/status", "{\"action\":\"end\"}"), Last());
            await Run(ZoomConnector.DeleteMeetingOperation, new() { ["meeting"] = "85746065432", ["occurrence_id"] = "1648194360000", ["schedule_for_reminder"] = "false" });
            Assert.Equal(("DELETE", Api + "/meetings/85746065432?occurrence_id=1648194360000&schedule_for_reminder=false", (string?)null), Last());
            await Run(ZoomConnector.DeleteMeetingOperation, new() { ["meeting"] = "85746065432" });
            Assert.Equal(("DELETE", Api + "/meetings/85746065432", (string?)null), Last());

            var added = Assert.Single(await Run(ZoomConnector.AddRegistrantOperation, new() { ["meeting"] = "85746065432", ["email"] = "ada@example.com", ["first_name"] = "Ada", ["last_name"] = "Lovelace" }));
            Assert.Equal(("POST", Api + "/meetings/85746065432/registrants", "{\"email\":\"ada@example.com\",\"first_name\":\"Ada\",\"last_name\":\"Lovelace\"}"), Last());
            Assert.Equal(("r9", "85746065432", "https://zoom.us/w/85746065432?tk=r9"), ((string?)added.Fields["registrant_id"], (string?)added.Fields["meeting"], (string?)added.Fields["join_url"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_zooms_refusal_is_its_words()
        {
            var zoom = new Zoom();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(zoom, operation, parameters)))).Message;

            Assert.Equal("'a b' is not a user - me, an id or an e-mail address.", await Refused(ZoomConnector.MeetingsOperation, new() { ["user"] = "a b" }));
            Assert.Equal("'past' is not a kind of meetings - scheduled, live, upcoming, upcoming_meetings or previous_meetings.", await Refused(ZoomConnector.MeetingsOperation, new() { ["type"] = "past" }));
            Assert.Equal("Give the meeting - its id.", await Refused(ZoomConnector.GetMeetingOperation, new()));
            Assert.Equal("'123' is not a meeting's id - 85746065432.", await Refused(ZoomConnector.DeleteMeetingOperation, new() { ["meeting"] = "123" }));
            Assert.Equal("'x' is not an occurrence's id.", await Refused(ZoomConnector.GetMeetingOperation, new() { ["meeting"] = "85746065432", ["occurrence_id"] = "x" }));
            Assert.Equal("'yes' is not true or false (show previous occurrences).", await Refused(ZoomConnector.GetMeetingOperation, new() { ["meeting"] = "85746065432", ["show_previous_occurrences"] = "yes" }));
            Assert.Equal("'1' is not true or false (schedule for reminder).", await Refused(ZoomConnector.DeleteMeetingOperation, new() { ["meeting"] = "85746065432", ["schedule_for_reminder"] = "1" }));
            Assert.Equal("Give the meeting's topic.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["duration"] = "30" }));
            Assert.Equal("The topic has a line break - one line only.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a\nb" }));
            Assert.Equal("'weekly' is not a kind of meeting - instant, scheduled, recurring or recurring_fixed.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["type"] = "weekly" }));
            Assert.Equal("'4' is not a kind of meeting - instant, scheduled, recurring or recurring_fixed.", await Refused(ZoomConnector.UpdateMeetingOperation, new() { ["meeting"] = "85746065432", ["type"] = "4" }));
            Assert.Equal("'soon' is not a time (start) - 2026-10-20T09:00:00+02:00.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["start_time"] = "soon" }));
            Assert.Equal("'2026-10-20T09:00:00+02:00' is not the time zone's clock (start) - 2026-10-20T09:00:00, with no offset beside a time zone.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["start_time"] = "2026-10-20T09:00:00+02:00", ["timezone"] = "Europe/Stockholm" }));
            Assert.Equal("'2026-10-20T09:00:00Z' is not the time zone's clock (start) - 2026-10-20T09:00:00, with no offset beside a time zone.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["start_time"] = "2026-10-20T09:00:00Z", ["timezone"] = "UTC" }));
            Assert.Equal("'Mars/Olympus' is not a time zone - Europe/Stockholm.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["timezone"] = "Mars/Olympus" }));
            Assert.Equal("'W. Europe Standard Time' is not a time zone - Europe/Stockholm.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["timezone"] = "W. Europe Standard Time" }));
            Assert.Equal("'0' is not a number of minutes - 1 to 1440.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["duration"] = "0" }));
            Assert.Equal("'1441' is not a number of minutes - 1 to 1440.", await Refused(ZoomConnector.UpdateMeetingOperation, new() { ["meeting"] = "85746065432", ["duration"] = "1441" }));
            Assert.Equal("The passcode is up to ten letters, digits and @-_* only.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["password"] = "a b" }));
            Assert.Equal("The passcode is up to ten letters, digits and @-_* only.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["password"] = "abcdefghijk" }));
            Assert.Equal("'lin' is not an e-mail address (scheduled for).", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["schedule_for"] = "lin" }));
            Assert.Equal("The settings are not a JSON object - {\"host_video\": true}.", await Refused(ZoomConnector.CreateMeetingOperation, new() { ["topic"] = "a", ["settings"] = "[1]" }));
            Assert.Equal("Give something to change - a topic, a kind, a start, a duration, a time zone, a passcode, an agenda, whom it is for or settings.", await Refused(ZoomConnector.UpdateMeetingOperation, new() { ["meeting"] = "85746065432" }));
            Assert.Equal("'waiting' is not a registrant's status - approved, pending or denied.", await Refused(ZoomConnector.RegistrantsOperation, new() { ["meeting"] = "85746065432", ["status"] = "waiting" }));
            Assert.Equal("Give the registrant's e-mail address.", await Refused(ZoomConnector.AddRegistrantOperation, new() { ["meeting"] = "85746065432", ["first_name"] = "Ada" }));
            Assert.Equal("'ada' is not an e-mail address.", await Refused(ZoomConnector.AddRegistrantOperation, new() { ["meeting"] = "85746065432", ["email"] = "ada", ["first_name"] = "Ada" }));
            Assert.Equal("Give the registrant's first name.", await Refused(ZoomConnector.AddRegistrantOperation, new() { ["meeting"] = "85746065432", ["email"] = "ada@example.com" }));
            Assert.Equal("'yesterday' is not a day (from) - 2026-10-01.", await Refused(ZoomConnector.RecordingsOperation, new() { ["from"] = "yesterday" }));
            Assert.Equal("'2026-10-12T09:00' is not a day (to) - 2026-10-01.", await Refused(ZoomConnector.RecordingsOperation, new() { ["to"] = "2026-10-12T09:00" }));
            Assert.Equal("'gone' is not a user's status - active, inactive or pending.", await Refused(ZoomConnector.UsersOperation, new() { ["status"] = "gone" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(ZoomConnector.UsersOperation, new() { ["max"] = "0" }));
            Assert.StartsWith("Zoom has no operation 'webinars' - meetings, get_meeting,", await Refused("webinars", new()));
            Assert.Empty(zoom.Requests); // nothing sent, not even the token asked for

            var missing = new Zoom { Answer = (_, _) => (HttpStatusCode.NotFound, """{"code":3001,"message":"Meeting does not exist: 85746065432."}""") };
            Assert.Equal("HTTP 404 3001: Meeting does not exist: 85746065432.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, ZoomConnector.GetMeetingOperation, new() { ["meeting"] = "85746065432" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user()
        {
            var zoom = new Zoom { Answer = (_, _) => (HttpStatusCode.OK, """{"id":"KDcuGIm1QgePTO8WbOqwIQ","first_name":"Ada","last_name":"Lovelace","email":"ada@example.com","account_id":"acct-1"}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(zoom), Api, settings: Settings, credentials: Credentials));
            Assert.Equal((true, "Signed in as Ada Lovelace (ada@example.com)."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/users/me", "Bearer access-1"), (zoom.Requests[1].Url, zoom.Requests[1].Authorization));
            var noAccount = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Zoom()), Api, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((false, "The OAuth2 token request is missing 'account-id' - check the connection's credentials."), (noAccount.Success, noAccount.Message));
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "zoom"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Productivity }, Connector().Categories);
            Assert.Equal(10, Connector().Operations.Count);
        }
    }
}
