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
using Stomme.Modules.Connectors.SendGrid;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// SendGrid: the API key as the bearer; contacts an SGQL query finds, or the latest, at most 50 and SendGrid's count
    /// of them on each; one got by its id or its address; contacts added or changed (one with its details, several by address)
    /// and deleted (by ids, or all); the lists page by page by SendGrid's token up to the most asked for, one got with a sample,
    /// made, renamed and deleted; a mail sent - a body or a dynamic template, copies, a reply-to, categories, headers, a time, the
    /// sandbox - its message id the record; what could go wrong refused before anything is sent; SendGrid's refusal its first
    /// error and field; a test reading the key's scopes.
    /// </summary>
    public sealed class SendGridConnectorTests
    {
        private const string Api = "https://api.sendgrid.com/v3";

        private const string Key = "SG.test-key.0123456789abcdefghijklmnopqrstuvwxyz";

        private sealed class SendGridApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode Status, string Body)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, request.RequestUri!.AbsoluteUri);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (request.RequestUri!.AbsolutePath.EndsWith("/mail/send", StringComparison.Ordinal) && status == HttpStatusCode.Accepted)
                {
                    response.Headers.Add("X-Message-Id", "msg-7f3a9c");
                }

                return response;
            }
        }

        private static ConnectorExtractContext Context(SendGridApi sendgrid, string operation, Dictionary<string, string> parameters, string endpoint = "https://api.sendgrid.com/v3") =>
            Contexts.Extract(new StubEgress(sendgrid), endpoint, operation, parameters, null, new Dictionary<string, string> { ["token"] = Key });

        private static SendGridConnector Connector() => new();

        private static string Contact(int n) =>
            "{\"id\":\"c" + n + "\",\"email\":\"ada" + n + "@example.com\",\"first_name\":\"Ada\",\"last_name\":\"Lovelace\",\"alternate_emails\":[\"ada@home.example\"],\"address_line_1\":\"12 Byron St\",\"city\":\"London\","
            + "\"state_province_region\":\"\",\"postal_code\":\"W1\",\"country\":\"UK\",\"list_ids\":[\"l1\",\"l2\"],\"custom_fields\":{\"e1_T\":\"Café <b>Gold</b>\"},\"created_at\":\"2026-10-01T10:00:00Z\",\"updated_at\":\"2026-10-02T10:00:00Z\"}";

        [Fact]
        public async Task Contacts_are_found_got_upserted_and_deleted_by_the_bearer()
        {
            var sendgrid = new SendGridApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"result\":[" + string.Join(",", Enumerable.Range(1, 50).Select(Contact)) + "],\"contact_count\":1234}") };
            var found = await Contexts.DrainAsync(Connector(), Context(sendgrid, SendGridConnector.ContactsOperation, new() { ["query"] = "email LIKE '%@example.com'", ["max"] = "20" }));
            Assert.Equal(20, found.Count); // the most asked for
            Assert.Equal((HttpMethod.Post, Api + "/marketing/contacts/search", "Bearer " + Key, "{\"query\":\"email LIKE '%@example.com'\"}"), (sendgrid.Requests[0].Method, sendgrid.Requests[0].Url, sendgrid.Requests[0].Authorization, sendgrid.Requests[0].Body));
            var contact = found[0].Fields;
            Assert.Equal(("c1", "ada1@example.com", "Ada", "Lovelace", "ada@home.example", "London", "l1, l2", "{\"e1_T\":\"Café <b>Gold</b>\"}", 1234L),
                ((string?)contact["id"], (string?)contact["email"], (string?)contact["first_name"], (string?)contact["last_name"], (string?)contact["alternate_emails"], (string?)contact["city"], (string?)contact["list_ids"], (string?)contact["custom_fields"], (long?)contact["total"]));

            var latest = new SendGridApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"result\":[" + string.Join(",", Enumerable.Range(1, 50).Select(Contact)) + "],\"contact_count\":50}") };
            Assert.Equal(50, (await Contexts.DrainAsync(Connector(), Context(latest, SendGridConnector.ContactsOperation, new()))).Count); // SendGrid's 50 by default
            Assert.Equal((HttpMethod.Get, Api + "/marketing/contacts"), (latest.Requests[0].Method, latest.Requests[0].Url));

            var one = new SendGridApi { Answer = (_, url) => (HttpStatusCode.OK, url.EndsWith("/emails", StringComparison.Ordinal) ? "{\"result\":{\"ada1@example.com\":{\"contact\":" + Contact(1) + "}}}" : Contact(7)) };
            Assert.Equal("c7", Assert.Single(await Contexts.DrainAsync(Connector(), Context(one, SendGridConnector.GetContactOperation, new() { ["contact"] = "c7" }))).Fields["id"]);
            Assert.Equal(Api + "/marketing/contacts/c7", one.Requests[^1].Url);
            Assert.Equal("c1", Assert.Single(await Contexts.DrainAsync(Connector(), Context(one, SendGridConnector.GetContactOperation, new() { ["email"] = "Ada1@Example.com" }))).Fields["id"]);
            Assert.Equal((Api + "/marketing/contacts/search/emails", "{\"emails\":[\"ada1@example.com\"]}"), (one.Requests[^1].Url, one.Requests[^1].Body)); // SendGrid keeps addresses lower-case

            var upsert = new SendGridApi { Answer = (_, _) => (HttpStatusCode.Accepted, "{\"job_id\":\"job-1\"}") };
            var job = Assert.Single(await Contexts.DrainAsync(Connector(), Context(upsert, SendGridConnector.UpsertContactsOperation, new()
            {
                ["email"] = "ada@example.com", ["first_name"] = "Ada", ["last_name"] = "Lovelace", ["alternate_emails"] = "ada@home.example, ada@work.example", ["city"] = "London",
                ["country"] = "UK", ["custom_fields"] = "{\"e1_T\": \"Café <b>Gold</b>\"}", ["lists"] = "l1, l2",
            })));
            Assert.Equal((HttpMethod.Put, Api + "/marketing/contacts", "{\"contacts\":[{\"email\":\"ada@example.com\",\"first_name\":\"Ada\",\"last_name\":\"Lovelace\",\"city\":\"London\",\"country\":\"UK\",\"alternate_emails\":[\"ada@home.example\",\"ada@work.example\"],\"custom_fields\":{\"e1_T\":\"Café <b>Gold</b>\"}}],\"list_ids\":[\"l1\",\"l2\"]}"),
                (upsert.Requests[^1].Method, upsert.Requests[^1].Url, upsert.Requests[^1].Body));
            Assert.Equal(("job-1", 1), ((string?)job.Fields["job_id"], (int?)job.Fields["contacts"]));
            await Contexts.DrainAsync(Connector(), Context(upsert, SendGridConnector.UpsertContactsOperation, new() { ["email"] = "a@example.com, b@example.com" }));
            Assert.Equal("{\"contacts\":[{\"email\":\"a@example.com\"},{\"email\":\"b@example.com\"}]}", upsert.Requests[^1].Body); // several by address

            var gone = new SendGridApi { Answer = (_, _) => (HttpStatusCode.Accepted, "{\"job_id\":\"job-2\"}") };
            await Contexts.DrainAsync(Connector(), Context(gone, SendGridConnector.DeleteContactsOperation, new() { ["contacts"] = "c1, c2" }));
            Assert.Equal((HttpMethod.Delete, Api + "/marketing/contacts?ids=c1,c2"), (gone.Requests[^1].Method, gone.Requests[^1].Url));
            var all = Assert.Single(await Contexts.DrainAsync(Connector(), Context(gone, SendGridConnector.DeleteContactsOperation, new() { ["all"] = "true" })));
            Assert.Equal(Api + "/marketing/contacts?delete_all_contacts=true", gone.Requests[^1].Url);
            Assert.Equal(("job-2", "all"), ((string?)all.Fields["job_id"], (string?)all.Fields["deleted"]));
        }

        [Fact]
        public async Task Lists_are_read_page_by_page_got_made_renamed_and_deleted()
        {
            var sendgrid = new SendGridApi
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page_token=", StringComparison.Ordinal)
                    ? "{\"result\":[" + string.Join(",", Enumerable.Range(1001, 1000).Select(n => "{\"id\":\"l" + n + "\",\"name\":\"List " + n + "\",\"contact_count\":" + n + "}")) + "],\"_metadata\":{}}"
                    : "{\"result\":[" + string.Join(",", Enumerable.Range(1, 1000).Select(n => "{\"id\":\"l" + n + "\",\"name\":\"List " + n + "\",\"contact_count\":" + n + "}")) + "],\"_metadata\":{\"next\":\"https://api.sendgrid.com/v3/marketing/lists?page_size=1000&page_token=tok%2B2\"}}"),
            };
            var lists = await Contexts.DrainAsync(Connector(), Context(sendgrid, SendGridConnector.ListsOperation, new() { ["max"] = "1200" }));
            Assert.Equal(1200, lists.Count);
            Assert.Equal(new[] { Api + "/marketing/lists?page_size=1000", Api + "/marketing/lists?page_size=200&page_token=tok%2B2" }, sendgrid.Requests.Select(request => request.Url)); // SendGrid's token; a page no bigger than what is left
            Assert.Equal(("l1", "List 1", 1L), ((string?)lists[0].Fields["id"], (string?)lists[0].Fields["name"], (long?)lists[0].Fields["contact_count"]));

            var one = new SendGridApi { Answer = (method, _) => (method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK, method == HttpMethod.Delete ? "" : "{\"id\":\"l9\",\"name\":\"VIP\",\"contact_count\":2,\"contact_sample\":[{\"email\":\"ada@example.com\"},{\"email\":\"grace@example.com\"}]}") };
            var got = Assert.Single(await Contexts.DrainAsync(Connector(), Context(one, SendGridConnector.GetListOperation, new() { ["list"] = "l9", ["contact_sample"] = "true" })));
            Assert.Equal(Api + "/marketing/lists/l9?contact_sample=true", one.Requests[^1].Url);
            Assert.Equal(("VIP", "ada@example.com, grace@example.com"), ((string?)got.Fields["name"], (string?)got.Fields["sample"]));
            await Contexts.DrainAsync(Connector(), Context(one, SendGridConnector.CreateListOperation, new() { ["name"] = "VIP" }));
            Assert.Equal((HttpMethod.Post, Api + "/marketing/lists", "{\"name\":\"VIP\"}"), (one.Requests[^1].Method, one.Requests[^1].Url, one.Requests[^1].Body));
            await Contexts.DrainAsync(Connector(), Context(one, SendGridConnector.RenameListOperation, new() { ["list"] = "l9", ["name"] = "Gold" }));
            Assert.Equal(("PATCH", Api + "/marketing/lists/l9", "{\"name\":\"Gold\"}"), (one.Requests[^1].Method.Method, one.Requests[^1].Url, one.Requests[^1].Body));
            var deleted = Assert.Single(await Contexts.DrainAsync(Connector(), Context(one, SendGridConnector.DeleteListOperation, new() { ["list"] = "l9", ["delete_contacts"] = "true" })));
            Assert.Equal((HttpMethod.Delete, Api + "/marketing/lists/l9?delete_contacts=true"), (one.Requests[^1].Method, one.Requests[^1].Url));
            Assert.Equal((true, true), ((bool?)deleted.Fields["deleted"], (bool?)deleted.Fields["contacts_deleted"]));
            await Contexts.DrainAsync(Connector(), Context(one, SendGridConnector.DeleteListOperation, new() { ["list"] = "l9" }));
            Assert.Equal(Api + "/marketing/lists/l9?delete_contacts=false", one.Requests[^1].Url);
        }

        [Fact]
        public async Task A_mail_is_sent_with_a_body_or_a_dynamic_template()
        {
            var sendgrid = new SendGridApi { Answer = (_, _) => (HttpStatusCode.Accepted, "") };
            var sent = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sendgrid, SendGridConnector.SendMailOperation, new()
            {
                ["from"] = "shop@example.com", ["from_name"] = "Café Shop", ["to"] = "ada@example.com, grace@example.com", ["cc"] = "boss@example.com", ["bcc"] = "log@example.com",
                ["reply_to"] = "help@example.com", ["subject"] = "Your order", ["body"] = "<p>Hello <b>Ada</b></p>\n<p>Thanks</p>", ["html"] = "true", ["categories"] = "orders, receipts",
                ["headers"] = "{\"X-Order\": \"1001\"}", ["ip_pool"] = "transactional", ["send_at"] = "2026-10-13T09:00:00Z", ["sandbox"] = "true",
            })));
            Assert.Equal((HttpMethod.Post, Api + "/mail/send"), (sendgrid.Requests[^1].Method, sendgrid.Requests[^1].Url));
            Assert.Equal("{\"personalizations\":[{\"to\":[{\"email\":\"ada@example.com\"},{\"email\":\"grace@example.com\"}],\"cc\":[{\"email\":\"boss@example.com\"}],\"bcc\":[{\"email\":\"log@example.com\"}],\"subject\":\"Your order\",\"send_at\":1791882000}],"
                + "\"from\":{\"email\":\"shop@example.com\",\"name\":\"Café Shop\"},\"reply_to_list\":[{\"email\":\"help@example.com\"}],\"content\":[{\"type\":\"text/html\",\"value\":\"<p>Hello <b>Ada</b></p>\\n<p>Thanks</p>\"}],"
                + "\"categories\":[\"orders\",\"receipts\"],\"headers\":{\"X-Order\":\"1001\"},\"ip_pool_name\":\"transactional\",\"mail_settings\":{\"sandbox_mode\":{\"enable\":true}}}", sendgrid.Requests[^1].Body);
            Assert.Equal(("msg-7f3a9c", "ada@example.com, grace@example.com", "Your order"), ((string?)sent.Fields["message_id"], (string?)sent.Fields["to"], (string?)sent.Fields["subject"]));

            await Contexts.DrainAsync(Connector(), Context(sendgrid, SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "Hi", ["body"] = "Plain" }));
            Assert.Equal("{\"personalizations\":[{\"to\":[{\"email\":\"ada@example.com\"}],\"subject\":\"Hi\"}],\"from\":{\"email\":\"shop@example.com\"},\"content\":[{\"type\":\"text/plain\",\"value\":\"Plain\"}]}", sendgrid.Requests[^1].Body);
            await Contexts.DrainAsync(Connector(), Context(sendgrid, SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["template"] = "d-0123456789abcdef0123456789abcdef", ["template_data"] = "{\"name\": \"Ada\", \"items\": [1, 2]}" }));
            Assert.Equal("{\"personalizations\":[{\"to\":[{\"email\":\"ada@example.com\"}],\"dynamic_template_data\":{\"name\":\"Ada\",\"items\":[1,2]}}],\"from\":{\"email\":\"shop@example.com\"},\"template_id\":\"d-0123456789abcdef0123456789abcdef\"}", sendgrid.Requests[^1].Body);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_sendgrids_refusal_is_its_words()
        {
            var sendgrid = new SendGridApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "https://api.sendgrid.com/v3") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(sendgrid, operation, parameters, endpoint)))).Message;

            Assert.Equal("The query has a line break - one line only.", await Refused(SendGridConnector.ContactsOperation, new() { ["query"] = "a\nb" }));
            Assert.Equal("Give the contact's id - or its address.", await Refused(SendGridConnector.GetContactOperation, new()));
            Assert.Equal("'c 1' is not a SendGrid contact's id.", await Refused(SendGridConnector.GetContactOperation, new() { ["contact"] = "c 1" }));
            Assert.Equal("'ada' is not an address.", await Refused(SendGridConnector.GetContactOperation, new() { ["email"] = "ada" }));
            Assert.Equal("'@example.com' is not an address.", await Refused(SendGridConnector.GetContactOperation, new() { ["email"] = "@example.com" }));
            Assert.Equal("'ada@' is not an address.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@" }));
            Assert.Equal("'ada lovelace@example.com' is not an address.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada lovelace@example.com" }));
            Assert.Equal("Give the contact's address - several comma-separated.", await Refused(SendGridConnector.UpsertContactsOperation, new() { ["first_name"] = "Ada" }));
            Assert.Equal("'ada at example.com' is not an address.", await Refused(SendGridConnector.UpsertContactsOperation, new() { ["email"] = "ada at example.com" }));
            Assert.Equal("Names, an address and custom fields are one contact's - give one address with them.", await Refused(SendGridConnector.UpsertContactsOperation, new() { ["email"] = "a@example.com, b@example.com", ["first_name"] = "Ada" }));
            Assert.Equal("The first name has a line break - one line only.", await Refused(SendGridConnector.UpsertContactsOperation, new() { ["email"] = "a@example.com", ["first_name"] = "A\nB" }));
            Assert.Equal("The custom fields are not JSON - {\"name\": \"Ada\"}.", await Refused(SendGridConnector.UpsertContactsOperation, new() { ["email"] = "a@example.com", ["custom_fields"] = "e1_T=Gold" }));
            Assert.Equal("The custom fields are not a JSON object - {\"name\": \"Ada\"}.", await Refused(SendGridConnector.UpsertContactsOperation, new() { ["email"] = "a@example.com", ["custom_fields"] = "[1]" }));
            Assert.Equal("'my list' is not a SendGrid list's id.", await Refused(SendGridConnector.UpsertContactsOperation, new() { ["email"] = "a@example.com", ["lists"] = "my list" }));
            Assert.Equal("Give the contacts' ids - or all = true for every contact - not both.", await Refused(SendGridConnector.DeleteContactsOperation, new()));
            Assert.Equal("Give the contacts' ids - or all = true for every contact - not both.", await Refused(SendGridConnector.DeleteContactsOperation, new() { ["contacts"] = "c1", ["all"] = "true" }));
            Assert.Equal("Give the list's name.", await Refused(SendGridConnector.CreateListOperation, new()));
            Assert.Equal("The name has a line break - one line only.", await Refused(SendGridConnector.RenameListOperation, new() { ["list"] = "l1", ["name"] = "a\nb" }));
            Assert.Equal("'' is not a SendGrid list's id.", await Refused(SendGridConnector.DeleteListOperation, new()));
            Assert.Equal("Give who it goes to - addresses, comma-separated.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com" }));
            Assert.Equal("Give the sender's address - a verified sender.", await Refused(SendGridConnector.SendMailOperation, new() { ["to"] = "ada@example.com" }));
            Assert.Equal("Give the body - or a dynamic template.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "Hi" }));
            Assert.Equal("Give the subject - or a dynamic template.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["body"] = "Hi" }));
            Assert.Equal("A dynamic template is the subject and the body - give the template, or a subject and a body.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["template"] = "d-0123456789abcdef0123456789abcdef", ["subject"] = "Hi" }));
            Assert.Equal("'welcome' is not a dynamic template's id - d- and 32 hex digits.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["template"] = "welcome" }));
            Assert.Equal("The template data are not a JSON object - {\"name\": \"Ada\"}.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["template"] = "d-0123456789abcdef0123456789abcdef", ["template_data"] = "\"Ada\"" }));
            Assert.Equal("Up to 10 categories.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "Hi", ["body"] = "x", ["categories"] = string.Join(",", Enumerable.Range(1, 11)) }));
            Assert.Equal("The header 'X Order' is not a header's name and text.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "Hi", ["body"] = "x", ["headers"] = "{\"X Order\": \"1\"}" }));
            Assert.Equal("The header 'X-Items' is not a header's name and text.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "Hi", ["body"] = "x", ["headers"] = "{\"X-Items\": [1]}" }));
            Assert.Equal("'tomorrow' is not a time - 2026-10-13T09:00:00Z.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "Hi", ["body"] = "x", ["send_at"] = "tomorrow" }));
            Assert.Equal("The subject has a line break - one line only.", await Refused(SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "a\nb", ["body"] = "x" }));
            Assert.StartsWith("SendGrid has no operation 'send_sms' - contacts, get_contact,", await Refused("send_sms", new()));
            Assert.Equal("The endpoint 'ftp://api.sendgrid.com' is not an absolute http(s) URL.", await Refused(SendGridConnector.ListsOperation, new(), "ftp://api.sendgrid.com"));
            Assert.Empty(sendgrid.Requests);

            var refusing = new SendGridApi { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"errors\":[{\"field\":\"from.email\",\"message\":\"The from address does not match a verified Sender Identity.\"}]}") };
            Assert.Equal("HTTP 400: The from address does not match a verified Sender Identity. (from.email)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, SendGridConnector.SendMailOperation, new() { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "Hi", ["body"] = "x" })))).Message);
            var plain = new SendGridApi { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"errors\":[{\"field\":null,\"message\":\"access forbidden\"}]}") };
            Assert.Equal("HTTP 403: access forbidden", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(plain, SendGridConnector.ListsOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_reads_the_keys_scopes()
        {
            var sendgrid = new SendGridApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"scopes\":[\"mail.send\",\"marketing.read\",\"marketing.contacts.write\"]}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(sendgrid), "https://api.sendgrid.com/v3", credentials: new Dictionary<string, string> { ["token"] = Key }));
            Assert.Equal((true, "The key works - 3 scopes, mail.send among them, marketing."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/scopes", "Bearer " + Key), (sendgrid.Requests[0].Url, sendgrid.Requests[0].Authorization));
            var sendOnly = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new SendGridApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"scopes\":[\"marketing.read\"]}") }), "https://api.sendgrid.com/v3", credentials: new Dictionary<string, string> { ["token"] = Key }));
            Assert.Equal((true, "The key works - 1 scope, not mail.send, marketing."), (sendOnly.Success, sendOnly.Message));
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new SendGridApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"errors\":[{\"field\":null,\"message\":\"authorization required\"}]}") }), "https://api.sendgrid.com/v3", credentials: new Dictionary<string, string> { ["token"] = Key }));
            Assert.Equal((false, "HTTP 401: authorization required"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "sendgrid", ConnectorCategory.Marketing), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "contacts", "get_contact", "upsert_contacts", "delete_contacts", "lists", "get_list", "create_list", "rename_list", "delete_list", "send_mail" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
