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
using Stomme.Modules.Connectors.Brevo;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Brevo (n8n's Brevo node: its contacts, contact attributes, transactional e-mail and senders): the key in Brevo's own
    /// api-key header; contacts page by page by offset up to the most asked for, in an order, changed since a time in UTC, each
    /// attribute a field; a contact got by its id or its address, made, made or changed, changed and deleted; an e-mail sent with a
    /// body or a template and its parameters, its addresses named; attributes made, changed and deleted by category; senders and
    /// lists; what could go wrong refused before anything is sent; Brevo's refusal its code and message; a test naming the account.
    /// </summary>
    public sealed class BrevoConnectorTests
    {
        private const string Api = "https://brevo.example.test/v3";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "xkeysib-1" };

        private sealed class Brevo : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Key, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.NoContent, "");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("api-key", out var key) ? key.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Brevo brevo, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(brevo), Api, operation, parameters, settings: new Dictionary<string, string>(), credentials: Credentials);

        private static string Contacts(int from, int count) =>
            "{\"contacts\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":" + n + ",\"email\":\"ada" + n + "@example.com\",\"emailBlacklisted\":false,\"smsBlacklisted\":true,\"createdAt\":\"2026-10-01T09:00:00.000Z\","
                + "\"modifiedAt\":\"2026-10-02T09:00:00.000Z\",\"listIds\":[2,7],\"attributes\":{\"FIRSTNAME\":\"Café <b>Ada</b>\",\"SCORE\":12,\"VIP\":true}}")) + "],\"count\":2000}";

        [Fact]
        public async Task Contacts_are_read_page_by_page_up_to_the_most_asked_for_each_attribute_a_field()
        {
            var brevo = new Brevo { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("offset=500", StringComparison.Ordinal) ? Contacts(501, 20) : Contacts(1, 500)) };
            var records = await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.ContactsOperation, new() { ["sort"] = "ASC", ["modified_since"] = "2026-10-01T11:00:00+02:00", ["max"] = "600" }));

            Assert.Equal(520, records.Count); // the last page short: the end
            Assert.All(brevo.Requests, request => Assert.Equal("xkeysib-1", request.Key));
            Assert.Equal(new[]
            {
                Api + "/contacts?limit=500&offset=0&sort=asc&modifiedSince=2026-10-01T09%3A00%3A00Z",
                Api + "/contacts?limit=100&offset=500&sort=asc&modifiedSince=2026-10-01T09%3A00%3A00Z",
            }, brevo.Requests.Select(request => request.Url)); // in UTC; a page no bigger than what is left
            var ada = records[0].Fields;
            Assert.Equal((1L, "ada1@example.com", "2, 7", false, true, "Café <b>Ada</b>", 12L, (object?)true),
                ((long?)ada["id"], (string?)ada["email"], (string?)ada["list_ids"], (bool?)ada["email_blacklisted"], (bool?)ada["sms_blacklisted"], (string?)ada["FIRSTNAME"], (long?)ada["SCORE"], ada["VIP"]));

            var thousand = new Brevo { Answer = (_, _) => (HttpStatusCode.OK, Contacts(1, 500)) };
            Assert.Equal(1000, (await Contexts.DrainAsync(new BrevoConnector(), Context(thousand, BrevoConnector.ContactsOperation, new()))).Count); // by default up to 1,000
            Assert.Equal(new[] { Api + "/contacts?limit=500&offset=0&sort=desc", Api + "/contacts?limit=500&offset=500&sort=desc" }, thousand.Requests.Select(request => request.Url)); // newest first by default

            var lists = new Brevo { Answer = (_, _) => (HttpStatusCode.OK, """{"lists":[{"id":2,"name":"Newsletter","uniqueSubscribers":1200,"folderId":1},{"id":7,"name":"VIP","totalSubscribers":40}]}""") };
            var read = await Contexts.DrainAsync(new BrevoConnector(), Context(lists, BrevoConnector.ListsOperation, new()));
            Assert.Equal(new[] { (2L, "Newsletter", 1200L), (7L, "VIP", 40L) }, read.Select(list => ((long)list.Fields["id"]!, (string)list.Fields["name"]!, (long)list.Fields["subscribers"]!)));
            Assert.Equal(Api + "/contacts/lists?limit=50&offset=0", Assert.Single(lists.Requests).Url);
        }

        [Fact]
        public async Task A_contact_is_got_made_made_or_changed_changed_and_deleted()
        {
            var brevo = new Brevo { Answer = (method, url) => method == HttpMethod.Get ? (HttpStatusCode.OK, Contacts(9, 1)[13..^15]) : method == HttpMethod.Post && url.EndsWith("/contacts", StringComparison.Ordinal) ? (HttpStatusCode.Created, """{"id":42}""") : (HttpStatusCode.NoContent, "") };
            (string, string, string?) Last() => (brevo.Requests[^1].Method.Method, brevo.Requests[^1].Url, brevo.Requests[^1].Body);

            var got = Assert.Single(await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.GetContactOperation, new() { ["contact"] = "ada+shop@example.com" })));
            Assert.Equal(("GET", Api + "/contacts/ada%2Bshop%40example.com", (string?)null), Last()); // an address escaped
            Assert.Equal(9L, (long?)got.Fields["id"]);
            var made = Assert.Single(await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.CreateContactOperation, new() { ["email"] = "ada@example.com", ["attributes"] = "{\"FIRSTNAME\": \"Café <b>Ada</b>\", \"SCORE\": 12}", ["list_ids"] = "2, 7" })));
            Assert.Equal(("POST", Api + "/contacts", "{\"email\":\"ada@example.com\",\"attributes\":{\"FIRSTNAME\":\"Café <b>Ada</b>\",\"SCORE\":12},\"listIds\":[2,7]}"), Last());
            Assert.Equal((42L, true), ((long?)made.Fields["id"], (bool?)made.Fields["created"]));
            var upserted = Assert.Single(await Contexts.DrainAsync(new BrevoConnector(), Context(new Brevo(), BrevoConnector.UpsertContactOperation, new() { ["email"] = "ada@example.com" })));
            Assert.True((bool?)upserted.Fields["updated"]); // known: changed, no id given back
            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.UpsertContactOperation, new() { ["email"] = "ada@example.com", ["attributes"] = "{\"VIP\": true}" }));
            Assert.Equal("{\"email\":\"ada@example.com\",\"attributes\":{\"VIP\":true},\"updateEnabled\":true}", Last().Item3);
            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.UpdateContactOperation, new() { ["contact"] = "42", ["attributes"] = "{\"SCORE\": 13}", ["list_ids"] = "9", ["unlink_list_ids"] = "2" }));
            Assert.Equal(("PUT", Api + "/contacts/42", "{\"attributes\":{\"SCORE\":13},\"listIds\":[9],\"unlinkListIds\":[2]}"), Last());
            var gone = Assert.Single(await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.DeleteContactOperation, new() { ["contact"] = "42" })));
            Assert.Equal(("DELETE", Api + "/contacts/42", (string?)null), Last());
            Assert.True((bool?)gone.Fields["done"]);
        }

        [Fact]
        public async Task An_email_is_sent_with_a_body_or_a_template_and_attributes_and_senders_are_kept()
        {
            var brevo = new Brevo
            {
                Answer = (method, url) => url.EndsWith("/smtp/email", StringComparison.Ordinal) ? (HttpStatusCode.Created, """{"messageId":"<2026@smtp-relay.mailin.fr>"}""")
                    : url.EndsWith("/senders", StringComparison.Ordinal) && method == HttpMethod.Get ? (HttpStatusCode.OK, """{"senders":[{"id":1,"name":"Shop","email":"shop@example.com","active":true}]}""")
                    : url.EndsWith("/senders", StringComparison.Ordinal) ? (HttpStatusCode.Created, """{"id":5}""")
                    : url.EndsWith("/contacts/attributes", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"attributes":[{"name":"TIER","category":"category","type":"category","enumeration":[{"value":1,"label":"Gold"},{"value":2,"label":"Silver"}]},{"name":"TOTAL","category":"calculated","calculatedValue":"SUM[ORDER_PRICE]"}]}""")
                    : (HttpStatusCode.NoContent, ""),
            };
            (string, string, string?) Last() => (brevo.Requests[^1].Method.Method, brevo.Requests[^1].Url, brevo.Requests[^1].Body);

            var sent = Assert.Single(await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.SendEmailOperation, new()
            {
                ["sender"] = "Café Shop <shop@example.com>", ["to"] = "\"Ada Lovelace\" <ada@example.com>, lin@example.com", ["cc"] = "boss@example.com", ["bcc"] = "Audit <audit@example.com>", ["reply_to"] = "help@example.com",
                ["subject"] = "Your order", ["html"] = "<p>Café</p>", ["text"] = "  Café\nThanks\n", ["tags"] = "orders, eu",
            })));
            Assert.Equal(("POST", Api + "/smtp/email", "{\"sender\":{\"email\":\"shop@example.com\",\"name\":\"Café Shop\"},\"to\":[{\"email\":\"ada@example.com\",\"name\":\"Ada Lovelace\"},{\"email\":\"lin@example.com\"}],"
                + "\"cc\":[{\"email\":\"boss@example.com\"}],\"bcc\":[{\"email\":\"audit@example.com\",\"name\":\"Audit\"}],\"replyTo\":{\"email\":\"help@example.com\"},\"subject\":\"Your order\",\"htmlContent\":\"<p>Café</p>\","
                + "\"textContent\":\"  Café\\nThanks\\n\",\"tags\":[\"orders\",\"eu\"]}"), Last()); // the text as written
            Assert.Equal("<2026@smtp-relay.mailin.fr>", (string?)sent.Fields["message_id"]);
            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.SendEmailOperation, new() { ["to"] = "ada@example.com", ["template_id"] = "12", ["params"] = "ORDER=1001, NAME = Ada " }));
            Assert.Equal("{\"to\":[{\"email\":\"ada@example.com\"}],\"templateId\":12,\"params\":{\"ORDER\":\"1001\",\"NAME\":\"Ada\"}}", Last().Item3); // the template's sender and subject; n8n's pairs
            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.SendEmailOperation, new() { ["to"] = "ada@example.com", ["template_id"] = "12", ["params"] = "{\"ITEMS\": [1, 2]}" }));
            Assert.Equal("{\"to\":[{\"email\":\"ada@example.com\"}],\"templateId\":12,\"params\":{\"ITEMS\":[1,2]}}", Last().Item3);

            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.CreateAttributeOperation, new() { ["name"] = "BIRTHDAY", ["type"] = "DATE" }));
            Assert.Equal(("POST", Api + "/contacts/attributes/normal/BIRTHDAY", "{\"type\":\"date\"}"), Last()); // normal by default
            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.CreateAttributeOperation, new() { ["category"] = "category", ["name"] = "TIER", ["options"] = "1:Gold, 2: Silver" }));
            Assert.Equal(("POST", Api + "/contacts/attributes/category/TIER", "{\"enumeration\":[{\"value\":1,\"label\":\"Gold\"},{\"value\":2,\"label\":\"Silver\"}]}"), Last());
            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.UpdateAttributeOperation, new() { ["name"] = "TOTAL", ["value"] = "SUM[ORDER_PRICE]" }));
            Assert.Equal(("PUT", Api + "/contacts/attributes/calculated/TOTAL", "{\"value\":\"SUM[ORDER_PRICE]\"}"), Last()); // calculated by default
            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.DeleteAttributeOperation, new() { ["category"] = "global", ["name"] = "OLD" }));
            Assert.Equal(("DELETE", Api + "/contacts/attributes/global/OLD", (string?)null), Last());
            var attributes = await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.AttributesOperation, new()));
            Assert.Equal(new[] { ("TIER", "category", "1:Gold, 2:Silver", (string?)null), ("TOTAL", "calculated", "", "SUM[ORDER_PRICE]") },
                attributes.Select(attribute => ((string)attribute.Fields["name"]!, (string)attribute.Fields["category"]!, (string)attribute.Fields["options"]!, (string?)attribute.Fields["formula"])));

            var senders = await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.SendersOperation, new()));
            Assert.Equal((1L, "Shop", "shop@example.com", true), ((long)Assert.Single(senders).Fields["id"]!, (string)senders[0].Fields["name"]!, (string)senders[0].Fields["email"]!, (bool)senders[0].Fields["active"]!));
            var sender = Assert.Single(await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.CreateSenderOperation, new() { ["name"] = "Café Shop", ["email"] = "shop@example.com" })));
            Assert.Equal(("POST", Api + "/senders", "{\"name\":\"Café Shop\",\"email\":\"shop@example.com\"}"), Last());
            Assert.Equal(5L, (long?)sender.Fields["id"]);
            await Contexts.DrainAsync(new BrevoConnector(), Context(brevo, BrevoConnector.DeleteSenderOperation, new() { ["sender_id"] = "5" }));
            Assert.Equal(("DELETE", Api + "/senders/5", (string?)null), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_brevos_refusal_is_its_words()
        {
            var brevo = new Brevo();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new BrevoConnector(), Context(brevo, operation, parameters)))).Message;

            Assert.Equal("'ada' is not a contact - its id or its e-mail address.", await Refused(BrevoConnector.GetContactOperation, new() { ["contact"] = "ada" }));
            Assert.Equal("'ada@x@y' is not an e-mail address.", await Refused(BrevoConnector.CreateContactOperation, new() { ["email"] = "ada@x@y" }));
            Assert.Equal("The attributes are not a JSON object - {\"FIRSTNAME\": \"Ada\"}.", await Refused(BrevoConnector.CreateContactOperation, new() { ["email"] = "ada@example.com", ["attributes"] = "FIRSTNAME=Ada" }));
            Assert.Equal("'news' is not a list's id.", await Refused(BrevoConnector.CreateContactOperation, new() { ["email"] = "ada@example.com", ["list_ids"] = "2, news" }));
            Assert.Equal("'-2' is not a list's id.", await Refused(BrevoConnector.UpdateContactOperation, new() { ["contact"] = "42", ["list_ids"] = "-2" }));
            Assert.Equal("Give something to change - attributes, lists to add or lists to leave.", await Refused(BrevoConnector.UpdateContactOperation, new() { ["contact"] = "42" }));
            Assert.Equal("Give the sender - Shop <shop@example.com>.", await Refused(BrevoConnector.SendEmailOperation, new() { ["to"] = "ada@example.com", ["subject"] = "x", ["text"] = "y" }));
            Assert.Equal("'Ada <ada at example.com>' is not an address (recipient) - Ada <ada@example.com>, or ada@example.com.", await Refused(BrevoConnector.SendEmailOperation, new() { ["sender"] = "shop@example.com", ["to"] = "Ada <ada at example.com>" }));
            Assert.Equal("Give one address (sender).", await Refused(BrevoConnector.SendEmailOperation, new() { ["sender"] = "shop@example.com, other@example.com", ["to"] = "ada@example.com", ["subject"] = "x", ["text"] = "y" }));
            Assert.Equal("Give a body - text or HTML - or a template.", await Refused(BrevoConnector.SendEmailOperation, new() { ["sender"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "x" }));
            Assert.Equal("Give the subject.", await Refused(BrevoConnector.SendEmailOperation, new() { ["sender"] = "shop@example.com", ["to"] = "ada@example.com", ["text"] = "y" }));
            Assert.Equal("The subject has a line break - one line only.", await Refused(BrevoConnector.SendEmailOperation, new() { ["sender"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "a\nb", ["text"] = "y" }));
            Assert.Equal("'welcome' is not a template's id.", await Refused(BrevoConnector.SendEmailOperation, new() { ["to"] = "ada@example.com", ["template_id"] = "welcome" }));
            Assert.Equal("'-12' is not a template's id.", await Refused(BrevoConnector.SendEmailOperation, new() { ["to"] = "ada@example.com", ["template_id"] = "-12" }));
            Assert.Equal("'ORDER' is not a parameter - ORDER=1001.", await Refused(BrevoConnector.SendEmailOperation, new() { ["to"] = "ada@example.com", ["template_id"] = "12", ["params"] = "ORDER" }));
            Assert.Equal("'secret' is not an attribute category - normal, transactional, category, calculated or global.", await Refused(BrevoConnector.CreateAttributeOperation, new() { ["category"] = "secret", ["name"] = "X" }));
            Assert.Equal("'my attr' is not an attribute's name - LOYALTY_TIER.", await Refused(BrevoConnector.CreateAttributeOperation, new() { ["name"] = "my attr" }));
            Assert.Equal("'number' is not an attribute type - text, date, float or boolean.", await Refused(BrevoConnector.CreateAttributeOperation, new() { ["name"] = "X", ["type"] = "number" }));
            Assert.Equal("Give a global attribute's value - its formula.", await Refused(BrevoConnector.CreateAttributeOperation, new() { ["category"] = "global", ["name"] = "X" }));
            Assert.Equal("'Gold' is not an option - 1:Gold.", await Refused(BrevoConnector.CreateAttributeOperation, new() { ["category"] = "category", ["name"] = "TIER", ["options"] = "Gold" }));
            Assert.Equal("'x:Gold' is not an option - 1:Gold.", await Refused(BrevoConnector.CreateAttributeOperation, new() { ["category"] = "category", ["name"] = "TIER", ["options"] = "x:Gold" }));
            Assert.Equal("A normal attribute cannot be changed - only a calculated, global or category one.", await Refused(BrevoConnector.UpdateAttributeOperation, new() { ["category"] = "normal", ["name"] = "X" }));
            Assert.Equal("'shop' is not a sender's id.", await Refused(BrevoConnector.DeleteSenderOperation, new() { ["sender_id"] = "shop" }));
            Assert.Equal("'newest' is not an order - desc or asc.", await Refused(BrevoConnector.ContactsOperation, new() { ["sort"] = "newest" }));
            Assert.Equal("'yesterday' is not a time (changed since) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(BrevoConnector.ContactsOperation, new() { ["modified_since"] = "yesterday" }));
            Assert.StartsWith("Brevo has no operation 'campaigns' - contacts, get_contact,", await Refused("campaigns", new()));
            Assert.Empty(brevo.Requests); // nothing sent

            var refusing = new Brevo { Answer = (_, _) => (HttpStatusCode.BadRequest, """{"code":"duplicate_parameter","message":"Contact already exist"}""") };
            Assert.Equal("HTTP 400 duplicate_parameter: Contact already exist", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new BrevoConnector(), Context(refusing, BrevoConnector.CreateContactOperation, new() { ["email"] = "ada@example.com" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_account()
        {
            var brevo = new Brevo { Answer = (_, _) => (HttpStatusCode.OK, """{"email":"owner@example.com","companyName":"Café Shop AB"}""") };
            var tested = await new BrevoConnector().TestConnectionAsync(Contexts.Test(new StubEgress(brevo), Api, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((true, "Signed in - Café Shop AB."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/account", "xkeysib-1"), (brevo.Requests[0].Url, brevo.Requests[0].Key));
            var keyless = await new BrevoConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Brevo()), Api, settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>()));
            Assert.Equal((false, "The Brevo connection has no API key."), (keyless.Success, keyless.Message));
            Assert.Equal((AuthenticationType.ApiKey, "brevo", ConnectorCategory.Marketing), (new BrevoConnector().AuthType, new BrevoConnector().ConnectorType, Assert.Single(new BrevoConnector().Categories)));
            Assert.Equal(15, new BrevoConnector().Operations.Count);
        }
    }
}
