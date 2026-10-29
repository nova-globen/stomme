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
using Stomme.Modules.Connectors.Pipedrive;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Pipedrive (n8n's Pipedrive node: deals, persons, organizations, activities and products listed, got, searched,
    /// created, updated and deleted, notes added) over Pipedrive's API v2: the token in Pipedrive's x-api-token header, never in
    /// an address; an object's records page by page by Pipedrive's cursor up to the most asked for, its filters held to Pipedrive's
    /// shapes; a search's finds each its score; a record got, created, updated by PATCH and deleted; a note added through API v1;
    /// an object, an id, a field's name, fields that are not a JSON object, a status, a time and a term refused before anything is
    /// sent; Pipedrive's refusal its error; a test naming the user.
    /// </summary>
    public sealed class PipedriveConnectorTests
    {
        private const string Company = "https://acme.pipedrive.example";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "pd-token-1" };

        private sealed class PipedriveApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Token, bool Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{\"success\":true,\"data\":{}}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("x-api-token", out var tokens) ? tokens.Single() : null, request.Headers.Authorization is not null, body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(PipedriveApi pipedrive, string operation, Dictionary<string, string> parameters, string endpoint = Company) =>
            Contexts.Extract(new StubEgress(pipedrive), endpoint, operation, parameters, null, Credentials);

        private static PipedriveConnector Connector() => new();

        private static string Deal(int id, string title) =>
            "{\"id\":" + id + ",\"title\":\"" + title + "\",\"owner_id\":7,\"person_id\":21,\"org_id\":31,\"stage_id\":2,\"value\":5000.5,\"currency\":\"SEK\",\"status\":\"open\",\"probability\":null,"
            + "\"is_deleted\":false,\"label_ids\":[3,4],\"custom_fields\":{\"a1b2\":\"Gold\"},\"add_time\":\"2026-10-01T10:00:00Z\",\"update_time\":\"2026-10-02T10:00:00Z\"}";

        [Fact]
        public async Task An_objects_records_are_read_page_by_page_by_pipedrives_cursor_up_to_the_most_asked_for()
        {
            var pipedrive = new PipedriveApi
            {
                Answer = (_, url, _) => url.Contains("cursor=c1", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"success\":true,\"data\":[" + Deal(3, "Third") + "," + Deal(4, "Fourth") + "],\"additional_data\":{\"next_cursor\":\"c2\"}}")
                    : (HttpStatusCode.OK, "{\"success\":true,\"data\":[" + Deal(1, "First") + "," + Deal(2, "Second") + "],\"additional_data\":{\"next_cursor\":\"c1\"}}"),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(pipedrive, PipedriveConnector.ListOperation, new()
            {
                ["object"] = "Deals", ["owner_id"] = "7", ["stage_id"] = "2", ["status"] = "open, Won", ["updated_since"] = "2026-10-01T12:00:00+02:00", ["newest_first"] = "true", ["max"] = "3",
            }, Company + "/deals/list"));

            Assert.Equal(new[] { "First", "Second", "Third" }, records.Select(record => (string?)record.Fields["title"]));
            Assert.Equal(2, pipedrive.Requests.Count); // the most asked for: no page more
            Assert.Equal(Company + "/api/v2/deals?owner_id=7&stage_id=2&status=open,won&updated_since=2026-10-01T10%3A00%3A00Z&sort_by=update_time&sort_direction=desc&limit=3", pipedrive.Requests[0].Url);
            Assert.EndsWith("&limit=1&cursor=c1", pipedrive.Requests[1].Url, StringComparison.Ordinal);
            Assert.All(pipedrive.Requests, request => Assert.Equal(("pd-token-1", false), (request.Token, request.Authorization)));
            Assert.All(pipedrive.Requests, request => Assert.DoesNotContain("pd-token-1", request.Url, StringComparison.Ordinal)); // never in an address
            var deal = records[0].Fields;
            Assert.Equal((1L, 7L, 5000.5, "SEK", false, "[3,4]", "{\"a1b2\":\"Gold\"}"), ((long?)deal["id"], (long?)deal["owner_id"], (double?)deal["value"], (string?)deal["currency"], (bool?)deal["is_deleted"], (string?)deal["label_ids"], (string?)deal["custom_fields"]));
            Assert.Null(deal["probability"]);

            var exact = new PipedriveApi { Answer = pipedrive.Answer };
            Assert.Equal(2, (await Contexts.DrainAsync(Connector(), Context(exact, PipedriveConnector.ListOperation, new() { ["object"] = "persons", ["max"] = "2" }))).Count);
            Assert.Single(exact.Requests); // a page ending at the most asked for: the next is not asked for
            Assert.Equal(Company + "/api/v2/persons?limit=2", exact.Requests[0].Url);

            var last = new PipedriveApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"success\":true,\"data\":[" + Deal(1, "Only") + "],\"additional_data\":{\"next_cursor\":null}}") };
            Assert.Single(await Contexts.DrainAsync(Connector(), Context(last, PipedriveConnector.ListOperation, new() { ["object"] = "deals" }, "")));
            Assert.Equal("https://api.pipedrive.com/api/v2/deals?limit=500", Assert.Single(last.Requests).Url); // Pipedrive's address by default, its last page
        }

        [Fact]
        public async Task A_search_finds_records_each_its_score_and_a_record_is_got_created_updated_deleted_and_noted()
        {
            var pipedrive = new PipedriveApi
            {
                Answer = (method, url, _) => (method.Method, url) switch
                {
                    ("GET", _) when url.Contains("/search?", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"success\":true,\"data\":{\"items\":[{\"result_score\":0.87,\"item\":{\"id\":21,\"type\":\"person\",\"name\":\"Ada Lovelace\",\"emails\":[\"ada@example.com\"]}}]},\"additional_data\":{\"next_cursor\":null}}"),
                    ("DELETE", _) => (HttpStatusCode.OK, "{\"success\":true,\"data\":{\"id\":5}}"),
                    ("POST", _) when url.EndsWith("/v1/notes", StringComparison.Ordinal) => (HttpStatusCode.Created, "{\"success\":true,\"data\":{\"id\":91,\"content\":\"Called\",\"deal_id\":5}}"),
                    _ => (HttpStatusCode.OK, "{\"success\":true,\"data\":" + Deal(5, "Big deal") + "}"),
                },
            };

            var found = await Contexts.DrainAsync(Connector(), Context(pipedrive, PipedriveConnector.SearchOperation, new() { ["object"] = "persons", ["term"] = "Ada & co", ["max"] = "10" }));
            Assert.Equal(Company + "/api/v2/persons/search?term=Ada%20%26%20co&limit=10", pipedrive.Requests[0].Url);
            Assert.Equal(("Ada Lovelace", 0.87, "[\"ada@example.com\"]"), ((string?)Assert.Single(found).Fields["name"], (double?)found[0].Fields["result_score"], (string?)found[0].Fields["emails"]));
            await Contexts.DrainAsync(Connector(), Context(pipedrive, PipedriveConnector.SearchOperation, new() { ["object"] = "deals", ["term"] = "B", ["exact_match"] = "true" }));
            Assert.Equal(Company + "/api/v2/deals/search?term=B&exact_match=true&limit=100", pipedrive.Requests[^1].Url); // one character with an exact match

            var got = await Contexts.DrainAsync(Connector(), Context(pipedrive, PipedriveConnector.GetOperation, new() { ["object"] = "deals", ["id"] = "5" }));
            Assert.Equal((HttpMethod.Get, Company + "/api/v2/deals/5"), (pipedrive.Requests[^1].Method, pipedrive.Requests[^1].Url));
            Assert.Equal("Big deal", Assert.Single(got).Fields["title"]);

            await Contexts.DrainAsync(Connector(), Context(pipedrive, PipedriveConnector.CreateOperation, new() { ["object"] = "deals", ["fields"] = "{\"title\": \"Big deal\", \"value\": 5000, \"custom_fields\": {\"a1b2\": \"Gold\"}}" }));
            Assert.Equal((HttpMethod.Post, Company + "/api/v2/deals", "{\"title\":\"Big deal\",\"value\":5000,\"custom_fields\":{\"a1b2\":\"Gold\"}}"), (pipedrive.Requests[^1].Method, pipedrive.Requests[^1].Url, pipedrive.Requests[^1].Body));

            await Contexts.DrainAsync(Connector(), Context(pipedrive, PipedriveConnector.UpdateOperation, new() { ["object"] = "deals", ["id"] = "5", ["fields"] = "{\"status\": \"won\"}" }));
            Assert.Equal((HttpMethod.Patch, Company + "/api/v2/deals/5", "{\"status\":\"won\"}"), (pipedrive.Requests[^1].Method, pipedrive.Requests[^1].Url, pipedrive.Requests[^1].Body));

            var removed = await Contexts.DrainAsync(Connector(), Context(pipedrive, PipedriveConnector.DeleteOperation, new() { ["object"] = "activities", ["id"] = "5" }));
            Assert.Equal((HttpMethod.Delete, Company + "/api/v2/activities/5"), (pipedrive.Requests[^1].Method, pipedrive.Requests[^1].Url));
            Assert.Equal((5L, "activities", true), ((long?)Assert.Single(removed).Fields["id"], (string?)removed[0].Fields["object"], (bool?)removed[0].Fields["deleted"]));

            var noted = await Contexts.DrainAsync(Connector(), Context(pipedrive, PipedriveConnector.AddNoteOperation, new() { ["content"] = "Called", ["deal_id"] = "5", ["lead_id"] = "4f6b2c1e-0a1b-4c2d-9e8f-001122334455" }));
            Assert.Equal((HttpMethod.Post, Company + "/v1/notes", "{\"content\":\"Called\",\"deal_id\":5,\"lead_id\":\"4f6b2c1e-0a1b-4c2d-9e8f-001122334455\"}"), (pipedrive.Requests[^1].Method, pipedrive.Requests[^1].Url, pipedrive.Requests[^1].Body));
            Assert.Equal(91L, Assert.Single(noted).Fields["id"]);
            Assert.All(pipedrive.Requests, request => Assert.Equal("pd-token-1", request.Token));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_pipedrives_refusal_is_its_error()
        {
            var pipedrive = new PipedriveApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Company) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(pipedrive, operation, parameters, endpoint)))).Message;

            Assert.Equal("'leads' is not an object Pipedrive keeps - deals, persons, organizations, activities, products.", await Refused(PipedriveConnector.GetOperation, new() { ["object"] = "leads", ["id"] = "1" }));
            Assert.Equal("'activities' is not an object Pipedrive searches - deals, persons, organizations, products.", await Refused(PipedriveConnector.SearchOperation, new() { ["object"] = "activities", ["term"] = "call" }));
            Assert.Equal("'5/../1' is not a Pipedrive record's id (digits).", await Refused(PipedriveConnector.DeleteOperation, new() { ["object"] = "deals", ["id"] = "5/../1" }));
            Assert.Equal("'me' is not a Pipedrive owner's id (digits).", await Refused(PipedriveConnector.ListOperation, new() { ["object"] = "deals", ["owner_id"] = "me" }));
            Assert.Equal("'Title' is not a Pipedrive field's name - a custom field goes inside custom_fields.", await Refused(PipedriveConnector.CreateOperation, new() { ["object"] = "deals", ["fields"] = "{\"Title\": \"x\"}" }));
            Assert.Equal("The fields are not JSON - {\"title\": \"Big deal\"}.", await Refused(PipedriveConnector.CreateOperation, new() { ["object"] = "deals", ["fields"] = "title=x" }));
            Assert.Equal("The fields are not a JSON object.", await Refused(PipedriveConnector.CreateOperation, new() { ["object"] = "deals", ["fields"] = "[{\"title\": \"x\"}]" }));
            Assert.Equal("Give at least one field.", await Refused(PipedriveConnector.UpdateOperation, new() { ["object"] = "deals", ["id"] = "5", ["fields"] = "{}" }));
            Assert.Equal("'open, pending' is not a deal's status - open, won, lost or deleted.", await Refused(PipedriveConnector.ListOperation, new() { ["object"] = "deals", ["status"] = "open, pending" }));
            Assert.Equal("Only deals are listed by status.", await Refused(PipedriveConnector.ListOperation, new() { ["object"] = "persons", ["status"] = "open" }));
            Assert.Equal("'last week' is not a date or a time.", await Refused(PipedriveConnector.ListOperation, new() { ["object"] = "deals", ["updated_since"] = "last week" }));
            Assert.Equal("Give a term of at least two characters - one with an exact match.", await Refused(PipedriveConnector.SearchOperation, new() { ["object"] = "deals", ["term"] = "B" }));
            Assert.Equal("Give what the note is on - a deal, a person, an organization or a lead.", await Refused(PipedriveConnector.AddNoteOperation, new() { ["content"] = "Called" }));
            Assert.Equal("'lead-1' is not a Pipedrive lead's id (a UUID).", await Refused(PipedriveConnector.AddNoteOperation, new() { ["content"] = "Called", ["lead_id"] = "lead-1" }));
            Assert.Equal("'ftp://acme.example' is not Pipedrive's address - https://api.pipedrive.com.", await Refused(PipedriveConnector.ListOperation, new() { ["object"] = "deals" }, "ftp://acme.example"));
            Assert.Empty(pipedrive.Requests);

            var refusing = new PipedriveApi { Answer = (_, _, _) => (HttpStatusCode.NotFound, "{\"success\":false,\"error\":\"Deal not found\",\"error_info\":\"Please check developers.pipedrive.com\"}") };
            Assert.Equal("HTTP 404: Deal not found", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, PipedriveConnector.GetOperation, new() { ["object"] = "deals", ["id"] = "9" })))).Message);
            var unsuccessful = new PipedriveApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"success\":false,\"error\":\"Bad request\"}") };
            Assert.Equal("HTTP 200: Bad request", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(unsuccessful, PipedriveConnector.GetOperation, new() { ["object"] = "deals", ["id"] = "9" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user()
        {
            var pipedrive = new PipedriveApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"success\":true,\"data\":{\"id\":7,\"name\":\"Ada Lovelace\",\"company_name\":\"Acme\"}}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(pipedrive), Company, credentials: Credentials));
            Assert.Equal((true, "Signed in as Ada Lovelace (Acme)."), (tested.Success, tested.Message));
            Assert.Equal((Company + "/v1/users/me", "pd-token-1"), (pipedrive.Requests[0].Url, pipedrive.Requests[0].Token));
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new PipedriveApi { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "{\"success\":false,\"error\":\"unauthorized access\",\"errorCode\":401}") }), Company, credentials: Credentials));
            Assert.Equal((false, "HTTP 401: unauthorized access"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "pipedrive", ConnectorCategory.Crm), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "list", "search", "get", "create", "update", "delete", "add_note" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
