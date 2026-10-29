#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.WooCommerce
{
    /// <summary>
    /// WooCommerce (n8n's WooCommerce node: orders, products and customers found, got, created, updated and deleted): a
    /// store's REST API (<c>https://shop.example.com/wp-json/wc/v3</c>), a REST API key's consumer key and secret by the Basic
    /// scheme (WooCommerce takes it over HTTPS). A test counts the store's orders.
    /// </summary>
    /// <remarks>
    /// An order, a product, a customer and a category or tag are named by their id (digits), each held to that shape before it
    /// reaches the path. <c>orders</c>: the store's orders - statuses, a customer, a product, a search, made after or before a
    /// time, sorted - page by page (100 a page, a page no bigger than what is left) until a short page, WooCommerce's last page or
    /// <c>max</c> (1,000); one record each: its id, number, status, currency, totals, customer, the billing address's email and
    /// name, payment, its line items, billing and shipping (JSON) and its times. <c>get_order</c> one. <c>create_order</c>: a
    /// status, a customer, payment, paid or not, a currency, a note, a transaction id, billing and shipping (JSON objects), line
    /// items, shipping, coupon and fee lines and meta data (JSON lists); <c>update_order</c> changes those given (something, or
    /// refused); <c>delete_order</c> moves one to the trash - or deletes it for good when asked. Products and customers the same
    /// way: <c>products</c> by a search, a SKU, a status, a type, a category, a tag and stock; <c>create_product</c> a name, a
    /// type, a status, prices, a SKU, words, categories and tags (ids), stock, a weight, images (addresses) and meta data;
    /// <c>customers</c> by an address, a search and a role; <c>create_customer</c> an address, names, a username, billing,
    /// shipping and meta data - a customer is deleted for good, WooCommerce keeps no trash for them. A throttled call waits as
    /// WooCommerce says; a failure is thrown with its status, WooCommerce's code and message, and the first invalid parameter.
    /// </remarks>
    public sealed class WooCommerceConnector : IConnector, IConnectorExtractor
    {
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

        public const string CustomersOperation = "customers";

        public const string GetCustomerOperation = "get_customer";

        public const string CreateCustomerOperation = "create_customer";

        public const string UpdateCustomerOperation = "update_customer";

        public const string DeleteCustomerOperation = "delete_customer";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> OrderStatuses = new(StringComparer.Ordinal) { "pending", "processing", "on-hold", "completed", "cancelled", "refunded", "failed", "trash", "checkout-draft" };

        private static readonly HashSet<string> ProductStatuses = new(StringComparer.Ordinal) { "draft", "pending", "private", "publish" };

        private static readonly HashSet<string> ProductTypes = new(StringComparer.Ordinal) { "simple", "grouped", "external", "variable" };

        private static readonly HashSet<string> StockStatuses = new(StringComparer.Ordinal) { "instock", "outofstock", "onbackorder" };

        private static readonly HashSet<string> Orders = new(StringComparer.Ordinal) { "asc", "desc" };

        private static readonly HashSet<string> OrderSorts = new(StringComparer.Ordinal) { "date", "id", "include", "title", "slug", "modified" };

        private static readonly HashSet<string> ProductSorts = new(StringComparer.Ordinal) { "date", "id", "include", "title", "slug", "modified", "price", "popularity", "rating", "menu_order" };

        private static readonly HashSet<string> CustomerSorts = new(StringComparer.Ordinal) { "id", "include", "name", "registered_date" };

        public string ConnectorType => "woocommerce";

        public string DisplayName => "WooCommerce";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        /// <summary>The Basic pair as WooCommerce names it: a REST API key's consumer key and secret.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "Consumer key (ck_...)", ["password"] = "Consumer secret (cs_...)" };

        public string EndpointExample => "https://shop.example.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Commerce };

        public Uri WebsiteUrl { get; } = new("https://woocommerce.com");

        public Uri ApiDocumentationUrl { get; } = new("https://woocommerce.github.io/woocommerce-rest-api-docs/");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField IdField(string name, string what) => new(name, what, ConnectorConfigFieldType.Text, Required: true, Description: $"The {what.ToLowerInvariant()}'s id (digits).");

        private static ConnectorConfigField ForceField(string what) => new("force", "For good", ConnectorConfigFieldType.Text, Required: false, Description: $"true deletes the {what} for good. Empty: it goes to the trash.");

        private static ConnectorConfigField[] SortFields(string sorts) => new[]
        {
            new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "asc or desc."),
            new ConnectorConfigField("orderby", "Sort by", ConnectorConfigFieldType.Text, Required: false, Description: sorts),
        };

        private static ConnectorConfigField[] OrderInput() => new[]
        {
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "pending, processing, on-hold, completed, cancelled, refunded or failed."),
            new ConnectorConfigField("customer_id", "Customer", ConnectorConfigFieldType.Text, Required: false, Description: "A customer's id. Empty: a guest."),
            new ConnectorConfigField("payment_method", "Payment method", ConnectorConfigFieldType.Text, Required: false, Description: "Its id - bacs, stripe."),
            new ConnectorConfigField("payment_method_title", "Payment method's title", ConnectorConfigFieldType.Text, Required: false, Description: "As the customer sees it."),
            new ConnectorConfigField("set_paid", "Paid", ConnectorConfigFieldType.Text, Required: false, Description: "true marks it paid (processing)."),
            new ConnectorConfigField("currency", "Currency", ConnectorConfigFieldType.Text, Required: false, Description: "Its code - SEK, EUR."),
            new ConnectorConfigField("customer_note", "Customer's note", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("transaction_id", "Transaction id", ConnectorConfigFieldType.Text, Required: false, Description: "The payment's id."),
            new ConnectorConfigField("billing", "Billing", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - {\"first_name\": \"Ada\", \"email\": \"ada@example.com\", \"city\": \"Lund\", \"country\": \"SE\"}."),
            new ConnectorConfigField("shipping", "Shipping", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON, as billing."),
            new ConnectorConfigField("line_items", "Line items", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - [{\"product_id\": 93, \"quantity\": 2}]."),
            new ConnectorConfigField("shipping_lines", "Shipping lines", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - [{\"method_id\": \"flat_rate\", \"method_title\": \"Flat rate\", \"total\": \"49.00\"}]."),
            new ConnectorConfigField("coupon_lines", "Coupons", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - [{\"code\": \"WINTER10\"}]."),
            new ConnectorConfigField("fee_lines", "Fees", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - [{\"name\": \"Gift wrap\", \"total\": \"25.00\"}]."),
            new ConnectorConfigField("meta_data", "Meta data", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - [{\"key\": \"source\", \"value\": \"pipeline\"}]."),
        };

        private static ConnectorConfigField[] ProductInput(bool create) => new[]
        {
            new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "simple, grouped, external or variable."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "draft, pending, private or publish."),
            new ConnectorConfigField("regular_price", "Price", ConnectorConfigFieldType.Text, Required: false, Description: "199.00"),
            new ConnectorConfigField("sale_price", "Sale price", ConnectorConfigFieldType.Text, Required: false, Description: "149.00"),
            new ConnectorConfigField("sku", "SKU", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Text, Required: false, Description: "One line - HTML allowed."),
            new ConnectorConfigField("short_description", "Short description", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("categories", "Categories", ConnectorConfigFieldType.Text, Required: false, Description: "Their ids, comma-separated."),
            new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Their ids, comma-separated."),
            new ConnectorConfigField("manage_stock", "Manage stock", ConnectorConfigFieldType.Text, Required: false, Description: "true or false."),
            new ConnectorConfigField("stock_quantity", "Stock", ConnectorConfigFieldType.Integer, Required: false, Description: "How many are in stock."),
            new ConnectorConfigField("stock_status", "Stock status", ConnectorConfigFieldType.Text, Required: false, Description: "instock, outofstock or onbackorder."),
            new ConnectorConfigField("weight", "Weight", ConnectorConfigFieldType.Text, Required: false, Description: "0.5"),
            new ConnectorConfigField("images", "Images", ConnectorConfigFieldType.Text, Required: false, Description: "Their addresses, comma-separated."),
            new ConnectorConfigField("meta_data", "Meta data", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - [{\"key\": \"source\", \"value\": \"pipeline\"}]."),
        };

        private static ConnectorConfigField[] CustomerInput(bool create) => new[]
        {
            new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: create, Description: "An address."),
            new ConnectorConfigField("first_name", "First name", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("last_name", "Last name", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("username", "Username", ConnectorConfigFieldType.Text, Required: false, Description: "Empty: WooCommerce makes one."),
            new ConnectorConfigField("billing", "Billing", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - {\"first_name\": \"Ada\", \"city\": \"Lund\", \"country\": \"SE\"}."),
            new ConnectorConfigField("shipping", "Shipping", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON, as billing."),
            new ConnectorConfigField("meta_data", "Meta data", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - [{\"key\": \"source\", \"value\": \"pipeline\"}]."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(OrdersOperation, "Get orders", "The store's orders - one record each: its number, status, totals, customer and line items - n8n's order Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "pending, processing, on-hold, completed, cancelled, refunded, failed or trash - comma-separated."),
                    new ConnectorConfigField("customer", "Customer", ConnectorConfigFieldType.Text, Required: false, Description: "Only this customer's - an id."),
                    new ConnectorConfigField("product", "Product", ConnectorConfigFieldType.Text, Required: false, Description: "Only those with this product - an id."),
                    new ConnectorConfigField("search", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words in an order."),
                    new ConnectorConfigField("after", "Made after", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-01T00:00:00Z."),
                    new ConnectorConfigField("before", "Made before", ConnectorConfigFieldType.Text, Required: false, Description: "A time."),
                }.Concat(SortFields("date, id, include, title, slug or modified.")).Append(MaxField).ToArray()),
            },
            new ConnectorOperation(GetOrderOperation, "Get an order", "One order - n8n's order Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("order", "Order") }),
            },
            new ConnectorOperation(CreateOrderOperation, "Create an order", "Adds an order - n8n's order Create.")
            {
                Parameters = new ConnectorConfigSchema(OrderInput()),
            },
            new ConnectorOperation(UpdateOrderOperation, "Update an order", "Changes an order - n8n's order Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("order", "Order") }.Concat(OrderInput()).ToArray()),
            },
            new ConnectorOperation(DeleteOrderOperation, "Delete an order", "Moves an order to the trash - or deletes it for good - n8n's order Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("order", "Order"), ForceField("order") }),
            },
            new ConnectorOperation(ProductsOperation, "Get products", "The store's products - one record each: its name, SKU, prices, stock and categories - n8n's product Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("search", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words in a product."),
                    new ConnectorConfigField("sku", "SKU", ConnectorConfigFieldType.Text, Required: false, Description: "Only this SKU."),
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "draft, pending, private or publish."),
                    new ConnectorConfigField("type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "simple, grouped, external or variable."),
                    new ConnectorConfigField("category", "Category", ConnectorConfigFieldType.Text, Required: false, Description: "A category's id."),
                    new ConnectorConfigField("tag", "Tag", ConnectorConfigFieldType.Text, Required: false, Description: "A tag's id."),
                    new ConnectorConfigField("stock_status", "Stock status", ConnectorConfigFieldType.Text, Required: false, Description: "instock, outofstock or onbackorder."),
                }.Concat(SortFields("date, id, title, slug, modified, price, popularity, rating or menu_order.")).Append(MaxField).ToArray()),
            },
            new ConnectorOperation(GetProductOperation, "Get a product", "One product - n8n's product Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("product", "Product") }),
            },
            new ConnectorOperation(CreateProductOperation, "Create a product", "Adds a product - n8n's product Create.")
            {
                Parameters = new ConnectorConfigSchema(ProductInput(create: true)),
            },
            new ConnectorOperation(UpdateProductOperation, "Update a product", "Changes a product - n8n's product Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("product", "Product") }.Concat(ProductInput(create: false)).ToArray()),
            },
            new ConnectorOperation(DeleteProductOperation, "Delete a product", "Moves a product to the trash - or deletes it for good - n8n's product Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("product", "Product"), ForceField("product") }),
            },
            new ConnectorOperation(CustomersOperation, "Get customers", "The store's customers - one record each: their address, names and billing - n8n's customer Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "Only this address."),
                    new ConnectorConfigField("search", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words in a customer."),
                    new ConnectorConfigField("role", "Role", ConnectorConfigFieldType.Text, Required: false, Description: "customer, subscriber, all - a WordPress role. Empty: customer."),
                }.Concat(SortFields("id, include, name or registered_date.")).Append(MaxField).ToArray()),
            },
            new ConnectorOperation(GetCustomerOperation, "Get a customer", "One customer - n8n's customer Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("customer", "Customer") }),
            },
            new ConnectorOperation(CreateCustomerOperation, "Create a customer", "Adds a customer - n8n's customer Create.")
            {
                Parameters = new ConnectorConfigSchema(CustomerInput(create: true)),
            },
            new ConnectorOperation(UpdateCustomerOperation, "Update a customer", "Changes a customer - n8n's customer Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("customer", "Customer") }.Concat(CustomerInput(create: false)).ToArray()),
            },
            new ConnectorOperation(DeleteCustomerOperation, "Delete a customer", "Deletes a customer for good - WooCommerce keeps no trash for them - n8n's customer Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("customer", "Customer") }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var (_, total, _) = await SendAsync(client, Authorization(context.Credentials), HttpMethod.Get, api + "/orders?per_page=1", null, cancellationToken);
                return ConnectionTestResult.Ok(total is { } count ? $"Reached the store: {count.ToString("N0", CultureInfo.InvariantCulture)} order{(count == 1 ? "" : "s")}." : "Reached the store.");
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
            var api = Api(context.Endpoint);
            var authorization = Authorization(context.Credentials);
            var operation = context.OperationName ?? OrdersOperation;
            var parameters = context.Parameters;
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;

            // What could be refused is refused before anything is sent.
            var (resource, record, singular) = Resource(operation);
            string url;
            HttpMethod method = HttpMethod.Get;
            JsonObject? body = null;
            var listed = false;
            string? deleted = null;
            if (Is(operation, resource))
            {
                url = $"{api}/{resource}" + Query(resource, parameters);
                listed = true;
            }
            else if (Is(operation, "get_" + singular))
            {
                url = $"{api}/{resource}/{Id(Values.Get(parameters, singular), singular)}";
            }
            else if (Is(operation, "create_" + singular) || Is(operation, "update_" + singular))
            {
                var create = Is(operation, "create_" + singular);
                var id = create ? null : Id(Values.Get(parameters, singular), singular);
                body = singular switch
                {
                    "order" => OrderChange(parameters),
                    "product" => ProductChange(parameters, create),
                    _ => CustomerChange(parameters, create),
                };
                if (!create && body.Count == 0)
                {
                    throw new InvalidOperationException($"Give something to change on the {singular}.");
                }

                url = create ? $"{api}/{resource}" : $"{api}/{resource}/{id}";
                method = create ? HttpMethod.Post : HttpMethod.Put;
            }
            else if (Is(operation, "delete_" + singular))
            {
                var id = Id(Values.Get(parameters, singular), singular);
                var forGood = singular == "customer" || Flag(Values.Get(parameters, "force") ?? "false", "force");
                url = $"{api}/{resource}/{id}?force={(forGood ? "true" : "false")}";
                method = HttpMethod.Delete;
                deleted = forGood ? "deleted" : "trashed";
            }
            else
            {
                throw new InvalidOperationException($"WooCommerce has no operation '{operation}' - orders, products or customers, and get_, create_, update_ and delete_ an order, a product or a customer.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (!listed)
            {
                var (answer, _, _) = await SendAsync(client, authorization, method, url, body, cancellationToken);
                var found = record(answer as JsonObject ?? new JsonObject());
                if (deleted is not null)
                {
                    var fields = new Dictionary<string, object?>(found.Fields, StringComparer.Ordinal) { [deleted] = true };
                    found = new ConnectorRecord(fields);
                }

                yield return found;
                yield break;
            }

            // Page by page, a page no bigger than what is left, until a short page, WooCommerce's last page or the most asked for.
            var read = 0;
            for (var number = 1; ; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = Math.Min(PageSize, max - read);
                var (page, _, pages) = await SendAsync(client, authorization, HttpMethod.Get, url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "per_page=" + size.ToString(CultureInfo.InvariantCulture) + "&page=" + number.ToString(CultureInfo.InvariantCulture), null, cancellationToken);
                var items = (page as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                foreach (var item in items)
                {
                    read++;
                    yield return record(item);
                    if (read >= max)
                    {
                        yield break;
                    }
                }

                if (items.Count < size || (pages is { } last && number >= last))
                {
                    yield break;
                }
            }
        }

        private static (string Resource, Func<JsonObject, ConnectorRecord> Record, string Singular) Resource(string operation)
        {
            var name = operation.ToLowerInvariant();
            return name.Contains("order", StringComparison.Ordinal) ? ("orders", Order, "order")
                : name.Contains("product", StringComparison.Ordinal) ? ("products", Product, "product")
                : name.Contains("customer", StringComparison.Ordinal) ? ("customers", Customer, "customer")
                : throw new InvalidOperationException($"WooCommerce has no operation '{operation}' - orders, products or customers, and get_, create_, update_ and delete_ an order, a product or a customer.");
        }

        // A Get Many's filters as WooCommerce's query: words held to WooCommerce's, ids to digits, times to ISO 8601.
        private static string Query(string resource, IReadOnlyDictionary<string, string> parameters)
        {
            var query = new List<string>();
            void Add(string name, string value) => query.Add(name + "=" + Uri.EscapeDataString(value));
            switch (resource)
            {
                case "orders":
                    if (Values.Get(parameters, "status") is { } statuses)
                    {
                        Add("status", string.Join(",", statuses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(status => Word(status, OrderStatuses, "an order's status"))));
                    }

                    foreach (var (given, what) in new[] { ("customer", "customer"), ("product", "product") })
                    {
                        if (Values.Get(parameters, given) is { } id)
                        {
                            Add(given, Id(id, what));
                        }
                    }

                    foreach (var given in new[] { "after", "before" })
                    {
                        if (Values.Get(parameters, given) is { } time)
                        {
                            Add(given, Time(time));
                        }
                    }

                    break;
                case "products":
                    foreach (var (given, words, what) in new[] { ("status", ProductStatuses, "a product's status"), ("type", ProductTypes, "a product type"), ("stock_status", StockStatuses, "a stock status") })
                    {
                        if (Values.Get(parameters, given) is { } value)
                        {
                            Add(given, Word(value, words, what));
                        }
                    }

                    foreach (var (given, what) in new[] { ("category", "category"), ("tag", "tag") })
                    {
                        if (Values.Get(parameters, given) is { } id)
                        {
                            Add(given, Id(id, what));
                        }
                    }

                    if (Values.Get(parameters, "sku") is { } sku)
                    {
                        Add("sku", OneLine(sku, "SKU"));
                    }

                    break;
                default:
                    if (Values.Get(parameters, "email") is { } email)
                    {
                        Add("email", Email(email));
                    }

                    if (Values.Get(parameters, "role") is { } role)
                    {
                        Add("role", Slug(role, "role"));
                    }

                    break;
            }

            if (Values.Get(parameters, "search") is { } search)
            {
                Add("search", OneLine(search, "search"));
            }

            if (Values.Get(parameters, "order") is { } order)
            {
                Add("order", Word(order, Orders, "a sort order"));
            }

            if (Values.Get(parameters, "orderby") is { } orderBy)
            {
                Add("orderby", Word(orderBy, resource switch { "orders" => OrderSorts, "products" => ProductSorts, _ => CustomerSorts }, "a field to sort by"));
            }

            return query.Count > 0 ? "?" + string.Join("&", query) : "";
        }

        private static JsonObject OrderChange(IReadOnlyDictionary<string, string> parameters)
        {
            var order = new JsonObject();
            if (Values.Get(parameters, "status") is { } status)
            {
                order["status"] = Word(status, OrderStatuses, "an order's status");
            }

            if (Values.Get(parameters, "customer_id") is { } customer)
            {
                order["customer_id"] = long.Parse(Id(customer, "customer"), CultureInfo.InvariantCulture);
            }

            foreach (var name in new[] { "payment_method", "payment_method_title", "currency", "customer_note", "transaction_id" })
            {
                AddLine(order, name, Values.Get(parameters, name));
            }

            if (Values.Get(parameters, "set_paid") is { } paid)
            {
                order["set_paid"] = Flag(paid, "set_paid");
            }

            foreach (var name in new[] { "billing", "shipping" })
            {
                if (Values.Get(parameters, name) is { } address)
                {
                    order[name] = Json<JsonObject>(address, name, Address);
                }
            }

            foreach (var name in new[] { "line_items", "shipping_lines", "coupon_lines", "fee_lines", "meta_data" })
            {
                if (Values.Get(parameters, name) is { } lines)
                {
                    order[name] = Lines(lines, name);
                }
            }

            return order;
        }

        private static JsonObject ProductChange(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var product = new JsonObject();
            if (create || Values.Get(parameters, "name") is not null)
            {
                product["name"] = OneLine(Values.Get(parameters, "name") ?? throw new InvalidOperationException("Give the product's name."), "name");
            }

            foreach (var (given, words, what) in new[] { ("type", ProductTypes, "a product type"), ("status", ProductStatuses, "a product's status"), ("stock_status", StockStatuses, "a stock status") })
            {
                if (Values.Get(parameters, given) is { } value)
                {
                    product[given] = Word(value, words, what);
                }
            }

            foreach (var name in new[] { "regular_price", "sale_price", "weight" })
            {
                if (Values.Get(parameters, name) is { } amount)
                {
                    product[name] = decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number >= 0
                        ? number.ToString(CultureInfo.InvariantCulture)
                        : throw new InvalidOperationException($"'{amount}' is not a {name.Replace('_', ' ')} - 199.00.");
                }
            }

            foreach (var name in new[] { "sku", "description", "short_description" })
            {
                AddLine(product, name, Values.Get(parameters, name));
            }

            foreach (var (name, what) in new[] { ("categories", "category"), ("tags", "tag") })
            {
                if (Values.Get(parameters, name) is { } ids)
                {
                    product[name] = new JsonArray(ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(id => (JsonNode)new JsonObject { ["id"] = long.Parse(Id(id, what), CultureInfo.InvariantCulture) }).ToArray());
                }
            }

            if (Values.Get(parameters, "manage_stock") is { } manage)
            {
                product["manage_stock"] = Flag(manage, "manage_stock");
            }

            if (Values.Get(parameters, "stock_quantity") is { } stock)
            {
                product["stock_quantity"] = int.TryParse(stock, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : throw new InvalidOperationException($"'{stock}' is not a stock quantity - a whole number.");
            }

            if (Values.Get(parameters, "images") is { } images)
            {
                product["images"] = new JsonArray(images.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(image => (JsonNode)new JsonObject { ["src"] = Stomme.Shared.Utilities.TryCreateAbsoluteUri(image, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp) ? image : throw new InvalidOperationException($"'{image}' is not an image's address - https://...") }).ToArray());
            }

            if (Values.Get(parameters, "meta_data") is { } meta)
            {
                product["meta_data"] = Lines(meta, "meta_data");
            }

            return product;
        }

        private static JsonObject CustomerChange(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var customer = new JsonObject();
            if (create || Values.Get(parameters, "email") is not null)
            {
                customer["email"] = Email(Values.Get(parameters, "email") ?? throw new InvalidOperationException("Give the customer's address."));
            }

            foreach (var name in new[] { "first_name", "last_name", "username" })
            {
                AddLine(customer, name, Values.Get(parameters, name));
            }

            foreach (var name in new[] { "billing", "shipping" })
            {
                if (Values.Get(parameters, name) is { } address)
                {
                    customer[name] = Json<JsonObject>(address, name, Address);
                }
            }

            if (Values.Get(parameters, "meta_data") is { } meta)
            {
                customer["meta_data"] = Lines(meta, "meta_data");
            }

            return customer;
        }

        private const string Address = "a JSON object - {\"first_name\": \"Ada\", \"city\": \"Lund\"}";

        // A JSON list of objects - line items, shipping, coupon and fee lines, meta data - each said with an example of its own.
        private static JsonArray Lines(string given, string name)
        {
            var shape = "a JSON list of objects - " + name switch
            {
                "line_items" => "[{\"product_id\": 93, \"quantity\": 2}]",
                "shipping_lines" => "[{\"method_id\": \"flat_rate\", \"total\": \"49.00\"}]",
                "coupon_lines" => "[{\"code\": \"WINTER10\"}]",
                "fee_lines" => "[{\"name\": \"Gift wrap\", \"total\": \"25.00\"}]",
                _ => "[{\"key\": \"source\", \"value\": \"pipeline\"}]",
            };
            var list = Json<JsonArray>(given, name, shape);
            return list.Count > 0 && list.All(item => item is JsonObject) ? list : throw new InvalidOperationException($"'{name}' is not {shape}.");
        }

        private static T Json<T>(string given, string name, string shape) where T : JsonNode
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"'{name}' is not JSON - {shape}.");
            }

            return parsed as T ?? throw new InvalidOperationException($"'{name}' is not {shape}.");
        }

        private static ConnectorRecord Order(JsonObject order) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Number(order["id"]),
            ["number"] = Text(order["number"]),
            ["status"] = Text(order["status"]),
            ["currency"] = Text(order["currency"]),
            ["total"] = Text(order["total"]),
            ["total_tax"] = Text(order["total_tax"]),
            ["shipping_total"] = Text(order["shipping_total"]),
            ["discount_total"] = Text(order["discount_total"]),
            ["customer_id"] = Number(order["customer_id"]),
            ["email"] = Text(order["billing"]?["email"]),
            ["name"] = string.Join(" ", new[] { Text(order["billing"]?["first_name"]), Text(order["billing"]?["last_name"]) }.Where(part => !string.IsNullOrEmpty(part))) is { Length: > 0 } name ? name : null,
            ["payment_method"] = Text(order["payment_method"]),
            ["payment_method_title"] = Text(order["payment_method_title"]),
            ["transaction_id"] = Text(order["transaction_id"]),
            ["customer_note"] = Text(order["customer_note"]),
            ["line_items"] = order["line_items"] is JsonArray items
                ? new JsonArray(items.OfType<JsonObject>().Select(item => (JsonNode)new JsonObject
                {
                    ["product_id"] = Number(item["product_id"]),
                    ["name"] = Text(item["name"]),
                    ["sku"] = Text(item["sku"]),
                    ["quantity"] = Number(item["quantity"]),
                    ["total"] = Text(item["total"]),
                }).ToArray()).ToJsonString()
                : "[]",
            ["billing"] = order["billing"]?.ToJsonString() ?? "{}",
            ["shipping"] = order["shipping"]?.ToJsonString() ?? "{}",
            ["date_created"] = Text(order["date_created_gmt"]) ?? Text(order["date_created"]),
            ["date_modified"] = Text(order["date_modified_gmt"]) ?? Text(order["date_modified"]),
            ["date_paid"] = Text(order["date_paid_gmt"]) ?? Text(order["date_paid"]),
            ["date_completed"] = Text(order["date_completed_gmt"]) ?? Text(order["date_completed"]),
        });

        private static ConnectorRecord Product(JsonObject product) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Number(product["id"]),
            ["name"] = Text(product["name"]),
            ["slug"] = Text(product["slug"]),
            ["permalink"] = Text(product["permalink"]),
            ["type"] = Text(product["type"]),
            ["status"] = Text(product["status"]),
            ["sku"] = Text(product["sku"]),
            ["price"] = Text(product["price"]),
            ["regular_price"] = Text(product["regular_price"]),
            ["sale_price"] = Text(product["sale_price"]),
            ["stock_status"] = Text(product["stock_status"]),
            ["stock_quantity"] = Number(product["stock_quantity"]),
            ["categories"] = Names(product["categories"]),
            ["tags"] = Names(product["tags"]),
            ["short_description"] = Text(product["short_description"]),
            ["date_created"] = Text(product["date_created_gmt"]) ?? Text(product["date_created"]),
            ["date_modified"] = Text(product["date_modified_gmt"]) ?? Text(product["date_modified"]),
        });

        private static ConnectorRecord Customer(JsonObject customer) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Number(customer["id"]),
            ["email"] = Text(customer["email"]),
            ["first_name"] = Text(customer["first_name"]),
            ["last_name"] = Text(customer["last_name"]),
            ["username"] = Text(customer["username"]),
            ["role"] = Text(customer["role"]),
            ["is_paying_customer"] = customer["is_paying_customer"] is JsonValue paying && paying.TryGetValue<bool>(out var isPaying) && isPaying,
            ["billing"] = customer["billing"]?.ToJsonString() ?? "{}",
            ["shipping"] = customer["shipping"]?.ToJsonString() ?? "{}",
            ["date_created"] = Text(customer["date_created_gmt"]) ?? Text(customer["date_created"]),
            ["date_modified"] = Text(customer["date_modified_gmt"]) ?? Text(customer["date_modified"]),
        });

        // The store's REST API: its own address (a store in a folder keeps it), a pasted admin or API address cut back to it, and
        // /wp-json/wc/v3.
        private static string Api(string endpoint)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var store) || (store.Scheme != Uri.UriSchemeHttps && store.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a store's address - https://shop.example.com.");
            }

            var path = store.AbsolutePath;
            foreach (var cut in new[] { "/wp-admin", "/wp-json", "/wp-login.php" })
            {
                var at = path.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    path = path[..at];
                }
            }

            return store.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/') + "/wp-json/wc/v3";
        }

        // A REST API key's pair: the consumer key and the consumer secret, by the Basic scheme.
        private static AuthenticationHeaderValue Authorization(IReadOnlyDictionary<string, string> credentials)
        {
            var key = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The WooCommerce connection has no consumer key.");
            var secret = credentials.TryGetValue("password", out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim() : throw new InvalidOperationException("The WooCommerce connection has no consumer secret.");
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret)));
        }

        private static string Id(string? given, string what) =>
            given is not null && Digits.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a WooCommerce {what}'s id (digits).");

        private static string Word(string given, HashSet<string> words, string what) =>
            words.Contains(given.Trim().ToLowerInvariant()) ? given.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", words)}.");

        private static string Slug(string given, string what) =>
            Regex.IsMatch(given.Trim(), "^[a-z0-9_-]{1,60}$", RegexOptions.CultureInvariant) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a {what} - customer, subscriber or all.");

        private static string Email(string given) =>
            given.Contains('@', StringComparison.Ordinal) && !given.Trim().Any(c => char.IsWhiteSpace(c) || c == '"') ? given.Trim() : throw new InvalidOperationException($"'{given}' is not an address.");

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        // A time as WooCommerce takes it: ISO 8601, in UTC.
        private static string Time(string given) =>
            DateTimeOffset.TryParse(given.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-01T00:00:00Z.");

        private static void AddLine(JsonObject input, string name, string? value)
        {
            if (value is not null)
            {
                input[name] = OneLine(value, name.Replace('_', ' '));
            }
        }

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static string? Names(JsonNode? list) => list is JsonArray items ? string.Join(", ", items.OfType<JsonObject>().Select(item => Text(item["name"])).Where(name => name is not null)) : null;

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // WooCommerce's answer as JSON with its total and its pages; a throttled call waits as WooCommerce says; a failure thrown with
        // its status, WooCommerce's code and message, and the first invalid parameter.
        private static async Task<(JsonNode? Json, long? Total, int? Pages)> SendAsync(HttpClient client, AuthenticationHeaderValue authorization, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = authorization;
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

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
                throw new InvalidOperationException($"WooCommerce answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Text(json?["code"]);
                var invalid = (json?["data"]?["params"] as JsonObject)?.Select(pair => pair.Key + ": " + (Text(pair.Value) ?? pair.Value?.ToJsonString())).FirstOrDefault();
                var words = string.Join(" - ", new[] { Text(json?["message"]), invalid }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}{(words.Length == 0 ? "." : ": " + (words.Length > 300 ? words[..300] : words))}");
            }

            long? total = response.Headers.TryGetValues("X-WP-Total", out var totals) && long.TryParse(totals.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var all) ? all : null;
            int? pages = response.Headers.TryGetValues("X-WP-TotalPages", out var counts) && int.TryParse(counts.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var last) ? last : null;
            return (json, total, pages);
        }
    }
}
