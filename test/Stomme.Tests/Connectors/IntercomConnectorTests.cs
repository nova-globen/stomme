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
using Stomme.Modules.Connectors.Intercom;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Intercom (n8n's Intercom node: its users, leads and companies): the token as a bearer with Intercom's API version;
    /// the contacts page by page by Intercom's cursor up to the most asked for, or searched by a role, an address and a query;
    /// each contact its fields, its times in UTC, its location, its tags' and companies' ids and its custom attributes; a contact
    /// found by its address or external id, got, made (a user by default), changed and deleted; companies page by page, got by id,
    /// company id or name, upserted and deleted, their contacts; a contact attached to and detached from a company, tagged and
    /// untagged by a tag's name or id, noted; what could go wrong refused before anything is sent; Intercom's errors in its words;
    /// a test naming the admin and the workspace.
    /// </summary>
    public sealed class IntercomConnectorTests
    {
        private const string Api = "https://api.intercom.example.test";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "dG9rOjEyMw==" };

        private sealed class Intercom : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Version, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Intercom-Version", out var version) ? version.Single() : null, body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Intercom intercom, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(intercom), Api, operation, parameters, settings: new Dictionary<string, string>(), credentials: Credentials);

        private static string Contact(int n) =>
            "{\"type\":\"contact\",\"id\":\"66" + n + "\",\"external_id\":\"ext-" + n + "\",\"role\":\"user\",\"email\":\"ada" + n + "@example.com\",\"phone\":null,\"name\":\"Café <b>Ada</b>\",\"owner_id\":991,"
            + "\"unsubscribed_from_emails\":false,\"has_hard_bounced\":true,\"created_at\":1700000000,\"updated_at\":1700000000,\"signed_up_at\":null,\"last_seen_at\":0,"
            + "\"location\":{\"type\":\"location\",\"city\":\"Lund\",\"country\":\"Sweden\"},\"tags\":{\"type\":\"list\",\"data\":[{\"type\":\"tag\",\"id\":\"17\"},{\"type\":\"tag\",\"id\":\"18\"}]},"
            + "\"companies\":{\"type\":\"list\",\"data\":[{\"type\":\"company\",\"id\":\"c1\"}]},\"custom_attributes\":{\"plan\":\"Pro\",\"seats\":5,\"vip\":true,\"name\":\"Custom\",\"tiers\":[1,2]}}";

        private static string Contacts(int from, int count, string? next) =>
            "{\"type\":\"list\",\"data\":[" + string.Join(",", Enumerable.Range(from, count).Select(Contact)) + "],\"total_count\":200,\"pages\":{\"type\":\"pages\",\"page\":1,\"per_page\":150"
            + (next is null ? "" : ",\"next\":{\"page\":2,\"starting_after\":\"" + next + "\"}") + "}}";

        [Fact]
        public async Task Contacts_are_read_page_by_page_by_cursor_or_searched_each_a_record()
        {
            var intercom = new Intercom { Answer = (_, url, _) => (HttpStatusCode.OK, url.Contains("starting_after", StringComparison.Ordinal) ? Contacts(151, 50, null) : Contacts(1, 150, "WzE2+MF0=")) };
            var records = await Contexts.DrainAsync(new IntercomConnector(), Context(intercom, IntercomConnector.ContactsOperation, new()));

            Assert.Equal(200, records.Count); // no next page: the end
            Assert.All(intercom.Requests, request => Assert.Equal(("Bearer dG9rOjEyMw==", "2.11"), (request.Authorization, request.Version)));
            Assert.Equal(new[] { Api + "/contacts?per_page=150", Api + "/contacts?per_page=150&starting_after=WzE2%2BMF0%3D" }, intercom.Requests.Select(request => request.Url));
            var ada = records[0].Fields;
            Assert.Equal(("661", "ext-1", "user", "ada1@example.com", (string?)null, "Café <b>Ada</b>", 991L), ((string?)ada["id"], (string?)ada["external_id"], (string?)ada["role"], (string?)ada["email"], (string?)ada["phone"], (string?)ada["name"], (long?)ada["owner_id"]));
            Assert.Equal((false, true, "2023-11-14T22:13:20Z", (string?)null, (string?)null), ((bool?)ada["unsubscribed_from_emails"], (bool?)ada["has_hard_bounced"], (string?)ada["created_at"], (string?)ada["signed_up_at"], (string?)ada["last_seen_at"])); // never seen: no time
            Assert.Equal(("Lund", "Sweden", "17, 18", "c1"), ((string?)ada["city"], (string?)ada["country"], (string?)ada["tag_ids"], (string?)ada["company_ids"]));
            Assert.Equal(("Pro", 5L, true, "Custom", "[1,2]"), ((string?)ada["plan"], (long?)ada["seats"], (bool?)ada["vip"], (string?)ada["custom_name"], (string?)ada["tiers"])); // a custom attribute named as a field: custom_ before it

            var few = new Intercom { Answer = (_, _, _) => (HttpStatusCode.OK, Contacts(1, 150, "next")) };
            Assert.Equal(160, (await Contexts.DrainAsync(new IntercomConnector(), Context(few, IntercomConnector.ContactsOperation, new() { ["max"] = "160" }))).Count);
            Assert.Equal(new[] { Api + "/contacts?per_page=150", Api + "/contacts?per_page=10&starting_after=next" }, few.Requests.Select(request => request.Url)); // a page no bigger than what is left

            var searched = new Intercom { Answer = (_, _, body) => (HttpStatusCode.OK, body!.Contains("starting_after", StringComparison.Ordinal) ? Contacts(3, 1, null) : Contacts(1, 2, "c2")) };
            Assert.Equal(3, (await Contexts.DrainAsync(new IntercomConnector(), Context(searched, IntercomConnector.ContactsOperation, new()
            {
                ["role"] = "Lead", ["email"] = "ada@example.com", ["query"] = "{\"field\": \"custom_attributes.plan\", \"operator\": \"=\", \"value\": \"Pro\"}",
            }))).Count);
            const string Query = "{\"query\":{\"operator\":\"AND\",\"value\":[{\"field\":\"role\",\"operator\":\"=\",\"value\":\"lead\"},{\"field\":\"email\",\"operator\":\"=\",\"value\":\"ada@example.com\"},"
                + "{\"field\":\"custom_attributes.plan\",\"operator\":\"=\",\"value\":\"Pro\"}]},";
            Assert.Equal(new[] { ("POST", Api + "/contacts/search", Query + "\"pagination\":{\"per_page\":150}}"), ("POST", Api + "/contacts/search", Query + "\"pagination\":{\"per_page\":150,\"starting_after\":\"c2\"}}") },
                searched.Requests.Select(request => (request.Method.Method, request.Url, request.Body!)));
            await Contexts.DrainAsync(new IntercomConnector(), Context(searched, IntercomConnector.ContactsOperation, new() { ["role"] = "user", ["max"] = "1" }));
            Assert.Equal("{\"query\":{\"field\":\"role\",\"operator\":\"=\",\"value\":\"user\"},\"pagination\":{\"per_page\":1}}", searched.Requests[^1].Body); // one condition alone
        }

        [Fact]
        public async Task A_contact_is_found_got_made_changed_and_deleted()
        {
            var intercom = new Intercom
            {
                Answer = (method, url, body) =>
                    url.EndsWith("/contacts/search", StringComparison.Ordinal) ? (HttpStatusCode.OK, body!.Contains("nobody", StringComparison.Ordinal) ? Contacts(1, 0, null) : Contacts(1, 1, null))
                    : method == HttpMethod.Delete ? (HttpStatusCode.OK, """{"id":"661","external_id":"ext-1","type":"contact","deleted":true}""")
                    : (HttpStatusCode.OK, Contact(1)),
            };
            (string, string, string?) Last() => (intercom.Requests[^1].Method.Method, intercom.Requests[^1].Url, intercom.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new IntercomConnector(), Context(intercom, operation, parameters));

            Assert.Equal("661", (string?)Assert.Single(await Run(IntercomConnector.GetContactOperation, new() { ["contact"] = "ada1@example.com" })).Fields["id"]);
            Assert.Equal(new[] { ("POST", Api + "/contacts/search", (string?)"{\"query\":{\"field\":\"email\",\"operator\":\"=\",\"value\":\"ada1@example.com\"},\"pagination\":{\"per_page\":1}}"), ("GET", Api + "/contacts/661", null) },
                intercom.Requests.Select(request => (request.Method.Method, request.Url, request.Body)));
            await Run(IntercomConnector.GetContactOperation, new() { ["contact"] = "external: ext-1" });
            Assert.Equal("{\"query\":{\"field\":\"external_id\",\"operator\":\"=\",\"value\":\"ext-1\"},\"pagination\":{\"per_page\":1}}", intercom.Requests[^2].Body);
            await Run(IntercomConnector.GetContactOperation, new() { ["contact"] = "5ba682d23d7cf92bef87bfd4" });
            Assert.Equal(("GET", Api + "/contacts/5ba682d23d7cf92bef87bfd4", (string?)null), Last()); // an id needs no lookup

            await Run(IntercomConnector.CreateContactOperation, new()
            {
                ["email"] = "ada@example.com", ["external_id"] = "ext-9", ["phone"] = "+46 8 123", ["name"] = "Café <b>Ada</b>", ["owner_id"] = "991", ["unsubscribed_from_emails"] = "TRUE",
                ["signed_up_at"] = "2026-10-01T11:00:00+02:00", ["last_seen_at"] = "2026-10-12", ["custom_attributes"] = "{\"plan\": \"Pro\", \"seats\": 5}",
            });
            Assert.Equal(("POST", Api + "/contacts", "{\"role\":\"user\",\"email\":\"ada@example.com\",\"external_id\":\"ext-9\",\"phone\":\"+46 8 123\",\"name\":\"Café <b>Ada</b>\",\"owner_id\":991,"
                + "\"unsubscribed_from_emails\":true,\"signed_up_at\":1790845200,\"last_seen_at\":1791763200,\"custom_attributes\":{\"plan\":\"Pro\",\"seats\":5}}"), Last()); // a user by default; times as Intercom keeps them
            await Run(IntercomConnector.CreateContactOperation, new() { ["role"] = "lead", ["name"] = "Visitor" });
            Assert.Equal("{\"role\":\"lead\",\"name\":\"Visitor\"}", Last().Item3); // a lead needs no address
            await Run(IntercomConnector.UpdateContactOperation, new() { ["contact"] = "661", ["phone"] = "+46 8 999", ["unsubscribed_from_emails"] = "false" });
            Assert.Equal(("PUT", Api + "/contacts/661", "{\"phone\":\"+46 8 999\",\"unsubscribed_from_emails\":false}"), Last());
            var gone = Assert.Single(await Run(IntercomConnector.DeleteContactOperation, new() { ["contact"] = "661" }));
            Assert.Equal(("DELETE", Api + "/contacts/661", (string?)null), Last());
            Assert.Equal(("661", true), ((string?)gone.Fields["id"], (bool?)gone.Fields["deleted"]));
            var kept = new Intercom { Answer = (_, _, _) => (HttpStatusCode.OK, """{"id":"661","type":"contact","deleted":false}""") };
            Assert.False((bool?)Assert.Single(await Contexts.DrainAsync(new IntercomConnector(), Context(kept, IntercomConnector.DeleteContactOperation, new() { ["contact"] = "661" }))).Fields["deleted"]); // as Intercom said
            Assert.Equal("No contact has the address nobody@example.com.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(IntercomConnector.DeleteContactOperation, new() { ["contact"] = "nobody@example.com" }))).Message);
            Assert.NotEqual(HttpMethod.Delete, intercom.Requests[^1].Method); // nothing deleted
        }

        [Fact]
        public async Task Companies_their_contacts_tags_and_notes_are_kept()
        {
            const string Company = """{"type":"company","id":"c1","company_id":"acme-ab","name":"Acme AB","plan":{"type":"plan","id":"3","name":"Pro"},"size":120,"website":"https://acme.example","industry":"Retail","monthly_spend":499.5,"user_count":4,"created_at":1700000000,"custom_attributes":{"region":"EU","name":"x"}}""";
            var intercom = new Intercom
            {
                Answer = (method, url, body) =>
                    url.Contains("/companies?page=", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"type\":\"list\",\"data\":[" + string.Join(",", Enumerable.Repeat(Company, url.Contains("page=2", StringComparison.Ordinal) ? 10 : 60)) + "],\"pages\":{\"page\":1,\"per_page\":60,\"total_pages\":2}}")
                    : url.Contains("/contacts?page=", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"type\":\"list\",\"data\":[" + Contact(1) + "," + Contact(2) + "],\"pages\":{\"page\":1,\"per_page\":60,\"total_pages\":1}}")
                    : url.EndsWith("/tags", StringComparison.Ordinal) && method == HttpMethod.Get ? (HttpStatusCode.OK, """{"type":"list","data":[{"type":"tag","id":"17","name":"VIP"},{"type":"tag","id":"18","name":"EU"}]}""")
                    : url.EndsWith("/tags", StringComparison.Ordinal) || url.Contains("/tags/", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"type":"tag","id":"17","name":"VIP"}""")
                    : url.EndsWith("/notes", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"type":"note","id":"n1","created_at":1700000000,"body":"<p>Café</p>"}""")
                    : url.Contains("/contacts/661/companies", StringComparison.Ordinal) ? (HttpStatusCode.OK, Company)
                    : method == HttpMethod.Delete ? (HttpStatusCode.OK, """{"id":"c1","object":"company","deleted":true}""")
                    : (HttpStatusCode.OK, Company),
            };
            (string, string, string?) Last() => (intercom.Requests[^1].Method.Method, intercom.Requests[^1].Url, intercom.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new IntercomConnector(), Context(intercom, operation, parameters));

            var companies = await Run(IntercomConnector.CompaniesOperation, new());
            Assert.Equal(70, companies.Count); // two pages of Intercom's numbering
            Assert.Equal(new[] { Api + "/companies?page=1&per_page=60", Api + "/companies?page=2&per_page=60" }, intercom.Requests.Select(request => request.Url));
            var acme = companies[0].Fields;
            Assert.Equal(("c1", "acme-ab", "Acme AB", "Pro", 120L, 499.5, 4L, "2023-11-14T22:13:20Z", "EU", "x"),
                ((string?)acme["id"], (string?)acme["company_id"], (string?)acme["name"], (string?)acme["plan"], (long?)acme["size"], (double?)acme["monthly_spend"], (long?)acme["user_count"], (string?)acme["created_at"], (string?)acme["region"], (string?)acme["custom_name"]));

            await Run(IntercomConnector.GetCompanyOperation, new() { ["company"] = "company:acme-ab" });
            Assert.Equal(("GET", Api + "/companies?company_id=acme-ab", (string?)null), Last());
            await Run(IntercomConnector.GetCompanyOperation, new() { ["company"] = "name: Acme & Co" });
            Assert.Equal(("GET", Api + "/companies?name=Acme%20%26%20Co", (string?)null), Last());
            await Run(IntercomConnector.GetCompanyOperation, new() { ["company"] = "c1" });
            Assert.Equal(("GET", Api + "/companies/c1", (string?)null), Last());
            await Run(IntercomConnector.UpsertCompanyOperation, new()
            {
                ["company_id"] = "acme-ab", ["name"] = "Café AB", ["plan"] = "Pro", ["industry"] = "Retail", ["website"] = "https://acme.example", ["size"] = "120", ["monthly_spend"] = "499.50", ["custom_attributes"] = "{\"region\": \"EU\"}",
            });
            Assert.Equal(("POST", Api + "/companies", "{\"company_id\":\"acme-ab\",\"name\":\"Café AB\",\"plan\":\"Pro\",\"industry\":\"Retail\",\"website\":\"https://acme.example\",\"size\":120,\"monthly_spend\":499.5,\"custom_attributes\":{\"region\":\"EU\"}}"), Last());
            intercom.Requests.Clear();
            Assert.True((bool?)Assert.Single(await Run(IntercomConnector.DeleteCompanyOperation, new() { ["company"] = "company:acme-ab" })).Fields["deleted"]);
            Assert.Equal(new[] { "GET " + Api + "/companies?company_id=acme-ab", "DELETE " + Api + "/companies/c1" }, intercom.Requests.Select(request => request.Method.Method + " " + request.Url)); // found by its own id, deleted by Intercom's

            var people = await Run(IntercomConnector.CompanyContactsOperation, new() { ["company"] = "c1" });
            Assert.Equal(("GET", Api + "/companies/c1/contacts?page=1&per_page=60", (string?)null), Last());
            Assert.Equal(new[] { "661", "662" }, people.Select(person => (string?)person.Fields["id"]));
            var attached = Assert.Single(await Run(IntercomConnector.AttachCompanyOperation, new() { ["contact"] = "661", ["company"] = "c1" }));
            Assert.Equal(("POST", Api + "/contacts/661/companies", "{\"id\":\"c1\"}"), Last());
            Assert.Equal(("661", "c1", true), ((string?)attached.Fields["contact"], (string?)attached.Fields["company"], (bool?)attached.Fields["attached"]));
            await Run(IntercomConnector.DetachCompanyOperation, new() { ["contact"] = "661", ["company"] = "c1" });
            Assert.Equal(("DELETE", Api + "/contacts/661/companies/c1", (string?)null), Last());

            Assert.Equal(new[] { ("17", "VIP"), ("18", "EU") }, (await Run(IntercomConnector.TagsOperation, new())).Select(tag => ((string)tag.Fields["id"]!, (string)tag.Fields["name"]!)));
            intercom.Requests.Clear();
            var tagged = Assert.Single(await Run(IntercomConnector.TagContactOperation, new() { ["contact"] = "661", ["tag"] = "vip" }));
            Assert.Equal(new[] { ("GET", Api + "/tags", (string?)null), ("POST", Api + "/contacts/661/tags", "{\"id\":\"17\"}") }, intercom.Requests.Select(request => (request.Method.Method, request.Url, request.Body)));
            Assert.Equal(("VIP", true), ((string?)tagged.Fields["tag"], (bool?)tagged.Fields["tagged"]));
            await Run(IntercomConnector.UntagContactOperation, new() { ["contact"] = "661", ["tag"] = "18" });
            Assert.Equal(("DELETE", Api + "/contacts/661/tags/18", (string?)null), Last()); // an id needs no lookup
            Assert.Equal("No tag is named 'Gold'.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(IntercomConnector.TagContactOperation, new() { ["contact"] = "661", ["tag"] = "Gold" }))).Message);

            var note = Assert.Single(await Run(IntercomConnector.AddNoteOperation, new() { ["contact"] = "661", ["body"] = "<p>Café</p>", ["admin_id"] = "991" }));
            Assert.Equal(("POST", Api + "/contacts/661/notes", "{\"body\":\"<p>Café</p>\",\"admin_id\":\"991\"}"), Last());
            Assert.Equal(("n1", "2023-11-14T22:13:20Z", "<p>Café</p>"), ((string?)note.Fields["id"], (string?)note.Fields["created"], (string?)note.Fields["body"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_intercoms_errors_are_its_words()
        {
            var intercom = new Intercom();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new IntercomConnector(), Context(intercom, operation, parameters)))).Message;

            Assert.Equal("Give the contact - its id, its e-mail address or external: and its external id.", await Refused(IntercomConnector.GetContactOperation, new()));
            Assert.Equal("'ada@x@y' is not a contact - its id, its e-mail address or external: and its external id.", await Refused(IntercomConnector.GetContactOperation, new() { ["contact"] = "ada@x@y" }));
            Assert.Equal("'external:' is not a contact - its id, its e-mail address or external: and its external id.", await Refused(IntercomConnector.DeleteContactOperation, new() { ["contact"] = "external:" }));
            Assert.Equal("'a b' is not a contact - its id, its e-mail address or external: and its external id.", await Refused(IntercomConnector.AddNoteOperation, new() { ["contact"] = "a b", ["body"] = "x" }));
            Assert.Equal("Give a user's e-mail address or external id.", await Refused(IntercomConnector.CreateContactOperation, new() { ["name"] = "Ada" }));
            Assert.Equal("'admin' is not a role - user or lead.", await Refused(IntercomConnector.CreateContactOperation, new() { ["role"] = "admin", ["email"] = "ada@example.com" }));
            Assert.Equal("'admin' is not a role - user or lead.", await Refused(IntercomConnector.ContactsOperation, new() { ["role"] = "admin" }));
            Assert.Equal("'ada' is not an e-mail address.", await Refused(IntercomConnector.ContactsOperation, new() { ["email"] = "ada" }));
            Assert.Equal("'ada' is not an e-mail address.", await Refused(IntercomConnector.UpdateContactOperation, new() { ["contact"] = "661", ["email"] = "ada" }));
            Assert.Equal("The query is not a JSON object - {\"field\": \"custom_attributes.plan\", \"operator\": \"=\", \"value\": \"Pro\"}.", await Refused(IntercomConnector.ContactsOperation, new() { ["query"] = "plan = Pro" }));
            Assert.Equal("'x' is not an admin's id.", await Refused(IntercomConnector.CreateContactOperation, new() { ["email"] = "ada@example.com", ["owner_id"] = "x" }));
            Assert.Equal("'yes' is not true or false (unsubscribed).", await Refused(IntercomConnector.UpdateContactOperation, new() { ["contact"] = "661", ["unsubscribed_from_emails"] = "yes" }));
            Assert.Equal("'soon' is not a time (signed up) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(IntercomConnector.UpdateContactOperation, new() { ["contact"] = "661", ["signed_up_at"] = "soon" }));
            Assert.Equal("'later' is not a time (last seen) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(IntercomConnector.UpdateContactOperation, new() { ["contact"] = "661", ["last_seen_at"] = "later" }));
            Assert.Equal("The custom attributes are not a JSON object - {\"plan\": \"Pro\"}.", await Refused(IntercomConnector.UpdateContactOperation, new() { ["contact"] = "661", ["custom_attributes"] = "plan=Pro" }));
            Assert.Equal("The name has a line break - one line only.", await Refused(IntercomConnector.UpdateContactOperation, new() { ["contact"] = "661", ["name"] = "Ada\nB" }));
            Assert.Equal("Give something to change - an address, an external id, a phone, a name, an owner, whether unsubscribed, when signed up or last seen, or custom attributes.", await Refused(IntercomConnector.UpdateContactOperation, new() { ["contact"] = "661" }));
            Assert.Equal("Give the company's own id - its company id.", await Refused(IntercomConnector.UpsertCompanyOperation, new() { ["name"] = "Acme" }));
            Assert.Equal("'acme.example' is not a website's address - https://acme.example.", await Refused(IntercomConnector.UpsertCompanyOperation, new() { ["company_id"] = "a", ["website"] = "acme.example" }));
            Assert.Equal("'ftp://acme.example' is not a website's address - https://acme.example.", await Refused(IntercomConnector.UpsertCompanyOperation, new() { ["company_id"] = "a", ["website"] = "ftp://acme.example" }));
            Assert.Equal("'big' is not a number of people (size).", await Refused(IntercomConnector.UpsertCompanyOperation, new() { ["company_id"] = "a", ["size"] = "big" }));
            Assert.Equal("'-5' is not an amount (monthly spend) - 499.50.", await Refused(IntercomConnector.UpsertCompanyOperation, new() { ["company_id"] = "a", ["monthly_spend"] = "-5" }));
            Assert.Equal("The custom attributes are not a JSON object - {\"region\": \"EU\"}.", await Refused(IntercomConnector.UpsertCompanyOperation, new() { ["company_id"] = "a", ["custom_attributes"] = "[1]" }));
            Assert.Equal("Give the company - its Intercom id or company: and its own company id.", await Refused(IntercomConnector.CompanyContactsOperation, new()));
            Assert.Equal("'name:Acme' is not a company - its Intercom id or company: and its own company id.", await Refused(IntercomConnector.DeleteCompanyOperation, new() { ["company"] = "name:Acme" })); // a name only to get one
            Assert.Equal("'acme ab' is not a company - its Intercom id, company: and its own company id, or name: and its name.", await Refused(IntercomConnector.GetCompanyOperation, new() { ["company"] = "acme ab" }));
            Assert.Equal("Give the tag - its id or its name.", await Refused(IntercomConnector.TagContactOperation, new() { ["contact"] = "661" }));
            Assert.Equal("Give the note.", await Refused(IntercomConnector.AddNoteOperation, new() { ["contact"] = "661" }));
            Assert.Equal("'x' is not an admin's id.", await Refused(IntercomConnector.AddNoteOperation, new() { ["contact"] = "661", ["body"] = "y", ["admin_id"] = "x" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(IntercomConnector.ContactsOperation, new() { ["max"] = "0" }));
            Assert.StartsWith("Intercom has no operation 'conversations' - contacts, get_contact,", await Refused("conversations", new()));
            Assert.Empty(intercom.Requests); // nothing sent

            var missing = new Intercom { Answer = (_, _, _) => (HttpStatusCode.NotFound, """{"type":"error.list","request_id":"r1","errors":[{"code":"not_found","message":"User Not Found"},{"code":"parameter_invalid"}]}""") };
            Assert.Equal("HTTP 404 not_found: User Not Found; parameter_invalid", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new IntercomConnector(), Context(missing, IntercomConnector.GetContactOperation, new() { ["contact"] = "661" })))).Message);
            var silent = new Intercom { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "") };
            Assert.Equal("HTTP 401.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new IntercomConnector(), Context(silent, IntercomConnector.TagsOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_admin_and_the_workspace()
        {
            var intercom = new Intercom { Answer = (_, _, _) => (HttpStatusCode.OK, """{"type":"admin","id":"991","name":"Ada Lovelace","email":"ada@example.com","app":{"type":"app","id_code":"abc","name":"Acme Support"}}""") };
            var tested = await new IntercomConnector().TestConnectionAsync(Contexts.Test(new StubEgress(intercom), Api, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((true, "Signed in as Ada Lovelace - Acme Support."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/me", "Bearer dG9rOjEyMw==", "2.11"), (intercom.Requests[0].Url, intercom.Requests[0].Authorization, intercom.Requests[0].Version));
            var keyless = await new IntercomConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Intercom()), Api, settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>()));
            Assert.Equal((false, "The Intercom connection has no access token."), (keyless.Success, keyless.Message));
            Assert.Equal((AuthenticationType.Bearer, "intercom"), (new IntercomConnector().AuthType, new IntercomConnector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Crm }, new IntercomConnector().Categories);
            Assert.Equal(16, new IntercomConnector().Operations.Count);
        }
    }
}
