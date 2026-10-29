#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Shopify
{
    /// <summary>
    /// Shopify (BL-229, n8n's Shopify node: orders and products listed, got, created, updated and deleted): a shop's GraphQL Admin
    /// API (<c>https://your-store.myshopify.com/admin/api/{version}/graphql.json</c>) - Shopify's own, its REST Admin API legacy
    /// since 2024 - an app's Admin API access token (<c>shpat_...</c>) in Shopify's <c>X-Shopify-Access-Token</c> header. A test
    /// names the shop.
    /// </summary>
    /// <remarks>
    /// An order and a product are named by their id - its digits, Shopify's <c>gid://shopify/Order/...</c>, or an admin address
    /// ending in it - and a variant the same way, each held to its shape before it is sent. <c>orders</c>: the orders a search
    /// finds - Shopify's own <c>query</c>, and a status, a financial and a fulfillment status, an address and times after which
    /// they were made or changed - newest first unless asked for the oldest, page by page by Shopify's cursor up to <c>max</c>;
    /// one record each: its id (digits) and gid, name (#1001), email, phone, times, financial and fulfillment status, total,
    /// subtotal, tax and currency (Shopify's decimal text), the customer by name and id, its line items (JSON), note, tags and
    /// whether it is a test. Without the read_all_orders scope Shopify gives an app the last 60 days of orders only.
    /// <c>get_order</c> one. <c>create_order</c>: line items as JSON - a variant's id and a quantity, or a title, a price and a
    /// quantity for an item of no product (the order's currency then given) - an email, a note, tags, a financial status,
    /// whether it is a test and whether Shopify sends the receipts; <c>update_order</c> its email, note or tags; <c>delete_order</c>
    /// removes one (Shopify deletes only some orders; others are cancelled in Shopify). <c>products</c>: the products a search
    /// finds - <c>query</c>, a status, a vendor, a type, a title and times - page by page; one record each: its id and gid, title,
    /// handle, status, vendor, type, tags, description (HTML), inventory, lowest and highest price, its variants (JSON), times and
    /// its address in the store. <c>get_product</c>, <c>create_product</c> (a title, a description, a vendor, a type, tags, a
    /// handle and a status), <c>update_product</c> and <c>delete_product</c>. A throttled query waits as Shopify's cost says; a
    /// failure is thrown with Shopify's message, a refused change with its user errors.
    /// </remarks>
    public sealed class ShopifyConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultApiVersion = "2026-10";

        public const string OrdersOperation = "orders";

        public const string GetOrderOperation = "get_order";

        public const string CreateOrderOperation = "create_order";

        public const string UpdateOrderOperation = "update_order";

        public const string DeleteOrderOperation = "delete_order";

        public const string ProductsOperation = "products";

        public const string GetProductOperation = "get_product";

        public const string CreateProductOperation = "create_product";

        public const string UpdateProductOperation = "update_product";

        public const string DeleteProductOperation = "delete_product";

        public const int DefaultMax = 1000;

        private const int MaxThrottleWaits = 5;

        private static readonly Regex ApiVersion = new("^20[0-9]{2}-(01|04|07|10)$", RegexOptions.CultureInvariant);

        private static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex ShopName = new("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> OrderStatuses = new(StringComparer.Ordinal) { "open", "closed", "cancelled", "not_closed" };

        private static readonly HashSet<string> FinancialStatuses = new(StringComparer.Ordinal) { "paid", "pending", "authorized", "partially_paid", "partially_refunded", "refunded", "voided", "expired" };

        private static readonly HashSet<string> FulfillmentStatuses = new(StringComparer.Ordinal) { "unshipped", "shipped", "fulfilled", "partial", "scheduled", "on_hold", "unfulfilled", "request_declined" };

        private static readonly HashSet<string> ProductStatuses = new(StringComparer.Ordinal) { "active", "draft", "archived" };

        private static readonly HashSet<string> NewOrderFinancialStatuses = new(StringComparer.Ordinal) { "authorized", "expired", "paid", "partially_paid", "partially_refunded", "pending", "refunded", "voided" };

        private const string OrderFields = """
            fragment OrderFields on Order {
              id legacyResourceId name email phone createdAt updatedAt processedAt cancelledAt closedAt test note tags
              displayFinancialStatus displayFulfillmentStatus currencyCode
              totalPriceSet { shopMoney { amount } } subtotalPriceSet { shopMoney { amount } } totalTaxSet { shopMoney { amount } }
              customer { id displayName }
              lineItems(first: 100) { nodes { title quantity sku variant { id } } }
            }
            """;

        private const string ProductFields = """
            fragment ProductFields on Product {
              id legacyResourceId title handle status vendor productType tags descriptionHtml totalInventory createdAt updatedAt publishedAt onlineStoreUrl
              priceRangeV2 { minVariantPrice { amount currencyCode } maxVariantPrice { amount } }
              variants(first: 100) { nodes { id title sku price inventoryQuantity } }
            }
            """;

        public string ConnectorType => "shopify";

        public string DisplayName => "Shopify";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The token as Shopify names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Admin API access token (shpat_...)" };

        public string EndpointExample => "https://your-store.myshopify.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Commerce };

        public Uri WebsiteUrl { get; } = new("https://www.shopify.com");

        public Uri ApiDocumentationUrl { get; } = new("https://shopify.dev/docs/api/admin-graphql");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("api_version", "API version", ConnectorConfigFieldType.Text, Required: false, Description: "Shopify's API version - " + DefaultApiVersion + ".") { Default = DefaultApiVersion },
        });

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField OrderField = new("order", "Order", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - its digits, gid://shopify/Order/..., or its address in the admin.");

        private static readonly ConnectorConfigField ProductField = new("product", "Product", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - its digits, gid://shopify/Product/..., or its address in the admin.");

        private static ConnectorConfigField[] Searched(params ConnectorConfigField[] filters) => new[]
        {
            new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Shopify's search syntax - tag:wholesale -status:cancelled."),
        }.Concat(filters).Concat(new[]
        {
            new ConnectorConfigField("created_after", "Made after", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
            new ConnectorConfigField("updated_after", "Changed after", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
            new ConnectorConfigField("oldest_first", "Oldest first", ConnectorConfigFieldType.Boolean, Required: false, Description: "The oldest first; otherwise the newest."),
            MaxField,
        }).ToArray();

        private static ConnectorConfigField[] ProductInput(bool title) => new[]
        {
            new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: title, Description: "One line."),
            new ConnectorConfigField("description_html", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "HTML."),
            new ConnectorConfigField("vendor", "Vendor", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("product_type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - replacing the product's."),
            new ConnectorConfigField("handle", "Handle", ConnectorConfigFieldType.Text, Required: false, Description: "Its address in the store - blue-mug."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "active, draft or archived."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(OrdersOperation, "Get orders", "The orders a search finds, newest first - one record each: its id, name, email, statuses, total, customer and line items - n8n's order Get Many.")
            {
                Parameters = new ConnectorConfigSchema(Searched(
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "open, closed, cancelled or not_closed. Empty: every one."),
                    new ConnectorConfigField("financial_status", "Financial status", ConnectorConfigFieldType.Text, Required: false, Description: "paid, pending, authorized, partially_paid, partially_refunded, refunded, voided or expired."),
                    new ConnectorConfigField("fulfillment_status", "Fulfillment status", ConnectorConfigFieldType.Text, Required: false, Description: "unfulfilled, unshipped, partial, shipped, fulfilled, scheduled, on_hold or request_declined."),
                    new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "Only this customer's orders."))),
            },
            new ConnectorOperation(GetOrderOperation, "Get an order", "One order - n8n's order Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { OrderField }),
            },
            new ConnectorOperation(CreateOrderOperation, "Create an order", "Adds an order from its line items - one record: the order - n8n's order Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("line_items", "Line items", ConnectorConfigFieldType.Multiline, Required: true, Description: "JSON - [{\"variant_id\": \"4372907\", \"quantity\": 2}], or [{\"title\": \"Gift wrap\", \"price\": \"25.00\", \"quantity\": 1}]."),
                    new ConnectorConfigField("currency", "Currency", ConnectorConfigFieldType.Text, Required: false, Description: "SEK - needed for an item of no product."),
                    new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "The customer's address."),
                    new ConnectorConfigField("note", "Note", ConnectorConfigFieldType.Text, Required: false, Description: "A note on the order."),
                    new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
                    new ConnectorConfigField("financial_status", "Financial status", ConnectorConfigFieldType.Text, Required: false, Description: "pending, authorized, paid, partially_paid, refunded, voided... Empty: Shopify's."),
                    new ConnectorConfigField("test", "Test order", ConnectorConfigFieldType.Boolean, Required: false, Description: "Mark it a test order."),
                    new ConnectorConfigField("send_receipt", "Send the receipt", ConnectorConfigFieldType.Boolean, Required: false, Description: "Shopify emails the customer its confirmation."),
                    new ConnectorConfigField("send_fulfillment_receipt", "Send the shipping confirmation", ConnectorConfigFieldType.Boolean, Required: false, Description: "Shopify emails the customer when it ships."),
                }),
            },
            new ConnectorOperation(UpdateOrderOperation, "Update an order", "Changes an order's email, note or tags - n8n's order Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    OrderField,
                    new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "The customer's address."),
                    new ConnectorConfigField("note", "Note", ConnectorConfigFieldType.Text, Required: false, Description: "A note on the order."),
                    new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - replacing the order's."),
                }),
            },
            new ConnectorOperation(DeleteOrderOperation, "Delete an order", "Removes an order Shopify lets you delete - n8n's order Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { OrderField }),
            },
            new ConnectorOperation(ProductsOperation, "Get products", "The products a search finds, newest first - one record each: its id, title, status, vendor, prices and variants - n8n's product Get Many.")
            {
                Parameters = new ConnectorConfigSchema(Searched(
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "active, draft or archived. Empty: every one."),
                    new ConnectorConfigField("vendor", "Vendor", ConnectorConfigFieldType.Text, Required: false, Description: "Only this vendor's."),
                    new ConnectorConfigField("product_type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "Only this type."),
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: false, Description: "Titles like it - mug*."))),
            },
            new ConnectorOperation(GetProductOperation, "Get a product", "One product - n8n's product Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ProductField }),
            },
            new ConnectorOperation(CreateProductOperation, "Create a product", "Adds a product - one record: the product - n8n's product Create.")
            {
                Parameters = new ConnectorConfigSchema(ProductInput(title: true)),
            },
            new ConnectorOperation(UpdateProductOperation, "Update a product", "Changes a product's title, description, vendor, type, tags, handle or status - n8n's product Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ProductField }.Concat(ProductInput(title: false)).ToArray()),
            },
            new ConnectorOperation(DeleteProductOperation, "Delete a product", "Removes a product - n8n's product Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ProductField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint, context.Settings);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var data = await QueryAsync(client, Token(context.Credentials), api, "query { shop { name myshopifyDomain currencyCode } }", new JsonObject(), cancellationToken);
                return ConnectionTestResult.Ok($"Signed in to {Text(data?["shop"]?["name"])} ({Text(data?["shop"]?["myshopifyDomain"])}).");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var api = Api(context.Endpoint, context.Settings);
            var token = Token(context.Credentials);
            var operation = context.OperationName ?? OrdersOperation;
            var parameters = context.Parameters;

            // What could be refused is refused before anything is sent.
            string query;
            JsonObject variables;
            Func<JsonNode?, IEnumerable<ConnectorRecord>> answer;
            string? listed = null;
            int max = 0;
            if (Is(operation, OrdersOperation) || Is(operation, ProductsOperation))
            {
                var orders = Is(operation, OrdersOperation);
                listed = orders ? "orders" : "products";
                max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                query = orders
                    ? "query Orders($first: Int!, $after: String, $query: String, $reverse: Boolean) { orders(first: $first, after: $after, query: $query, sortKey: CREATED_AT, reverse: $reverse) { pageInfo { hasNextPage endCursor } nodes { ...OrderFields } } } " + OrderFields
                    : "query Products($first: Int!, $after: String, $query: String, $reverse: Boolean) { products(first: $first, after: $after, query: $query, sortKey: CREATED_AT, reverse: $reverse) { pageInfo { hasNextPage endCursor } nodes { ...ProductFields } } } " + ProductFields;
                variables = new JsonObject { ["reverse"] = !Values.GetBool(parameters, "oldest_first", false) };
                if (Search(parameters, orders) is { } search)
                {
                    variables["query"] = search;
                }

                answer = _ => Array.Empty<ConnectorRecord>();
            }
            else if (Is(operation, GetOrderOperation))
            {
                query = "query Order($id: ID!) { order(id: $id) { ...OrderFields } } " + OrderFields;
                variables = new JsonObject { ["id"] = Gid("Order", Values.Get(parameters, "order"), "order") };
                answer = data => new[] { OrderRecord(data?["order"] as JsonObject ?? throw new InvalidOperationException($"Shopify has no order {Values.Get(parameters, "order")}.")) };
            }
            else if (Is(operation, CreateOrderOperation))
            {
                query = "mutation OrderCreate($order: OrderCreateOrderInput!, $options: OrderCreateOptionsInput) { orderCreate(order: $order, options: $options) { order { ...OrderFields } userErrors { field message } } } " + OrderFields;
                variables = new JsonObject { ["order"] = NewOrder(parameters), ["options"] = new JsonObject { ["sendReceipt"] = Values.GetBool(parameters, "send_receipt", false), ["sendFulfillmentReceipt"] = Values.GetBool(parameters, "send_fulfillment_receipt", false) } };
                answer = data => new[] { OrderRecord(Changed(data, "orderCreate")?["order"] as JsonObject ?? new JsonObject()) };
            }
            else if (Is(operation, UpdateOrderOperation))
            {
                var input = new JsonObject { ["id"] = Gid("Order", Values.Get(parameters, "order"), "order") };
                AddLine(input, "email", Email(Values.Get(parameters, "email")));
                AddLine(input, "note", Values.Get(parameters, "note"));
                if (Values.Get(parameters, "tags") is { } tags)
                {
                    input["tags"] = Tags(tags);
                }

                if (input.Count == 1)
                {
                    throw new InvalidOperationException("Give something to change - email, note or tags.");
                }

                query = "mutation OrderUpdate($input: OrderInput!) { orderUpdate(input: $input) { order { ...OrderFields } userErrors { field message } } } " + OrderFields;
                variables = new JsonObject { ["input"] = input };
                answer = data => new[] { OrderRecord(Changed(data, "orderUpdate")?["order"] as JsonObject ?? new JsonObject()) };
            }
            else if (Is(operation, DeleteOrderOperation))
            {
                var id = Gid("Order", Values.Get(parameters, "order"), "order");
                query = "mutation OrderDelete($orderId: ID!) { orderDelete(orderId: $orderId) { deletedId userErrors { field message } } }";
                variables = new JsonObject { ["orderId"] = id };
                answer = data => new[] { Deleted(Text(Changed(data, "orderDelete")?["deletedId"]) ?? id) };
            }
            else if (Is(operation, GetProductOperation))
            {
                query = "query Product($id: ID!) { product(id: $id) { ...ProductFields } } " + ProductFields;
                variables = new JsonObject { ["id"] = Gid("Product", Values.Get(parameters, "product"), "product") };
                answer = data => new[] { ProductRecord(data?["product"] as JsonObject ?? throw new InvalidOperationException($"Shopify has no product {Values.Get(parameters, "product")}.")) };
            }
            else if (Is(operation, CreateProductOperation) || Is(operation, UpdateProductOperation))
            {
                var create = Is(operation, CreateProductOperation);
                var product = ProductChange(parameters, create);
                if (!create)
                {
                    product["id"] = Gid("Product", Values.Get(parameters, "product"), "product");
                    if (product.Count == 1)
                    {
                        throw new InvalidOperationException("Give something to change - title, description, vendor, type, tags, handle or status.");
                    }
                }

                var mutation = create ? "productCreate" : "productUpdate";
                query = $"mutation Product($product: {(create ? "ProductCreateInput" : "ProductUpdateInput")}!) {{ {mutation}(product: $product) {{ product {{ ...ProductFields }} userErrors {{ field message }} }} }} " + ProductFields;
                variables = new JsonObject { ["product"] = product };
                answer = data => new[] { ProductRecord(Changed(data, mutation)?["product"] as JsonObject ?? new JsonObject()) };
            }
            else if (Is(operation, DeleteProductOperation))
            {
                var id = Gid("Product", Values.Get(parameters, "product"), "product");
                query = "mutation ProductDelete($input: ProductDeleteInput!) { productDelete(input: $input) { deletedProductId userErrors { field message } } }";
                variables = new JsonObject { ["input"] = new JsonObject { ["id"] = id } };
                answer = data => new[] { Deleted(Text(Changed(data, "productDelete")?["deletedProductId"]) ?? id) };
            }
            else
            {
                throw new InvalidOperationException($"Shopify has no operation '{operation}' - orders, get_order, create_order, update_order, delete_order, products, get_product, create_product, update_product or delete_product.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (listed is null)
            {
                foreach (var record in answer(await QueryAsync(client, token, api, query, variables, cancellationToken)))
                {
                    yield return record;
                }

                yield break;
            }

            var read = 0;
            string? after = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                variables["first"] = Math.Min(100, max - read);
                variables["after"] = after;
                var data = await QueryAsync(client, token, api, query, variables, cancellationToken);
                var connection = data?[listed] as JsonObject;
                var nodes = (connection?["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                foreach (var node in nodes)
                {
                    read++;
                    yield return listed == "orders" ? OrderRecord(node) : ProductRecord(node);
                    if (read >= max)
                    {
                        yield break;
                    }
                }

                var more = connection?["pageInfo"]?["hasNextPage"] is JsonValue next && next.TryGetValue<bool>(out var hasNext) && hasNext;
                after = Text(connection?["pageInfo"]?["endCursor"]);
                if (!more || nodes.Count == 0 || after is null)
                {
                    yield break;
                }
            }
        }

        // Shopify's search: the query as written, and each filter given as a term of it - a status held to Shopify's words, a time
        // in UTC, a text quoted.
        private static string? Search(IReadOnlyDictionary<string, string> parameters, bool orders)
        {
            var terms = new List<string>();
            if (Values.Get(parameters, "query") is { } written)
            {
                terms.Add(written.Trim());
            }

            if (Values.Get(parameters, "status") is { } status)
            {
                terms.Add("status:" + Word(status, orders ? OrderStatuses : ProductStatuses, "status"));
            }

            if (orders)
            {
                if (Values.Get(parameters, "financial_status") is { } financial)
                {
                    terms.Add("financial_status:" + Word(financial, FinancialStatuses, "financial status"));
                }

                if (Values.Get(parameters, "fulfillment_status") is { } fulfillment)
                {
                    terms.Add("fulfillment_status:" + Word(fulfillment, FulfillmentStatuses, "fulfillment status"));
                }

                if (Email(Values.Get(parameters, "email")) is { } email)
                {
                    terms.Add("email:" + Quoted(email));
                }
            }
            else
            {
                foreach (var (field, term) in new[] { ("vendor", "vendor"), ("product_type", "product_type"), ("title", "title") })
                {
                    if (Values.Get(parameters, field) is { } value)
                    {
                        terms.Add(term + ":" + (field == "title" && value.EndsWith('*') && !value.Any(char.IsWhiteSpace) ? value : Quoted(value)));
                    }
                }
            }

            foreach (var (field, term) in new[] { ("created_after", "created_at"), ("updated_after", "updated_at") })
            {
                if (Values.Get(parameters, field) is { } when)
                {
                    terms.Add(term + ":>=" + Quoted(Moment(when, field)));
                }
            }

            return terms.Count == 0 ? null : string.Join(" ", terms);
        }

        // An order's input from the line items given - a variant and a quantity, or a title, a price and a quantity - and its email,
        // note, tags, financial status and test flag.
        private static JsonObject NewOrder(IReadOnlyDictionary<string, string> parameters)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(Values.Get(parameters, "line_items") ?? throw new InvalidOperationException("Give the line items - [{\"variant_id\": \"4372907\", \"quantity\": 1}]."));
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The line items are not JSON - [{\"variant_id\": \"4372907\", \"quantity\": 1}].");
            }

            var given = parsed as JsonArray ?? (parsed is JsonObject single ? new JsonArray(single.DeepClone()) : throw new InvalidOperationException("The line items are not a JSON list."));
            var currency = Values.Get(parameters, "currency") is { } code
                ? (code.Trim().Length == 3 && code.Trim().All(char.IsAsciiLetter) ? code.Trim().ToUpperInvariant() : throw new InvalidOperationException($"'{code}' is not a currency - SEK."))
                : null;
            var items = new JsonArray();
            foreach (var item in given)
            {
                if (item is not JsonObject line)
                {
                    throw new InvalidOperationException("A line item is not a JSON object.");
                }

                var quantity = line["quantity"] switch
                {
                    null => 1,
                    JsonValue value when value.TryGetValue<int>(out var whole) => whole,
                    JsonValue value when value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedQuantity) => parsedQuantity,
                    _ => 0,
                };
                if (quantity < 1)
                {
                    throw new InvalidOperationException("A line item's quantity is not a whole number of at least 1.");
                }

                var variant = Scalar(line["variant_id"]);
                if (variant is not null)
                {
                    items.Add(new JsonObject { ["variantId"] = Gid("ProductVariant", variant, "variant"), ["quantity"] = quantity });
                    continue;
                }

                var title = Scalar(line["title"]);
                var price = Scalar(line["price"]);
                if (title is null || price is null)
                {
                    throw new InvalidOperationException("A line item names neither a variant (variant_id) nor a title and a price.");
                }

                if (!decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
                {
                    throw new InvalidOperationException($"'{price}' is not a price - 25.00.");
                }

                items.Add(new JsonObject
                {
                    ["title"] = OneLine(title, "line item's title"),
                    ["quantity"] = quantity,
                    ["priceSet"] = new JsonObject { ["shopMoney"] = new JsonObject { ["amount"] = amount.ToString(CultureInfo.InvariantCulture), ["currencyCode"] = currency ?? throw new InvalidOperationException("Give the currency - an item of no product has its price in it.") } },
                });
            }

            if (items.Count == 0)
            {
                throw new InvalidOperationException("Give at least one line item.");
            }

            var order = new JsonObject { ["lineItems"] = items };
            if (currency is not null)
            {
                order["currency"] = currency;
            }

            AddLine(order, "email", Email(Values.Get(parameters, "email")));
            AddLine(order, "note", Values.Get(parameters, "note"));
            if (Values.Get(parameters, "tags") is { } tags)
            {
                order["tags"] = Tags(tags);
            }

            if (Values.Get(parameters, "financial_status") is { } financial)
            {
                order["financialStatus"] = Word(financial, NewOrderFinancialStatuses, "financial status").ToUpperInvariant();
            }

            if (Values.GetBool(parameters, "test", false))
            {
                order["test"] = true;
            }

            return order;
        }

        // A product's fields as Shopify's input: those given, a title one line, a status one of Shopify's.
        private static JsonObject ProductChange(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var product = new JsonObject();
            var title = Values.Get(parameters, "title");
            if (create && title is null)
            {
                throw new InvalidOperationException("Give the product's title.");
            }

            AddLine(product, "title", title);
            if (Values.Get(parameters, "description_html") is { } description)
            {
                product["descriptionHtml"] = description;
            }

            AddLine(product, "vendor", Values.Get(parameters, "vendor"));
            AddLine(product, "productType", Values.Get(parameters, "product_type"));
            AddLine(product, "handle", Values.Get(parameters, "handle"));
            if (Values.Get(parameters, "tags") is { } tags)
            {
                product["tags"] = Tags(tags);
            }

            if (Values.Get(parameters, "status") is { } status)
            {
                product["status"] = Word(status, ProductStatuses, "product status").ToUpperInvariant();
            }

            return product;
        }

        private static ConnectorRecord OrderRecord(JsonObject order) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(order["legacyResourceId"]),
            ["gid"] = Text(order["id"]),
            ["name"] = Text(order["name"]),
            ["email"] = Text(order["email"]),
            ["phone"] = Text(order["phone"]),
            ["created_at"] = Text(order["createdAt"]),
            ["updated_at"] = Text(order["updatedAt"]),
            ["processed_at"] = Text(order["processedAt"]),
            ["cancelled_at"] = Text(order["cancelledAt"]),
            ["closed_at"] = Text(order["closedAt"]),
            ["financial_status"] = Text(order["displayFinancialStatus"])?.ToLowerInvariant(),
            ["fulfillment_status"] = Text(order["displayFulfillmentStatus"])?.ToLowerInvariant(),
            ["total"] = Text(order["totalPriceSet"]?["shopMoney"]?["amount"]),
            ["subtotal"] = Text(order["subtotalPriceSet"]?["shopMoney"]?["amount"]),
            ["tax"] = Text(order["totalTaxSet"]?["shopMoney"]?["amount"]),
            ["currency"] = Text(order["currencyCode"]),
            ["customer"] = Text(order["customer"]?["displayName"]),
            ["customer_id"] = Legacy(Text(order["customer"]?["id"])),
            ["line_items"] = new JsonArray((order["lineItems"]?["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(line => (JsonNode)new JsonObject
            {
                ["title"] = Text(line["title"]),
                ["quantity"] = line["quantity"] is JsonValue quantity && quantity.TryGetValue<long>(out var count) ? count : null,
                ["sku"] = Text(line["sku"]),
                ["variant_id"] = Legacy(Text(line["variant"]?["id"])),
            }).ToArray()).ToJsonString(),
            ["note"] = Text(order["note"]),
            ["tags"] = string.Join(", ", (order["tags"] as JsonArray ?? new JsonArray()).Select(Text).Where(tag => tag is not null)),
            ["test"] = order["test"] is JsonValue test && test.TryGetValue<bool>(out var isTest) && isTest,
        });

        private static ConnectorRecord ProductRecord(JsonObject product) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(product["legacyResourceId"]),
            ["gid"] = Text(product["id"]),
            ["title"] = Text(product["title"]),
            ["handle"] = Text(product["handle"]),
            ["status"] = Text(product["status"])?.ToLowerInvariant(),
            ["vendor"] = Text(product["vendor"]),
            ["product_type"] = Text(product["productType"]),
            ["tags"] = string.Join(", ", (product["tags"] as JsonArray ?? new JsonArray()).Select(Text).Where(tag => tag is not null)),
            ["description_html"] = Text(product["descriptionHtml"]),
            ["total_inventory"] = product["totalInventory"] is JsonValue inventory && inventory.TryGetValue<long>(out var stock) ? stock : null,
            ["min_price"] = Text(product["priceRangeV2"]?["minVariantPrice"]?["amount"]),
            ["max_price"] = Text(product["priceRangeV2"]?["maxVariantPrice"]?["amount"]),
            ["currency"] = Text(product["priceRangeV2"]?["minVariantPrice"]?["currencyCode"]),
            ["variants"] = new JsonArray((product["variants"]?["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(variant => (JsonNode)new JsonObject
            {
                ["id"] = Legacy(Text(variant["id"])),
                ["title"] = Text(variant["title"]),
                ["sku"] = Text(variant["sku"]),
                ["price"] = Text(variant["price"]),
                ["inventory"] = variant["inventoryQuantity"] is JsonValue quantity && quantity.TryGetValue<long>(out var count) ? count : null,
            }).ToArray()).ToJsonString(),
            ["created_at"] = Text(product["createdAt"]),
            ["updated_at"] = Text(product["updatedAt"]),
            ["published_at"] = Text(product["publishedAt"]),
            ["url"] = Text(product["onlineStoreUrl"]),
        });

        private static ConnectorRecord Deleted(string gid) =>
            new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Legacy(gid), ["gid"] = gid, ["deleted"] = true });

        // A mutation's payload - its user errors, if any, Shopify's refusal.
        private static JsonObject? Changed(JsonNode? data, string mutation)
        {
            var payload = data?[mutation] as JsonObject;
            var errors = (payload?["userErrors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("Shopify refused it: " + Cut(string.Join("; ", errors.Select(error =>
                    (error["field"] is JsonArray field && field.Count > 0 ? string.Join(".", field.Select(Text)) + ": " : "") + Text(error["message"])))));
            }

            return payload;
        }

        // The shop's GraphQL endpoint: its own address (a myshopify.com host, or the admin's /store/{name}), the API version the
        // setting names.
        private static string Api(string endpoint, IReadOnlyDictionary<string, string> settings)
        {
            var given = endpoint?.Trim() ?? "";
            if (!given.Contains("://", StringComparison.Ordinal))
            {
                given = "https://" + given;
            }

            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var shop) || (shop.Scheme != Uri.UriSchemeHttps && shop.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a shop's address - https://your-store.myshopify.com.");
            }

            var origin = shop.GetLeftPart(UriPartial.Authority);
            if (string.Equals(shop.Host, "admin.shopify.com", StringComparison.OrdinalIgnoreCase))
            {
                var segments = shop.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                origin = segments.Length >= 2 && segments[0] == "store" && ShopName.IsMatch(segments[1])
                    ? $"https://{segments[1]}.myshopify.com"
                    : throw new InvalidOperationException($"'{endpoint}' names no shop - https://your-store.myshopify.com.");
            }

            var version = Values.GetOrDefault(settings, "api_version", DefaultApiVersion).Trim();
            return ApiVersion.IsMatch(version)
                ? $"{origin}/admin/api/{version}/graphql.json"
                : throw new InvalidOperationException($"'{version}' is not a Shopify API version - {DefaultApiVersion}.");
        }

        // An id as Shopify's gid: its digits, its gid of that kind, or an address ending in its digits.
        private static string Gid(string kind, string? given, string what)
        {
            var value = given?.Trim() ?? "";
            if (value.StartsWith("gid://shopify/" + kind + "/", StringComparison.Ordinal))
            {
                value = value["gid://shopify/".Length..][(kind.Length + 1)..];
            }
            else if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(value, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
            {
                value = address.AbsolutePath.TrimEnd('/').Split('/')[^1];
            }

            return Digits.IsMatch(value) ? $"gid://shopify/{kind}/{value}" : throw new InvalidOperationException($"'{given}' is not a Shopify {what}'s id.");
        }

        private static string? Legacy(string? gid) => gid is null ? null : gid[(gid.LastIndexOf('/') + 1)..];

        private static string Word(string given, HashSet<string> words, string what) =>
            words.Contains(given.Trim().ToLowerInvariant()) ? given.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not a {what} - {string.Join(", ", words)}.");

        private static string? Email(string? given) =>
            given is null ? null : given.Contains('@', StringComparison.Ordinal) && !given.Trim().Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\\') ? given.Trim() : throw new InvalidOperationException($"'{given}' is not an address.");

        // A date or a time as Shopify's search takes it - in UTC.
        private static string Moment(string given, string what) =>
            DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a date or a time ({what}).");

        private static string Quoted(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        private static JsonArray Tags(string tags) =>
            new(tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(tag => (JsonNode)JsonValue.Create(OneLine(tag, "tag"))!).ToArray());

        private static void AddLine(JsonObject input, string name, string? value)
        {
            if (value is not null)
            {
                input[name] = OneLine(value, name);
            }
        }

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static string? Scalar(JsonNode? node) => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text.Trim().Length > 0 ? text.Trim() : null,
            JsonValue value => value.ToJsonString(),
            _ => null,
        };

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The Shopify connection has no token - an Admin API access token.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static string Cut(string text) => text.Length > 300 ? text[..300] : text;

        // A GraphQL query's data; a throttled query waits as Shopify's cost says and is asked again; a failure thrown with Shopify's
        // message.
        private static async Task<JsonNode?> QueryAsync(HttpClient client, string token, string api, string query, JsonObject variables, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["query"] = query, ["variables"] = variables.DeepClone() }.ToJsonString();
            for (var attempt = 0; ; attempt++)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, api) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    request.Headers.Add("X-Shopify-Access-Token", token);
                    return request;
                }, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Shopify answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                var errors = json?["errors"];
                var said = errors switch
                {
                    JsonValue text when text.TryGetValue<string>(out var message) => message,
                    JsonArray list => string.Join("; ", list.OfType<JsonObject>().Select(error => Text(error["message"])).Where(message => message is not null)),
                    JsonObject single => Text(single["message"]) ?? single.ToJsonString(),
                    _ => null,
                };
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + Cut(said))}");
                }

                var throttled = errors is JsonArray all && all.OfType<JsonObject>().Any(error => Text(error["extensions"]?["code"]) == "THROTTLED");
                if (throttled && attempt < MaxThrottleWaits)
                {
                    await Task.Delay(ThrottleWait(json?["extensions"]?["cost"]), cancellationToken);
                    continue;
                }

                if (!string.IsNullOrEmpty(said))
                {
                    throw new InvalidOperationException("Shopify said: " + Cut(said));
                }

                return json?["data"];
            }
        }

        // How long until the shop's bucket holds what the query costs, at its restore rate - at most ten seconds.
        private static TimeSpan ThrottleWait(JsonNode? cost)
        {
            static double Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
            var needed = Number(cost?["requestedQueryCost"]) - Number(cost?["throttleStatus"]?["currentlyAvailable"]);
            var rate = Number(cost?["throttleStatus"]?["restoreRate"]);
            var seconds = rate > 0 && needed > 0 ? needed / rate : 1;
            return TimeSpan.FromSeconds(Math.Min(10, seconds));
        }
    }
}
