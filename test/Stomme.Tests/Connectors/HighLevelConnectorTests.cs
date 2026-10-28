#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.HighLevel;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - HighLevel (n8n's HighLevel node, its second version) without a server: every id, address, number, time and choice
    /// held to its shape before anything is sent; the token as a bearer with HighLevel's Version header; contacts and opportunities
    /// page by page by HighLevel's startAfter, the location the connection's; a contact upserted by its email or phone with its
    /// tags and custom fields as HighLevel takes them and a note added after, updated and deleted; an opportunity made in its
    /// pipeline and the connection's location, changed, read and deleted; a contact's tasks, a task made due at a moment without
    /// fractions, one updated with its title and due read first; a calendar's free slots by days given as dates or milliseconds,
    /// each a record; an appointment booked; HighLevel's message - one or several - on a failure; a test counting the contacts.
    /// </summary>
    public sealed class HighLevelConnectorTests
    {
        private const string Api = "https://services.leadconnectorhq.com";
        private const string Location = "ve9EPM428h8vShlRW1KT";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "pit-kafe-token" };
        private static readonly Dictionary<string, string> Settings = new() { ["location"] = Location };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Version, string? Body);

        private sealed class HighLevel(Func<Sent, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Version", out var versions) ? versions.Single() : null, body);
                Requests.Add(sent);
                var (status, json) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(HighLevel highLevel, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new HighLevelConnector(), Contexts.Extract(new StubEgress(highLevel), "", operation, parameters, Settings, Credentials));

        private static string Contact(string id, string email) => $"{{\"id\":\"{id}\",\"locationId\":\"{Location}\",\"email\":\"{email}\",\"firstName\":\"Ada\",\"tags\":[\"stamkund\"],\"dnd\":false,\"customFields\":[{{\"id\":\"6dvNaf7VhkQ9snc5vnjJ\",\"value\":\"guld\"}}]}}";

        [Fact]
        public void It_declares_itself_as_n8ns_highlevel_node()
        {
            IConnector connector = new HighLevelConnector();
            Assert.Equal(("highlevel", "HighLevel", AuthenticationType.Bearer), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "contacts", "contact", "contact_upsert", "contact_update", "contact_delete", "opportunities", "opportunity", "opportunity_create", "opportunity_update", "opportunity_delete", "tasks", "task", "task_create", "task_update", "task_delete", "free_slots", "appointment_book" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null)
            {
                var highLevel = new HighLevel(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new HighLevelConnector(), Contexts.Extract(new StubEgress(highLevel), "", operation, parameters, settings ?? Settings, Credentials)));
                Assert.Empty(highLevel.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("HighLevel has no operation 'workflows' - contacts, contact,", await Refused("workflows", new()), StringComparison.Ordinal);
            Assert.Equal("Give the sub-account's location id (location) - Settings > Business Profile.", await Refused("contacts", new(), new Dictionary<string, string>()));
            Assert.Equal("'ve9/EPM' is not a location's id (location).", await Refused("contacts", new(), new Dictionary<string, string> { ["location"] = "ve9/EPM" }));
            Assert.Equal("'up' is not desc or asc (order).", await Refused("contacts", new() { ["order"] = "up" }));
            Assert.Equal("'name' is not date_added or date_updated (sort_by).", await Refused("contacts", new() { ["sort_by"] = "name" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("contacts", new() { ["max"] = "0" }));
            Assert.Equal("Give the contact's id (contact).", await Refused("contact", new()));
            Assert.Equal("'abc/../x' is not an id (contact).", await Refused("contact_delete", new() { ["contact"] = "abc/../x" }));
            Assert.Equal("Give the contact's email or phone - HighLevel finds or makes it by them (email, phone).", await Refused("contact_upsert", new() { ["first_name"] = "Ada" }));
            Assert.Equal("'ada.kafe.example' is not an email address (email).", await Refused("contact_upsert", new() { ["email"] = "ada.kafe.example" }));
            Assert.Equal("'ring mig' is not a phone number - +46 70 174 06 05 (phone).", await Refused("contact_upsert", new() { ["phone"] = "ring mig" }));
            Assert.Equal("'kanske' is not true or false (dnd).", await Refused("contact_upsert", new() { ["email"] = "ada@kafe.example", ["dnd"] = "kanske" }));
            Assert.Equal("The custom fields are a JSON object of their ids and values - {\"6dvNaf7VhkQ9snc5vnjJ\": \"guld\"} (custom_fields).", await Refused("contact_upsert", new() { ["email"] = "ada@kafe.example", ["custom_fields"] = "[\"guld\"]" }));
            Assert.Equal("The custom fields are a JSON object of their ids and values - {\"6dvNaf7VhkQ9snc5vnjJ\": \"guld\"} (custom_fields).", await Refused("contact_update", new() { ["contact"] = "c1", ["custom_fields"] = "{\"medlemsnivå\": \"guld\"}" }));
            Assert.Equal("Give what to change - email, phone, name, tags or another of its fields.", await Refused("contact_update", new() { ["contact"] = "c1" }));
            Assert.Equal("'pending' is not open, won, lost, abandoned or all (status).", await Refused("opportunities", new() { ["status"] = "pending" }));
            Assert.Equal("'2026-13-01' is not a day - 2026-10-01 (start).", await Refused("opportunities", new() { ["start"] = "2026-13-01" }));
            Assert.Equal("Give the opportunity's name (name).", await Refused("opportunity_create", new() { ["contact"] = "c1", ["pipeline"] = "p1" }));
            Assert.Equal("Give the pipeline it is in - its id (pipeline).", await Refused("opportunity_create", new() { ["contact"] = "c1", ["name"] = "Kassan" }));
            Assert.Equal("'pending' is not open, won, lost or abandoned (status).", await Refused("opportunity_create", new() { ["contact"] = "c1", ["name"] = "Kassan", ["pipeline"] = "p1", ["status"] = "pending" }));
            Assert.Equal("'1 250 kr' is not an amount - 1250.50 (monetary_value).", await Refused("opportunity_create", new() { ["contact"] = "c1", ["name"] = "Kassan", ["pipeline"] = "p1", ["monetary_value"] = "1 250 kr" }));
            Assert.Equal("Give what to change - name, status, stage, value or who it is assigned to.", await Refused("opportunity_update", new() { ["opportunity"] = "o1" }));
            Assert.Equal("Give the task's title (title).", await Refused("task_create", new() { ["contact"] = "c1", ["due"] = "2026-10-20T09:00:00+02:00" }));
            Assert.Equal("Give when the task is due - 2026-10-20T09:00:00+02:00 (due).", await Refused("task_create", new() { ["contact"] = "c1", ["title"] = "Ring" }));
            Assert.Equal("'i morgon' is not a time - 2026-10-20T09:00:00+02:00 (due).", await Refused("task_create", new() { ["contact"] = "c1", ["title"] = "Ring", ["due"] = "i morgon" }));
            Assert.Equal("Give what to change - title, due, body, completed or who it is assigned to.", await Refused("task_update", new() { ["contact"] = "c1", ["task"] = "t1" }));
            Assert.Equal("Give the first day - 2026-10-20 (start).", await Refused("free_slots", new() { ["calendar"] = "k1", ["end"] = "2026-10-27" }));
            Assert.Equal("'nästa vecka' is not a day, a time or milliseconds since 1970 - 2026-10-20 (end).", await Refused("free_slots", new() { ["calendar"] = "k1", ["start"] = "2026-10-20", ["end"] = "nästa vecka" }));
            Assert.Equal("The end is before the start (start, end).", await Refused("free_slots", new() { ["calendar"] = "k1", ["start"] = "2026-10-27", ["end"] = "2026-10-20" }));
            Assert.Equal("'u 2' is not an id (users).", await Refused("free_slots", new() { ["calendar"] = "k1", ["start"] = "2026-10-20", ["end"] = "2026-10-27", ["users"] = "u1, u 2" }));
            Assert.Equal("Give when it starts - 2026-10-20T09:00:00+02:00 (start).", await Refused("appointment_book", new() { ["calendar"] = "k1", ["contact"] = "c1" }));
            Assert.Equal("'maybe' is not new, confirmed, cancelled, showed, noshow or invalid (appointment_status).", await Refused("appointment_book", new() { ["calendar"] = "k1", ["contact"] = "c1", ["start"] = "2026-10-20T09:00:00+02:00", ["appointment_status"] = "maybe" }));
        }

        [Fact]
        public async Task Contacts_are_listed_page_by_page_upserted_with_a_note_updated_and_deleted()
        {
            var highLevel = new HighLevel(sent => (sent.Method, new Uri(sent.Url).AbsolutePath) switch
            {
                ("GET", "/contacts/") when sent.Url.Contains("startAfterId=c99", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"contacts\":[" + string.Join(",", Enumerable.Range(100, 30).Select(i => Contact("c" + i, $"p{i}@kafe.example"))) + "],\"meta\":{\"total\":130,\"startAfterId\":\"c129\",\"startAfter\":1760000099000}}"), // the total reached: no page after it
                ("GET", "/contacts/") => (HttpStatusCode.OK, "{\"contacts\":[" + string.Join(",", Enumerable.Range(0, 100).Select(i => Contact("c" + i, $"p{i}@kafe.example"))) + "],\"meta\":{\"total\":130,\"startAfterId\":\"c99\",\"startAfter\":1760000000000}}"),
                ("GET", _) => (HttpStatusCode.OK, "{\"contact\":" + Contact("c7", "ada@kafe.example") + "}"),
                ("POST", "/contacts/upsert") => (HttpStatusCode.Created, "{\"new\":true,\"contact\":" + Contact("c8", "ada@kafe.example") + ",\"traceId\":\"t-1\"}"),
                ("POST", "/contacts/c8/notes") => (HttpStatusCode.Created, "{\"note\":{\"id\":\"n1\",\"body\":\"Stamkund sedan 2019.\"}}"),
                ("PUT", _) => (HttpStatusCode.OK, "{\"succeded\":true,\"contact\":" + Contact("c7", "ada@kafe.example") + "}"),
                _ => (HttpStatusCode.OK, "{\"succeded\":true}"),
            });
            var all = await Run(highLevel, "contacts", new() { ["query"] = "kafe & co", ["order"] = "ASC", ["sort_by"] = "date_updated", ["max"] = "500" });
            Assert.Equal(130, all.Count);
            Assert.Equal(new[]
            {
                Api + "/contacts/?query=kafe%20%26%20co&order=asc&sortBy=date_updated&locationId=" + Location + "&limit=100",
                Api + "/contacts/?query=kafe%20%26%20co&order=asc&sortBy=date_updated&locationId=" + Location + "&limit=100&startAfterId=c99&startAfter=1760000000000",
            }, highLevel.Requests.Select(sent => sent.Url));
            Assert.All(highLevel.Requests, sent => Assert.Equal(("Bearer pit-kafe-token", "2021-07-28"), (sent.Authorization, sent.Version)));
            var first = all[0].Fields;
            Assert.Equal(("c0", "[\"stamkund\"]", false, "[{\"id\":\"6dvNaf7VhkQ9snc5vnjJ\",\"value\":\"guld\"}]"), ((string?)first["id"], (string?)first["tags"], (bool?)first["dnd"], (string?)first["customFields"]));
            highLevel.Requests.Clear();
            Assert.Equal(5, (await Run(highLevel, "contacts", new() { ["max"] = "5" })).Count);
            Assert.Equal(Api + "/contacts/?order=desc&sortBy=date_added&locationId=" + Location + "&limit=5", highLevel.Requests.Single().Url); // n8n's order, a page no larger than wanted

            Assert.Equal("ada@kafe.example", Assert.Single(await Run(highLevel, "contact", new() { ["contact"] = "c7" })).Fields["email"]);
            Assert.Equal(("GET", Api + "/contacts/c7"), (highLevel.Requests.Last().Method, highLevel.Requests.Last().Url));

            highLevel.Requests.Clear();
            var made = Assert.Single(await Run(highLevel, "contact_upsert", new() { ["email"] = "ada@kafe.example", ["phone"] = "+46 70 174 06 05", ["first_name"] = "Ada", ["last_name"] = "Lovelace", ["postal_code"] = "411 01", ["dnd"] = "true", ["tags"] = "stamkund, kassa, stamkund", ["custom_fields"] = "{\"6dvNaf7VhkQ9snc5vnjJ\": \"guld\", \"x9Kf2\": 3}", ["source"] = "Kassan", ["note"] = "Stamkund sedan 2019." })).Fields;
            Assert.Equal(("c8", true, "n1"), ((string?)made["id"], (bool?)made["new"], (string?)made["note_id"]));
            Assert.Equal("{\"email\":\"ada@kafe.example\",\"phone\":\"+46 70 174 06 05\",\"firstName\":\"Ada\",\"lastName\":\"Lovelace\",\"postalCode\":\"411 01\",\"source\":\"Kassan\",\"dnd\":true,\"tags\":[\"stamkund\",\"kassa\"],\"customFields\":[{\"id\":\"6dvNaf7VhkQ9snc5vnjJ\",\"field_value\":\"guld\"},{\"id\":\"x9Kf2\",\"field_value\":3}],\"locationId\":\"" + Location + "\"}", highLevel.Requests[0].Body);
            Assert.Equal(("POST", Api + "/contacts/c8/notes", "{\"body\":\"Stamkund sedan 2019.\"}"), (highLevel.Requests[1].Method, highLevel.Requests[1].Url, highLevel.Requests[1].Body));

            highLevel.Requests.Clear();
            await Run(highLevel, "contact_update", new() { ["contact"] = "c7", ["name"] = "Ada Lovelace", ["source"] = "Kassan", ["dnd"] = "false" });
            Assert.Equal(("PUT", Api + "/contacts/c7", "{\"name\":\"Ada Lovelace\",\"dnd\":false}"), (highLevel.Requests[0].Method, highLevel.Requests[0].Url, highLevel.Requests[0].Body)); // a source is the upsert's alone, as n8n's
            Assert.Equal(new Dictionary<string, object?> { ["contact"] = "c7", ["deleted"] = true }, Assert.Single(await Run(highLevel, "contact_delete", new() { ["contact"] = "c7" })).Fields);
            Assert.Equal(("DELETE", Api + "/contacts/c7"), (highLevel.Requests.Last().Method, highLevel.Requests.Last().Url));
        }

        [Fact]
        public async Task Opportunities_are_searched_made_in_their_pipeline_changed_and_deleted()
        {
            const string Opportunity = "{\"opportunity\":{\"id\":\"o1\",\"name\":\"Kassan, nytt avtal\",\"monetaryValue\":1250.5,\"status\":\"open\",\"pipelineId\":\"p1\",\"pipelineStageId\":\"s2\",\"contactId\":\"c1\"}}";
            var highLevel = new HighLevel(sent => (sent.Method, new Uri(sent.Url).AbsolutePath) switch
            {
                ("GET", "/opportunities/search") => (HttpStatusCode.OK, "{\"opportunities\":[{\"id\":\"o1\",\"name\":\"Kassan\"},{\"id\":\"o2\",\"name\":\"Kafét\"}],\"meta\":{\"total\":2}}"),
                ("DELETE", _) => (HttpStatusCode.OK, "{\"succeded\":true}"),
                _ => (HttpStatusCode.OK, Opportunity),
            });
            var found = await Run(highLevel, "opportunities", new() { ["query"] = "Kassan", ["pipeline"] = "p1", ["stage"] = "s2", ["status"] = "Won", ["assigned_to"] = "u1", ["campaign"] = "k9", ["start"] = "2026-10-01", ["end"] = "2026-10-31" });
            Assert.Equal(new[] { "o1", "o2" }, found.Select(record => record.Fields["id"]));
            Assert.Equal(Api + "/opportunities/search?q=Kassan&pipeline_id=p1&pipeline_stage_id=s2&assigned_to=u1&campaignId=k9&status=won&date=10-01-2026&endDate=10-31-2026&location_id=" + Location + "&limit=100", highLevel.Requests.Single().Url);

            highLevel.Requests.Clear();
            var made = Assert.Single(await Run(highLevel, "opportunity_create", new() { ["contact"] = "c1", ["name"] = "Kassan, nytt avtal", ["pipeline"] = "p1", ["stage"] = "s2", ["monetary_value"] = "1250.50", ["assigned_to"] = "u1", ["company_name"] = "Kafé Kassan AB", ["tags"] = "kassa" })).Fields;
            Assert.Equal(("o1", 1250.5), ((string?)made["id"], (double?)made["monetaryValue"]));
            Assert.Equal(("POST", Api + "/opportunities/", "{\"contactId\":\"c1\",\"name\":\"Kassan, nytt avtal\",\"pipelineId\":\"p1\",\"status\":\"open\",\"pipelineStageId\":\"s2\",\"monetaryValue\":1250.50,\"assignedTo\":\"u1\",\"companyName\":\"Kafé Kassan AB\",\"tags\":[\"kassa\"],\"locationId\":\"" + Location + "\"}"), (highLevel.Requests[0].Method, highLevel.Requests[0].Url, highLevel.Requests[0].Body));
            await Run(highLevel, "opportunity_update", new() { ["opportunity"] = "o1", ["status"] = "lost", ["company_name"] = "skippas" });
            Assert.Equal(("PUT", Api + "/opportunities/o1", "{\"status\":\"lost\"}"), (highLevel.Requests.Last().Method, highLevel.Requests.Last().Url, highLevel.Requests.Last().Body));
            Assert.Equal("Kassan, nytt avtal", Assert.Single(await Run(highLevel, "opportunity", new() { ["opportunity"] = "o1" })).Fields["name"]);
            Assert.Equal(new Dictionary<string, object?> { ["opportunity"] = "o1", ["deleted"] = true }, Assert.Single(await Run(highLevel, "opportunity_delete", new() { ["opportunity"] = "o1" })).Fields);
        }

        [Fact]
        public async Task A_contacts_tasks_are_read_made_updated_from_what_they_were_and_deleted()
        {
            const string Task = "{\"task\":{\"id\":\"t1\",\"title\":\"Ring tillbaka\",\"dueDate\":\"2026-10-20T07:00:00Z\",\"completed\":false,\"contactId\":\"c1\"}}";
            var highLevel = new HighLevel(sent => (sent.Method, new Uri(sent.Url).AbsolutePath) switch
            {
                ("GET", "/contacts/c1/tasks") => (HttpStatusCode.OK, "{\"tasks\":[{\"id\":\"t1\",\"title\":\"Ring\"},{\"id\":\"t2\",\"title\":\"Skriv\"},{\"id\":\"t3\",\"title\":\"Boka\"}]}"),
                ("DELETE", _) => (HttpStatusCode.OK, "{\"succeded\":true}"),
                _ => (HttpStatusCode.OK, Task),
            });
            Assert.Equal(new[] { "t1", "t2" }, (await Run(highLevel, "tasks", new() { ["contact"] = "c1", ["max"] = "2" })).Select(record => record.Fields["id"]));
            highLevel.Requests.Clear();
            var made = Assert.Single(await Run(highLevel, "task_create", new() { ["contact"] = "c1", ["title"] = "Ring tillbaka", ["due"] = "2026-10-20T09:00:00.250+02:00", ["body"] = "Om kassan." })).Fields;
            Assert.Equal(("t1", false), ((string?)made["id"], (bool?)made["completed"]));
            Assert.Equal(("POST", Api + "/contacts/c1/tasks", "{\"title\":\"Ring tillbaka\",\"dueDate\":\"2026-10-20T09:00:00+02:00\",\"body\":\"Om kassan.\",\"completed\":false}"), (highLevel.Requests[0].Method, highLevel.Requests[0].Url, highLevel.Requests[0].Body)); // no fractions; not completed unless said

            highLevel.Requests.Clear();
            await Run(highLevel, "task_update", new() { ["contact"] = "c1", ["task"] = "t1", ["completed"] = "true" });
            Assert.Equal(new[] { "GET " + Api + "/contacts/c1/tasks/t1", "PUT " + Api + "/contacts/c1/tasks/t1" }, highLevel.Requests.Select(sent => sent.Method + " " + sent.Url));
            Assert.Equal("{\"completed\":true,\"title\":\"Ring tillbaka\",\"dueDate\":\"2026-10-20T07:00:00Z\"}", highLevel.Requests[1].Body); // its title and due read first, as HighLevel needs them
            highLevel.Requests.Clear();
            await Run(highLevel, "task_update", new() { ["contact"] = "c1", ["task"] = "t1", ["title"] = "Ring", ["due"] = "2026-10-21T10:00:00Z" });
            Assert.Equal("PUT", Assert.Single(highLevel.Requests).Method); // both given: nothing read
            Assert.Equal("Ring tillbaka", Assert.Single(await Run(highLevel, "task", new() { ["contact"] = "c1", ["task"] = "t1" })).Fields["title"]);
            Assert.Equal(new Dictionary<string, object?> { ["task"] = "t1", ["deleted"] = true }, Assert.Single(await Run(highLevel, "task_delete", new() { ["contact"] = "c1", ["task"] = "t1" })).Fields);
            Assert.Equal(("DELETE", Api + "/contacts/c1/tasks/t1"), (highLevel.Requests.Last().Method, highLevel.Requests.Last().Url));
        }

        [Fact]
        public async Task A_calendars_free_slots_are_each_a_record_an_appointment_is_booked_and_a_failure_is_highlevels_words()
        {
            var highLevel = new HighLevel(sent => new Uri(sent.Url).AbsolutePath switch
            {
                "/calendars/k1/free-slots" => (HttpStatusCode.OK, "{\"2026-10-21\":{\"slots\":[\"2026-10-21T09:00:00+02:00\"]},\"2026-10-20\":{\"slots\":[\"2026-10-20T09:00:00+02:00\",\"2026-10-20T09:30:00+02:00\"]},\"traceId\":\"t-9\"}"),
                "/contacts/" => (HttpStatusCode.OK, "{\"contacts\":[{\"id\":\"c1\"}],\"meta\":{\"total\":412}}"),
                _ => (HttpStatusCode.Created, "{\"id\":\"a1\",\"calendarId\":\"k1\",\"contactId\":\"c1\",\"startTime\":\"2026-10-20T09:00:00+02:00\",\"appointmentStatus\":\"confirmed\"}"),
            });
            var slots = await Run(highLevel, "free_slots", new() { ["calendar"] = "k1", ["start"] = "2026-10-20", ["end"] = "1793138400000", ["timezone"] = "Europe/Stockholm", ["users"] = "u1, u2" });
            Assert.Equal(new[] { "2026-10-20 2026-10-20T09:00:00+02:00", "2026-10-20 2026-10-20T09:30:00+02:00", "2026-10-21 2026-10-21T09:00:00+02:00" }, slots.Select(slot => slot.Fields["date"] + " " + slot.Fields["slot"]));
            Assert.Equal(Api + "/calendars/k1/free-slots?startDate=1792454400000&endDate=1793138400000&timezone=Europe%2FStockholm&userIds=u1%2Cu2", highLevel.Requests.Single().Url); // a day as its first millisecond, UTC
            Assert.Equal(2, (await Run(highLevel, "free_slots", new() { ["calendar"] = "k1", ["start"] = "2026-10-20", ["end"] = "2026-10-27", ["max"] = "2" })).Count); // the most across the days

            highLevel.Requests.Clear();
            var booked = Assert.Single(await Run(highLevel, "appointment_book", new() { ["calendar"] = "k1", ["contact"] = "c1", ["start"] = "2026-10-20T09:00:00+02:00", ["end"] = "2026-10-20T09:30:00+02:00", ["title"] = "Kassagenomgång", ["appointment_status"] = "Confirmed", ["assigned_user"] = "u1", ["notify"] = "false" })).Fields;
            Assert.Equal(("a1", "confirmed"), ((string?)booked["id"], (string?)booked["appointmentStatus"]));
            Assert.Equal(("POST", Api + "/calendars/events/appointments", "{\"calendarId\":\"k1\",\"contactId\":\"c1\",\"startTime\":\"2026-10-20T09:00:00+02:00\",\"endTime\":\"2026-10-20T09:30:00+02:00\",\"title\":\"Kassagenomgång\",\"appointmentStatus\":\"confirmed\",\"assignedUserId\":\"u1\",\"toNotify\":false,\"locationId\":\"" + Location + "\"}"), (highLevel.Requests[0].Method, highLevel.Requests[0].Url, highLevel.Requests[0].Body));

            var tested = await new HighLevelConnector().TestConnectionAsync(Contexts.Test(new StubEgress(highLevel), "", Settings, Credentials));
            Assert.Equal((true, "HighLevel answers - location " + Location + ", 412 contacts."), (tested.Success, tested.Message));
            var refusing = new HighLevel(_ => (HttpStatusCode.Unauthorized, "{\"statusCode\":401,\"message\":\"The token is not authorized for this scope.\"}"));
            Assert.Equal("HighLevel answered HTTP 401: The token is not authorized for this scope. - check the Private Integration token, its scopes and the location.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "contacts", new()))).Message);
            var invalid = new HighLevel(_ => (HttpStatusCode.UnprocessableEntity, "{\"statusCode\":422,\"message\":[\"email must be an email\",\"phone must be a valid phone number\"],\"error\":\"Unprocessable Entity\"}"));
            Assert.Equal("HighLevel answered HTTP 422: email must be an email; phone must be a valid phone number", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(invalid, "contact_upsert", new() { ["email"] = "ada@kafe.example" }))).Message);
            var odd = new HighLevel(_ => (HttpStatusCode.OK, "[]"));
            Assert.Equal("HighLevel answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(odd, "contacts", new()))).Message);
            var elsewhere = await new HighLevelConnector().TestConnectionAsync(Contexts.Test(new StubEgress(highLevel), "https://rest.gohighlevel.com/v1", Settings, Credentials));
            Assert.Equal((false, "'https://rest.gohighlevel.com/v1' is not HighLevel's address - its own, https://services.leadconnectorhq.com."), (elsewhere.Success, elsewhere.Message));
        }
    }
}
