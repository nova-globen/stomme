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
using Stomme.Modules.Connectors.Mailchimp;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Mailchimp (n8n's Mailchimp node: an audience's members and the account's campaigns) over the Marketing API: the
    /// key by Mailchimp's Basic pair; an admin page's address taken to its data center's API and a key of another data center
    /// refused; an audience's members by status and change, page by page by offset up to the most asked for, each a plain record;
    /// a member got, added, updated, added-or-updated, archived, deleted for good and tagged - named by the MD5 of the address in
    /// lower case, or by that hash; the audiences; campaigns by status, type, audience and time, sorted; a campaign got, sent,
    /// replicated, resent and deleted; what is not an id, an address, a status, a time, merge fields or a flag refused before
    /// anything is sent; Mailchimp's refusal its title, detail and first field error; a test naming the account.
    /// </summary>
    public sealed class MailchimpConnectorTests
    {
        private const string Api = "https://us21.api.mailchimp.com/3.0";

        // An invented key, built at run time: written out, its shape is a Mailchimp key's and secret scanners (GitHub's push
        // protection among them) refuse the file.
        private static string FakeKey(string dataCenter) => string.Concat(Enumerable.Repeat("0123456789abcdef", 2)) + "-" + dataCenter;

        private static readonly string Key = FakeKey("us21");

        private sealed class MailchimpApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(MailchimpApi mailchimp, string operation, Dictionary<string, string> parameters, string endpoint = "https://us21.api.mailchimp.com", string? key = null) =>
            Contexts.Extract(new StubEgress(mailchimp), endpoint, operation, parameters, null, new Dictionary<string, string> { ["api-key"] = key ?? Key });

        private static MailchimpConnector Connector() => new();

        private static string Hash(string email) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(email))).ToLowerInvariant();

        private static string Members(int from, int count, int total) =>
            "{\"members\":[" + string.Join(",", Enumerable.Range(from, count).Select(id => "{\"id\":\"" + Hash("user" + id + "@example.com") + "\",\"email_address\":\"user" + id + "@example.com\",\"status\":\"subscribed\",\"full_name\":\"User " + id
                + "\",\"merge_fields\":{\"FNAME\":\"User\"},\"tags\":[{\"id\":1,\"name\":\"vip\"},{\"id\":2,\"name\":\"web\"}],\"vip\":true,\"language\":\"en\",\"email_type\":\"html\",\"member_rating\":4,\"list_id\":\"a1b2c3d4e5\",\"web_id\":" + id
                + ",\"timestamp_signup\":\"2026-10-01T10:00:00+00:00\",\"last_changed\":\"2026-10-02T10:00:00+00:00\"}")) + "],\"total_items\":" + total + "}";

        [Fact]
        public async Task An_audiences_members_are_read_page_by_page_by_offset_up_to_the_most_asked_for_the_key_by_basic()
        {
            var mailchimp = new MailchimpApi
            {
                Answer = (_, url) => url.Contains("offset=0", StringComparison.Ordinal) ? (HttpStatusCode.OK, Members(1, 1000, 5000)) : (HttpStatusCode.OK, Members(1001, 500, 5000)),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.MembersOperation, new()
            {
                ["list"] = "a1b2c3d4e5", ["status"] = "Subscribed", ["since_last_changed"] = "2026-10-01T02:00:00+02:00", ["max"] = "1500",
            }, endpoint: "https://us21.admin.mailchimp.com/lists/members/?id=1"));

            Assert.Equal(1500, records.Count); // the most asked for
            Assert.Equal(2, mailchimp.Requests.Count);
            Assert.Equal(Api + "/lists/a1b2c3d4e5/members?status=subscribed&since_last_changed=2026-10-01T00%3A00%3A00%2B00%3A00&count=1000&offset=0", mailchimp.Requests[0].Url); // an admin page's address: its data center's API
            Assert.EndsWith("&count=500&offset=1000", mailchimp.Requests[1].Url, StringComparison.Ordinal); // a page no bigger than what is left
            Assert.All(mailchimp.Requests, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("stomme:" + Key)), request.Authorization));
            var member = records[0].Fields;
            Assert.Equal((Hash("user1@example.com"), "user1@example.com", "subscribed", "User 1", "{\"FNAME\":\"User\"}", "vip, web", true, 4L, "a1b2c3d4e5"),
                ((string?)member["id"], (string?)member["email"], (string?)member["status"], (string?)member["full_name"], (string?)member["merge_fields"], (string?)member["tags"], (bool?)member["vip"], (long?)member["member_rating"], (string?)member["list_id"]));

            var total = new MailchimpApi { Answer = (_, _) => (HttpStatusCode.OK, Members(1, 2, 2)) };
            Assert.Equal(2, (await Contexts.DrainAsync(Connector(), Context(total, MailchimpConnector.MembersOperation, new() { ["list"] = "a1b2c3d4e5" }, endpoint: "http://127.0.0.1:5280"))).Count);
            Assert.Equal("http://127.0.0.1:5280/3.0/lists/a1b2c3d4e5/members?count=1000&offset=0", Assert.Single(total.Requests).Url); // a stand-in's address as given; a short page the last

            var full = new MailchimpApi { Answer = (_, _) => (HttpStatusCode.OK, Members(1, 1000, 1000)) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(full, MailchimpConnector.MembersOperation, new() { ["list"] = "a1b2c3d4e5", ["max"] = "2000" }))).Count);
            Assert.Single(full.Requests); // a full page that is Mailchimp's total: no other asked for
        }

        [Fact]
        public async Task A_member_is_got_added_updated_upserted_archived_deleted_and_tagged_by_the_hash_of_the_address()
        {
            var mailchimp = new MailchimpApi { Answer = (method, url) => url.EndsWith("/tags", StringComparison.Ordinal) || method == HttpMethod.Delete || url.EndsWith("delete-permanent", StringComparison.Ordinal) ? (HttpStatusCode.NoContent, "") : (HttpStatusCode.OK, "{\"id\":\"" + Hash("ada@example.com") + "\",\"email_address\":\"ada@example.com\",\"status\":\"subscribed\"}") };
            var ada = Api + "/lists/a1b2c3d4e5/members/" + Hash("ada@example.com");

            var got = Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.GetMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "Ada@Example.com" })));
            Assert.Equal((HttpMethod.Get, ada), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url)); // the MD5 of the address in lower case
            Assert.Equal("ada@example.com", got.Fields["email"]);
            await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.GetMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = Hash("ada@example.com") }));
            Assert.Equal(ada, mailchimp.Requests[^1].Url); // a hash as given

            await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.AddMemberOperation, new()
            {
                ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["status"] = "pending", ["merge_fields"] = "{\"FNAME\": \"Ada\", \"ADDRESS\": {\"city\": \"Lund\"}}", ["tags"] = "vip, web", ["language"] = "en", ["vip"] = "TRUE", ["email_type"] = "text",
            }));
            Assert.Equal((HttpMethod.Post, Api + "/lists/a1b2c3d4e5/members", "{\"email_address\":\"ada@example.com\",\"status\":\"pending\",\"merge_fields\":{\"FNAME\":\"Ada\",\"ADDRESS\":{\"city\":\"Lund\"}},\"language\":\"en\",\"vip\":true,\"email_type\":\"text\",\"tags\":[\"vip\",\"web\"]}"),
                (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url, mailchimp.Requests[^1].Body));

            await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.UpdateMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["status"] = "unsubscribed", ["vip"] = "false" }));
            Assert.Equal((HttpMethod.Patch, ada, "{\"status\":\"unsubscribed\",\"vip\":false}"), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url, mailchimp.Requests[^1].Body));

            await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.UpsertMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "Ada@example.com", ["status_if_new"] = "subscribed", ["merge_fields"] = "{\"FNAME\": \"Ada\"}" }));
            Assert.Equal((HttpMethod.Put, ada, "{\"email_address\":\"Ada@example.com\",\"status_if_new\":\"subscribed\",\"merge_fields\":{\"FNAME\":\"Ada\"}}"), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url, mailchimp.Requests[^1].Body));

            var archived = Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.DeleteMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com" })));
            Assert.Equal((HttpMethod.Delete, ada), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url));
            Assert.Equal(("ada@example.com", true), ((string?)archived.Fields["email"], (bool?)archived.Fields["archived"]));
            var deleted = Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.DeleteMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["permanently"] = "true" })));
            Assert.Equal((HttpMethod.Post, ada + "/actions/delete-permanent"), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url));
            Assert.Equal(true, deleted.Fields["deleted"]);

            var tagged = Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.TagMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["add"] = "vip, 2026", ["remove"] = "trial" })));
            Assert.Equal((HttpMethod.Post, ada + "/tags", "{\"tags\":[{\"name\":\"vip\",\"status\":\"active\"},{\"name\":\"2026\",\"status\":\"active\"},{\"name\":\"trial\",\"status\":\"inactive\"}]}"),
                (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url, mailchimp.Requests[^1].Body));
            Assert.Equal(("vip, 2026", "trial"), ((string?)tagged.Fields["added"], (string?)tagged.Fields["removed"]));
        }

        [Fact]
        public async Task Audiences_and_campaigns_are_read_and_a_campaign_got_sent_replicated_resent_and_deleted()
        {
            var campaign = "{\"id\":\"c0ffee1234\",\"web_id\":77,\"type\":\"regular\",\"status\":\"sent\",\"emails_sent\":1200,\"create_time\":\"2026-10-01T10:00:00+00:00\",\"send_time\":\"2026-10-02T10:00:00+00:00\",\"archive_url\":\"https://eepurl.com/x\","
                + "\"settings\":{\"title\":\"October news\",\"subject_line\":\"What's new\",\"from_name\":\"Acme\"},\"recipients\":{\"list_id\":\"a1b2c3d4e5\",\"list_name\":\"Customers\"}}";
            var mailchimp = new MailchimpApi
            {
                Answer = (method, url) => url.Contains("/lists?", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"lists\":[{\"id\":\"a1b2c3d4e5\",\"web_id\":5,\"name\":\"Customers\",\"stats\":{\"member_count\":1200,\"unsubscribe_count\":7,\"cleaned_count\":2},\"date_created\":\"2026-01-01T00:00:00+00:00\"}],\"total_items\":1}")
                    : url.Contains("/campaigns?", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"campaigns\":[" + campaign + "],\"total_items\":1}")
                    : url.EndsWith("/actions/send", StringComparison.Ordinal) || method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "")
                    : (HttpStatusCode.OK, campaign),
            };

            var audience = Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.ListsOperation, new())));
            Assert.Equal(Api + "/lists?count=1000&offset=0", mailchimp.Requests[^1].Url);
            Assert.Equal(("a1b2c3d4e5", "Customers", 1200L, 7L), ((string?)audience.Fields["id"], (string?)audience.Fields["name"], (long?)audience.Fields["member_count"], (long?)audience.Fields["unsubscribe_count"]));

            var found = Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.CampaignsOperation, new()
            {
                ["status"] = "sent", ["type"] = "regular", ["sort_field"] = "send_time", ["sort_dir"] = "desc", ["list"] = "a1b2c3d4e5", ["since_send_time"] = "2026-10-01", ["max"] = "25",
            })));
            Assert.Equal(Api + "/campaigns?status=sent&type=regular&sort_field=send_time&sort_dir=DESC&list_id=a1b2c3d4e5&since_send_time=2026-10-01T00%3A00%3A00%2B00%3A00&count=25&offset=0", mailchimp.Requests[^1].Url);
            Assert.Equal(("c0ffee1234", "October news", "What's new", "a1b2c3d4e5", "sent", 1200L), ((string?)found.Fields["id"], (string?)found.Fields["title"], (string?)found.Fields["subject_line"], (string?)found.Fields["list_id"], (string?)found.Fields["status"], (long?)found.Fields["emails_sent"]));

            Assert.Equal("October news", Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.GetCampaignOperation, new() { ["campaign"] = "c0ffee1234" }))).Fields["title"]);
            Assert.Equal((HttpMethod.Get, Api + "/campaigns/c0ffee1234"), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url));
            Assert.Equal(true, Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.SendCampaignOperation, new() { ["campaign"] = "c0ffee1234" }))).Fields["sent"]);
            Assert.Equal((HttpMethod.Post, Api + "/campaigns/c0ffee1234/actions/send"), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url));
            await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.ReplicateCampaignOperation, new() { ["campaign"] = "c0ffee1234" }));
            Assert.Equal((HttpMethod.Post, Api + "/campaigns/c0ffee1234/actions/replicate"), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url));
            await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.ResendCampaignOperation, new() { ["campaign"] = "c0ffee1234", ["to"] = "new_subscribers" }));
            Assert.Equal((HttpMethod.Post, Api + "/campaigns/c0ffee1234/actions/create-resend", "{\"shortcut_type\":\"to_new_subscribers\"}"), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url, mailchimp.Requests[^1].Body));
            await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.ResendCampaignOperation, new() { ["campaign"] = "c0ffee1234" }));
            Assert.Null(mailchimp.Requests[^1].Body); // Mailchimp's default: the non-openers
            Assert.Equal(true, Assert.Single(await Contexts.DrainAsync(Connector(), Context(mailchimp, MailchimpConnector.DeleteCampaignOperation, new() { ["campaign"] = "c0ffee1234" }))).Fields["deleted"]);
            Assert.Equal((HttpMethod.Delete, Api + "/campaigns/c0ffee1234"), (mailchimp.Requests[^1].Method, mailchimp.Requests[^1].Url));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_mailchimps_refusal_is_its_words()
        {
            var mailchimp = new MailchimpApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "https://us21.api.mailchimp.com", string? key = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(mailchimp, operation, parameters, endpoint, key)))).Message;

            Assert.Equal("'a1/../x' is not a Mailchimp audience's id (letters and digits).", await Refused(MailchimpConnector.MembersOperation, new() { ["list"] = "a1/../x" }));
            Assert.Equal("'ada' is not an address.", await Refused(MailchimpConnector.GetMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada" }));
            Assert.StartsWith("'gone' is not a member's status", await Refused(MailchimpConnector.MembersOperation, new() { ["list"] = "a1b2c3d4e5", ["status"] = "gone" }));
            Assert.StartsWith("'archived' is not a member's status", await Refused(MailchimpConnector.AddMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["status"] = "archived" }));
            Assert.Equal("Give the member's status - subscribed, or pending for a double opt-in.", await Refused(MailchimpConnector.AddMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com" }));
            Assert.Equal("'yesterday' is not a time - 2026-10-01T00:00:00Z.", await Refused(MailchimpConnector.MembersOperation, new() { ["list"] = "a1b2c3d4e5", ["since_last_changed"] = "yesterday" }));
            Assert.Equal("The merge fields are not JSON - {\"FNAME\": \"Ada\"}.", await Refused(MailchimpConnector.AddMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["status"] = "subscribed", ["merge_fields"] = "FNAME=Ada" }));
            Assert.Equal("The merge fields are a JSON object of the audience's merge tags - {\"FNAME\": \"Ada\"}.", await Refused(MailchimpConnector.UpdateMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["merge_fields"] = "[\"Ada\"]" }));
            Assert.Equal("The merge fields are a JSON object of the audience's merge tags - {\"FNAME\": \"Ada\"}.", await Refused(MailchimpConnector.UpdateMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["merge_fields"] = "{}" }));
            Assert.Equal("'FIRST NAME' is not a merge tag (letters, digits and _) - FNAME.", await Refused(MailchimpConnector.UpdateMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["merge_fields"] = "{\"FIRST NAME\": \"Ada\"}" }));
            Assert.Equal("'maybe' is not true or false (vip).", await Refused(MailchimpConnector.UpdateMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["vip"] = "maybe" }));
            Assert.StartsWith("'pdf' is not a kind of email", await Refused(MailchimpConnector.UpdateMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["email_type"] = "pdf" }));
            Assert.Equal("Give something to change - a status, merge fields, a language, VIP or an email type.", await Refused(MailchimpConnector.UpdateMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com" }));
            Assert.Equal("Give the member's address - adding one needs it, not its hash.", await Refused(MailchimpConnector.UpsertMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = Hash("ada@example.com"), ["status_if_new"] = "subscribed" }));
            Assert.Equal("Give the status a new member gets - subscribed, or pending for a double opt-in.", await Refused(MailchimpConnector.UpsertMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com" }));
            Assert.Equal("'yes' is not true or false (permanently).", await Refused(MailchimpConnector.DeleteMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["permanently"] = "yes" }));
            Assert.Equal("Give a tag to add or to remove.", await Refused(MailchimpConnector.TagMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com" }));
            Assert.Equal("The tag has a line break - one line only.", await Refused(MailchimpConnector.TagMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["add"] = "vip\nx" }));
            Assert.StartsWith("'draft' is not a campaign's status", await Refused(MailchimpConnector.CampaignsOperation, new() { ["status"] = "draft" }));
            Assert.StartsWith("'up' is not a sort order", await Refused(MailchimpConnector.CampaignsOperation, new() { ["sort_dir"] = "up" }));
            Assert.StartsWith("'everyone' is not a group to resend to", await Refused(MailchimpConnector.ResendCampaignOperation, new() { ["campaign"] = "c0ffee1234", ["to"] = "everyone" }));
            Assert.Equal("'c0ffee 1234' is not a Mailchimp campaign's id (letters and digits).", await Refused(MailchimpConnector.SendCampaignOperation, new() { ["campaign"] = "c0ffee 1234" }));
            Assert.Equal("The API key is for the us6 data center and the address for us21 - use https://us6.api.mailchimp.com.", await Refused(MailchimpConnector.ListsOperation, new(), key: FakeKey("us6")));
            Assert.Equal("'https://mailchimp.com/' names no data center - use its API's address, https://us21.api.mailchimp.com.", await Refused(MailchimpConnector.ListsOperation, new(), endpoint: "https://mailchimp.com/", key: "nodatacenter"));
            Assert.Equal("'ftp://x.example' is not a Mailchimp API address - https://us21.api.mailchimp.com.", await Refused(MailchimpConnector.ListsOperation, new(), endpoint: "ftp://x.example"));
            Assert.Empty(mailchimp.Requests);

            var keyed = new MailchimpApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"lists\":[],\"total_items\":0}") };
            await Contexts.DrainAsync(Connector(), Context(keyed, MailchimpConnector.ListsOperation, new(), endpoint: "https://login.mailchimp.com/"));
            Assert.StartsWith(Api + "/lists?", keyed.Requests[0].Url, StringComparison.Ordinal); // another Mailchimp page: the key's data center

            var refusing = new MailchimpApi { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"type\":\"https://mailchimp.com/developer/marketing/docs/errors/\",\"title\":\"Invalid Resource\",\"status\":400,\"detail\":\"Your merge fields were invalid.\",\"errors\":[{\"field\":\"FNAME\",\"message\":\"Please enter a value\"}]}") };
            Assert.Equal("HTTP 400: Invalid Resource - Your merge fields were invalid. - FNAME: Please enter a value",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, MailchimpConnector.AddMemberOperation, new() { ["list"] = "a1b2c3d4e5", ["email"] = "ada@example.com", ["status"] = "subscribed" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_account()
        {
            var mailchimp = new MailchimpApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"account_id\":\"x1\",\"account_name\":\"Acme Shop\",\"email\":\"ada@acme.example\"}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(mailchimp), "https://us21.api.mailchimp.com", credentials: new Dictionary<string, string> { ["api-key"] = Key }));
            Assert.Equal((true, "Reached the account Acme Shop (ada@acme.example)."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/", mailchimp.Requests[0].Url);
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new MailchimpApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"title\":\"API Key Invalid\",\"status\":401,\"detail\":\"Your API key may be invalid, or you've attempted to access the wrong datacenter.\"}") }),
                "https://us21.api.mailchimp.com", credentials: new Dictionary<string, string> { ["api-key"] = Key }));
            Assert.Equal((false, "HTTP 401: API Key Invalid - Your API key may be invalid, or you've attempted to access the wrong datacenter."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "mailchimp", ConnectorCategory.Marketing), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "members", "get_member", "add_member", "update_member", "upsert_member", "delete_member", "tag_member", "lists", "campaigns", "get_campaign", "send_campaign", "replicate_campaign", "resend_campaign", "delete_campaign" },
                Connector().Operations.Select(operation => operation.Name));
        }
    }
}
