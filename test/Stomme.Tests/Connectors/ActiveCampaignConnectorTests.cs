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
using Stomme.Modules.Connectors.ActiveCampaign;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// ActiveCampaign (n8n's ActiveCampaign node: its contacts, lists, tags, deals, accounts and e-commerce records): the
    /// key in ActiveCampaign's Api-Token header; contacts page by page up to the most asked for, filtered by a search, a list and a
    /// tag by their names, a status, times in UTC, an order and ActiveCampaign's own filters, the count it gives ending the pages;
    /// a contact found by its address, got with its lists, tags and custom fields by their personalization tags, made, synced,
    /// changed and deleted with custom fields by tag or id; lists, tags and a contact's tags; deals and their notes; accounts and
    /// their contacts; any other collection as records; what could go wrong refused before anything is sent; ActiveCampaign's
    /// refusal in its words; a test naming the key's user.
    /// </summary>
    public sealed class ActiveCampaignConnectorTests
    {
        private const string Endpoint = "https://acme.api-us1.example.test";

        private const string Api = Endpoint + "/api/3";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "ac-key-1" };

        private sealed class ActiveCampaign : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Key, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("Api-Token", out var key) ? key.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(ActiveCampaign account, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(account), Endpoint, operation, parameters, settings: new Dictionary<string, string>(), credentials: Credentials);

        private static string Contacts(int from, int count, int total) =>
            "{\"scoreValues\":[],\"contacts\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":\"" + n + "\",\"email\":\"ada" + n + "@example.com\",\"firstName\":\"Café <b>Ada</b>\",\"cdate\":\"2026-10-01T09:00:00-05:00\","
                + "\"deleted\":\"0\",\"scoreValues\":[],\"links\":{\"bounceLogs\":\"https://acme.api-us1.example.test/api/3/contacts/" + n + "/bounceLogs\"}}")) + "],\"meta\":{\"total\":\"" + total + "\"}}";

        private static (HttpStatusCode, string) Ok(string json) => (HttpStatusCode.OK, json);

        [Fact]
        public async Task Contacts_are_read_page_by_page_by_their_filters_up_to_the_most_asked_for()
        {
            var account = new ActiveCampaign
            {
                Answer = (_, url) => url.Contains("/lists?", StringComparison.Ordinal) ? Ok("""{"lists":[{"id":"7","name":"Newsletter archive"},{"id":"2","name":"newsletter"}]}""")
                    : url.Contains("/tags?", StringComparison.Ordinal) ? Ok("""{"tags":[{"id":"4","tag":"VIP customers"},{"id":"3","tag":"VIP"}]}""")
                    : Ok(url.Contains("offset=100", StringComparison.Ordinal) ? Contacts(101, 30, 130) : Contacts(1, 100, 130)),
            };
            var records = await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, ActiveCampaignConnector.ContactsOperation, new()
            {
                ["search"] = "Ada Lovelace", ["list"] = "Newsletter", ["tag"] = "vip", ["status"] = "Active", ["created_after"] = "2026-10-01T11:00:00+02:00", ["updated_before"] = "2026-10-12",
                ["order"] = "first_name DESC", ["query"] = "email_like=@example.com, filters[seriesid]=3", ["max"] = "500",
            }));

            Assert.Equal(130, records.Count); // the last page short: the end
            Assert.All(account.Requests, request => Assert.Equal("ac-key-1", request.Key));
            const string Filters = "search=Ada%20Lovelace&status=1&filters%5Bcreated_after%5D=2026-10-01T09%3A00%3A00Z&filters%5Bupdated_before%5D=2026-10-12T00%3A00%3A00Z&orders%5Bfirst_name%5D=DESC"
                + "&email_like=%40example.com&filters%5Bseriesid%5D=3&listid=2&tagid=3";
            Assert.Equal(new[]
            {
                Api + "/lists?filters%5Bname%5D=Newsletter&limit=100", // a list by its name, whatever its case
                Api + "/tags?search=vip&limit=100", // the tag of that name, not one holding it
                Api + "/contacts?" + Filters + "&limit=100&offset=0",
                Api + "/contacts?" + Filters + "&limit=100&offset=100",
            }, account.Requests.Select(request => request.Url));
            var ada = records[0].Fields;
            Assert.Equal(("1", "ada1@example.com", "Café <b>Ada</b>", "0", "[]"), ((string?)ada["id"], (string?)ada["email"], (string?)ada["firstName"], (string?)ada["deleted"], (string?)ada["scoreValues"]));
            Assert.False(ada.ContainsKey("links"));

            var counted = new ActiveCampaign { Answer = (_, _) => Ok(Contacts(1, 100, 200)) };
            Assert.Equal(200, (await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(counted, ActiveCampaignConnector.ContactsOperation, new()))).Count); // by default up to 1,000
            Assert.Equal(new[] { Api + "/contacts?limit=100&offset=0", Api + "/contacts?limit=100&offset=100" }, counted.Requests.Select(request => request.Url)); // its count the end

            var few = new ActiveCampaign { Answer = (_, _) => Ok(Contacts(1, 100, 500)) };
            Assert.Equal(130, (await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(few, ActiveCampaignConnector.ContactsOperation, new() { ["max"] = "130", ["order"] = "score", ["segment"] = "12", ["email"] = "ada@example.com" }))).Count);
            Assert.Equal(new[] { Api + "/contacts?email=ada%40example.com&segmentid=12&orders%5Bscore%5D=ASC&limit=100&offset=0", Api + "/contacts?email=ada%40example.com&segmentid=12&orders%5Bscore%5D=ASC&limit=30&offset=100" }, few.Requests.Select(request => request.Url)); // a page no bigger than what is left

            var lists = new ActiveCampaign { Answer = (_, _) => Ok("""{"lists":[{"id":"2","name":"Newsletter","stringid":"newsletter"}],"meta":{"total":"1"}}""") };
            var read = Assert.Single(await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(lists, ActiveCampaignConnector.ListsOperation, new() { ["search"] = "News" })));
            Assert.Equal(("2", "Newsletter"), ((string?)read.Fields["id"], (string?)read.Fields["name"]));
            Assert.Equal(Api + "/lists?filters%5Bname%5D=News&limit=100&offset=0", Assert.Single(lists.Requests).Url);
            var fields = new ActiveCampaign { Answer = (_, _) => Ok("""{"fields":[{"id":"1","title":"Company","perstag":"COMPANY","type":"text"}],"meta":{"total":"1"}}""") };
            Assert.Equal("COMPANY", (string?)Assert.Single(await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(fields, ActiveCampaignConnector.FieldsOperation, new()))).Fields["perstag"]);
            Assert.Equal(Api + "/fields?limit=100&offset=0", Assert.Single(fields.Requests).Url);
        }

        [Fact]
        public async Task A_contact_is_found_by_its_address_got_made_synced_changed_and_deleted()
        {
            var account = new ActiveCampaign
            {
                Answer = (method, url) =>
                    url.Contains("/contacts?email=", StringComparison.Ordinal) ? Ok(url.Contains("nobody", StringComparison.Ordinal) ? """{"contacts":[],"meta":{"total":"0"}}""" : """{"contacts":[{"id":"42","email":"ada@example.com"}],"meta":{"total":"1"}}""")
                    : url.StartsWith(Api + "/fields?", StringComparison.Ordinal) ? Ok("""{"fields":[{"id":"1","title":"Company","perstag":"COMPANY"},{"id":"5","title":"Tier","perstag":"TIER"}],"meta":{"total":"2"}}""")
                    : method == HttpMethod.Get ? Ok("""{"contactLists":[{"list":"2","status":"1"},{"list":"5","status":"2"},{"list":"6","status":"1"}],"contactTags":[{"tag":"3"},{"tag":"8"}],"fieldValues":[{"field":"1","value":"Café <b>Acme</b>"},{"field":"9","value":"x"}],"contact":{"id":"42","email":"ada@example.com","firstName":"Ada","links":{}}}""")
                    : method == HttpMethod.Delete ? Ok("{}")
                    : Ok("""{"fieldValues":[],"contact":{"id":"42","email":"ada@example.com","lastName":"Lovelace"}}"""),
            };
            (string, string, string?) Last() => (account.Requests[^1].Method.Method, account.Requests[^1].Url, account.Requests[^1].Body);

            var got = Assert.Single(await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, ActiveCampaignConnector.GetContactOperation, new() { ["contact"] = "ada@example.com" })));
            Assert.Equal(new[] { Api + "/contacts?email=ada%40example.com&limit=1", Api + "/contacts/42", Api + "/fields?limit=100&offset=0" }, account.Requests.Select(request => request.Url));
            Assert.Equal(("42", "Ada", "2, 6", "3, 8", "Café <b>Acme</b>", "x"), ((string?)got.Fields["id"], (string?)got.Fields["firstName"], (string?)got.Fields["lists"], (string?)got.Fields["tags"], (string?)got.Fields["COMPANY"], (string?)got.Fields["field_9"])); // subscribed lists; a field by its tag, or its id

            account.Requests.Clear();
            var made = Assert.Single(await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, ActiveCampaignConnector.CreateContactOperation, new()
            {
                ["email"] = "ada@example.com", ["first_name"] = "Ada", ["last_name"] = "Lovelace", ["phone"] = "+46 8 465 004 01", ["fields"] = "{\"%company%\": \"Café <b>Acme</b>\", \"7\": [\"Gold\", \"Silver\"], \"TIER\": 2}",
            })));
            Assert.Equal(("POST", Api + "/contacts", "{\"contact\":{\"email\":\"ada@example.com\",\"firstName\":\"Ada\",\"lastName\":\"Lovelace\",\"phone\":\"+46 8 465 004 01\","
                + "\"fieldValues\":[{\"field\":\"1\",\"value\":\"Café <b>Acme</b>\"},{\"field\":\"7\",\"value\":\"||Gold||Silver||\"},{\"field\":\"5\",\"value\":\"2\"}]}}"), Last());
            Assert.Equal(("42", "Lovelace"), ((string?)made.Fields["id"], (string?)made.Fields["lastName"]));
            Assert.Equal(Api + "/fields?limit=100&offset=0", account.Requests[0].Url); // the tags read once
            Assert.Equal(2, account.Requests.Count);

            await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, ActiveCampaignConnector.SyncContactOperation, new() { ["email"] = "ada@example.com", ["fields"] = "{\"7\": \"Gold\"}" }));
            Assert.Equal(("POST", Api + "/contact/sync", "{\"contact\":{\"email\":\"ada@example.com\",\"fieldValues\":[{\"field\":\"7\",\"value\":\"Gold\"}]}}"), Last()); // an id needs no lookup
            await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, ActiveCampaignConnector.UpdateContactOperation, new() { ["contact"] = "ada@example.com", ["last_name"] = "Byron" }));
            Assert.Equal(("PUT", Api + "/contacts/42", "{\"contact\":{\"lastName\":\"Byron\"}}"), Last());
            var gone = Assert.Single(await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, ActiveCampaignConnector.DeleteContactOperation, new() { ["contact"] = "42" })));
            Assert.Equal(("DELETE", Api + "/contacts/42", (string?)null), Last());
            Assert.True((bool?)gone.Fields["done"]);

            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, operation, parameters)))).Message;
            Assert.Equal("No contact has the address nobody@example.com.", await Refused(ActiveCampaignConnector.DeleteContactOperation, new() { ["contact"] = "nobody@example.com" }));
            Assert.Equal("No custom field has the personalization tag NOPE - its id, or one of COMPANY, TIER.", await Refused(ActiveCampaignConnector.CreateContactOperation, new() { ["email"] = "ada@example.com", ["fields"] = "{\"NOPE\": 1}" }));
            Assert.DoesNotContain(account.Requests, request => request.Url.EndsWith("/contacts", StringComparison.Ordinal) && request.Body!.Contains("NOPE", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Lists_tags_and_a_contacts_tags_are_kept()
        {
            var account = new ActiveCampaign
            {
                Answer = (method, url) =>
                    url.Contains("/lists?", StringComparison.Ordinal) ? Ok("""{"lists":[{"id":"2","name":"Newsletter"}]}""")
                    : url.Contains("/tags?", StringComparison.Ordinal) ? Ok("""{"tags":[{"id":"3","tag":"VIP"}]}""")
                    : url.EndsWith("/contactTags", StringComparison.Ordinal) && method == HttpMethod.Get ? Ok(url.Contains("/contacts/43/", StringComparison.Ordinal) ? """{"contactTags":[{"id":"70","tag":"8"}]}""" : """{"contactTags":[{"id":"70","tag":"8"},{"id":"77","tag":"3"}]}""")
                    : url.EndsWith("/contactTags", StringComparison.Ordinal) ? Ok("""{"contactTag":{"id":"77","contact":"42","tag":"3"}}""")
                    : url.EndsWith("/contactLists", StringComparison.Ordinal) ? Ok("""{"contacts":[],"contactList":{"id":"9","list":"2","contact":"42","status":"1"}}""")
                    : url.Contains("/tags", StringComparison.Ordinal) && method != HttpMethod.Delete ? Ok("""{"tag":{"id":"3","tag":"VIP","tagType":"contact"}}""")
                    : Ok(""),
            };
            (string, string, string?) Last() => (account.Requests[^1].Method.Method, account.Requests[^1].Url, account.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, operation, parameters));

            var listed = Assert.Single(await Run(ActiveCampaignConnector.AddToListOperation, new() { ["contact"] = "42", ["list"] = "Newsletter" }));
            Assert.Equal(("POST", Api + "/contactLists", "{\"contactList\":{\"list\":\"2\",\"contact\":\"42\",\"status\":1}}"), Last());
            Assert.Equal("1", (string?)listed.Fields["status"]);
            await Run(ActiveCampaignConnector.RemoveFromListOperation, new() { ["contact"] = "42", ["list"] = "2" });
            Assert.Equal(("POST", Api + "/contactLists", "{\"contactList\":{\"list\":\"2\",\"contact\":\"42\",\"status\":2}}"), Last()); // unsubscribed; an id needs no lookup
            Assert.Equal(Api + "/contactLists", account.Requests[^2].Url);

            await Run(ActiveCampaignConnector.CreateTagOperation, new() { ["name"] = "VIP", ["description"] = "Café <b>best</b>" });
            Assert.Equal(("POST", Api + "/tags", "{\"tag\":{\"tag\":\"VIP\",\"tagType\":\"contact\",\"description\":\"Café <b>best</b>\"}}"), Last()); // a contact tag by default
            await Run(ActiveCampaignConnector.CreateTagOperation, new() { ["name"] = "Welcome", ["type"] = "Template" });
            Assert.Equal("{\"tag\":{\"tag\":\"Welcome\",\"tagType\":\"template\"}}", Last().Item3);
            await Run(ActiveCampaignConnector.UpdateTagOperation, new() { ["tag"] = "VIP", ["name"] = "VIP customer" });
            Assert.Equal(("PUT", Api + "/tags/3", "{\"tag\":{\"tag\":\"VIP customer\"}}"), Last());
            Assert.Equal(Api + "/tags?search=VIP&limit=100", account.Requests[^2].Url);
            var tag = Assert.Single(await Run(ActiveCampaignConnector.GetTagOperation, new() { ["tag"] = "3" }));
            Assert.Equal(("GET", Api + "/tags/3", (string?)null), Last());
            Assert.Equal("contact", (string?)tag.Fields["tagType"]);
            await Run(ActiveCampaignConnector.DeleteTagOperation, new() { ["tag"] = "VIP" });
            Assert.Equal(("DELETE", Api + "/tags/3", (string?)null), Last());

            var tagged = Assert.Single(await Run(ActiveCampaignConnector.AddTagOperation, new() { ["contact"] = "42", ["tag"] = "VIP" }));
            Assert.Equal(("POST", Api + "/contactTags", "{\"contactTag\":{\"contact\":\"42\",\"tag\":\"3\"}}"), Last());
            Assert.Equal("77", (string?)tagged.Fields["id"]);
            account.Requests.Clear();
            Assert.True((bool?)Assert.Single(await Run(ActiveCampaignConnector.RemoveTagOperation, new() { ["contact"] = "42", ["tag"] = "VIP" })).Fields["removed"]);
            Assert.Equal(new[] { "GET " + Api + "/tags?search=VIP&limit=100", "GET " + Api + "/contacts/42/contactTags", "DELETE " + Api + "/contactTags/77" }, account.Requests.Select(request => request.Method.Method + " " + request.Url)); // its tag, not another
            account.Requests.Clear();
            Assert.False((bool?)Assert.Single(await Run(ActiveCampaignConnector.RemoveTagOperation, new() { ["contact"] = "43", ["tag"] = "3" })).Fields["removed"]); // it had no such tag
            Assert.DoesNotContain(account.Requests, request => request.Method == HttpMethod.Delete);
            await Run(ActiveCampaignConnector.RemoveTagOperation, new() { ["contact_tag"] = "77" });
            Assert.Equal(("DELETE", Api + "/contactTags/77", (string?)null), Last()); // n8n's contact tag id
            Assert.Single(account.Requests, request => request.Method == HttpMethod.Delete);

            var tags = new ActiveCampaign { Answer = (_, _) => Ok("""{"tags":[{"id":"3","tag":"VIP"},{"id":"4","tag":"VIP customers"}],"meta":{"total":"2"}}""") };
            Assert.Equal(2, (await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(tags, ActiveCampaignConnector.TagsOperation, new() { ["search"] = "VIP" }))).Count);
            Assert.Equal(Api + "/tags?search=VIP&limit=100&offset=0", Assert.Single(tags.Requests).Url);

            var none = new ActiveCampaign { Answer = (_, _) => Ok("""{"lists":[{"id":"7","name":"Newsletter archive"}]}""") };
            Assert.Equal("No list is named 'Newsletter'.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ActiveCampaignConnector(), Context(none, ActiveCampaignConnector.AddToListOperation, new() { ["contact"] = "42", ["list"] = "Newsletter" })))).Message);
            Assert.Single(none.Requests); // nothing written
        }

        [Fact]
        public async Task Deals_their_notes_accounts_and_other_records_are_kept()
        {
            var account = new ActiveCampaign
            {
                Answer = (method, url) =>
                    url.Contains("/contacts?email=", StringComparison.Ordinal) ? Ok("""{"contacts":[{"id":"42"}]}""")
                    : url.Contains("/notes", StringComparison.Ordinal) ? Ok("""{"note":{"id":"4","note":"Called."}}""")
                    : url.Contains("/deals", StringComparison.Ordinal) && method != HttpMethod.Delete ? Ok("""{"deal":{"id":"9","title":"Acme renewal","value":"45000","currency":"eur"}}""")
                    : url.Contains("/accountContacts", StringComparison.Ordinal) && method != HttpMethod.Delete ? Ok("""{"accountContact":{"id":"8","account":"5","contact":"42","jobTitle":"Buyer"}}""")
                    : url.Contains("/accounts", StringComparison.Ordinal) && method != HttpMethod.Delete ? Ok("""{"account":{"id":"5","name":"Acme AB","fields":[{"customFieldId":3,"fieldValue":"Retail"}]}}""")
                    : url.Contains("/ecomCustomers", StringComparison.Ordinal) && method != HttpMethod.Delete ? Ok("""{"ecomCustomer":{"id":"11","email":"ada@example.com"}}""")
                    : url.Contains("/orderProducts", StringComparison.Ordinal) ? Ok("""{"ecomOrderProducts":[{"id":"1","name":"Café"},{"id":"2","name":"Tea"}],"meta":{"total":"2"}}""")
                    : url.Contains("/ecomOrders?", StringComparison.Ordinal) ? Ok("""{"ecomOrders":[{"id":"12","totalPrice":"1999"}],"meta":{"total":"1"}}""")
                    : url.Contains("/ecomOrders/", StringComparison.Ordinal) && method != HttpMethod.Delete ? Ok("""{"ecomOrder":{"id":"12","orderNumber":"1001"}}""")
                    : Ok(""),
            };
            (string, string, string?) Last() => (account.Requests[^1].Method.Method, account.Requests[^1].Url, account.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, operation, parameters));

            var deal = Assert.Single(await Run(ActiveCampaignConnector.CreateDealOperation, new()
            {
                ["title"] = "Acme renewal", ["contact"] = "ada@example.com", ["value"] = "45000", ["currency"] = "EUR", ["pipeline"] = "1", ["stage"] = "2", ["owner"] = "1", ["description"] = "Café <b>deal</b>", ["percent"] = "50", ["status"] = "Won",
            }));
            Assert.Equal(("POST", Api + "/deals", "{\"deal\":{\"title\":\"Acme renewal\",\"value\":45000,\"currency\":\"eur\",\"group\":\"1\",\"stage\":\"2\",\"owner\":\"1\",\"description\":\"Café <b>deal</b>\",\"percent\":50,\"status\":1,\"contact\":\"42\"}}"), Last());
            Assert.Equal(("9", "45000"), ((string?)deal.Fields["id"], (string?)deal.Fields["value"]));
            await Run(ActiveCampaignConnector.CreateDealOperation, new() { ["title"] = "Small", ["contact"] = "42", ["value"] = "0" });
            Assert.Equal("{\"deal\":{\"title\":\"Small\",\"value\":0,\"currency\":\"usd\",\"contact\":\"42\"}}", Last().Item3); // dollars by default
            await Run(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["value"] = "50000", ["status"] = "lost" });
            Assert.Equal(("PUT", Api + "/deals/9", "{\"deal\":{\"value\":50000,\"status\":2}}"), Last());
            await Run(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["contact"] = "43" });
            Assert.Equal("{\"deal\":{\"contact\":\"43\"}}", Last().Item3);
            await Run(ActiveCampaignConnector.GetDealOperation, new() { ["deal"] = "9" });
            Assert.Equal(("GET", Api + "/deals/9", (string?)null), Last());
            Assert.True((bool?)Assert.Single(await Run(ActiveCampaignConnector.DeleteDealOperation, new() { ["deal"] = "9" })).Fields["done"]);
            Assert.Equal(("DELETE", Api + "/deals/9", (string?)null), Last());
            var note = Assert.Single(await Run(ActiveCampaignConnector.AddDealNoteOperation, new() { ["deal"] = "9", ["note"] = "Called.\nCafé <b>soon</b>" }));
            Assert.Equal(("POST", Api + "/deals/9/notes", "{\"note\":{\"note\":\"Called.\\nCafé <b>soon</b>\"}}"), Last());
            Assert.Equal("4", (string?)note.Fields["id"]);
            await Run(ActiveCampaignConnector.UpdateDealNoteOperation, new() { ["deal"] = "9", ["note_id"] = "4", ["note"] = "Signed." });
            Assert.Equal(("PUT", Api + "/deals/9/notes/4", "{\"note\":{\"note\":\"Signed.\"}}"), Last());
            var deals = new ActiveCampaign { Answer = (_, _) => Ok("""{"deals":[{"id":"9"}],"meta":{"total":"1"}}""") };
            await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(deals, ActiveCampaignConnector.DealsOperation, new() { ["search"] = "Acme", ["pipeline"] = "1", ["stage"] = "2", ["owner"] = "3", ["status"] = "open" }));
            Assert.Equal(Api + "/deals?filters%5Bsearch%5D=Acme&filters%5Bgroup%5D=1&filters%5Bstage%5D=2&filters%5Bowner%5D=3&filters%5Bstatus%5D=0&limit=100&offset=0", Assert.Single(deals.Requests).Url);

            var company = Assert.Single(await Run(ActiveCampaignConnector.CreateAccountOperation, new() { ["name"] = "Acme AB", ["url"] = "https://acme.example", ["fields"] = "{\"3\": \"Retail\", \"4\": [\"EU\", \"US\"]}" }));
            Assert.Equal(("POST", Api + "/accounts", "{\"account\":{\"name\":\"Acme AB\",\"accountUrl\":\"https://acme.example\",\"fields\":[{\"customFieldId\":3,\"fieldValue\":\"Retail\"},{\"customFieldId\":4,\"fieldValue\":\"||EU||US||\"}]}}"), Last());
            Assert.Equal(("5", "[{\"customFieldId\":3,\"fieldValue\":\"Retail\"}]"), ((string?)company.Fields["id"], (string?)company.Fields["fields"])); // what is not one value, as its JSON
            await Run(ActiveCampaignConnector.UpdateAccountOperation, new() { ["account"] = "5", ["name"] = "Acme Group" });
            Assert.Equal(("PUT", Api + "/accounts/5", "{\"account\":{\"name\":\"Acme Group\"}}"), Last());
            await Run(ActiveCampaignConnector.GetAccountOperation, new() { ["account"] = "5" });
            Assert.Equal(("GET", Api + "/accounts/5", (string?)null), Last());
            await Run(ActiveCampaignConnector.DeleteAccountOperation, new() { ["account"] = "5" });
            Assert.Equal(("DELETE", Api + "/accounts/5", (string?)null), Last());
            var linked = Assert.Single(await Run(ActiveCampaignConnector.AddAccountContactOperation, new() { ["account"] = "5", ["contact"] = "ada@example.com", ["job_title"] = "Buyer" }));
            Assert.Equal(("POST", Api + "/accountContacts", "{\"accountContact\":{\"account\":\"5\",\"jobTitle\":\"Buyer\",\"contact\":\"42\"}}"), Last());
            Assert.Equal("8", (string?)linked.Fields["id"]);
            await Run(ActiveCampaignConnector.UpdateAccountContactOperation, new() { ["account_contact"] = "8", ["job_title"] = "CFO" });
            Assert.Equal(("PUT", Api + "/accountContacts/8", "{\"accountContact\":{\"jobTitle\":\"CFO\"}}"), Last());
            await Run(ActiveCampaignConnector.RemoveAccountContactOperation, new() { ["account_contact"] = "8" });
            Assert.Equal(("DELETE", Api + "/accountContacts/8", (string?)null), Last());
            var accounts = new ActiveCampaign { Answer = (_, _) => Ok("""{"accounts":[{"id":"5","name":"Acme AB"}],"meta":{"total":"1"}}""") };
            await Contexts.DrainAsync(new ActiveCampaignConnector(), Context(accounts, ActiveCampaignConnector.AccountsOperation, new() { ["search"] = "Acme" }));
            Assert.Equal(Api + "/accounts?search=Acme&limit=100&offset=0", Assert.Single(accounts.Requests).Url);

            var orders = await Run(ActiveCampaignConnector.RecordsOperation, new() { ["resource"] = "/ecomOrders", ["query"] = "filters[email]=ada@example.com" });
            Assert.Equal(("GET", Api + "/ecomOrders?filters%5Bemail%5D=ada%40example.com&limit=100&offset=0", (string?)null), Last());
            Assert.Equal("1999", (string?)Assert.Single(orders).Fields["totalPrice"]);
            var products = await Run(ActiveCampaignConnector.RecordsOperation, new() { ["resource"] = "ecomOrders/12/orderProducts" });
            Assert.Equal(new[] { "Café", "Tea" }, products.Select(product => (string?)product.Fields["name"])); // its list, by whatever name it comes
            var customer = Assert.Single(await Run(ActiveCampaignConnector.CreateRecordOperation, new() { ["resource"] = "ecomCustomers", ["values"] = "{\"connectionid\": 1, \"externalid\": \"56\", \"email\": \"ada@example.com\", \"acceptsMarketing\": \"1\"}" }));
            Assert.Equal(("POST", Api + "/ecomCustomers", "{\"ecomCustomer\":{\"connectionid\":1,\"externalid\":\"56\",\"email\":\"ada@example.com\",\"acceptsMarketing\":\"1\"}}"), Last()); // its name, the resource's without its s
            Assert.Equal("11", (string?)customer.Fields["id"]);
            await Run(ActiveCampaignConnector.UpdateRecordOperation, new() { ["resource"] = "ecomOrders", ["id"] = "12", ["values"] = "{\"orderNumber\": \"1001\"}", ["root"] = "ecomOrder" });
            Assert.Equal(("PUT", Api + "/ecomOrders/12", "{\"ecomOrder\":{\"orderNumber\":\"1001\"}}"), Last());
            Assert.Equal("1001", (string?)Assert.Single(await Run(ActiveCampaignConnector.GetRecordOperation, new() { ["resource"] = "ecomOrders", ["id"] = "12" })).Fields["orderNumber"]);
            Assert.Equal(("GET", Api + "/ecomOrders/12", (string?)null), Last());
            await Run(ActiveCampaignConnector.DeleteRecordOperation, new() { ["resource"] = "ecomOrders", ["id"] = "12" });
            Assert.Equal(("DELETE", Api + "/ecomOrders/12", (string?)null), Last());
            await Run(ActiveCampaignConnector.CreateRecordOperation, new() { ["resource"] = "dealCategories", ["values"] = "{\"title\": \"Renewals\"}" });
            Assert.Equal("{\"dealCategory\":{\"title\":\"Renewals\"}}", Last().Item3); // -ies, -y
            await Run(ActiveCampaignConnector.UpdateRecordOperation, new() { ["resource"] = "addresses", ["id"] = "3", ["values"] = "{\"city\": \"Lund\"}", ["root"] = "address" });
            Assert.Equal(("PUT", Api + "/addresses/3", "{\"address\":{\"city\":\"Lund\"}}"), Last()); // its name as given
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_activecampaigns_refusal_is_its_words()
        {
            var account = new ActiveCampaign();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ActiveCampaignConnector(), Context(account, operation, parameters)))).Message;

            Assert.Equal("'ada' is not a contact - its id or its e-mail address.", await Refused(ActiveCampaignConnector.GetContactOperation, new() { ["contact"] = "ada" }));
            Assert.Equal("'0' is not a contact - its id or its e-mail address.", await Refused(ActiveCampaignConnector.DeleteContactOperation, new() { ["contact"] = "0" }));
            Assert.Equal("Give the contact - its id or its e-mail address.", await Refused(ActiveCampaignConnector.GetContactOperation, new()));
            Assert.Equal("Give the contact's e-mail address.", await Refused(ActiveCampaignConnector.CreateContactOperation, new() { ["first_name"] = "Ada" }));
            Assert.Equal("'ada@x@y' is not an e-mail address.", await Refused(ActiveCampaignConnector.SyncContactOperation, new() { ["email"] = "ada@x@y" }));
            Assert.Equal("'ada at example.com' is not an e-mail address.", await Refused(ActiveCampaignConnector.UpdateContactOperation, new() { ["contact"] = "42", ["email"] = "ada at example.com" }));
            Assert.Equal("Give something to change - an address, a name, a phone or custom fields.", await Refused(ActiveCampaignConnector.UpdateContactOperation, new() { ["contact"] = "42" }));
            Assert.Equal("The custom fields are not a JSON object - {\"COMPANY\": \"Acme\"}.", await Refused(ActiveCampaignConnector.CreateContactOperation, new() { ["email"] = "ada@example.com", ["fields"] = "COMPANY=Acme" }));
            Assert.Equal("The first name has a line break - one line only.", await Refused(ActiveCampaignConnector.CreateContactOperation, new() { ["email"] = "ada@example.com", ["first_name"] = "Ada\nB" }));
            Assert.Equal("'gone' is not a contact's status - any, unconfirmed, active, unsubscribed or bounced.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["status"] = "gone" }));
            Assert.Equal("'age' is not an order - cdate, email, first_name, last_name, name, score, then asc or desc.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["order"] = "age" }));
            Assert.Equal("'cdate up' is not an order - cdate, email, first_name, last_name, name, score, then asc or desc.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["order"] = "cdate up" }));
            Assert.Equal("'cdate desc now' is not an order - cdate, email, first_name, last_name, name, score, then asc or desc.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["order"] = "cdate desc now" }));
            Assert.Equal("'yesterday' is not a time (made after) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["created_after"] = "yesterday" }));
            Assert.Equal("'soon' is not a time (changed before) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["updated_before"] = "soon" }));
            Assert.Equal("'vip' is not an id.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["segment"] = "vip" }));
            Assert.Equal("'ada' is not an e-mail address.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["email"] = "ada" }));
            Assert.Equal("'email_like' is not a filter - name=value, filters[status]=1.", await Refused(ActiveCampaignConnector.ContactsOperation, new() { ["query"] = "email_like" }));
            Assert.Equal("'a b=1' is not a filter - name=value, filters[status]=1.", await Refused(ActiveCampaignConnector.RecordsOperation, new() { ["resource"] = "ecomOrders", ["query"] = "a b=1" }));
            Assert.Equal("'filters[a]b=1' is not a filter - name=value, filters[status]=1.", await Refused(ActiveCampaignConnector.RecordsOperation, new() { ["resource"] = "ecomOrders", ["query"] = "filters[a]b=1" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(ActiveCampaignConnector.TagsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the list - its id or name.", await Refused(ActiveCampaignConnector.AddToListOperation, new() { ["contact"] = "42" }));
            Assert.Equal("The list's name has a line break - one line only.", await Refused(ActiveCampaignConnector.RemoveFromListOperation, new() { ["contact"] = "42", ["list"] = "News\nletter" }));
            Assert.Equal("Give the tag's name.", await Refused(ActiveCampaignConnector.CreateTagOperation, new()));
            Assert.Equal("'deal' is not a tag's type - contact or template.", await Refused(ActiveCampaignConnector.CreateTagOperation, new() { ["name"] = "VIP", ["type"] = "deal" }));
            Assert.Equal("Give something to change - a name or a description.", await Refused(ActiveCampaignConnector.UpdateTagOperation, new() { ["tag"] = "3" }));
            Assert.Equal("Give the tag - its id or name.", await Refused(ActiveCampaignConnector.AddTagOperation, new() { ["contact"] = "42" }));
            Assert.Equal("Give the tag - its id or name - or the contact tag's id.", await Refused(ActiveCampaignConnector.RemoveTagOperation, new() { ["contact"] = "42" }));
            Assert.Equal("Give the contact - its id or its e-mail address.", await Refused(ActiveCampaignConnector.RemoveTagOperation, new() { ["tag"] = "3" }));
            Assert.Equal("'x' is not a contact tag's id.", await Refused(ActiveCampaignConnector.RemoveTagOperation, new() { ["contact_tag"] = "x" }));
            Assert.Equal("Give the deal's title.", await Refused(ActiveCampaignConnector.CreateDealOperation, new() { ["contact"] = "42", ["value"] = "1" }));
            Assert.Equal("Give the contact - its id or its e-mail address.", await Refused(ActiveCampaignConnector.CreateDealOperation, new() { ["title"] = "A", ["value"] = "1" }));
            Assert.Equal("Give the deal's value in cents - 45000 for 450.00.", await Refused(ActiveCampaignConnector.CreateDealOperation, new() { ["title"] = "A", ["contact"] = "42" }));
            Assert.Equal("'450.00' is not a value in cents - 45000 for 450.00.", await Refused(ActiveCampaignConnector.CreateDealOperation, new() { ["title"] = "A", ["contact"] = "42", ["value"] = "450.00" }));
            Assert.Equal("'-5' is not a value in cents - 45000 for 450.00.", await Refused(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["value"] = "-5" }));
            Assert.Equal("'euro' is not a currency - three letters, eur.", await Refused(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["currency"] = "euro" }));
            Assert.Equal("'main' is not a pipeline's id.", await Refused(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["pipeline"] = "main" }));
            Assert.Equal("'-1' is not an owner's id.", await Refused(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["owner"] = "-1" }));
            Assert.Equal("'150' is not a probability - 0 to 100.", await Refused(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["percent"] = "150" }));
            Assert.Equal("'-1' is not a probability - 0 to 100.", await Refused(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["percent"] = "-1" }));
            Assert.Equal("'pending' is not a deal's status - open, won or lost.", await Refused(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9", ["status"] = "pending" }));
            Assert.Equal("'pending' is not a deal's status - open, won or lost.", await Refused(ActiveCampaignConnector.DealsOperation, new() { ["status"] = "pending" }));
            Assert.Equal("Give something to change - a title, a contact, a value, a currency, a pipeline, a stage, an owner, a description, a probability or a status.", await Refused(ActiveCampaignConnector.UpdateDealOperation, new() { ["deal"] = "9" }));
            Assert.Equal("Give a deal's id.", await Refused(ActiveCampaignConnector.GetDealOperation, new()));
            Assert.Equal("'x' is not a deal's id.", await Refused(ActiveCampaignConnector.DeleteDealOperation, new() { ["deal"] = "x" }));
            Assert.Equal("Give the note.", await Refused(ActiveCampaignConnector.AddDealNoteOperation, new() { ["deal"] = "9" }));
            Assert.Equal("'x' is not a note's id.", await Refused(ActiveCampaignConnector.UpdateDealNoteOperation, new() { ["deal"] = "9", ["note_id"] = "x", ["note"] = "y" }));
            Assert.Equal("Give the account's name.", await Refused(ActiveCampaignConnector.CreateAccountOperation, new() { ["url"] = "https://acme.example" }));
            Assert.Equal("'acme.example' is not a website's address - https://acme.example.", await Refused(ActiveCampaignConnector.CreateAccountOperation, new() { ["name"] = "Acme", ["url"] = "acme.example" }));
            Assert.Equal("'ftp://acme.example' is not a website's address - https://acme.example.", await Refused(ActiveCampaignConnector.UpdateAccountOperation, new() { ["account"] = "5", ["url"] = "ftp://acme.example" }));
            Assert.Equal("'industry' is not an account field's id.", await Refused(ActiveCampaignConnector.CreateAccountOperation, new() { ["name"] = "Acme", ["fields"] = "{\"industry\": \"Retail\"}" }));
            Assert.Equal("Give something to change - a name, a website or custom fields.", await Refused(ActiveCampaignConnector.UpdateAccountOperation, new() { ["account"] = "5" }));
            Assert.Equal("Give an account's id.", await Refused(ActiveCampaignConnector.AddAccountContactOperation, new() { ["contact"] = "42" }));
            Assert.Equal("Give the job title.", await Refused(ActiveCampaignConnector.UpdateAccountContactOperation, new() { ["account_contact"] = "8" }));
            Assert.Equal("'x' is not an account contact's id.", await Refused(ActiveCampaignConnector.RemoveAccountContactOperation, new() { ["account_contact"] = "x" }));
            Assert.Equal("'../users' is not a resource - its path under /api/3, ecomOrders or ecomOrders/12/orderProducts.", await Refused(ActiveCampaignConnector.RecordsOperation, new() { ["resource"] = "../users" }));
            Assert.Equal("'ecomOrders/x/orderProducts' is not a resource - its path under /api/3, ecomOrders or ecomOrders/12/orderProducts.", await Refused(ActiveCampaignConnector.GetRecordOperation, new() { ["resource"] = "ecomOrders/x/orderProducts", ["id"] = "1" }));
            Assert.Equal("Give the values - a JSON object.", await Refused(ActiveCampaignConnector.CreateRecordOperation, new() { ["resource"] = "ecomCustomers" }));
            Assert.Equal("The values are not a JSON object - {\"email\": \"ada@example.com\"}.", await Refused(ActiveCampaignConnector.CreateRecordOperation, new() { ["resource"] = "ecomCustomers", ["values"] = "[1]" }));
            Assert.Equal("'ecom order' is not a record's name - ecomOrder.", await Refused(ActiveCampaignConnector.UpdateRecordOperation, new() { ["resource"] = "ecomOrders", ["id"] = "1", ["values"] = "{}", ["root"] = "ecom order" }));
            Assert.Equal("'0' is not a record's id.", await Refused(ActiveCampaignConnector.GetRecordOperation, new() { ["resource"] = "ecomOrders", ["id"] = "0" }));
            Assert.StartsWith("ActiveCampaign has no operation 'campaigns' - contacts, get_contact,", await Refused("campaigns", new()));
            Assert.Empty(account.Requests); // nothing sent

            var refusing = new ActiveCampaign { Answer = (_, _) => (HttpStatusCode.UnprocessableEntity, """{"errors":[{"title":"Email address already exists in the system","detail":"","code":"duplicate"},{"title":"Phone is invalid","detail":"+46 x"}]}""") };
            Assert.Equal("HTTP 422: Email address already exists in the system; Phone is invalid (+46 x)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ActiveCampaignConnector(), Context(refusing, ActiveCampaignConnector.CreateContactOperation, new() { ["email"] = "ada@example.com" })))).Message);
            var missing = new ActiveCampaign { Answer = (_, _) => (HttpStatusCode.NotFound, """{"message":"No Result found for Deal with id 99"}""") };
            Assert.Equal("HTTP 404: No Result found for Deal with id 99", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ActiveCampaignConnector(), Context(missing, ActiveCampaignConnector.GetDealOperation, new() { ["deal"] = "99" })))).Message);
            var silent = new ActiveCampaign { Answer = (_, _) => (HttpStatusCode.Forbidden, "") };
            Assert.Equal("HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ActiveCampaignConnector(), Context(silent, ActiveCampaignConnector.TagsOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_keys_user()
        {
            var account = new ActiveCampaign { Answer = (_, _) => Ok("""{"user":{"username":"admin","firstName":"Ada","lastName":"Lovelace","email":"ada@example.com"}}""") };
            var tested = await new ActiveCampaignConnector().TestConnectionAsync(Contexts.Test(new StubEgress(account), Endpoint + "/api/3/", settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((true, "Signed in as Ada Lovelace (admin)."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/users/me", "ac-key-1"), (account.Requests[0].Url, account.Requests[0].Key)); // the API's address given whole, or the account's
            var nameless = new ActiveCampaign { Answer = (_, _) => Ok("""{"user":{"email":"ada@example.com"}}""") };
            Assert.Equal("Signed in as ada@example.com.", (await new ActiveCampaignConnector().TestConnectionAsync(Contexts.Test(new StubEgress(nameless), Endpoint, settings: new Dictionary<string, string>(), credentials: Credentials))).Message);
            var keyless = await new ActiveCampaignConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new ActiveCampaign()), Endpoint, settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>()));
            Assert.Equal((false, "The ActiveCampaign connection has no API key."), (keyless.Success, keyless.Message));
            var nowhere = await new ActiveCampaignConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new ActiveCampaign()), "", settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((false, "Give the account's API URL - https://youraccount.api-us1.com, under Settings > Developer."), (nowhere.Success, nowhere.Message));
            Assert.Equal((AuthenticationType.ApiKey, "activecampaign"), (new ActiveCampaignConnector().AuthType, new ActiveCampaignConnector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Marketing, ConnectorCategory.Crm }, new ActiveCampaignConnector().Categories);
            Assert.Equal(37, new ActiveCampaignConnector().Operations.Count);
        }
    }
}
