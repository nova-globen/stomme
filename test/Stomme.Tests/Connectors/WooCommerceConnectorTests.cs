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
using Stomme.Modules.Connectors.WooCommerce;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - WooCommerce (n8n's WooCommerce node: orders, products and customers) over a store's REST API: the consumer key and
    /// secret by the Basic scheme; a pasted admin address cut back to the store's, a store in a folder kept; orders by status,
    /// customer, product, search and time, sorted, page by page up to the most asked for or WooCommerce's last page, each a plain
    /// record with its line items; an order, a product and a customer got, created, updated and deleted - to the trash, or for
    /// good when asked, a customer always for good; what is not an id, a word of WooCommerce's, an address, a time, a price, a
    /// flag, JSON of the right shape or one line refused before anything is sent; WooCommerce's refusal its code, message and the
    /// first invalid parameter; a test counting the orders.
    /// </summary>
    public sealed class WooCommerceConnectorTests
    {
        private const string Store = "https://shop.example.com";

        private const string Api = Store + "/wp-json/wc/v3";

        private sealed class WooCommerceApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode Status, string Body, int? Pages)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                var (status, answer, pages) = Answer(request.Method, url);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (pages is { } count)
                {
                    response.Headers.Add("X-WP-TotalPages", count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    response.Headers.Add("X-WP-Total", (count * 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                return response;
            }
        }

        private static ConnectorExtractContext Context(WooCommerceApi store, string operation, Dictionary<string, string> parameters, string endpoint = Store) =>
            Contexts.Extract(new StubEgress(store), endpoint, operation, parameters, null, new Dictionary<string, string> { ["username"] = "ck_test_1", ["password"] = "cs_test_1" });

        private static WooCommerceConnector Connector() => new();

        private static string Orders(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(id => "{\"id\":" + id + ",\"number\":\"" + id + "\",\"status\":\"processing\",\"currency\":\"SEK\",\"total\":\"249.00\",\"total_tax\":\"49.80\",\"customer_id\":7,"
                + "\"billing\":{\"first_name\":\"Ada\",\"last_name\":\"Lovelace\",\"email\":\"ada@example.com\"},\"shipping\":{\"city\":\"Lund\"},\"payment_method\":\"bacs\",\"line_items\":[{\"id\":1,\"product_id\":93,\"name\":\"Mug\",\"sku\":\"MUG-1\",\"quantity\":2,\"total\":\"199.00\",\"taxes\":[]}],"
                + "\"date_created\":\"2026-10-01T12:00:00\",\"date_created_gmt\":\"2026-10-01T10:00:00\"}")) + "]";

        [Fact]
        public async Task A_stores_orders_are_read_page_by_page_up_to_the_most_asked_for_the_key_by_basic()
        {
            var store = new WooCommerceApi
            {
                Answer = (_, url) => url.EndsWith("&page=1", StringComparison.Ordinal) ? (HttpStatusCode.OK, Orders(1, 100), 9) : (HttpStatusCode.OK, Orders(101, 50), 9),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.OrdersOperation, new()
            {
                ["status"] = "Processing, on-hold", ["customer"] = "7", ["product"] = "93", ["search"] = "mug & tea", ["after"] = "2026-10-01T02:00:00+02:00", ["order"] = "desc", ["orderby"] = "date", ["max"] = "150",
            }, endpoint: "https://shop.example.com/wp-admin/edit.php?post_type=shop_order"));

            Assert.Equal(150, records.Count); // the most asked for
            Assert.Equal(2, store.Requests.Count);
            Assert.Equal(Api + "/orders?status=processing%2Con-hold&customer=7&product=93&after=2026-10-01T00%3A00%3A00&search=mug%20%26%20tea&order=desc&orderby=date&per_page=100&page=1", store.Requests[0].Url); // a pasted admin address: the store's
            Assert.EndsWith("&per_page=50&page=2", store.Requests[1].Url, StringComparison.Ordinal); // a page no bigger than what is left
            Assert.All(store.Requests, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ck_test_1:cs_test_1")), request.Authorization));
            var order = records[0].Fields;
            Assert.Equal((1L, "1", "processing", "SEK", "249.00", 7L, "ada@example.com", "Ada Lovelace", "2026-10-01T10:00:00"),
                ((long?)order["id"], (string?)order["number"], (string?)order["status"], (string?)order["currency"], (string?)order["total"], (long?)order["customer_id"], (string?)order["email"], (string?)order["name"], (string?)order["date_created"]));
            Assert.Equal("[{\"product_id\":93,\"name\":\"Mug\",\"sku\":\"MUG-1\",\"quantity\":2,\"total\":\"199.00\"}]", order["line_items"]);

            var last = new WooCommerceApi { Answer = (_, _) => (HttpStatusCode.OK, Orders(1, 100), 1) };
            Assert.Equal(100, (await Contexts.DrainAsync(Connector(), Context(last, WooCommerceConnector.OrdersOperation, new(), endpoint: "http://127.0.0.1:5276/shop/"))).Count);
            Assert.Equal("http://127.0.0.1:5276/shop/wp-json/wc/v3/orders?per_page=100&page=1", Assert.Single(last.Requests).Url); // a store in a folder kept; WooCommerce's last page the last

            var three = new WooCommerceApi { Answer = (_, url) => (HttpStatusCode.OK, Orders(1, 100), 3) };
            Assert.Equal(300, (await Contexts.DrainAsync(Connector(), Context(three, WooCommerceConnector.OrdersOperation, new()))).Count); // by default up to 1,000 - every one of WooCommerce's three pages
            Assert.Equal(3, three.Requests.Count);

            var short_ = new WooCommerceApi { Answer = (_, _) => (HttpStatusCode.OK, Orders(1, 3), null) };
            Assert.Equal(3, (await Contexts.DrainAsync(Connector(), Context(short_, WooCommerceConnector.ProductsOperation, new()
            {
                ["search"] = "mug", ["sku"] = "MUG-1", ["status"] = "publish", ["type"] = "simple", ["category"] = "15", ["tag"] = "4", ["stock_status"] = "instock", ["orderby"] = "price",
            }))).Count);
            Assert.Equal(Api + "/products?status=publish&type=simple&stock_status=instock&category=15&tag=4&sku=MUG-1&search=mug&orderby=price&per_page=100&page=1", Assert.Single(short_.Requests).Url); // a short page the last

            var customers = new WooCommerceApi { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":7,\"email\":\"ada@example.com\",\"first_name\":\"Ada\",\"role\":\"customer\",\"is_paying_customer\":true,\"billing\":{\"city\":\"Lund\"}}]", 1) };
            var customer = Assert.Single(await Contexts.DrainAsync(Connector(), Context(customers, WooCommerceConnector.CustomersOperation, new() { ["email"] = "ada@example.com", ["role"] = "all", ["orderby"] = "registered_date" }))).Fields;
            Assert.Equal(Api + "/customers?email=ada%40example.com&role=all&orderby=registered_date&per_page=100&page=1", customers.Requests[0].Url);
            Assert.Equal((7L, "ada@example.com", "Ada", true, "{\"city\":\"Lund\"}"), ((long?)customer["id"], (string?)customer["email"], (string?)customer["first_name"], (bool?)customer["is_paying_customer"], (string?)customer["billing"]));
        }

        [Fact]
        public async Task An_order_a_product_and_a_customer_are_got_created_updated_and_deleted()
        {
            var store = new WooCommerceApi
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.Contains("/products", StringComparison.Ordinal) ? "{\"id\":93,\"name\":\"Mug\",\"sku\":\"MUG-1\",\"price\":\"199.00\",\"categories\":[{\"id\":15,\"name\":\"Kitchen\"}],\"tags\":[{\"id\":4,\"name\":\"gift\"}]}"
                    : url.Contains("/customers", StringComparison.Ordinal) ? "{\"id\":7,\"email\":\"ada@example.com\"}" : Orders(1001, 1)[1..^1], null),
            };

            Assert.Equal(1001L, Assert.Single(await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.GetOrderOperation, new() { ["order"] = "1001" }))).Fields["id"]);
            Assert.Equal((HttpMethod.Get, Api + "/orders/1001"), (store.Requests[^1].Method, store.Requests[^1].Url));

            await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.CreateOrderOperation, new()
            {
                ["status"] = "processing", ["customer_id"] = "7", ["payment_method"] = "bacs", ["payment_method_title"] = "Bank transfer", ["set_paid"] = "true", ["currency"] = "SEK", ["customer_note"] = "Gift wrap",
                ["billing"] = "{\"first_name\": \"Ada\", \"email\": \"ada@example.com\"}", ["line_items"] = "[{\"product_id\": 93, \"quantity\": 2}]", ["coupon_lines"] = "[{\"code\": \"WINTER10\"}]",
            }));
            Assert.Equal((HttpMethod.Post, Api + "/orders", "{\"status\":\"processing\",\"customer_id\":7,\"payment_method\":\"bacs\",\"payment_method_title\":\"Bank transfer\",\"currency\":\"SEK\",\"customer_note\":\"Gift wrap\",\"set_paid\":true,\"billing\":{\"first_name\":\"Ada\",\"email\":\"ada@example.com\"},\"line_items\":[{\"product_id\":93,\"quantity\":2}],\"coupon_lines\":[{\"code\":\"WINTER10\"}]}"),
                (store.Requests[^1].Method, store.Requests[^1].Url, store.Requests[^1].Body));

            await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.UpdateOrderOperation, new() { ["order"] = "1001", ["status"] = "completed", ["transaction_id"] = "tx_9" }));
            Assert.Equal((HttpMethod.Put, Api + "/orders/1001", "{\"status\":\"completed\",\"transaction_id\":\"tx_9\"}"), (store.Requests[^1].Method, store.Requests[^1].Url, store.Requests[^1].Body));

            var trashed = Assert.Single(await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.DeleteOrderOperation, new() { ["order"] = "1001" })));
            Assert.Equal((HttpMethod.Delete, Api + "/orders/1001?force=false"), (store.Requests[^1].Method, store.Requests[^1].Url));
            Assert.Equal((1001L, true), ((long?)trashed.Fields["id"], (bool?)trashed.Fields["trashed"]));
            var gone = Assert.Single(await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.DeleteOrderOperation, new() { ["order"] = "1001", ["force"] = "TRUE" })));
            Assert.Equal(Api + "/orders/1001?force=true", store.Requests[^1].Url);
            Assert.Equal(true, gone.Fields["deleted"]);

            var made = Assert.Single(await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.CreateProductOperation, new()
            {
                ["name"] = "Mug", ["type"] = "simple", ["status"] = "publish", ["regular_price"] = "199", ["sale_price"] = "149.50", ["sku"] = "MUG-1", ["categories"] = "15, 16", ["tags"] = "4",
                ["manage_stock"] = "true", ["stock_quantity"] = "12", ["images"] = "https://cdn.example.com/mug.png", ["meta_data"] = "[{\"key\": \"source\", \"value\": \"pipeline\"}]",
            })));
            Assert.Equal((HttpMethod.Post, Api + "/products", "{\"name\":\"Mug\",\"type\":\"simple\",\"status\":\"publish\",\"regular_price\":\"199\",\"sale_price\":\"149.50\",\"sku\":\"MUG-1\",\"categories\":[{\"id\":15},{\"id\":16}],\"tags\":[{\"id\":4}],\"manage_stock\":true,\"stock_quantity\":12,\"images\":[{\"src\":\"https://cdn.example.com/mug.png\"}],\"meta_data\":[{\"key\":\"source\",\"value\":\"pipeline\"}]}"),
                (store.Requests[^1].Method, store.Requests[^1].Url, store.Requests[^1].Body));
            Assert.Equal(("Mug", "Kitchen", "gift", "199.00"), ((string?)made.Fields["name"], (string?)made.Fields["categories"], (string?)made.Fields["tags"], (string?)made.Fields["price"]));
            await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.UpdateProductOperation, new() { ["product"] = "93", ["sale_price"] = "129", ["stock_status"] = "onbackorder" }));
            Assert.Equal((HttpMethod.Put, Api + "/products/93", "{\"stock_status\":\"onbackorder\",\"sale_price\":\"129\"}"), (store.Requests[^1].Method, store.Requests[^1].Url, store.Requests[^1].Body));
            await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.GetProductOperation, new() { ["product"] = "93" }));
            Assert.Equal((HttpMethod.Get, Api + "/products/93"), (store.Requests[^1].Method, store.Requests[^1].Url));
            await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.DeleteProductOperation, new() { ["product"] = "93" }));
            Assert.Equal((HttpMethod.Delete, Api + "/products/93?force=false"), (store.Requests[^1].Method, store.Requests[^1].Url));

            await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.CreateCustomerOperation, new() { ["email"] = "ada@example.com", ["first_name"] = "Ada", ["username"] = "ada", ["shipping"] = "{\"city\": \"Lund\"}" }));
            Assert.Equal((HttpMethod.Post, Api + "/customers", "{\"email\":\"ada@example.com\",\"first_name\":\"Ada\",\"username\":\"ada\",\"shipping\":{\"city\":\"Lund\"}}"), (store.Requests[^1].Method, store.Requests[^1].Url, store.Requests[^1].Body));
            await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.UpdateCustomerOperation, new() { ["customer"] = "7", ["last_name"] = "Lovelace" }));
            Assert.Equal((HttpMethod.Put, Api + "/customers/7", "{\"last_name\":\"Lovelace\"}"), (store.Requests[^1].Method, store.Requests[^1].Url, store.Requests[^1].Body));
            await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.GetCustomerOperation, new() { ["customer"] = "7" }));
            Assert.Equal((HttpMethod.Get, Api + "/customers/7"), (store.Requests[^1].Method, store.Requests[^1].Url));
            var deleted = Assert.Single(await Contexts.DrainAsync(Connector(), Context(store, WooCommerceConnector.DeleteCustomerOperation, new() { ["customer"] = "7" })));
            Assert.Equal((HttpMethod.Delete, Api + "/customers/7?force=true"), (store.Requests[^1].Method, store.Requests[^1].Url)); // no trash for a customer
            Assert.Equal(true, deleted.Fields["deleted"]);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_woocommerces_refusal_is_its_words()
        {
            var store = new WooCommerceApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Store) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(store, operation, parameters, endpoint)))).Message;

            Assert.Equal("'1001/../x' is not a WooCommerce order's id (digits).", await Refused(WooCommerceConnector.GetOrderOperation, new() { ["order"] = "1001/../x" }));
            Assert.StartsWith("'shipped' is not an order's status", await Refused(WooCommerceConnector.OrdersOperation, new() { ["status"] = "processing, shipped" }));
            Assert.Equal("'abc' is not a WooCommerce customer's id (digits).", await Refused(WooCommerceConnector.OrdersOperation, new() { ["customer"] = "abc" }));
            Assert.Equal("'last week' is not a time - 2026-10-01T00:00:00Z.", await Refused(WooCommerceConnector.OrdersOperation, new() { ["before"] = "last week" }));
            Assert.StartsWith("'cheapest' is not a field to sort by", await Refused(WooCommerceConnector.OrdersOperation, new() { ["orderby"] = "cheapest" }));
            Assert.StartsWith("'up' is not a sort order", await Refused(WooCommerceConnector.ProductsOperation, new() { ["order"] = "up" }));
            Assert.StartsWith("'sold' is not a stock status", await Refused(WooCommerceConnector.ProductsOperation, new() { ["stock_status"] = "sold" }));
            Assert.Equal("'Shop Manager' is not a role - customer, subscriber or all.", await Refused(WooCommerceConnector.CustomersOperation, new() { ["role"] = "Shop Manager" }));
            Assert.Equal("'ada' is not an address.", await Refused(WooCommerceConnector.CustomersOperation, new() { ["email"] = "ada" }));
            Assert.Equal("The search has a line break - one line only.", await Refused(WooCommerceConnector.CustomersOperation, new() { ["search"] = "ada\nx" }));
            Assert.Equal("'maybe' is not true or false (set_paid).", await Refused(WooCommerceConnector.CreateOrderOperation, new() { ["set_paid"] = "maybe" }));
            Assert.Equal("'billing' is not JSON - a JSON object - {\"first_name\": \"Ada\", \"city\": \"Lund\"}.", await Refused(WooCommerceConnector.CreateOrderOperation, new() { ["billing"] = "Ada, Lund" }));
            Assert.Equal("'shipping' is not a JSON object - {\"first_name\": \"Ada\", \"city\": \"Lund\"}.", await Refused(WooCommerceConnector.CreateOrderOperation, new() { ["shipping"] = "[1]" }));
            Assert.Equal("'line_items' is not a JSON list of objects - [{\"product_id\": 93, \"quantity\": 2}].", await Refused(WooCommerceConnector.CreateOrderOperation, new() { ["line_items"] = "[93, 94]" }));
            Assert.Equal("'coupon_lines' is not a JSON list of objects - [{\"code\": \"WINTER10\"}].", await Refused(WooCommerceConnector.UpdateOrderOperation, new() { ["order"] = "1001", ["coupon_lines"] = "[]" }));
            Assert.Equal("'line_items' is not a JSON list of objects - [{\"product_id\": 93, \"quantity\": 2}].", await Refused(WooCommerceConnector.CreateOrderOperation, new() { ["line_items"] = "{\"product_id\": 93}" }));
            Assert.Equal("Give something to change on the order.", await Refused(WooCommerceConnector.UpdateOrderOperation, new() { ["order"] = "1001" }));
            Assert.Equal("'yes' is not true or false (force).", await Refused(WooCommerceConnector.DeleteOrderOperation, new() { ["order"] = "1001", ["force"] = "yes" }));
            Assert.Equal("Give the product's name.", await Refused(WooCommerceConnector.CreateProductOperation, new() { ["sku"] = "MUG-1" }));
            Assert.Equal("'free' is not a regular price - 199.00.", await Refused(WooCommerceConnector.CreateProductOperation, new() { ["name"] = "Mug", ["regular_price"] = "free" }));
            Assert.Equal("'-5' is not a sale price - 199.00.", await Refused(WooCommerceConnector.UpdateProductOperation, new() { ["product"] = "93", ["sale_price"] = "-5" }));
            Assert.Equal("'a few' is not a stock quantity - a whole number.", await Refused(WooCommerceConnector.UpdateProductOperation, new() { ["product"] = "93", ["stock_quantity"] = "a few" }));
            Assert.Equal("'mug.png' is not an image's address - https://...", await Refused(WooCommerceConnector.UpdateProductOperation, new() { ["product"] = "93", ["images"] = "mug.png" }));
            Assert.Equal("'x' is not a WooCommerce category's id (digits).", await Refused(WooCommerceConnector.UpdateProductOperation, new() { ["product"] = "93", ["categories"] = "15, x" }));
            Assert.Equal("Give the customer's address.", await Refused(WooCommerceConnector.CreateCustomerOperation, new() { ["first_name"] = "Ada" }));
            Assert.Equal("Give something to change on the customer.", await Refused(WooCommerceConnector.UpdateCustomerOperation, new() { ["customer"] = "7" }));
            Assert.StartsWith("WooCommerce has no operation 'refund_order'", await Refused("refund_order", new() { ["order"] = "1001" }));
            Assert.Equal("'ftp://x.example' is not a store's address - https://shop.example.com.", await Refused(WooCommerceConnector.OrdersOperation, new(), "ftp://x.example"));
            Assert.Empty(store.Requests);

            var refusing = new WooCommerceApi { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"code\":\"rest_invalid_param\",\"message\":\"Invalid parameter(s): line_items\",\"data\":{\"status\":400,\"params\":{\"line_items\":\"line_items[0][product_id] is not of type integer.\"}}}", null) };
            Assert.Equal("HTTP 400 (rest_invalid_param): Invalid parameter(s): line_items - line_items: line_items[0][product_id] is not of type integer.",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, WooCommerceConnector.CreateOrderOperation, new() { ["line_items"] = "[{\"product_id\": \"x\"}]" })))).Message);
        }

        [Fact]
        public async Task A_test_counts_the_orders()
        {
            var store = new WooCommerceApi { Answer = (_, _) => (HttpStatusCode.OK, "[]", 12) };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(store), Store, credentials: new Dictionary<string, string> { ["username"] = "ck_test_1", ["password"] = "cs_test_1" }));
            Assert.Equal((true, "Reached the store: 1,200 orders."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/orders?per_page=1", store.Requests[0].Url);
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new WooCommerceApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"code\":\"woocommerce_rest_cannot_view\",\"message\":\"Sorry, you cannot list resources.\",\"data\":{\"status\":401}}", null) }),
                Store, credentials: new Dictionary<string, string> { ["username"] = "ck_test_1", ["password"] = "cs_test_1" }));
            Assert.Equal((false, "HTTP 401 (woocommerce_rest_cannot_view): Sorry, you cannot list resources."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "woocommerce", ConnectorCategory.Commerce), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "orders", "get_order", "create_order", "update_order", "delete_order", "products", "get_product", "create_product", "update_product", "delete_product", "customers", "get_customer", "create_customer", "update_customer", "delete_customer" },
                Connector().Operations.Select(operation => operation.Name));
        }
    }
}
