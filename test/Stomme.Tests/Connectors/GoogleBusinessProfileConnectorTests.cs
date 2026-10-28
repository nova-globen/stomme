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
using Stomme.Modules.Connectors.GoogleBusinessProfile;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Google Business Profile (n8n's Google Business Profile node) without a server: every account, location, post,
    /// review, type, time, day, call to action, link, length and number held to Google's shape before a token is asked for;
    /// the accounts and an account's locations from Google's Account Management and Business Information APIs, posts and
    /// reviews from the My Business API, each page by page by Google's page token up to the most; a post made as each of its
    /// four kinds - an event's times as Google's UTC date and time, an offer's days and coupon - and changed with Google's
    /// update mask named from what is given; a post or review named in full carrying its account and location; a review's stars
    /// as a number; a reply put and deleted; Google's status and message on a failure; a test counting the accounts.
    /// </summary>
    public sealed class GoogleBusinessProfileConnectorTests
    {
        private const string Api = "https://mybusiness.googleapis.com/v4";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Google : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                var (status, answer) = sent.Url == GoogleBusinessProfileConnector.DefaultTokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"business-token\",\"expires_in\":3600}") : Answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static List<Sent> Calls(Google google) => google.Requests.Where(sent => sent.Url != GoogleBusinessProfileConnector.DefaultTokenUrl).ToList();

        private static Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new GoogleBusinessProfileConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(google), "", operation, parameters, settings ?? new Dictionary<string, string>(), Credentials));

        private static Dictionary<string, string> At(params (string Name, string Value)[] more)
        {
            var parameters = new Dictionary<string, string> { ["account"] = "accounts/111", ["location"] = "locations/222" };
            foreach (var (name, value) in more)
            {
                parameters[name] = value;
            }

            return parameters;
        }

        [Fact]
        public void It_declares_itself_as_n8ns_google_business_profile_node()
        {
            IConnector connector = new GoogleBusinessProfileConnector();
            Assert.Equal(("googlebusinessprofile", "Google Business Profile", AuthenticationType.OAuth2RefreshToken), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "accounts", "locations", "posts", "post", "post_create", "post_update", "post_delete", "reviews", "review", "review_reply", "review_reply_delete" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_googles_shape_before_a_token_is_asked_for()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var google = new Google();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters));
                Assert.Empty(google.Requests); // refused before a token was asked for
                return refusal.Message;
            }

            Assert.StartsWith("Google Business Profile has no operation 'questions' - accounts, locations,", await Refused("questions", At()), StringComparison.Ordinal);
            Assert.Equal("Give an account - accounts/0123456789 (account) - or set the connection's.", await Refused("posts", new()));
            Assert.Equal("'acc-1' is not an account - accounts/0123456789 (account).", await Refused("posts", new() { ["account"] = "acc-1" }));
            Assert.Equal("Give a location - locations/0123456789 (location) - or set the connection's.", await Refused("reviews", new() { ["account"] = "111" }));
            Assert.Equal("'Kafé Kassan' is not a location - locations/0123456789 (location).", await Refused("reviews", new() { ["account"] = "111", ["location"] = "Kafé Kassan" }));
            Assert.Equal("Give a post - its name or id (post).", await Refused("post", At()));
            Assert.Equal("'kassa/1' is not a post - its name or id (post).", await Refused("post_delete", At(("post", "kassa/1"))));
            Assert.Equal("'ab cd' is not a review - its name or id (review).", await Refused("review", At(("review", "ab cd"))));
            Assert.Equal("'NEWS' is not STANDARD, EVENT, OFFER or ALERT (type).", await Refused("post_create", At(("type", "NEWS"), ("summary", "Nytt"))));
            Assert.Equal("Give the post's text (summary).", await Refused("post_create", At()));
            Assert.Equal("A post's text is at most 1500 characters - this is 1501 (summary).", await Refused("post_create", At(("summary", new string('k', 1501)))));
            Assert.Equal("Give the event's title (title).", await Refused("post_create", At(("type", "event"), ("start", "2026-11-01T18:00:00+01:00"), ("end", "2026-11-01T22:00:00+01:00"))));
            Assert.Equal("Give when the event starts (start).", await Refused("post_create", At(("type", "EVENT"), ("title", "Kassafest"))));
            Assert.Equal("'snart' is not a time - 2026-11-01T18:00:00+01:00 (start).", await Refused("post_create", At(("type", "EVENT"), ("title", "Kassafest"), ("start", "snart"), ("end", "2026-11-01T22:00:00+01:00"))));
            Assert.Equal("'2026-11-01T18:00:00' is not a day - 2026-11-01 (start).", await Refused("post_create", At(("type", "OFFER"), ("title", "10 %"), ("start", "2026-11-01T18:00:00"), ("end", "2026-11-30"))));
            Assert.Equal("'2026-13-01' is not a day - 2026-11-01 (end).", await Refused("post_create", At(("type", "OFFER"), ("title", "10 %"), ("start", "2026-11-01"), ("end", "2026-13-01"))));
            Assert.Equal("'BUY' is not BOOK, CALL, LEARN_MORE, ORDER, SHOP or SIGN_UP (call_to_action).", await Refused("post_create", At(("summary", "Nytt"), ("call_to_action", "BUY"), ("url", "https://kafe.example"))));
            Assert.Equal("Give where the BOOK leads - https://kafe.example (url).", await Refused("post_create", At(("summary", "Nytt"), ("call_to_action", "book"))));
            Assert.Equal("A CALL calls the location's number - it takes no link (url).", await Refused("post_create", At(("summary", "Nytt"), ("call_to_action", "CALL"), ("url", "https://kafe.example"))));
            Assert.Equal("Give the call to action the link is for (call_to_action).", await Refused("post_create", At(("summary", "Nytt"), ("url", "https://kafe.example"))));
            Assert.Equal("'kafe.example/boka' is not a web address - https://kafe.example (url).", await Refused("post_create", At(("summary", "Nytt"), ("call_to_action", "BOOK"), ("url", "kafe.example/boka"))));
            Assert.Equal("'javascript:alert(1)' is not a web address - https://kafe.example (url).", await Refused("post_create", At(("summary", "Nytt"), ("call_to_action", "BOOK"), ("url", "javascript:alert(1)"))));
            Assert.Equal("'ftp://kafe.example/boka' is not a web address - https://kafe.example (url).", await Refused("post_create", At(("summary", "Nytt"), ("call_to_action", "LEARN_MORE"), ("url", "ftp://kafe.example/boka"))));
            Assert.Equal("An offer has no call to action - its coupon code and redeem link stand for one (call_to_action).", await Refused("post_create", At(("type", "OFFER"), ("title", "10 %"), ("start", "2026-11-01"), ("end", "2026-11-30"), ("call_to_action", "SHOP"))));
            Assert.Equal("Only an offer has a coupon code (coupon_code).", await Refused("post_create", At(("summary", "Nytt"), ("coupon_code", "KASSA10"))));
            Assert.Equal("'svenska' is not a language's code - sv, en-GB (language).", await Refused("post_create", At(("summary", "Nytt"), ("language", "svenska"))));
            Assert.Equal("Give what to change - summary, title, start, end, call to action, link, coupon code, redeem link, terms or language.", await Refused("post_update", At(("post", "333"))));
            Assert.Equal("Give the reply (reply).", await Refused("review_reply", At(("review", "AbC_1"))));
            Assert.Equal("A reply is at most 4096 characters - this is 4097 (reply).", await Refused("review_reply", At(("review", "AbC_1"), ("reply", new string('k', 4097)))));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("posts", At(("max", "0"))));
        }

        [Fact]
        public async Task Posts_are_listed_page_by_page_made_as_each_kind_changed_by_their_mask_read_and_deleted()
        {
            var google = new Google
            {
                Answer = sent => (sent.Method, new Uri(sent.Url).AbsolutePath) switch
                {
                    ("GET", "/v4/accounts/111/locations/222/localPosts") when sent.Url.Contains("pageToken=p2", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"localPosts\":[" + string.Join(",", Enumerable.Range(100, 20).Select(i => $"{{\"name\":\"accounts/111/locations/222/localPosts/{i}\",\"summary\":\"Post {i}\"}}")) + "]}"),
                    ("GET", "/v4/accounts/111/locations/222/localPosts") => (HttpStatusCode.OK, "{\"localPosts\":[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"name\":\"accounts/111/locations/222/localPosts/{i}\",\"summary\":\"Post {i}\"}}")) + "],\"nextPageToken\":\"p2\"}"),
                    ("DELETE", _) => (HttpStatusCode.OK, "{}"),
                    _ => (HttpStatusCode.OK, "{\"name\":\"accounts/111/locations/222/localPosts/333\",\"summary\":\"Nytt på kassan\",\"state\":\"LIVE\",\"callToAction\":{\"actionType\":\"BOOK\",\"url\":\"https://kafe.example/boka\"}}"),
                },
            };
            var all = await Run(google, "posts", At(("max", "1000")), null);
            Assert.Equal(120, all.Count);
            Assert.Equal(new[] { Api + "/accounts/111/locations/222/localPosts?pageSize=100", Api + "/accounts/111/locations/222/localPosts?pageSize=100&pageToken=p2" }, Calls(google).Select(sent => sent.Url));
            Assert.All(Calls(google), sent => Assert.Equal("Bearer business-token", sent.Authorization));
            google.Requests.Clear();
            Assert.Equal(7, (await Run(google, "posts", new() { ["max"] = "7" }, new() { ["account"] = "111", ["location"] = "222" })).Count); // the connection's account and location
            Assert.Equal(Api + "/accounts/111/locations/222/localPosts?pageSize=7", Calls(google).Single().Url); // a page no larger than what is wanted

            async Task<string> Made(Dictionary<string, string> parameters)
            {
                google.Requests.Clear();
                var made = Assert.Single(await Run(google, "post_create", parameters)).Fields;
                Assert.Equal("accounts/111/locations/222/localPosts/333", made["name"]);
                var sent = Calls(google).Single();
                Assert.Equal(("POST", Api + "/accounts/111/locations/222/localPosts"), (sent.Method, sent.Url));
                return sent.Body!;
            }

            Assert.Equal("{\"topicType\":\"STANDARD\",\"summary\":\"Nytt på kassan\",\"languageCode\":\"sv\",\"callToAction\":{\"actionType\":\"BOOK\",\"url\":\"https://kafe.example/boka\"}}", await Made(At(("summary", "Nytt på kassan"), ("language", "sv"), ("call_to_action", "book"), ("url", "https://kafe.example/boka")))); // n8n's STANDARD by default
            Assert.Equal("{\"topicType\":\"EVENT\",\"summary\":\"Välkommen\",\"event\":{\"title\":\"Kassafest\",\"schedule\":{\"startDate\":{\"year\":2026,\"month\":11,\"day\":1},\"startTime\":{\"hours\":17,\"minutes\":0,\"seconds\":0,\"nanos\":0},\"endDate\":{\"year\":2026,\"month\":11,\"day\":2},\"endTime\":{\"hours\":0,\"minutes\":30,\"seconds\":0,\"nanos\":0}}},\"callToAction\":{\"actionType\":\"CALL\"}}",
                await Made(At(("type", "event"), ("summary", "Välkommen"), ("title", "Kassafest"), ("start", "2026-11-01T18:00:00+01:00"), ("end", "2026-11-02T01:30:00+01:00"), ("call_to_action", "CALL")))); // in UTC, as n8n's node sends them
            Assert.Equal("{\"topicType\":\"OFFER\",\"event\":{\"title\":\"10 % på kaffe\",\"schedule\":{\"startDate\":{\"year\":2026,\"month\":11,\"day\":1},\"endDate\":{\"year\":2026,\"month\":11,\"day\":30}}},\"offer\":{\"couponCode\":\"KASSA10\",\"redeemOnlineUrl\":\"https://kafe.example/erbjudande\",\"termsConditions\":\"En gång per kund.\"}}",
                await Made(At(("type", "OFFER"), ("title", "10 % på kaffe"), ("start", "2026-11-01"), ("end", "2026-11-30"), ("coupon_code", "KASSA10"), ("redeem_url", "https://kafe.example/erbjudande"), ("terms", "En gång per kund."))));
            Assert.Equal("{\"topicType\":\"ALERT\",\"summary\":\"Stängt i dag\",\"alertType\":\"COVID_19\"}", await Made(At(("type", "ALERT"), ("summary", "Stängt i dag"))));

            google.Requests.Clear();
            await Run(google, "post_update", new() { ["post"] = "accounts/111/locations/222/localPosts/333", ["summary"] = "Nytt igen", ["start"] = "2026-11-03T10:00:00Z", ["coupon_code"] = "KASSA20" }); // named in full: its account and location
            var update = Calls(google).Single();
            Assert.Equal(("PATCH", Api + "/accounts/111/locations/222/localPosts/333?updateMask=summary%2Cevent.schedule.startDate%2Cevent.schedule.startTime%2Coffer.couponCode"), (update.Method, update.Url));
            Assert.Equal("{\"summary\":\"Nytt igen\",\"event\":{\"schedule\":{\"startDate\":{\"year\":2026,\"month\":11,\"day\":3},\"startTime\":{\"hours\":10,\"minutes\":0,\"seconds\":0,\"nanos\":0}}},\"offer\":{\"couponCode\":\"KASSA20\"}}", update.Body);
            Assert.Equal("LIVE", Assert.Single(await Run(google, "post", At(("post", "333")))).Fields["state"]);
            Assert.Equal(("GET", Api + "/accounts/111/locations/222/localPosts/333"), (Calls(google).Last().Method, Calls(google).Last().Url));
            Assert.Equal(new Dictionary<string, object?> { ["post"] = "accounts/111/locations/222/localPosts/333", ["deleted"] = true }, Assert.Single(await Run(google, "post_delete", At(("post", "333")))).Fields);
            Assert.Equal("DELETE", Calls(google).Last().Method);
        }

        [Fact]
        public async Task Reviews_accounts_and_locations_are_read_a_reply_put_and_deleted_and_a_failure_is_googles_words()
        {
            var google = new Google
            {
                Answer = sent => new Uri(sent.Url) switch
                {
                    { Host: "mybusinessaccountmanagement.googleapis.com" } => (HttpStatusCode.OK, "{\"accounts\":[{\"name\":\"accounts/111\",\"accountName\":\"Kafé Kassan\",\"type\":\"PERSONAL\"}]}"),
                    { Host: "mybusinessbusinessinformation.googleapis.com" } => (HttpStatusCode.OK, "{\"locations\":[{\"name\":\"locations/222\",\"title\":\"Kafé Kassan Göteborg\"}]}"),
                    { AbsolutePath: "/v4/accounts/111/locations/222/reviews" } => (HttpStatusCode.OK, "{\"reviews\":[{\"reviewId\":\"AbC_1\",\"name\":\"accounts/111/locations/222/reviews/AbC_1\",\"starRating\":\"FOUR\",\"comment\":\"Gott kaffe\",\"reviewer\":{\"displayName\":\"Ada\"}},{\"reviewId\":\"AbC_2\",\"starRating\":\"STAR_RATING_UNSPECIFIED\"}],\"averageRating\":4,\"totalReviewCount\":2}"),
                    { AbsolutePath: "/v4/accounts/111/locations/222/reviews/AbC_1/reply" } when sent.Method == "PUT" => (HttpStatusCode.OK, "{\"comment\":\"Tack, Ada!\",\"updateTime\":\"2026-10-18T10:00:00Z\"}"),
                    _ => (HttpStatusCode.OK, "{\"reviewId\":\"AbC_1\",\"starRating\":\"FIVE\",\"comment\":\"Bäst i stan\"}"),
                },
            };
            var reviews = await Run(google, "reviews", At());
            Assert.Equal(new object?[] { 4, null }, reviews.Select(review => review.Fields.GetValueOrDefault("stars")));
            Assert.Equal(("Gott kaffe", "{\"displayName\":\"Ada\"}"), ((string?)reviews[0].Fields["comment"], (string?)reviews[0].Fields["reviewer"]));
            Assert.Equal(Api + "/accounts/111/locations/222/reviews?pageSize=50", Calls(google).Single().Url); // Google's most for reviews
            Assert.Equal(5, Assert.Single(await Run(google, "review", At(("review", "AbC_1")))).Fields["stars"]);

            google.Requests.Clear();
            var replied = Assert.Single(await Run(google, "review_reply", new() { ["review"] = "accounts/111/locations/222/reviews/AbC_1", ["reply"] = "Tack, Ada!" })).Fields;
            Assert.Equal(("Tack, Ada!", "accounts/111/locations/222/reviews/AbC_1"), ((string?)replied["comment"], (string?)replied["review"]));
            Assert.Equal(("PUT", Api + "/accounts/111/locations/222/reviews/AbC_1/reply", "{\"comment\":\"Tack, Ada!\"}"), (Calls(google).Single().Method, Calls(google).Single().Url, Calls(google).Single().Body));
            Assert.Equal(new Dictionary<string, object?> { ["review"] = "accounts/111/locations/222/reviews/AbC_1", ["deleted"] = true }, Assert.Single(await Run(google, "review_reply_delete", At(("review", "AbC_1")))).Fields);
            Assert.Equal(("DELETE", Api + "/accounts/111/locations/222/reviews/AbC_1/reply"), (Calls(google).Last().Method, Calls(google).Last().Url));

            google.Requests.Clear();
            Assert.Equal("Kafé Kassan", Assert.Single(await Run(google, "accounts", new())).Fields["accountName"]);
            Assert.Equal("Kafé Kassan Göteborg", Assert.Single(await Run(google, "locations", new() { ["account"] = "accounts/111" })).Fields["title"]);
            Assert.Equal(new[] { "https://mybusinessaccountmanagement.googleapis.com/v1/accounts?pageSize=20", "https://mybusinessbusinessinformation.googleapis.com/v1/accounts/111/locations?readMask=name%2Ctitle%2CstorefrontAddress%2CwebsiteUri%2CphoneNumbers%2Ccategories&pageSize=100" }, Calls(google).Select(sent => sent.Url)); // Google's own APIs for each

            var refusing = new Google { Answer = _ => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"The caller does not have permission\",\"status\":\"PERMISSION_DENIED\"}}") };
            Assert.Equal("Google Business Profile answered HTTP 403 PERMISSION_DENIED: The caller does not have permission - check the refresh token, its business.manage scope and the person's access to the location.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "reviews", At()))).Message);
            var odd = new Google { Answer = _ => (HttpStatusCode.OK, "[]") };
            Assert.Equal("Google Business Profile answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(odd, "posts", At()))).Message);

            google.Requests.Clear();
            var tested = await new GoogleBusinessProfileConnector().TestConnectionAsync(Contexts.Test(new StubEgress(google), "", new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "Google Business Profile answers - 1 account."), (tested.Success, tested.Message));
            var nothing = await new GoogleBusinessProfileConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Google { Answer = _ => (HttpStatusCode.OK, "") }), "", new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "Google Business Profile answered with nothing - check the address."), (nothing.Success, nothing.Message));
            var elsewhere = await new GoogleBusinessProfileConnector().TestConnectionAsync(Contexts.Test(new StubEgress(google), "https://mybusiness.googleapis.com/v1", new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "'https://mybusiness.googleapis.com/v1' is not Google Business Profile's address - its own, https://mybusiness.googleapis.com."), (elsewhere.Success, elsewhere.Message));
        }
    }
}
