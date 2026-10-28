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
using Stomme.Modules.Connectors.Shopify;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Shopify (n8n's Shopify node: orders and products listed, got, created, updated and deleted) over Shopify's GraphQL
    /// Admin API: the token in Shopify's own header; the orders and products a search finds - its query, statuses, an address, a
    /// vendor, a type, a title and times written into Shopify's search syntax - newest first, page by page by Shopify's cursor up
    /// to the most asked for, each a plain record; an order made from variants and items of no product, changed and deleted; a
    /// product made, changed and deleted; an id, a variant, line items, a status, an address, a time, a currency and an API
    /// version refused before anything is sent; a throttled query asked again; Shopify's failure and its user errors said; a test
    /// naming the shop.
    /// </summary>
    public sealed class ShopifyConnectorTests
    {
        private const string Shop = "https://acme.myshopify.example";

        private const string Api = Shop + "/admin/api/2026-10/graphql.json";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "shpat_test_1" };

        private sealed class ShopifyApi : HttpMessageHandler
        {
            public List<(string Url, string? Token, JsonObject Body)> Requests { get; } = new();

            public Func<string, JsonObject, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"data\":{}}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("X-Shopify-Access-Token", out var tokens) ? tokens.Single() : null, body));
                var (status, answer) = Answer((string)body["query"]!, body["variables"]!.AsObject());
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(ShopifyApi shopify, string operation, Dictionary<string, string> parameters, string endpoint = Shop, Dictionary<string, string>? settings = null) =>
            Contexts.Extract(new StubEgress(shopify), endpoint, operation, parameters, settings, Credentials);

        private static string Order(string id, string name) =>
            "{\"id\":\"gid://shopify/Order/" + id + "\",\"legacyResourceId\":\"" + id + "\",\"name\":\"" + name + "\",\"email\":\"ada@example.com\",\"phone\":null,"
            + "\"createdAt\":\"2026-10-02T10:00:00Z\",\"updatedAt\":\"2026-10-02T11:00:00Z\",\"processedAt\":\"2026-10-02T10:00:00Z\",\"cancelledAt\":null,\"closedAt\":null,"
            + "\"test\":false,\"note\":\"Gift\",\"tags\":[\"wholesale\",\"vip\"],\"displayFinancialStatus\":\"PAID\",\"displayFulfillmentStatus\":\"UNFULFILLED\",\"currencyCode\":\"SEK\","
            + "\"totalPriceSet\":{\"shopMoney\":{\"amount\":\"250.0\"}},\"subtotalPriceSet\":{\"shopMoney\":{\"amount\":\"200.0\"}},\"totalTaxSet\":{\"shopMoney\":{\"amount\":\"50.0\"}},"
            + "\"customer\":{\"id\":\"gid://shopify/Customer/77\",\"displayName\":\"Ada Lovelace\"},"
            + "\"lineItems\":{\"nodes\":[{\"title\":\"Blue mug\",\"quantity\":2,\"sku\":\"MUG-B\",\"variant\":{\"id\":\"gid://shopify/ProductVariant/4372907\"}}]}}";

        private static string Product(string id, string title) =>
            "{\"id\":\"gid://shopify/Product/" + id + "\",\"legacyResourceId\":\"" + id + "\",\"title\":\"" + title + "\",\"handle\":\"blue-mug\",\"status\":\"ACTIVE\",\"vendor\":\"Acme Ceramics\","
            + "\"productType\":\"Mugs\",\"tags\":[\"kitchen\"],\"descriptionHtml\":\"<p>Blue.</p>\",\"totalInventory\":12,\"createdAt\":\"2026-09-01T08:00:00Z\",\"updatedAt\":\"2026-09-02T08:00:00Z\","
            + "\"publishedAt\":\"2026-09-01T09:00:00Z\",\"onlineStoreUrl\":\"https://acme.example/products/blue-mug\",\"priceRangeV2\":{\"minVariantPrice\":{\"amount\":\"100.0\",\"currencyCode\":\"SEK\"},\"maxVariantPrice\":{\"amount\":\"120.0\"}},"
            + "\"variants\":{\"nodes\":[{\"id\":\"gid://shopify/ProductVariant/4372907\",\"title\":\"Large\",\"sku\":\"MUG-B\",\"price\":\"120.0\",\"inventoryQuantity\":12}]}}";

        private static ShopifyConnector Connector() => new();

        [Fact]
        public async Task The_orders_a_search_finds_are_read_newest_first_page_by_page_by_shopifys_cursor_up_to_the_most_asked_for()
        {
            var shopify = new ShopifyApi
            {
                Answer = (_, variables) => variables["after"] is null
                    ? (HttpStatusCode.OK, "{\"data\":{\"orders\":{\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"c1\"},\"nodes\":[" + Order("1001", "#1001") + "," + Order("1002", "#1002") + "]}}}")
                    : (HttpStatusCode.OK, "{\"data\":{\"orders\":{\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"c2\"},\"nodes\":[" + Order("1003", "#1003") + "," + Order("1004", "#1004") + "]}}}"),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.OrdersOperation, new()
            {
                ["query"] = "tag:wholesale", ["status"] = "Open", ["financial_status"] = "paid", ["fulfillment_status"] = "unfulfilled", ["email"] = "ada@example.com",
                ["created_after"] = "2026-10-01", ["updated_after"] = "2026-10-01T12:00:00+02:00", ["max"] = "3",
            }));

            Assert.Equal(new[] { "#1001", "#1002", "#1003" }, records.Select(record => (string?)record.Fields["name"]));
            Assert.Equal(2, shopify.Requests.Count); // the most asked for: no page more
            Assert.All(shopify.Requests, request => Assert.Equal((Api, "shpat_test_1"), (request.Url, request.Token)));
            var first = shopify.Requests[0].Body["variables"]!;
            Assert.Equal("tag:wholesale status:open financial_status:paid fulfillment_status:unfulfilled email:\"ada@example.com\" created_at:>=\"2026-10-01T00:00:00Z\" updated_at:>=\"2026-10-01T10:00:00Z\"", (string?)first["query"]);
            Assert.Equal((3, true), ((int)first["first"]!, (bool)first["reverse"]!)); // newest first, a page no bigger than what is left
            Assert.Equal(("c1", 1), ((string?)shopify.Requests[1].Body["variables"]!["after"], (int)shopify.Requests[1].Body["variables"]!["first"]!));
            Assert.Contains("sortKey: CREATED_AT", (string)shopify.Requests[0].Body["query"]!, StringComparison.Ordinal);

            var order = records[0].Fields;
            Assert.Equal(("1001", "gid://shopify/Order/1001", "paid", "unfulfilled", "250.0", "200.0", "50.0", "SEK"),
                ((string?)order["id"], (string?)order["gid"], (string?)order["financial_status"], (string?)order["fulfillment_status"], (string?)order["total"], (string?)order["subtotal"], (string?)order["tax"], (string?)order["currency"]));
            Assert.Equal(("Ada Lovelace", "77", "wholesale, vip", false, "Gift"), ((string?)order["customer"], (string?)order["customer_id"], (string?)order["tags"], (bool?)order["test"], (string?)order["note"]));
            Assert.Equal("[{\"title\":\"Blue mug\",\"quantity\":2,\"sku\":\"MUG-B\",\"variant_id\":\"4372907\"}]", (string?)order["line_items"]);

            var exact = new ShopifyApi { Answer = shopify.Answer };
            Assert.Equal(2, (await Contexts.DrainAsync(Connector(), Context(exact, ShopifyConnector.OrdersOperation, new() { ["max"] = "2", ["oldest_first"] = "true" }))).Count);
            Assert.Single(exact.Requests); // a page ending at the most asked for: the next is not asked for
            Assert.False((bool)exact.Requests[0].Body["variables"]!["reverse"]!);
            Assert.False(exact.Requests[0].Body["variables"]!.AsObject().ContainsKey("query")); // no filter, no search

            var last = new ShopifyApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"orders\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":\"c9\"},\"nodes\":[" + Order("1001", "#1001") + "]}}}") };
            Assert.Single(await Contexts.DrainAsync(Connector(), Context(last, ShopifyConnector.OrdersOperation, new())));
            Assert.Single(last.Requests); // Shopify's last page

            var admin = new ShopifyApi { Answer = last.Answer };
            await Contexts.DrainAsync(Connector(), Context(admin, ShopifyConnector.OrdersOperation, new(), "https://admin.shopify.com/store/acme-shop/orders", new() { ["api_version"] = "2026-07" }));
            Assert.Equal("https://acme-shop.myshopify.com/admin/api/2026-07/graphql.json", admin.Requests[0].Url); // the admin's address, the version the setting names
        }

        [Fact]
        public async Task An_order_is_got_made_from_variants_and_items_of_no_product_changed_and_deleted()
        {
            var shopify = new ShopifyApi
            {
                Answer = (query, _) => query switch
                {
                    _ when query.Contains("orderCreate(", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{\"orderCreate\":{\"order\":" + Order("1005", "#1005") + ",\"userErrors\":[]}}}"),
                    _ when query.Contains("orderUpdate(", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{\"orderUpdate\":{\"order\":" + Order("1005", "#1005") + ",\"userErrors\":[]}}}"),
                    _ when query.Contains("orderDelete(", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{\"orderDelete\":{\"deletedId\":\"gid://shopify/Order/1005\",\"userErrors\":[]}}}"),
                    _ => (HttpStatusCode.OK, "{\"data\":{\"order\":" + Order("1001", "#1001") + "}}"),
                },
            };

            foreach (var given in new[] { "1001", "gid://shopify/Order/1001", "https://admin.shopify.com/store/acme/orders/1001" })
            {
                var got = await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.GetOrderOperation, new() { ["order"] = given }));
                Assert.Equal("#1001", Assert.Single(got).Fields["name"]);
                Assert.Equal("gid://shopify/Order/1001", (string?)shopify.Requests[^1].Body["variables"]!["id"]);
            }

            var made = await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.CreateOrderOperation, new()
            {
                ["line_items"] = "[{\"variant_id\": 4372907, \"quantity\": 2}, {\"title\": \"Gift wrap\", \"price\": \"25.00\", \"quantity\": \"1\"}]", ["currency"] = "sek",
                ["email"] = "ada@example.com", ["note"] = "Gift", ["tags"] = "web, vip", ["financial_status"] = "Paid", ["test"] = "true", ["send_receipt"] = "true",
            }));
            Assert.Equal("1005", Assert.Single(made).Fields["id"]);
            var created = shopify.Requests[^1].Body["variables"]!;
            Assert.Equal("{\"lineItems\":[{\"variantId\":\"gid://shopify/ProductVariant/4372907\",\"quantity\":2},{\"title\":\"Gift wrap\",\"quantity\":1,\"priceSet\":{\"shopMoney\":{\"amount\":\"25.00\",\"currencyCode\":\"SEK\"}}}],"
                + "\"currency\":\"SEK\",\"email\":\"ada@example.com\",\"note\":\"Gift\",\"tags\":[\"web\",\"vip\"],\"financialStatus\":\"PAID\",\"test\":true}", created["order"]!.ToJsonString());
            Assert.Equal("{\"sendReceipt\":true,\"sendFulfillmentReceipt\":false}", created["options"]!.ToJsonString());

            await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.UpdateOrderOperation, new() { ["order"] = "1005", ["tags"] = "shipped" }));
            Assert.Equal("{\"id\":\"gid://shopify/Order/1005\",\"tags\":[\"shipped\"]}", shopify.Requests[^1].Body["variables"]!["input"]!.ToJsonString());

            var removed = await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.DeleteOrderOperation, new() { ["order"] = "1005" }));
            Assert.Equal(("1005", true), ((string?)Assert.Single(removed).Fields["id"], (bool?)removed[0].Fields["deleted"]));
            Assert.Equal("gid://shopify/Order/1005", (string?)shopify.Requests[^1].Body["variables"]!["orderId"]);

            var none = new ShopifyApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"order\":null}}") };
            Assert.Equal("Shopify has no order 9.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(none, ShopifyConnector.GetOrderOperation, new() { ["order"] = "9" })))).Message);
        }

        [Fact]
        public async Task The_products_a_search_finds_are_read_and_a_product_is_made_changed_and_deleted()
        {
            var shopify = new ShopifyApi
            {
                Answer = (query, _) => query switch
                {
                    _ when query.Contains("productCreate(", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{\"productCreate\":{\"product\":" + Product("88", "Blue mug") + ",\"userErrors\":[]}}}"),
                    _ when query.Contains("productUpdate(", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{\"productUpdate\":{\"product\":" + Product("88", "Blue mug") + ",\"userErrors\":[]}}}"),
                    _ when query.Contains("productDelete(", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{\"productDelete\":{\"deletedProductId\":\"gid://shopify/Product/88\",\"userErrors\":[]}}}"),
                    _ when query.Contains("products(", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{\"products\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},\"nodes\":[" + Product("88", "Blue mug") + "]}}}"),
                    _ => (HttpStatusCode.OK, "{\"data\":{\"product\":" + Product("88", "Blue mug") + "}}"),
                },
            };

            var found = await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.ProductsOperation, new() { ["status"] = "active", ["vendor"] = "Acme \"Ceramics\"", ["product_type"] = "Mugs", ["title"] = "mug*" }));
            Assert.Equal("status:active vendor:\"Acme \\\"Ceramics\\\"\" product_type:\"Mugs\" title:mug*", (string?)shopify.Requests[0].Body["variables"]!["query"]);
            var product = Assert.Single(found).Fields;
            Assert.Equal(("88", "Blue mug", "active", "Acme Ceramics", "Mugs", "kitchen", 12L, "100.0", "120.0", "SEK"),
                ((string?)product["id"], (string?)product["title"], (string?)product["status"], (string?)product["vendor"], (string?)product["product_type"], (string?)product["tags"], (long?)product["total_inventory"], (string?)product["min_price"], (string?)product["max_price"], (string?)product["currency"]));
            Assert.Equal("[{\"id\":\"4372907\",\"title\":\"Large\",\"sku\":\"MUG-B\",\"price\":\"120.0\",\"inventory\":12}]", (string?)product["variants"]);

            await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.GetProductOperation, new() { ["product"] = "https://admin.shopify.com/store/acme/products/88" }));
            Assert.Equal("gid://shopify/Product/88", (string?)shopify.Requests[^1].Body["variables"]!["id"]);

            await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.CreateProductOperation, new() { ["title"] = "Blue mug", ["description_html"] = "<p>Blue.</p>\n<p>Big.</p>", ["vendor"] = "Acme", ["tags"] = "kitchen", ["status"] = "draft" }));
            Assert.Equal("{\"title\":\"Blue mug\",\"descriptionHtml\":\"<p>Blue.</p>\\n<p>Big.</p>\",\"vendor\":\"Acme\",\"tags\":[\"kitchen\"],\"status\":\"DRAFT\"}", shopify.Requests[^1].Body["variables"]!["product"]!.ToJsonString(Plain));
            Assert.Contains("productCreate(product: $product)", (string)shopify.Requests[^1].Body["query"]!, StringComparison.Ordinal);

            await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.UpdateProductOperation, new() { ["product"] = "88", ["status"] = "archived" }));
            Assert.Equal("{\"status\":\"ARCHIVED\",\"id\":\"gid://shopify/Product/88\"}", shopify.Requests[^1].Body["variables"]!["product"]!.ToJsonString());
            Assert.Contains("$product: ProductUpdateInput!) { productUpdate(product: $product)", (string)shopify.Requests[^1].Body["query"]!, StringComparison.Ordinal);

            var removed = await Contexts.DrainAsync(Connector(), Context(shopify, ShopifyConnector.DeleteProductOperation, new() { ["product"] = "gid://shopify/Product/88" }));
            Assert.Equal(("88", true), ((string?)Assert.Single(removed).Fields["id"], (bool?)removed[0].Fields["deleted"]));
            Assert.Equal("{\"id\":\"gid://shopify/Product/88\"}", shopify.Requests[^1].Body["variables"]!["input"]!.ToJsonString());
        }

        private static readonly System.Text.Json.JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent()
        {
            var shopify = new ShopifyApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Shop, Dictionary<string, string>? settings = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(shopify, operation, parameters, endpoint, settings)))).Message;

            Assert.Equal("'gid://shopify/Product/1' is not a Shopify order's id.", await Refused(ShopifyConnector.GetOrderOperation, new() { ["order"] = "gid://shopify/Product/1" }));
            Assert.Equal("'10/../1' is not a Shopify product's id.", await Refused(ShopifyConnector.DeleteProductOperation, new() { ["product"] = "10/../1" }));
            Assert.Equal("'abc' is not a Shopify variant's id.", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "[{\"variant_id\": \"abc\"}]" }));
            Assert.Equal("The line items are not JSON - [{\"variant_id\": \"4372907\", \"quantity\": 1}].", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "4372907 x 2" }));
            Assert.Equal("The line items are not a JSON list.", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "4372907" }));
            Assert.Equal("A line item's quantity is not a whole number of at least 1.", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "[{\"variant_id\": \"1\", \"quantity\": 0}]" }));
            Assert.Equal("A line item names neither a variant (variant_id) nor a title and a price.", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "[{\"title\": \"Gift wrap\"}]" }));
            Assert.Equal("Give the currency - an item of no product has its price in it.", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "[{\"title\": \"Gift wrap\", \"price\": \"25\"}]" }));
            Assert.Equal("'25 kr' is not a price - 25.00.", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "[{\"title\": \"Gift wrap\", \"price\": \"25 kr\"}]", ["currency"] = "SEK" }));
            Assert.Equal("'kronor' is not a currency - SEK.", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "[{\"variant_id\": \"1\"}]", ["currency"] = "kronor" }));
            Assert.Equal("Give at least one line item.", await Refused(ShopifyConnector.CreateOrderOperation, new() { ["line_items"] = "[]" }));
            Assert.Equal("The note has a line break - one line only.", await Refused(ShopifyConnector.UpdateOrderOperation, new() { ["order"] = "1", ["note"] = "a\nb" }));
            Assert.Equal("Give something to change - email, note or tags.", await Refused(ShopifyConnector.UpdateOrderOperation, new() { ["order"] = "1" }));
            Assert.Equal("Give something to change - title, description, vendor, type, tags, handle or status.", await Refused(ShopifyConnector.UpdateProductOperation, new() { ["product"] = "1" }));
            Assert.Equal("Give the product's title.", await Refused(ShopifyConnector.CreateProductOperation, new() { ["vendor"] = "Acme" }));
            Assert.StartsWith("'shipped' is not a status", await Refused(ShopifyConnector.OrdersOperation, new() { ["status"] = "shipped" }));
            Assert.StartsWith("'unpaid' is not a financial status", await Refused(ShopifyConnector.OrdersOperation, new() { ["financial_status"] = "unpaid" }));
            Assert.StartsWith("'live' is not a product status", await Refused(ShopifyConnector.CreateProductOperation, new() { ["title"] = "Mug", ["status"] = "live" }));
            Assert.Equal("'ada at example.com' is not an address.", await Refused(ShopifyConnector.OrdersOperation, new() { ["email"] = "ada at example.com" }));
            Assert.Equal("'yesterday' is not a date or a time (created_after).", await Refused(ShopifyConnector.OrdersOperation, new() { ["created_after"] = "yesterday" }));
            Assert.Equal("'latest' is not a Shopify API version - 2026-10.", await Refused(ShopifyConnector.OrdersOperation, new(), settings: new() { ["api_version"] = "latest" }));
            Assert.Equal("'https://admin.shopify.com/orders' names no shop - https://your-store.myshopify.com.", await Refused(ShopifyConnector.OrdersOperation, new(), "https://admin.shopify.com/orders"));
            Assert.Empty(shopify.Requests);
        }

        [Fact]
        public async Task A_throttled_query_is_asked_again_and_shopifys_failures_and_user_errors_are_said()
        {
            var calls = 0;
            var throttled = new ShopifyApi
            {
                Answer = (_, _) => ++calls == 1
                    ? (HttpStatusCode.OK, "{\"errors\":[{\"message\":\"Throttled\",\"extensions\":{\"code\":\"THROTTLED\"}}],\"extensions\":{\"cost\":{\"requestedQueryCost\":52,\"throttleStatus\":{\"maximumAvailable\":2000,\"currentlyAvailable\":50,\"restoreRate\":100}}}}")
                    : (HttpStatusCode.OK, "{\"data\":{\"order\":" + Order("1001", "#1001") + "}}"),
            };
            Assert.Single(await Contexts.DrainAsync(Connector(), Context(throttled, ShopifyConnector.GetOrderOperation, new() { ["order"] = "1001" })));
            Assert.Equal(2, throttled.Requests.Count);

            async Task<string> Failed(HttpStatusCode status, string answer, string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(new ShopifyApi { Answer = (_, _) => (status, answer) }, operation, parameters)))).Message;

            Assert.Equal("HTTP 401: [API] Invalid API key or access token (unrecognized login or wrong password)",
                await Failed(HttpStatusCode.Unauthorized, "{\"errors\":\"[API] Invalid API key or access token (unrecognized login or wrong password)\"}", ShopifyConnector.OrdersOperation, new()));
            Assert.Equal("Shopify said: Field 'nope' doesn't exist on type 'Order'",
                await Failed(HttpStatusCode.OK, "{\"errors\":[{\"message\":\"Field 'nope' doesn't exist on type 'Order'\"}]}", ShopifyConnector.GetOrderOperation, new() { ["order"] = "1" }));
            Assert.Equal("Shopify refused it: product.title: Title can't be blank; handle has already been taken",
                await Failed(HttpStatusCode.OK, "{\"data\":{\"productCreate\":{\"product\":null,\"userErrors\":[{\"field\":[\"product\",\"title\"],\"message\":\"Title can't be blank\"},{\"field\":null,\"message\":\"handle has already been taken\"}]}}}", ShopifyConnector.CreateProductOperation, new() { ["title"] = "x" }));

            var stuck = new ShopifyApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"errors\":[{\"message\":\"Throttled\",\"extensions\":{\"code\":\"THROTTLED\"}}],\"extensions\":{\"cost\":{\"requestedQueryCost\":1,\"throttleStatus\":{\"currentlyAvailable\":0,\"restoreRate\":1000}}}}") };
            Assert.Equal("Shopify said: Throttled", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(stuck, ShopifyConnector.GetOrderOperation, new() { ["order"] = "1" })))).Message);
            Assert.Equal(6, stuck.Requests.Count); // asked again five times, then said
        }

        [Fact]
        public async Task A_test_names_the_shop()
        {
            var shopify = new ShopifyApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"shop\":{\"name\":\"Acme\",\"myshopifyDomain\":\"acme.myshopify.com\",\"currencyCode\":\"SEK\"}}}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(shopify), "acme.myshopify.example", credentials: Credentials));
            Assert.Equal((true, "Signed in to Acme (acme.myshopify.com)."), (tested.Success, tested.Message));
            Assert.Equal(("https://acme.myshopify.example/admin/api/2026-10/graphql.json", "shpat_test_1"), (shopify.Requests[0].Url, shopify.Requests[0].Token)); // a bare host taken as https
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new ShopifyApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"errors\":\"[API] Invalid API key or access token\"}") }), Shop, credentials: Credentials));
            Assert.Equal((false, "HTTP 401: [API] Invalid API key or access token"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "shopify", ConnectorCategory.Commerce), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "orders", "get_order", "create_order", "update_order", "delete_order", "products", "get_product", "create_product", "update_product", "delete_product" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
