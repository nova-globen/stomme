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
using Stomme.Modules.Connectors.Mautic;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Mautic (n8n's Mautic node: contacts, companies, segments, campaigns and emails): every call an instance's API
    /// with the user's name and password; contacts read searched, ordered and published only, page by page by Mautic's total -
    /// never ended by a short page, which a Mautic capping its pages answers - as their fields or raw; a contact got,
    /// created with its fields, tags, owner, stage, address and last activity, updated, deleted, its points changed, its
    /// do-not-contact set and lifted, an email sent; companies read, got, created, updated and deleted, a contact added and
    /// removed; segments, campaigns and emails read, a contact added to and removed from each, a segment email sent; what could
    /// go wrong refused before anything is sent; Mautic's word on a failure, its error answered with HTTP 200 too; a test
    /// naming whose the credentials are.
    /// </summary>
    public sealed class MauticConnectorTests
    {
        private const string Site = "https://mautic.example.test";

        private const string Api = Site + "/api";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe-api", ["password"] = "mautic-pass-1" };

        private sealed class Mautic : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"success\":1}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Mautic mautic, string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new MauticConnector(), Contexts.Extract(new StubEgress(mautic), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Contact(int id) =>
            "{\"id\":" + id + ",\"isPublished\":true,\"points\":" + id + ",\"tags\":[{\"tag\":\"fika\"}],\"fields\":{\"core\":{},\"all\":{\"id\":" + id + ",\"firstname\":\"Ada " + id + "\",\"email\":\"ada" + id + "@kafe.example.test\",\"city\":\"Malmö\"}}}";

        private static string Contacts(int first, int count, string total) =>
            "{\"total\":" + total + ",\"contacts\":{" + string.Join(",", Enumerable.Range(first, count).Select(i => "\"" + i + "\":" + Contact(i))) + "}}";

        [Fact]
        public async Task Contacts_are_read_page_by_page_got_created_updated_deleted_and_kept()
        {
            var mautic = new Mautic
            {
                Answer = (method, url) => (HttpStatusCode.OK, (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("start=200", StringComparison.Ordinal) => Contacts(201, 50, "\"250\""),
                    ("GET", var u) when u.Contains("start=100", StringComparison.Ordinal) => Contacts(101, 100, "\"250\""),
                    ("GET", var u) when u.Contains("/contacts?", StringComparison.Ordinal) => Contacts(1, 100, "\"250\""),
                    ("GET", _) or ("PATCH", _) or ("DELETE", _) => "{\"contact\":" + Contact(47) + "}",
                    ("POST", var u) when u.EndsWith("/contacts/new", StringComparison.Ordinal) => "{\"contact\":" + Contact(47) + "}",
                    ("POST", var u) when u.Contains("/dnc/", StringComparison.Ordinal) => "{\"recordFound\":true,\"contact\":" + Contact(47) + "}",
                    _ => "{\"success\":1}",
                }),
            };
            (string, string, string?) Last() => (mautic.Requests[^1].Method.Method, mautic.Requests[^1].Url, mautic.Requests[^1].Body);

            var read = await Run(mautic, MauticConnector.ContactsOperation, new() { ["search"] = "city:Malmö & fika", ["order_by"] = "last_active", ["order_dir"] = "Desc", ["published_only"] = "TRUE", ["max"] = "120" });
            Assert.Equal(120, read.Count); // no more than asked
            const string Query = "search=city%3AMalm%C3%B6%20%26%20fika&orderBy=last_active&orderByDir=DESC&publishedOnly=true";
            Assert.Equal(new[] { $"{Api}/contacts?{Query}&start=0&limit=100", $"{Api}/contacts?{Query}&start=100&limit=100" }, mautic.Requests.Select(request => request.Url)); // by Mautic's offset
            Assert.All(mautic.Requests, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("kafe-api:mautic-pass-1")), request.Authorization));
            Assert.Equal((1L, "Ada 1", "ada1@kafe.example.test", "Malmö"), ((long?)read[0].Fields["id"], (string?)read[0].Fields["firstname"], (string?)read[0].Fields["email"], (string?)read[0].Fields["city"])); // its fields
            Assert.Equal(120L, (long?)read[119].Fields["id"]);
            mautic.Requests.Clear();
            Assert.Equal(250, (await Run(mautic, MauticConnector.ContactsOperation, new())).Count); // every one, Mautic's total the end
            Assert.Equal(3, mautic.Requests.Count);
            var whole = (await Run(mautic, MauticConnector.ContactsOperation, new() { ["raw"] = "true", ["max"] = "1" }))[0].Fields;
            Assert.Equal((1L, true, 1L, "[{\"tag\":\"fika\"}]"), ((long?)whole["id"], (bool?)whole["isPublished"], (long?)whole["points"], (string?)whole["tags"])); // raw, the whole of it
            Assert.StartsWith("{\"core\":{},\"all\":{\"id\":1,\"firstname\":\"Ada 1\"", (string?)whole["fields"]);
            Assert.Equal($"{Api}/contacts?start=0&limit=1", Last().Item2); // a page as small as asked

            // Never ended by a short page: a Mautic capping its pages at 30 is read to its total.
            mautic.Requests.Clear();
            mautic.Answer = (_, url) => (HttpStatusCode.OK, url.Contains("start=60", StringComparison.Ordinal) ? Contacts(61, 10, "70") : url.Contains("start=30", StringComparison.Ordinal) ? Contacts(31, 30, "70") : Contacts(1, 30, "70"));
            Assert.Equal(70, (await Run(mautic, MauticConnector.ContactsOperation, new())).Count);
            Assert.Equal(new[] { $"{Api}/contacts?start=0&limit=100", $"{Api}/contacts?start=30&limit=100", $"{Api}/contacts?start=60&limit=100" }, mautic.Requests.Select(request => request.Url));
            mautic.Requests.Clear();
            mautic.Answer = (_, _) => (HttpStatusCode.OK, "{\"total\":\"0\",\"contacts\":[]}");
            Assert.Empty(await Run(mautic, MauticConnector.ContactsOperation, new())); // none, a list when empty
            Assert.Single(mautic.Requests);
            mautic.Requests.Clear();
            mautic.Answer = (_, _) => (HttpStatusCode.OK, Contacts(1, 100, "\"x\""));
            Assert.Equal(100, (await Run(mautic, MauticConnector.ContactsOperation, new())).Count); // no total, no next page
            Assert.Single(mautic.Requests);
            mautic.Requests.Clear();
            mautic.Answer = (_, _) => (HttpStatusCode.OK, "{\"total\":5,\"contacts\":[]}");
            Assert.Empty(await Run(mautic, MauticConnector.ContactsOperation, new()));
            Assert.Single(mautic.Requests); // an empty page the end, though Mautic counts more
            Assert.Empty(await Run(mautic, null!, new()));
            Assert.Equal($"{Api}/contacts?start=0&limit=100", mautic.Requests[^1].Url); // no operation: the contacts

            mautic.Answer = (method, url) => (HttpStatusCode.OK, (method.Method, url) switch
            {
                ("POST", var u) when u.Contains("/points/", StringComparison.Ordinal) || u.EndsWith("/send", StringComparison.Ordinal) => "{\"success\":true}",
                ("POST", var u) when u.Contains("/dnc/", StringComparison.Ordinal) => "{\"recordFound\":true,\"contact\":" + Contact(47) + "}",
                _ => "{\"contact\":" + Contact(47) + "}",
            });
            var got = Assert.Single(await Run(mautic, MauticConnector.ContactOperation, new() { ["contact"] = "47" }, endpoint: Api + "/")).Fields;
            Assert.Equal(("GET", $"{Api}/contacts/47", (string?)null), Last()); // /api given once
            Assert.Equal((47L, "Ada 47"), ((long?)got["id"], (string?)got["firstname"]));

            await Run(mautic, MauticConnector.CreateContactOperation, new()
            {
                ["email"] = "ada@kafe.example.test", ["firstname"] = "Ada", ["lastname"] = "Lovelace", ["company"] = "Kafé & bröd", ["position"] = "Barista", ["title"] = "Dr",
                ["phone"] = "+46 40 123", ["mobile"] = "+46 70 123", ["tags"] = "fika, -old", ["owner"] = "1", ["stage"] = "2", ["ip_address"] = "2001:db8::1",
                ["last_active"] = "2026-10-13T12:00:00+02:00", ["fields"] = "{\"city\": \"Malmö\", \"interests\": [\"fika\"], \"visits\": 3, \"note\": null}", ["overwrite_with_blank"] = "True",
            });
            Assert.Equal(("POST", $"{Api}/contacts/new", (string?)"{\"city\":\"Malmö\",\"interests\":[\"fika\"],\"visits\":3,\"note\":null,\"email\":\"ada@kafe.example.test\",\"firstname\":\"Ada\",\"lastname\":\"Lovelace\",\"company\":\"Kafé & bröd\",\"position\":\"Barista\",\"title\":\"Dr\",\"phone\":\"+46 40 123\",\"mobile\":\"+46 70 123\",\"tags\":[\"fika\",\"-old\"],\"owner\":1,\"stage\":2,\"ipAddress\":\"2001:db8::1\",\"lastActive\":\"2026-10-13 10:00:00\",\"overwriteWithBlank\":true}"), Last()); // a time in UTC as Mautic reads it
            await Run(mautic, MauticConnector.CreateContactOperation, new() { ["firstname"] = "Bo", ["ip_address"] = "192.0.2.10" });
            Assert.Equal(("POST", $"{Api}/contacts/new", (string?)"{\"firstname\":\"Bo\",\"ipAddress\":\"192.0.2.10\"}"), Last());
            await Run(mautic, MauticConnector.UpdateContactOperation, new() { ["contact"] = "47", ["email"] = "ada@kafe.example.test", ["overwrite_with_blank"] = "false" });
            Assert.Equal(("PATCH", $"{Api}/contacts/47/edit", (string?)"{\"email\":\"ada@kafe.example.test\",\"overwriteWithBlank\":false}"), Last());
            Assert.Equal(47L, (long?)Assert.Single(await Run(mautic, MauticConnector.DeleteContactOperation, new() { ["contact"] = "47" })).Fields["id"]);
            Assert.Equal(("DELETE", $"{Api}/contacts/47/delete", (string?)null), Last());

            var added = Assert.Single(await Run(mautic, MauticConnector.PointsOperation, new() { ["contact"] = "47", ["points"] = "5" })).Fields;
            Assert.Equal(("POST", $"{Api}/contacts/47/points/plus/5", (string?)null), Last());
            Assert.Equal(("47", 5L), ((string?)added["contact"], (long?)added["points"]));
            Assert.Equal(-3L, (long?)Assert.Single(await Run(mautic, MauticConnector.PointsOperation, new() { ["contact"] = "47", ["points"] = "-3" })).Fields["points"]); // taken
            Assert.Equal($"{Api}/contacts/47/points/minus/3", Last().Item2);

            Assert.Equal(47L, (long?)Assert.Single(await Run(mautic, MauticConnector.AddDoNotContactOperation, new() { ["contact"] = "47", ["channel"] = "SMS", ["reason"] = "Bounced", ["comments"] = "Studsade två gånger" })).Fields["id"]);
            Assert.Equal(("POST", $"{Api}/contacts/47/dnc/sms/add", (string?)"{\"reason\":2,\"comments\":\"Studsade två gånger\"}"), Last());
            await Run(mautic, MauticConnector.AddDoNotContactOperation, new() { ["contact"] = "47" });
            Assert.Equal(("POST", $"{Api}/contacts/47/dnc/email/add", (string?)"{\"reason\":3}"), Last()); // email and manual its defaults
            await Run(mautic, MauticConnector.RemoveDoNotContactOperation, new() { ["contact"] = "47", ["channel"] = "email" });
            Assert.Equal(("POST", $"{Api}/contacts/47/dnc/email/remove", (string?)null), Last());

            var sent = Assert.Single(await Run(mautic, MauticConnector.SendEmailOperation, new() { ["contact"] = "47", ["email_id"] = "3" })).Fields;
            Assert.Equal(("POST", $"{Api}/emails/3/contact/47/send", (string?)null), Last());
            Assert.Equal(("47", "3", true), ((string?)sent["contact"], (string?)sent["email_id"], (bool?)sent["sent"]));
        }

        [Fact]
        public async Task Companies_segments_campaigns_and_emails_are_read_and_kept()
        {
            var mautic = new Mautic
            {
                Answer = (method, url) => (HttpStatusCode.OK, (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/companies?", StringComparison.Ordinal) => "{\"total\":2,\"companies\":{\"5\":{\"id\":5,\"fields\":{\"all\":{\"id\":5,\"companyname\":\"Kafé AB\"}}},\"6\":{\"id\":6,\"fields\":{\"all\":{\"id\":6,\"companyname\":\"Bageri AB\"}}}}}",
                    ("GET", var u) when u.Contains("/segments?", StringComparison.Ordinal) => "{\"total\":1,\"lists\":{\"2\":{\"id\":2,\"name\":\"Fika\",\"alias\":\"fika\",\"isPublished\":true,\"filters\":[]}}}",
                    ("GET", var u) when u.Contains("/campaigns?", StringComparison.Ordinal) => "{\"total\":1,\"campaigns\":[{\"id\":4,\"name\":\"Välkommen\"}]}", // a list as a list

                    ("GET", var u) when u.Contains("/emails?", StringComparison.Ordinal) => "{\"total\":1,\"emails\":{\"3\":{\"id\":3,\"name\":\"Welcome\",\"emailType\":\"template\"}}}",
                    ("POST", var u) when u.EndsWith("/emails/9/send", StringComparison.Ordinal) => "{\"success\":1,\"sentCount\":120,\"failedRecipients\":2}",
                    ("POST", var u) when u.Contains("/contact/", StringComparison.Ordinal) => "{\"success\":true}",
                    _ => "{\"company\":{\"id\":5,\"isPublished\":true,\"fields\":{\"all\":{\"id\":5,\"companyname\":\"Kafé AB\",\"companycity\":\"Malmö\"}}}}",
                }),
            };
            (string, string, string?) Last() => (mautic.Requests[^1].Method.Method, mautic.Requests[^1].Url, mautic.Requests[^1].Body);

            var companies = await Run(mautic, MauticConnector.CompaniesOperation, new() { ["search"] = "Kafé", ["order_by"] = "companyname", ["order_dir"] = "asc" });
            Assert.Equal(($"{Api}/companies?search=Kaf%C3%A9&orderBy=companyname&orderByDir=ASC&start=0&limit=100"), Last().Item2);
            Assert.Equal(new[] { (5L, "Kafé AB"), (6L, "Bageri AB") }, companies.Select(company => ((long)company.Fields["id"]!, (string)company.Fields["companyname"]!))); // their fields; the total the end
            Assert.Single(mautic.Requests);
            var raw = Assert.Single(await Run(mautic, MauticConnector.CompanyOperation, new() { ["company"] = "5", ["raw"] = "true" })).Fields;
            Assert.Equal(("GET", $"{Api}/companies/5", (string?)null), Last());
            Assert.Equal((5L, true), ((long?)raw["id"], (bool?)raw["isPublished"]));
            Assert.Equal("Malmö", (string?)Assert.Single(await Run(mautic, MauticConnector.CreateCompanyOperation, new() { ["name"] = "Kafé AB", ["fields"] = "{\"companyemail\": \"info@kafe.example.test\"}", ["overwrite_with_blank"] = "true" })).Fields["companycity"]);
            Assert.Equal(("POST", $"{Api}/companies/new", (string?)"{\"companyemail\":\"info@kafe.example.test\",\"companyname\":\"Kafé AB\",\"overwriteWithBlank\":true}"), Last());
            await Run(mautic, MauticConnector.UpdateCompanyOperation, new() { ["company"] = "5", ["fields"] = "{\"companycity\": \"Lund\"}" });
            Assert.Equal(("PATCH", $"{Api}/companies/5/edit", (string?)"{\"companycity\":\"Lund\"}"), Last());
            await Run(mautic, MauticConnector.UpdateCompanyOperation, new() { ["company"] = "5", ["name"] = "Kafé" });
            Assert.Equal(("PATCH", $"{Api}/companies/5/edit", (string?)"{\"companyname\":\"Kafé\"}"), Last());
            await Run(mautic, MauticConnector.DeleteCompanyOperation, new() { ["company"] = "5" });
            Assert.Equal(("DELETE", $"{Api}/companies/5/delete", (string?)null), Last());

            var joined = Assert.Single(await Run(mautic, MauticConnector.AddToCompanyOperation, new() { ["contact"] = "47", ["company"] = "5" })).Fields;
            Assert.Equal(("POST", $"{Api}/companies/5/contact/47/add", (string?)null), Last());
            Assert.Equal(("47", "5", true), ((string?)joined["contact"], (string?)joined["company"], (bool?)joined["added"]));
            Assert.True((bool?)Assert.Single(await Run(mautic, MauticConnector.RemoveFromCompanyOperation, new() { ["contact"] = "47", ["company"] = "5" })).Fields["removed"]);
            Assert.Equal($"{Api}/companies/5/contact/47/remove", Last().Item2);

            var segment = Assert.Single(await Run(mautic, MauticConnector.SegmentsOperation, new() { ["search"] = "fika" })).Fields;
            Assert.Equal($"{Api}/segments?search=fika&start=0&limit=100", Last().Item2);
            Assert.Equal((2L, "Fika", true, "[]"), ((long?)segment["id"], (string?)segment["name"], (bool?)segment["isPublished"], (string?)segment["filters"]));
            Assert.Equal("2", (string?)Assert.Single(await Run(mautic, MauticConnector.AddToSegmentOperation, new() { ["contact"] = "47", ["segment"] = "2" })).Fields["segment"]);
            Assert.Equal($"{Api}/segments/2/contact/47/add", Last().Item2);
            await Run(mautic, MauticConnector.RemoveFromSegmentOperation, new() { ["contact"] = "47", ["segment"] = "2" });
            Assert.Equal($"{Api}/segments/2/contact/47/remove", Last().Item2);

            Assert.Equal("Välkommen", (string?)Assert.Single(await Run(mautic, MauticConnector.CampaignsOperation, new())).Fields["name"]);
            Assert.Equal($"{Api}/campaigns?start=0&limit=100", Last().Item2);
            Assert.Equal("4", (string?)Assert.Single(await Run(mautic, MauticConnector.AddToCampaignOperation, new() { ["contact"] = "47", ["campaign"] = "4" })).Fields["campaign"]);
            Assert.Equal($"{Api}/campaigns/4/contact/47/add", Last().Item2);
            await Run(mautic, MauticConnector.RemoveFromCampaignOperation, new() { ["contact"] = "47", ["campaign"] = "4" });
            Assert.Equal($"{Api}/campaigns/4/contact/47/remove", Last().Item2);

            Assert.Equal("template", (string?)Assert.Single(await Run(mautic, MauticConnector.EmailsOperation, new())).Fields["emailType"]);
            Assert.Equal($"{Api}/emails?start=0&limit=100", Last().Item2);
            var sent = Assert.Single(await Run(mautic, MauticConnector.SendSegmentEmailOperation, new() { ["email_id"] = "9" })).Fields;
            Assert.Equal(("POST", $"{Api}/emails/9/send", (string?)null), Last());
            Assert.Equal(("9", 120L, 2L), ((string?)sent["email_id"], (long?)sent["sent"], (long?)sent["failed"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_mautics_word_on_a_failure()
        {
            var mautic = new Mautic();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(mautic, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Mautic has no operation 'merge' - contacts, contact, create_contact,", await Refused("merge", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(MauticConnector.ContactsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the contact's id.", await Refused(MauticConnector.ContactOperation, new()));
            Assert.Equal("'ada' is not an id (contact).", await Refused(MauticConnector.DeleteContactOperation, new() { ["contact"] = "ada" }));
            Assert.Equal("'0' is not an id (contact).", await Refused(MauticConnector.SendEmailOperation, new() { ["contact"] = "0", ["email_id"] = "3" }));
            Assert.Equal("Give the email's id.", await Refused(MauticConnector.SendSegmentEmailOperation, new()));
            Assert.Equal("Give the company's id.", await Refused(MauticConnector.AddToCompanyOperation, new() { ["contact"] = "47" }));
            Assert.Equal("Give the segment's id.", await Refused(MauticConnector.RemoveFromSegmentOperation, new() { ["contact"] = "47" }));
            Assert.Equal("'x' is not an id (campaign).", await Refused(MauticConnector.AddToCampaignOperation, new() { ["contact"] = "47", ["campaign"] = "x" }));
            Assert.Equal("'fax' is not email or sms (channel).", await Refused(MauticConnector.RemoveDoNotContactOperation, new() { ["contact"] = "47", ["channel"] = "fax" }));
            Assert.Equal("'spam' is not unsubscribed, bounced or manual (reason).", await Refused(MauticConnector.AddDoNotContactOperation, new() { ["contact"] = "47", ["reason"] = "spam" }));
            Assert.Equal("'ada' is not an e-mail (email).", await Refused(MauticConnector.CreateContactOperation, new() { ["email"] = "ada" }));
            Assert.Equal("The other fields are not a JSON object - {\"city\": \"Malmö\"}.", await Refused(MauticConnector.CreateContactOperation, new() { ["fields"] = "city = Malmö" }));
            Assert.Equal("'address' is an object - a field holds a text, a number, a list or null (fields).", await Refused(MauticConnector.UpdateCompanyOperation, new() { ["company"] = "5", ["fields"] = "{\"address\": {\"city\": \"Malmö\"}}" }));
            Assert.Equal("'Ada' is not an id (owner).", await Refused(MauticConnector.CreateContactOperation, new() { ["owner"] = "Ada" }));
            Assert.Equal("'192.0.2' is not an IP address (ip_address).", await Refused(MauticConnector.CreateContactOperation, new() { ["ip_address"] = "192.0.2" }));
            Assert.Equal("'yesterday' is not a time - 2026-10-13 10:00:00 (last_active).", await Refused(MauticConnector.CreateContactOperation, new() { ["last_active"] = "yesterday" }));
            Assert.Equal("Give what to change - a field, tags, the owner, the stage, an address or when they were last active.", await Refused(MauticConnector.UpdateContactOperation, new() { ["contact"] = "47", ["overwrite_with_blank"] = "true" }));
            Assert.Equal("Give the company's name.", await Refused(MauticConnector.CreateCompanyOperation, new() { ["fields"] = "{\"companycity\": \"Lund\"}" }));
            Assert.Equal("Give what to change - its name or other fields.", await Refused(MauticConnector.UpdateCompanyOperation, new() { ["company"] = "5" }));
            Assert.Equal("Give the points - 5 adds five, -5 takes five.", await Refused(MauticConnector.PointsOperation, new() { ["contact"] = "47" }));
            Assert.Equal("'0' is not a number other than 0 (points).", await Refused(MauticConnector.PointsOperation, new() { ["contact"] = "47", ["points"] = "0" }));
            Assert.Equal("'1.5' is not a number other than 0 (points).", await Refused(MauticConnector.PointsOperation, new() { ["contact"] = "47", ["points"] = "1.5" }));
            Assert.Equal("'1x' is not a column (order_by).", await Refused(MauticConnector.ContactsOperation, new() { ["order_by"] = "1x" }));
            Assert.Equal("'up' is not asc or desc (order_dir).", await Refused(MauticConnector.CompaniesOperation, new() { ["order_dir"] = "up" }));
            Assert.Equal("'yes' is not true or false (published_only).", await Refused(MauticConnector.ContactsOperation, new() { ["published_only"] = "yes" }));
            Assert.Equal("'yes' is not true or false (raw).", await Refused(MauticConnector.ContactOperation, new() { ["contact"] = "47", ["raw"] = "yes" }));
            Assert.Equal("Give Mautic's address - https://mautic.example.com.", await Refused(MauticConnector.ContactsOperation, new(), endpoint: ""));
            Assert.Equal("The Mautic connection has no user name.", await Refused(MauticConnector.ContactsOperation, new(), credentials: new() { ["password"] = "p" }));
            Assert.Equal("The Mautic connection has no password.", await Refused(MauticConnector.ContactsOperation, new(), credentials: new() { ["username"] = "kafe-api" }));
            Assert.Empty(mautic.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = MauticConnector.CreateContactOperation) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Mautic { Answer = (_, _) => (status, answer) }, operation, new() { ["email"] = "ada@kafe.example.test", ["contact"] = "47", ["segment"] = "2" }))).Message;
            Assert.Equal("HTTP 404: Item was not found.", await Failed(HttpStatusCode.NotFound, "{\"errors\":[{\"code\":404,\"message\":\"Item was not found.\",\"details\":[]}]}"));
            Assert.Equal("HTTP 400: email: A valid email is required. - email: A valid email is required. It is taken.; firstname: Too long.", await Failed(HttpStatusCode.BadRequest, "{\"errors\":[{\"code\":400,\"message\":\"email: A valid email is required.\",\"details\":{\"email\":[\"A valid email is required.\",\"It is taken.\"],\"firstname\":\"Too long.\"}}]}"));
            Assert.Equal("HTTP 200: You do not have access to the requested area/action.", await Failed(HttpStatusCode.OK, "{\"errors\":[{\"code\":403,\"message\":\"You do not have access to the requested area/action.\"}]}")); // an error answered with 200
            Assert.Equal("HTTP 403: API disabled", await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"API disabled\",\"code\":403}}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"errors\":[{\"message\":\"" + new string('e', 400) + "\"}]}")); // a long word cut
            Assert.Equal("Mautic answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Login</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            Assert.Equal("Mautic did not say it succeeded.", await Failed(HttpStatusCode.OK, "{\"success\":0}", MauticConnector.AddToSegmentOperation));
            Assert.Equal("Mautic did not say it succeeded.", await Failed(HttpStatusCode.OK, "{\"success\":false}", MauticConnector.AddToSegmentOperation));
            Assert.Equal("Mautic did not say it succeeded.", await Failed(HttpStatusCode.OK, "{}", MauticConnector.AddToSegmentOperation));
        }

        [Fact]
        public async Task A_test_names_whose_the_credentials_are()
        {
            Task<ConnectionTestResult> Test(Mautic mautic, string endpoint = Site) =>
                new MauticConnector().TestConnectionAsync(Contexts.Test(new StubEgress(mautic), endpoint, null, Credentials));
            var self = new Mautic { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":1,\"username\":\"kafe-api\",\"firstName\":\"Kafé\",\"lastName\":\"API\"}") };
            var tested = await Test(self, Api);
            Assert.Equal((true, "Mautic: the credentials are Kafé API's (kafe-api)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/users/self", "GET"), (self.Requests[0].Url, self.Requests[0].Method.Method)); // /api given once
            Assert.Equal("Mautic: the credentials are a user's.", (await Test(new Mautic { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":1,\"firstName\":\" \"}") })).Message);
            var refused = await Test(new Mautic { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"errors\":[{\"code\":401,\"message\":\"API authorization denied.\"}]}") });
            Assert.Equal((false, "HTTP 401: API authorization denied."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "mautic"), (new MauticConnector().AuthType, new MauticConnector().ConnectorType));
            Assert.Equal(24, new MauticConnector().Operations.Count);
        }
    }
}
