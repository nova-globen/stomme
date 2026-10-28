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
using Stomme.Modules.Connectors.GoogleContacts;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Google Contacts (n8n's Google Contacts node: a contact created, got, read, updated and deleted): the refresh token
    /// exchanged at the token endpoint, the access token sent as a bearer; the contacts page by page by Google's token up to the
    /// most asked for, in an order, or those a search finds (warmed first, at most 30); the fields read held to Google's; each a
    /// plain record, its e-mail addresses and phone numbers typed; a contact made with names, typed addresses and numbers, a
    /// company, notes as written, a birthday with or without a year, an address, groups and custom fields; changed - its etag read
    /// first, a name part merged into its name, only what is given named as changed; deleted; the groups; what could go wrong
    /// refused before anything is sent; Google's refusal its status word and message; a test naming the groups.
    /// </summary>
    public sealed class GoogleContactsConnectorTests
    {
        private const string Api = "https://people.example.test/v1";

        private const string Fields = "names%2CemailAddresses%2CphoneNumbers%2Corganizations%2Cbiographies%2Cbirthdays%2Caddresses%2Cmemberships%2Cmetadata";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> People { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.StartsWith("https://oauth2.example.test/token", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""")
                    : People(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> PeopleRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://oauth2.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static ConnectorExtractContext Context(Google google, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(google), Api, operation, parameters,
                settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials);

        private static GoogleContactsConnector Connector() => new(new OAuth2TokenProvider());

        private static string PersonJson(int n) =>
            "{\"resourceName\":\"people/c" + n + "\",\"etag\":\"%EgUBAj\",\"names\":[{\"displayName\":\"Ada Lovelace " + n + "\",\"givenName\":\"Ada\",\"familyName\":\"Lovelace " + n + "\"}],"
            + "\"emailAddresses\":[{\"value\":\"ada" + n + "@example.com\",\"type\":\"work\"},{\"value\":\"ada@example.org\"}],\"phoneNumbers\":[{\"value\":\"+46 70 123\",\"type\":\"mobile\"}],"
            + "\"organizations\":[{\"name\":\"Analytical & Co\",\"title\":\"Countess\"}],\"biographies\":[{\"value\":\"Café <b>notes</b>\",\"contentType\":\"TEXT_PLAIN\"}],"
            + "\"birthdays\":[{\"date\":{\"year\":1815,\"month\":12,\"day\":10}}],\"addresses\":[{\"formattedValue\":\"12 Byron St, London\"},{\"streetAddress\":\"1 Main St\",\"city\":\"Leeds\"}],"
            + "\"memberships\":[{\"contactGroupMembership\":{\"contactGroupResourceName\":\"contactGroups/myContacts\"}}],\"metadata\":{\"sources\":[{\"updateTime\":\"2026-10-01T09:00:00Z\"}]}}";

        private static string Page(int from, int count, string? next) =>
            "{\"connections\":[" + string.Join(",", Enumerable.Range(from, count).Select(PersonJson)) + "]" + (next is null ? "" : ",\"nextPageToken\":\"" + next + "\"") + "}";

        [Fact]
        public async Task The_contacts_are_read_page_by_page_up_to_the_most_asked_for_by_the_exchanged_token()
        {
            var google = new Google { People = (_, url) => (HttpStatusCode.OK, url.Contains("pageToken=p%2B2", StringComparison.Ordinal) ? Page(101, 100, null) : Page(1, 100, "p+2")) };
            var records = await Contexts.DrainAsync(Connector(), Context(google, GoogleContactsConnector.ContactsOperation, new() { ["sort_order"] = "last_modified_descending", ["max"] = "150" }));

            Assert.Equal(150, records.Count);
            Assert.Equal("https://oauth2.example.test/token", google.Requests[0].Url);
            Assert.Contains("refresh_token=refresh-1", google.Requests[0].Body);
            Assert.All(google.PeopleRequests, request => Assert.Equal("Bearer access-1", request.Authorization));
            Assert.Equal(new[]
            {
                Api + "/people/me/connections?personFields=" + Fields + "&pageSize=100&sortOrder=LAST_MODIFIED_DESCENDING",
                Api + "/people/me/connections?personFields=" + Fields + "&pageSize=50&sortOrder=LAST_MODIFIED_DESCENDING&pageToken=p%2B2",
            }, google.PeopleRequests.Select(request => request.Url));
            var ada = records[0].Fields;
            Assert.Equal(("c1", "people/c1", "Ada Lovelace 1", "Ada", "Lovelace 1"), ((string?)ada["id"], (string?)ada["resource_name"], (string?)ada["name"], (string?)ada["given_name"], (string?)ada["family_name"]));
            Assert.Equal(("work: ada1@example.com, ada@example.org", "mobile: +46 70 123", "Analytical & Co", "Countess", "Café <b>notes</b>", "1815-12-10"),
                ((string?)ada["emails"], (string?)ada["phones"], (string?)ada["company"], (string?)ada["job_title"], (string?)ada["notes"], (string?)ada["birthday"]));
            Assert.Equal(("12 Byron St, London; 1 Main St, Leeds", "contactGroups/myContacts", "2026-10-01T09:00:00Z"), ((string?)ada["addresses"], (string?)ada["groups"], (string?)ada["updated"]));

            var all = new Google { People = (_, _) => (HttpStatusCode.OK, Page(1, 100, "more")) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(all, GoogleContactsConnector.ContactsOperation, new() { ["fields"] = "names, emailAddresses" }))).Count); // by default up to 1,000
            Assert.Equal(10, all.PeopleRequests.Count);
            Assert.StartsWith(Api + "/people/me/connections?personFields=names%2CemailAddresses&pageSize=100", all.PeopleRequests[0].Url);

            var searching = new Google { People = (_, url) => (HttpStatusCode.OK, url.Contains("query=&", StringComparison.Ordinal) ? "{}" : "{\"results\":[{\"person\":" + PersonJson(7) + "},{\"person\":" + PersonJson(8) + "}]}") };
            var found = await Contexts.DrainAsync(Connector(), Context(searching, GoogleContactsConnector.ContactsOperation, new() { ["query"] = "Ada & co", ["fields"] = "*", ["max"] = "1" }));
            Assert.Equal("c7", (string?)Assert.Single(found).Fields["id"]); // the most asked for
            Assert.Equal(Api + "/people:searchContacts?query=&readMask=names", searching.PeopleRequests[0].Url); // warmed first
            Assert.StartsWith(Api + "/people:searchContacts?query=Ada%20%26%20co&readMask=addresses%2CageRanges%2C", searching.PeopleRequests[1].Url);
            Assert.EndsWith("%2CuserDefined&pageSize=1", searching.PeopleRequests[1].Url);
        }

        [Fact]
        public async Task A_contact_is_made_got_changed_with_its_etag_and_deleted_and_the_groups_read()
        {
            var google = new Google
            {
                People = (method, url) => (method == HttpMethod.Delete ? HttpStatusCode.OK : HttpStatusCode.OK, method == HttpMethod.Delete ? "{}"
                    : url.Contains("contactGroups", StringComparison.Ordinal) ? """{"contactGroups":[{"resourceName":"contactGroups/myContacts","formattedName":"My Contacts","groupType":"SYSTEM_CONTACT_GROUP","memberCount":42},{"resourceName":"contactGroups/abc","name":"Clients","groupType":"USER_CONTACT_GROUP"}]}"""
                    : PersonJson(7)),
            };
            (string, string, string?) Last() => (google.PeopleRequests[^1].Method.Method, google.PeopleRequests[^1].Url, google.PeopleRequests[^1].Body);

            var made = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleContactsConnector.CreateContactOperation, new()
            {
                ["given_name"] = "Ada", ["family_name"] = "Lovelace", ["prefix"] = "Countess", ["emails"] = "work: ada@example.com, ada@example.org", ["phones"] = "mobile: +46 70 174 06 05, 0123",
                ["company"] = "Analytical & Co", ["job_title"] = "Programmer", ["notes"] = "Café <b>notes</b>\nline two", ["birthday"] = "--12-10",
                ["address"] = "12 Byron St", ["city"] = "London", ["country_code"] = "GB", ["address_type"] = "WORK", ["groups"] = "abc, contactGroups/def", ["custom_fields"] = "{\"Customer id\": 1001, \"Tier\": \"Gold\"}",
            })));
            Assert.Equal(("POST", Api + "/people:createContact?personFields=" + Fields,
                "{\"names\":[{\"givenName\":\"Ada\",\"familyName\":\"Lovelace\",\"honorificPrefix\":\"Countess\"}],"
                + "\"emailAddresses\":[{\"value\":\"ada@example.com\",\"type\":\"work\"},{\"value\":\"ada@example.org\"}],\"phoneNumbers\":[{\"value\":\"+46 70 174 06 05\",\"type\":\"mobile\"},{\"value\":\"0123\"}],"
                + "\"organizations\":[{\"name\":\"Analytical & Co\",\"title\":\"Programmer\"}],\"biographies\":[{\"value\":\"Café <b>notes</b>\\nline two\",\"contentType\":\"TEXT_PLAIN\"}],"
                + "\"birthdays\":[{\"date\":{\"month\":12,\"day\":10}}],\"addresses\":[{\"streetAddress\":\"12 Byron St\",\"city\":\"London\",\"countryCode\":\"GB\",\"type\":\"work\"}],"
                + "\"memberships\":[{\"contactGroupMembership\":{\"contactGroupResourceName\":\"contactGroups/abc\"}},{\"contactGroupMembership\":{\"contactGroupResourceName\":\"contactGroups/def\"}}],"
                + "\"userDefined\":[{\"key\":\"Customer id\",\"value\":\"1001\"},{\"key\":\"Tier\",\"value\":\"Gold\"}]}"), Last());
            Assert.Equal("c7", (string?)made.Fields["id"]);
            await Contexts.DrainAsync(Connector(), Context(google, GoogleContactsConnector.CreateContactOperation, new() { ["family_name"] = "Hopper", ["birthday"] = "1906-12-09", ["company"] = "Navy" }));
            Assert.Equal("{\"names\":[{\"familyName\":\"Hopper\"}],\"organizations\":[{\"name\":\"Navy\"}],\"birthdays\":[{\"date\":{\"year\":1906,\"month\":12,\"day\":9}}]}", Last().Item3);
            await Contexts.DrainAsync(Connector(), Context(google, GoogleContactsConnector.CreateContactOperation, new() { ["family_name"] = "Hopper", ["city"] = "Arlington" }));
            Assert.Equal("{\"names\":[{\"familyName\":\"Hopper\"}],\"addresses\":[{\"city\":\"Arlington\",\"type\":\"home\"}]}", Last().Item3); // home by default

            await Contexts.DrainAsync(Connector(), Context(google, GoogleContactsConnector.GetContactOperation, new() { ["contact"] = "people/c7", ["fields"] = "names" }));
            var yearless = new Google { People = (_, _) => (HttpStatusCode.OK, "{\"resourceName\":\"people/c9\",\"birthdays\":[{\"text\":\"Dec 10\"},{\"date\":{\"month\":12,\"day\":10}}]}") };
            var noYear = Assert.Single(await Contexts.DrainAsync(Connector(), Context(yearless, GoogleContactsConnector.GetContactOperation, new() { ["contact"] = "c9" })));
            Assert.Equal(("--12-10", ""), ((string?)noYear.Fields["birthday"], (string?)noYear.Fields["emails"])); // a birthday with no year; the first with a date
            Assert.Equal(("GET", Api + "/people/c7?personFields=names", (string?)null), Last());

            var updating = new Google { People = google.People };
            await Contexts.DrainAsync(Connector(), Context(updating, GoogleContactsConnector.UpdateContactOperation, new() { ["contact"] = "c7", ["given_name"] = "Augusta", ["phones"] = "+46 8 555" }));
            Assert.Equal(("GET", Api + "/people/c7?personFields=names"), (updating.PeopleRequests[0].Method.Method, updating.PeopleRequests[0].Url)); // its etag and name first
            Assert.Equal(("PATCH", Api + "/people/c7:updateContact?updatePersonFields=names,phoneNumbers&personFields=" + Fields,
                "{\"names\":[{\"givenName\":\"Augusta\",\"familyName\":\"Lovelace 7\"}],\"phoneNumbers\":[{\"value\":\"+46 8 555\"}],\"etag\":\"%EgUBAj\"}"),
                (updating.PeopleRequests[1].Method.Method, updating.PeopleRequests[1].Url, updating.PeopleRequests[1].Body)); // the family name it had kept
            var noting = new Google { People = google.People };
            await Contexts.DrainAsync(Connector(), Context(noting, GoogleContactsConnector.UpdateContactOperation, new() { ["contact"] = "c7", ["notes"] = "Met at the exhibition" }));
            Assert.Equal(Api + "/people/c7:updateContact?updatePersonFields=biographies&personFields=" + Fields, noting.PeopleRequests[1].Url); // no name touched

            var gone = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleContactsConnector.DeleteContactOperation, new() { ["contact"] = "c7" })));
            Assert.Equal(("DELETE", Api + "/people/c7:deleteContact", (string?)null), Last());
            Assert.Equal(("c7", true), ((string?)gone.Fields["id"], (bool?)gone.Fields["deleted"]));

            var groups = await Contexts.DrainAsync(Connector(), Context(google, GoogleContactsConnector.GroupsOperation, new()));
            Assert.Equal(("GET", Api + "/contactGroups?pageSize=1000", (string?)null), Last());
            Assert.Equal(new[] { ("contactGroups/myContacts", "My Contacts", 42L), ("contactGroups/abc", "Clients", 0L) }, groups.Select(group => ((string)group.Fields["resource_name"]!, (string)group.Fields["name"]!, (long)group.Fields["members"]!)));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_googles_refusal_is_its_words()
        {
            var google = new Google();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, operation, parameters)))).Message;

            Assert.Equal("'ada' is not a contact's id - c123... or people/c123....", await Refused(GoogleContactsConnector.GetContactOperation, new() { ["contact"] = "ada" }));
            Assert.Equal("'people/c1/../x' is not a contact's id - c123... or people/c123....", await Refused(GoogleContactsConnector.DeleteContactOperation, new() { ["contact"] = "people/c1/../x" }));
            Assert.Equal("'' is not a contact's id - c123... or people/c123....", await Refused(GoogleContactsConnector.UpdateContactOperation, new() { ["notes"] = "x" }));
            Assert.Equal("'emails' is not one of Google's person fields - names, emailAddresses, phoneNumbers... or *.", await Refused(GoogleContactsConnector.ContactsOperation, new() { ["fields"] = "names, emails" }));
            Assert.Equal("'newest' is not an order - LAST_MODIFIED_ASCENDING, LAST_MODIFIED_DESCENDING, FIRST_NAME_ASCENDING or LAST_NAME_ASCENDING.", await Refused(GoogleContactsConnector.ContactsOperation, new() { ["sort_order"] = "newest" }));
            Assert.Equal("A search has no order - give a search or an order, not both.", await Refused(GoogleContactsConnector.ContactsOperation, new() { ["query"] = "Ada", ["sort_order"] = "FIRST_NAME_ASCENDING" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(GoogleContactsConnector.ContactsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the contact a name - a given name or a family name.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["emails"] = "ada@example.com" }));
            Assert.Equal("The given name has a line break - one line only.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["given_name"] = "Ada\nB" }));
            Assert.Equal("'ada at example.com' is not an e-mail address.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["given_name"] = "Ada", ["emails"] = "work: ada at example.com" }));
            Assert.Equal("'call me' is not a phone number.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["given_name"] = "Ada", ["phones"] = "call me" }));
            Assert.Equal("'10/12/1815' is not a birthday - 1815-12-10, or --12-10 with no year.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["given_name"] = "Ada", ["birthday"] = "10/12/1815" }));
            Assert.Equal("'1815-13-10' is not a birthday - 1815-12-10, or --12-10 with no year.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["given_name"] = "Ada", ["birthday"] = "1815-13-10" }));
            Assert.Equal("'office' is not an address type - home, work or other.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["given_name"] = "Ada", ["city"] = "London", ["address_type"] = "office" }));
            Assert.Equal("'contactGroups/a/b' is not a contact group - contactGroups/... or its id.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["given_name"] = "Ada", ["groups"] = "contactGroups/a/b" }));
            Assert.Equal("The custom fields are not a JSON object - {\"Customer id\": \"1001\"}.", await Refused(GoogleContactsConnector.CreateContactOperation, new() { ["given_name"] = "Ada", ["custom_fields"] = "Tier=Gold" }));
            Assert.Equal("Give something to change - a name, e-mail addresses, phone numbers, a company, notes, a birthday, an address, groups or custom fields.", await Refused(GoogleContactsConnector.UpdateContactOperation, new() { ["contact"] = "c7" }));
            Assert.StartsWith("Google Contacts has no operation 'merge' - contacts, get_contact,", await Refused("merge", new()));
            Assert.Empty(google.Requests); // not even a token asked for

            var refusing = new Google { People = (_, _) => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND"}}""") };
            Assert.Equal("HTTP 404 NOT_FOUND: Requested entity was not found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, GoogleContactsConnector.GetContactOperation, new() { ["contact"] = "c7" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_groups()
        {
            var google = new Google { People = (_, _) => (HttpStatusCode.OK, """{"contactGroups":[{"resourceName":"contactGroups/myContacts","formattedName":"My Contacts"},{"resourceName":"contactGroups/abc","name":"Clients"}]}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(google), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials));
            Assert.Equal((true, "Signed in - 2 contact groups, My Contacts first."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/contactGroups?pageSize=100", "Bearer access-1"), (google.PeopleRequests[0].Url, google.PeopleRequests[0].Authorization));
            var none = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Google()), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials));
            Assert.Equal((true, "Signed in - no contact groups."), (none.Success, none.Message));
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "googlecontacts", ConnectorCategory.Productivity), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "contacts", "get_contact", "create_contact", "update_contact", "delete_contact", "groups" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
