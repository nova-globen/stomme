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
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Connectors.Webflow;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Webflow (n8n's Webflow node: a collection's items created, got, read, updated and deleted): the token as a
    /// bearer; a collection's items, staged or live, page by page by offset up to the most asked for - a page no bigger than
    /// what is left, the end at Webflow's total or a short page - by a name or a slug, in an order and a locale, each item its
    /// own values and then its fields by their slugs; an item got, made, changed, deleted, unpublished and published; the sites,
    /// a site's collections, a collection's fields; a site published; what could go wrong refused before anything is sent;
    /// Webflow's errors in its words; a test naming the sites and scopes the token reaches.
    /// </summary>
    public sealed class WebflowConnectorTests
    {
        private const string Api = "https://api.webflow.example.test/v2";

        private const string Site = "580e63e98c9a982ac9b8b741";

        private const string Collection = "580e63fc8c9a982ac9b8b745";

        private const string Locale = "653ad57de882f528b32e810e";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "wf-site-token-1" };

        private sealed class Webflow : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Webflow webflow, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(webflow), Api, operation, parameters, settings: new Dictionary<string, string>(), credentials: Credentials);

        private static string ItemId(int n) => n.ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

        private static string Item(int n) =>
            "{\"id\":\"" + ItemId(n) + "\",\"cmsLocaleId\":\"" + Locale + "\",\"lastPublished\":\"2026-10-01T09:00:00.000Z\",\"lastUpdated\":\"2026-10-02T09:00:00.000Z\",\"createdOn\":\"2026-09-30T09:00:00.000Z\",\"isArchived\":false,\"isDraft\":true,"
            + "\"fieldData\":{\"name\":\"Café <b>" + n + "</b>\",\"slug\":\"cafe-" + n + "\",\"post-body\":\"<p>Öppet</p>\",\"price\":12.5,\"featured\":true,\"seats\":3,\"image\":{\"fileId\":\"f1\",\"url\":\"https://cdn.example/a.png\",\"alt\":null},\"id\":\"own\",\"author\":null}}";

        private static string Items(int from, int count, int? total) =>
            "{\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(Item)) + "],\"pagination\":{\"limit\":100,\"offset\":" + (from - 1) + (total is null ? "" : ",\"total\":" + total) + "}}";

        [Fact]
        public async Task A_collections_items_are_read_page_by_page_by_offset_each_a_record()
        {
            var webflow = new Webflow { Answer = (_, url, _) => (HttpStatusCode.OK, url.Contains("offset=200", StringComparison.Ordinal) ? Items(201, 50, 250) : url.Contains("offset=100", StringComparison.Ordinal) ? Items(101, 100, 250) : Items(1, 100, 250)) };
            var records = await Contexts.DrainAsync(new WebflowConnector(), Context(webflow, WebflowConnector.ItemsOperation, new() { ["collection"] = Collection }));

            Assert.Equal(250, records.Count);
            Assert.All(webflow.Requests, request => Assert.Equal(("GET", "Bearer wf-site-token-1"), (request.Method.Method, request.Authorization)));
            Assert.Equal(new[] { 0, 100, 200 }.Select(offset => $"{Api}/collections/{Collection}/items?offset={offset}&limit=100"), webflow.Requests.Select(request => request.Url));
            var first = records[0].Fields;
            Assert.Equal((ItemId(1), "2026-09-30T09:00:00.000Z", "2026-10-02T09:00:00.000Z", "2026-10-01T09:00:00.000Z", true, false, Locale),
                ((string?)first["id"], (string?)first["created_on"], (string?)first["last_updated"], (string?)first["last_published"], (bool?)first["is_draft"], (bool?)first["is_archived"], (string?)first["locale"]));
            Assert.Equal(("Café <b>1</b>", "cafe-1", "<p>Öppet</p>", 12.5, true, 3L, (string?)null), ((string?)first["name"], (string?)first["slug"], (string?)first["post-body"], (double?)first["price"], (bool?)first["featured"], (long?)first["seats"], (string?)first["author"]));
            Assert.Equal("{\"fileId\":\"f1\",\"url\":\"https://cdn.example/a.png\",\"alt\":null}", (string?)first["image"]); // not one value: its JSON
            Assert.Equal("own", (string?)first["field.id"]); // a field named as the item's own value: kept apart

            var exact = new Webflow { Answer = (_, _, _) => (HttpStatusCode.OK, Items(1, 100, 200)) };
            Assert.Equal(200, (await Contexts.DrainAsync(new WebflowConnector(), Context(exact, WebflowConnector.ItemsOperation, new() { ["collection"] = Collection }))).Count);
            Assert.Equal(2, exact.Requests.Count); // Webflow's total reached: no third page

            var untold = new Webflow { Answer = (_, url, _) => (HttpStatusCode.OK, url.Contains("offset=100", StringComparison.Ordinal) ? Items(101, 7, null) : Items(1, 100, null)) };
            Assert.Equal(107, (await Contexts.DrainAsync(new WebflowConnector(), Context(untold, WebflowConnector.ItemsOperation, new() { ["collection"] = Collection }))).Count);
            Assert.Equal(2, untold.Requests.Count); // no total: a short page the end

            var empty = new Webflow { Answer = (_, url, _) => (HttpStatusCode.OK, url.Contains("offset=100", StringComparison.Ordinal) ? Items(101, 0, null) : Items(1, 100, null)) };
            Assert.Equal(100, (await Contexts.DrainAsync(new WebflowConnector(), Context(empty, WebflowConnector.ItemsOperation, new() { ["collection"] = Collection }))).Count);
            Assert.Equal(2, empty.Requests.Count);

            var few = new Webflow { Answer = (_, _, _) => (HttpStatusCode.OK, Items(1, 100, 900)) };
            Assert.Equal(150, (await Contexts.DrainAsync(new WebflowConnector(), Context(few, WebflowConnector.ItemsOperation, new() { ["collection"] = Collection, ["max"] = "150" }))).Count);
            Assert.Equal(new[] { $"{Api}/collections/{Collection}/items?offset=0&limit=100", $"{Api}/collections/{Collection}/items?offset=100&limit=50" }, few.Requests.Select(request => request.Url)); // a page no bigger than what is left

            var found = new Webflow { Answer = (_, _, _) => (HttpStatusCode.OK, Items(1, 1, 1)) };
            await Contexts.DrainAsync(new WebflowConnector(), Context(found, WebflowConnector.ItemsOperation, new()
            {
                ["collection"] = Collection, ["live"] = "TRUE", ["name"] = "Café <b>1</b>", ["slug"] = "cafe-1", ["sort_by"] = "lastpublished", ["sort_order"] = "DESC", ["locale"] = Locale,
            }));
            Assert.Equal($"{Api}/collections/{Collection}/items/live?name=Caf%C3%A9%20%3Cb%3E1%3C%2Fb%3E&slug=cafe-1&sortBy=lastPublished&sortOrder=desc&cmsLocaleId={Locale}&offset=0&limit=100", Assert.Single(found.Requests).Url);
        }

        [Fact]
        public async Task An_item_is_got_made_changed_deleted_unpublished_and_published()
        {
            var item = ItemId(7);
            var webflow = new Webflow
            {
                Answer = (method, url, _) =>
                    method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "")
                    : url.EndsWith("/items/publish", StringComparison.Ordinal) ? (HttpStatusCode.Accepted, "{\"publishedItemIds\":[\"" + item + "\"],\"errors\":[\"" + ItemId(8) + " is archived\",{\"id\":\"x\"}]}")
                    : method == HttpMethod.Post ? (HttpStatusCode.Accepted, Item(7))
                    : (HttpStatusCode.OK, Item(7)),
            };
            (string, string, string?) Last() => (webflow.Requests[^1].Method.Method, webflow.Requests[^1].Url, webflow.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new WebflowConnector(), Context(webflow, operation, parameters));

            Assert.Equal(item, (string?)Assert.Single(await Run(WebflowConnector.GetItemOperation, new() { ["collection"] = Collection, ["item"] = " " + item + " " })).Fields["id"]);
            Assert.Equal(("GET", $"{Api}/collections/{Collection}/items/{item}", (string?)null), Last());
            await Run(WebflowConnector.GetItemOperation, new() { ["collection"] = Collection, ["item"] = item, ["live"] = "true", ["locale"] = Locale });
            Assert.Equal(("GET", $"{Api}/collections/{Collection}/items/{item}/live?cmsLocaleId={Locale}", (string?)null), Last());

            var made = Assert.Single(await Run(WebflowConnector.CreateItemOperation, new()
            {
                ["collection"] = Collection, ["fields"] = "{\"name\": \"Café <b>7</b>\", \"slug\": \"cafe-7\", \"post-body\": \"<p>Öppet</p>\", \"price\": 12.5, \"featured\": true}", ["draft"] = "False", ["locale"] = Locale,
            }));
            Assert.Equal(("POST", $"{Api}/collections/{Collection}/items", (string?)("{\"fieldData\":{\"name\":\"Café <b>7</b>\",\"slug\":\"cafe-7\",\"post-body\":\"<p>Öppet</p>\",\"price\":12.5,\"featured\":true},\"isDraft\":false,\"cmsLocaleId\":\"" + Locale + "\"}")), Last());
            Assert.Equal(("cafe-7", 12.5), ((string?)made.Fields["slug"], (double?)made.Fields["price"])); // Webflow's 202 read as the item
            await Run(WebflowConnector.CreateItemOperation, new() { ["collection"] = Collection, ["fields"] = "{\"name\": \"A\", \"slug\": \"a\"}", ["live"] = "true" });
            Assert.Equal(("POST", $"{Api}/collections/{Collection}/items/live", (string?)"{\"fieldData\":{\"name\":\"A\",\"slug\":\"a\"}}"), Last()); // published at once; nothing said of a draft

            await Run(WebflowConnector.UpdateItemOperation, new() { ["collection"] = Collection, ["item"] = item, ["fields"] = "{\"price\": 14}" });
            Assert.Equal(("PATCH", $"{Api}/collections/{Collection}/items/{item}", (string?)"{\"fieldData\":{\"price\":14}}"), Last());
            await Run(WebflowConnector.UpdateItemOperation, new() { ["collection"] = Collection, ["item"] = item, ["archived"] = "true", ["live"] = "true" });
            Assert.Equal(("PATCH", $"{Api}/collections/{Collection}/items/{item}/live", (string?)"{\"isArchived\":true}"), Last());

            var gone = Assert.Single(await Run(WebflowConnector.DeleteItemOperation, new() { ["collection"] = Collection, ["item"] = item }));
            Assert.Equal(("DELETE", $"{Api}/collections/{Collection}/items/{item}", (string?)null), Last());
            Assert.Equal((Collection, item, true), ((string?)gone.Fields["collection"], (string?)gone.Fields["item"], (bool?)gone.Fields["done"]));
            await Run(WebflowConnector.UnpublishItemOperation, new() { ["collection"] = Collection, ["item"] = item });
            Assert.Equal(("DELETE", $"{Api}/collections/{Collection}/items/{item}/live", (string?)null), Last());

            var published = Assert.Single(await Run(WebflowConnector.PublishItemsOperation, new() { ["collection"] = Collection, ["items"] = item + ", " + ItemId(8) + ",," + item }));
            Assert.Equal(("POST", $"{Api}/collections/{Collection}/items/publish", (string?)("{\"itemIds\":[\"" + item + "\",\"" + ItemId(8) + "\"]}")), Last()); // once each
            Assert.Equal((item, ItemId(8) + " is archived; {\"id\":\"x\"}"), ((string?)published.Fields["published"], (string?)published.Fields["errors"]));
        }

        [Fact]
        public async Task Sites_their_collections_and_fields_are_read_and_a_site_published()
        {
            var webflow = new Webflow
            {
                Answer = (method, url, _) =>
                    url.EndsWith("/sites", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"sites\":[{\"id\":\"" + Site + "\",\"workspaceId\":\"w1\",\"displayName\":\"Café shop\",\"shortName\":\"cafe-shop\",\"previewUrl\":\"https://cafe.webflow.io/preview.png\",\"timeZone\":\"Europe/Stockholm\","
                        + "\"createdOn\":\"2026-01-01T00:00:00Z\",\"lastUpdated\":\"2026-10-01T00:00:00Z\",\"lastPublished\":null,\"customDomains\":[{\"id\":\"589a331aa51e760df7ccb89d\",\"url\":\"shop.example.com\"},{\"id\":\"589a331aa51e760df7ccb89e\",\"url\":\"www.shop.example.com\"}]},{\"id\":\"x2\",\"displayName\":\"Blog\"}]}")
                    : url.EndsWith("/collections", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"collections\":[{\"id\":\"" + Collection + "\",\"displayName\":\"Menu items\",\"singularName\":\"Menu item\",\"slug\":\"menu\",\"createdOn\":\"2026-01-02T00:00:00Z\",\"lastUpdated\":\"2026-10-02T00:00:00Z\"}]}")
                    : url.EndsWith("/publish", StringComparison.Ordinal) ? (HttpStatusCode.Accepted, "{\"customDomains\":[{\"id\":\"589a331aa51e760df7ccb89d\",\"url\":\"shop.example.com\",\"lastPublished\":null}],\"publishToWebflowSubdomain\":false,\"publishScope\":\"site\"}")
                    : (HttpStatusCode.OK, "{\"id\":\"" + Collection + "\",\"displayName\":\"Menu items\",\"fields\":[{\"id\":\"f1\",\"isRequired\":true,\"isEditable\":true,\"type\":\"PlainText\",\"slug\":\"name\",\"displayName\":\"Name\",\"helpText\":null},"
                        + "{\"id\":\"f2\",\"isRequired\":false,\"isEditable\":true,\"type\":\"RichText\",\"slug\":\"post-body\",\"displayName\":\"Body\",\"helpText\":\"What it is\"},"
                        + "{\"id\":\"f3\",\"isRequired\":true,\"isEditable\":false,\"type\":\"PlainText\",\"slug\":\"slug\",\"displayName\":\"Slug\"}]}"),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new WebflowConnector(), Context(webflow, operation, parameters));

            var sites = await Run(WebflowConnector.SitesOperation, new());
            Assert.Equal(2, sites.Count);
            Assert.Equal((Site, "Café shop", "cafe-shop", "w1", "https://cafe.webflow.io/preview.png", "Europe/Stockholm", (string?)null), ((string?)sites[0].Fields["id"], (string?)sites[0].Fields["name"], (string?)sites[0].Fields["short_name"], (string?)sites[0].Fields["workspace_id"], (string?)sites[0].Fields["preview_url"], (string?)sites[0].Fields["time_zone"], (string?)sites[0].Fields["last_published"]));
            Assert.Equal(("shop.example.com, www.shop.example.com", "589a331aa51e760df7ccb89d, 589a331aa51e760df7ccb89e", "", ""), ((string?)sites[0].Fields["domains"], (string?)sites[0].Fields["domain_ids"], (string?)sites[1].Fields["domains"], (string?)sites[1].Fields["domain_ids"]));
            Assert.Equal($"{Api}/sites", webflow.Requests[^1].Url);

            var collection = Assert.Single(await Run(WebflowConnector.CollectionsOperation, new() { ["site"] = Site }));
            Assert.Equal($"{Api}/sites/{Site}/collections", webflow.Requests[^1].Url);
            Assert.Equal((Collection, "Menu items", "Menu item", "menu", "2026-01-02T00:00:00Z", "2026-10-02T00:00:00Z"), ((string?)collection.Fields["id"], (string?)collection.Fields["name"], (string?)collection.Fields["singular_name"], (string?)collection.Fields["slug"], (string?)collection.Fields["created_on"], (string?)collection.Fields["last_updated"]));

            var fields = await Run(WebflowConnector.FieldsOperation, new() { ["collection"] = Collection });
            Assert.Equal($"{Api}/collections/{Collection}", webflow.Requests[^1].Url);
            Assert.Equal(new[] { ("f1", "name", "Name", "PlainText", (bool?)true, (bool?)true, (string?)null), ("f2", "post-body", "Body", "RichText", false, true, "What it is"), ("f3", "slug", "Slug", "PlainText", true, false, null) },
                fields.Select(field => ((string)field.Fields["id"]!, (string)field.Fields["slug"]!, (string)field.Fields["name"]!, (string)field.Fields["type"]!, (bool?)field.Fields["required"], (bool?)field.Fields["editable"], (string?)field.Fields["help_text"])));

            var published = Assert.Single(await Run(WebflowConnector.PublishSiteOperation, new() { ["site"] = Site, ["domains"] = "589a331aa51e760df7ccb89d, 589a331aa51e760df7ccb89d" }));
            Assert.Equal(("POST", $"{Api}/sites/{Site}/publish", (string?)"{\"publishToWebflowSubdomain\":false,\"customDomains\":[\"589a331aa51e760df7ccb89d\"]}"), (webflow.Requests[^1].Method.Method, webflow.Requests[^1].Url, webflow.Requests[^1].Body));
            Assert.Equal((Site, false, "shop.example.com", "site"), ((string?)published.Fields["site"], (bool?)published.Fields["subdomain"], (string?)published.Fields["domains"], (string?)published.Fields["scope"]));
            await Run(WebflowConnector.PublishSiteOperation, new() { ["site"] = Site });
            Assert.Equal("{\"publishToWebflowSubdomain\":true}", webflow.Requests[^1].Body); // no domain: the Webflow subdomain
            await Run(WebflowConnector.PublishSiteOperation, new() { ["site"] = Site, ["domains"] = "589a331aa51e760df7ccb89d", ["subdomain"] = "true" });
            Assert.Equal("{\"publishToWebflowSubdomain\":true,\"customDomains\":[\"589a331aa51e760df7ccb89d\"]}", webflow.Requests[^1].Body);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_webflows_errors_are_its_words()
        {
            var webflow = new Webflow();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new WebflowConnector(), Context(webflow, operation, parameters)))).Message;

            const string Shape = " - 24 hexadecimal digits, 580e63fc8c9a982ac9b8b745.";
            Assert.Equal("Give the collection - its id.", await Refused(WebflowConnector.ItemsOperation, new()));
            Assert.Equal("'menu' is not a collection's id" + Shape, await Refused(WebflowConnector.FieldsOperation, new() { ["collection"] = "menu" }));
            Assert.Equal("'580E63FC8C9A982AC9B8B745' is not a collection's id" + Shape, await Refused(WebflowConnector.ItemsOperation, new() { ["collection"] = "580E63FC8C9A982AC9B8B745" }));
            Assert.Equal("Give the site - its id.", await Refused(WebflowConnector.CollectionsOperation, new()));
            Assert.Equal("'shop' is not a site's id" + Shape, await Refused(WebflowConnector.PublishSiteOperation, new() { ["site"] = "shop" }));
            Assert.Equal("Give the item - its id.", await Refused(WebflowConnector.GetItemOperation, new() { ["collection"] = Collection }));
            Assert.Equal("'7' is not an item's id" + Shape, await Refused(WebflowConnector.DeleteItemOperation, new() { ["collection"] = Collection, ["item"] = "7" }));
            Assert.Equal("'7' is not an item's id" + Shape, await Refused(WebflowConnector.UnpublishItemOperation, new() { ["collection"] = Collection, ["item"] = "7" }));
            Assert.Equal("'yes' is not true or false (live).", await Refused(WebflowConnector.ItemsOperation, new() { ["collection"] = Collection, ["live"] = "yes" }));
            Assert.Equal("'en' is not a locale's id" + Shape, await Refused(WebflowConnector.GetItemOperation, new() { ["collection"] = Collection, ["item"] = ItemId(1), ["locale"] = "en" }));
            Assert.Equal("'price' is not a field to sort by - createdOn, lastPublished, lastUpdated, name or slug.", await Refused(WebflowConnector.ItemsOperation, new() { ["collection"] = Collection, ["sort_by"] = "price" }));
            Assert.Equal("'up' is not an order - asc or desc.", await Refused(WebflowConnector.ItemsOperation, new() { ["collection"] = Collection, ["sort_order"] = "up" }));
            Assert.Equal("The slug has a line break - one line only.", await Refused(WebflowConnector.ItemsOperation, new() { ["collection"] = Collection, ["slug"] = "a\nb" }));
            Assert.Equal("Give the item's fields - {\"name\": \"Café\", \"slug\": \"cafe\"}.", await Refused(WebflowConnector.CreateItemOperation, new() { ["collection"] = Collection }));
            Assert.Equal("The fields are not a JSON object - {\"name\": \"Café\", \"slug\": \"cafe\"}.", await Refused(WebflowConnector.CreateItemOperation, new() { ["collection"] = Collection, ["fields"] = "name = Café" }));
            Assert.Equal("The fields are not a JSON object - {\"name\": \"Café\", \"slug\": \"cafe\"}.", await Refused(WebflowConnector.UpdateItemOperation, new() { ["collection"] = Collection, ["item"] = ItemId(1), ["fields"] = "[1]" }));
            Assert.Equal("'post body' is not a field's slug - post-body.", await Refused(WebflowConnector.UpdateItemOperation, new() { ["collection"] = Collection, ["item"] = ItemId(1), ["fields"] = "{\"price\": 1, \"post body\": \"x\"}" }));
            Assert.Equal("Give the item's name and slug among its fields - {\"name\": \"Café\", \"slug\": \"cafe\"}.", await Refused(WebflowConnector.CreateItemOperation, new() { ["collection"] = Collection, ["fields"] = "{\"name\": \"Café\"}" }));
            Assert.Equal("Give the item's name and slug among its fields - {\"name\": \"Café\", \"slug\": \"cafe\"}.", await Refused(WebflowConnector.CreateItemOperation, new() { ["collection"] = Collection, ["fields"] = "{\"slug\": \"cafe\"}" }));
            Assert.Equal("Give something to change - its fields, draft or archived.", await Refused(WebflowConnector.UpdateItemOperation, new() { ["collection"] = Collection, ["item"] = ItemId(1) }));
            Assert.Equal("Give something to change - its fields, draft or archived.", await Refused(WebflowConnector.UpdateItemOperation, new() { ["collection"] = Collection, ["item"] = ItemId(1), ["fields"] = "{}", ["locale"] = Locale })); // a locale alone changes nothing
            Assert.Equal("'maybe' is not true or false (draft).", await Refused(WebflowConnector.UpdateItemOperation, new() { ["collection"] = Collection, ["item"] = ItemId(1), ["draft"] = "maybe" }));
            Assert.Equal("'no' is not true or false (archived).", await Refused(WebflowConnector.CreateItemOperation, new() { ["collection"] = Collection, ["fields"] = "{\"name\": \"A\", \"slug\": \"a\"}", ["archived"] = "no" }));
            Assert.Equal("Give the items - their ids, comma-separated.", await Refused(WebflowConnector.PublishItemsOperation, new() { ["collection"] = Collection }));
            Assert.Equal("Give 1 to 100 items - 0 given.", await Refused(WebflowConnector.PublishItemsOperation, new() { ["collection"] = Collection, ["items"] = ", ," }));
            Assert.Equal("Give 1 to 100 items - 101 given.", await Refused(WebflowConnector.PublishItemsOperation, new() { ["collection"] = Collection, ["items"] = string.Join(",", Enumerable.Range(1, 101).Select(ItemId)) }));
            Assert.Equal("'x' is not an item's id" + Shape, await Refused(WebflowConnector.PublishItemsOperation, new() { ["collection"] = Collection, ["items"] = ItemId(1) + ",x" }));
            Assert.Equal("Publish somewhere - its custom domains, its Webflow subdomain or both.", await Refused(WebflowConnector.PublishSiteOperation, new() { ["site"] = Site, ["subdomain"] = "false" }));
            Assert.Equal("'shop.example.com' is not a custom domain's id" + Shape, await Refused(WebflowConnector.PublishSiteOperation, new() { ["site"] = Site, ["domains"] = "shop.example.com" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(WebflowConnector.ItemsOperation, new() { ["collection"] = Collection, ["max"] = "0" }));
            Assert.StartsWith("Webflow has no operation 'pages' - sites, collections, fields, items,", await Refused("pages", new()));
            Assert.Empty(webflow.Requests); // nothing sent

            var invalid = new Webflow { Answer = (_, _, _) => (HttpStatusCode.BadRequest, """{"code":"validation_error","message":"Validation Error","externalReference":"https://developers.webflow.com","details":[{"param":"fieldData.slug","description":"Unique value is already in database"},{"param":"name"},{"param":"price","message":"Not a number"},"bad"]}""") };
            Assert.Equal("HTTP 400 validation_error: Validation Error (fieldData.slug: Unique value is already in database; name; price: Not a number; bad)",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new WebflowConnector(), Context(invalid, WebflowConnector.CreateItemOperation, new() { ["collection"] = Collection, ["fields"] = "{\"name\": \"A\", \"slug\": \"a\"}" })))).Message);
            var missing = new Webflow { Answer = (_, _, _) => (HttpStatusCode.NotFound, """{"code":"collection_not_found","message":"Requested collection not found"}""") };
            Assert.Equal("HTTP 404 collection_not_found: Requested collection not found", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new WebflowConnector(), Context(missing, WebflowConnector.ItemsOperation, new() { ["collection"] = Collection })))).Message);
            var silent = new Webflow { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "") };
            Assert.Equal("HTTP 401.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new WebflowConnector(), Context(silent, WebflowConnector.SitesOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_sites_and_the_scopes_the_token_reaches()
        {
            var webflow = new Webflow { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"authorization\":{\"id\":\"a1\",\"grantType\":\"site_token\",\"rateLimit\":60,\"scope\":\"cms:read,cms:write,sites:read\",\"authorizedTo\":{\"siteIds\":[\"" + Site + "\"],\"workspaceIds\":[],\"userIds\":[]}}}") };
            var tested = await new WebflowConnector().TestConnectionAsync(Contexts.Test(new StubEgress(webflow), Api, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((true, "The token reaches one site (cms:read, cms:write, sites:read)."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/token/introspect", "Bearer wf-site-token-1"), (webflow.Requests[0].Url, webflow.Requests[0].Authorization));
            var app = new Webflow { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"authorization\":{\"scope\":\"\",\"authorizedTo\":{\"siteIds\":[\"a\",\"b\"]}},\"application\":{\"id\":\"p1\",\"displayName\":\"Shop sync\"}}") };
            var named = await new WebflowConnector().TestConnectionAsync(Contexts.Test(new StubEgress(app), Api, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((true, "Shop sync's token reaches 2 sites."), (named.Success, named.Message));
            var refused = new Webflow { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, """{"code":"not_authorized","message":"Request not authorized"}""") };
            var failed = await new WebflowConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), Api, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((false, "HTTP 401 not_authorized: Request not authorized"), (failed.Success, failed.Message));
            var keyless = await new WebflowConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Webflow()), Api, settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>()));
            Assert.Equal((false, "The Webflow connection has no API token."), (keyless.Success, keyless.Message));
            Assert.Equal((AuthenticationType.Bearer, "webflow"), (new WebflowConnector().AuthType, new WebflowConnector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Marketing }, new WebflowConnector().Categories);
            Assert.Equal(11, new WebflowConnector().Operations.Count);
        }
    }
}
