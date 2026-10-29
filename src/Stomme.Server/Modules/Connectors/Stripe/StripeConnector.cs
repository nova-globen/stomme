#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Stripe
{
    /// <summary>
    /// Stripe (n8n's Stripe node: customers listed, got, created, updated and deleted, charges listed, the balance got):
    /// Stripe's API (<c>https://api.stripe.com/v1</c>), a secret or restricted key (<c>sk_...</c>, <c>rk_...</c>) as the bearer;
    /// bodies form-encoded, as Stripe takes them. A test reads the balance - what any key may read - and says whether the key is a
    /// live one or a test one.
    /// </summary>
    /// <remarks>
    /// A customer is named by its id (<c>cus_...</c>), held to that shape before it reaches the path. <c>customers</c>: the
    /// customers - those with an <c>email</c> when given - page by page by Stripe's <c>starting_after</c> up to <c>max</c>; one
    /// record each: id, email, name, phone, description, balance (its smallest unit), currency, delinquent, created (in UTC) and
    /// metadata (JSON). <c>get_customer</c> the same for one. <c>create_customer</c>: an email, a name, a phone, a description and
    /// metadata (a JSON object of text); <c>update_customer</c> changes those given; <c>delete_customer</c> removes one.
    /// <c>charges</c>: the charges - a customer's when given - page by page; one record each: id, amount (smallest unit),
    /// amount_captured, amount_refunded, currency, status, paid, refunded, customer, description, receipt_email, payment_intent,
    /// created. <c>balance</c>: what is available and pending, one record per currency. A throttled call waits as Stripe says; a
    /// failure is thrown with its status and Stripe's message.
    /// </remarks>
    public sealed class StripeConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.stripe.com/v1";

        public const string CustomersOperation = "customers";

        public const string GetCustomerOperation = "get_customer";

        public const string CreateCustomerOperation = "create_customer";

        public const string UpdateCustomerOperation = "update_customer";

        public const string DeleteCustomerOperation = "delete_customer";

        public const string ChargesOperation = "charges";

        public const string BalanceOperation = "balance";

        public const int DefaultMax = 1000;

        private static readonly Regex CustomerId = new("^cus_[A-Za-z0-9]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex MetadataKey = new("^[A-Za-z0-9_.-]{1,40}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "stripe";

        public string DisplayName => "Stripe";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Stripe names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Secret or restricted key (sk_..., rk_...)" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Commerce };

        public Uri WebsiteUrl { get; } = new("https://stripe.com");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.stripe.com/api");

        private static readonly ConnectorConfigField CustomerField = new("customer", "Customer", ConnectorConfigFieldType.Text, Required: true, Description: "The customer's id (cus_...).");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] CustomerFields() => new[]
        {
            new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "An address."),
            new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("phone", "Phone", ConnectorConfigFieldType.Text, Required: false, Description: "+46701740605"),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Text, Required: false, Description: "A note for your own use."),
            new ConnectorConfigField("metadata", "Metadata", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object of text - {\"order\": \"1001\"}."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(CustomersOperation, "Get customers", "The customers - those with an address when given - one record each: its id, email, name, balance and metadata - n8n's customer Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "Only customers with this address."), MaxField }),
            },
            new ConnectorOperation(GetCustomerOperation, "Get a customer", "One customer - n8n's customer Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CustomerField }),
            },
            new ConnectorOperation(CreateCustomerOperation, "Create a customer", "Adds a customer - one record: the customer - n8n's customer Create.")
            {
                Parameters = new ConnectorConfigSchema(CustomerFields()),
            },
            new ConnectorOperation(UpdateCustomerOperation, "Update a customer", "Changes a customer's email, name, phone, description or metadata - n8n's customer Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CustomerField }.Concat(CustomerFields()).ToArray()),
            },
            new ConnectorOperation(DeleteCustomerOperation, "Delete a customer", "Removes a customer - n8n's customer Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CustomerField }),
            },
            new ConnectorOperation(ChargesOperation, "Get charges", "The charges - a customer's when given - one record each: its id, amount, currency, status and customer - n8n's charge Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("customer", "Customer", ConnectorConfigFieldType.Text, Required: false, Description: "Only this customer's (cus_...)."), MaxField }),
            },
            new ConnectorOperation(BalanceOperation, "Get the balance", "What is available and pending, one record per currency - n8n's balance Get."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var balance = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, api + "/balance", null, cancellationToken);
                var live = balance?["livemode"] is JsonValue mode && mode.TryGetValue<bool>(out var isLive) && isLive;
                return ConnectionTestResult.Ok($"Signed in with a {(live ? "LIVE" : "test")} key.");
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
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var token = Token(context.Credentials);
            var operation = context.OperationName ?? CustomersOperation;
            var parameters = context.Parameters;
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, CustomersOperation) || Is(operation, ChargesOperation))
            {
                var charges = Is(operation, ChargesOperation);
                var filter = charges
                    ? Values.Get(parameters, "customer") is { } customer ? "customer=" + Customer(customer) : null
                    : Values.Get(parameters, "email") is { } email ? "email=" + Uri.EscapeDataString(email) : null;
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var read = 0;
                string? after = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var query = new List<string> { "limit=" + Math.Min(100, max - read).ToString(CultureInfo.InvariantCulture) };
                    if (filter is not null)
                    {
                        query.Add(filter);
                    }

                    if (after is not null)
                    {
                        query.Add("starting_after=" + Uri.EscapeDataString(after));
                    }

                    var page = await SendAsync(client, token, HttpMethod.Get, $"{api}/{(charges ? "charges" : "customers")}?{string.Join("&", query)}", null, cancellationToken);
                    var items = (page?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        if (read >= max)
                        {
                            yield break;
                        }

                        read++;
                        yield return charges ? ChargeRecord(item) : CustomerRecord(item);
                    }

                    var more = page?["has_more"] is JsonValue hasMore && hasMore.TryGetValue<bool>(out var flag) && flag;
                    if (!more || items.Count == 0 || read >= max)
                    {
                        yield break;
                    }

                    after = Text(items[^1]["id"]);
                }
            }

            if (Is(operation, BalanceOperation))
            {
                var balance = await SendAsync(client, token, HttpMethod.Get, api + "/balance", null, cancellationToken);
                var byCurrency = new SortedDictionary<string, (long Available, long Pending)>(StringComparer.Ordinal);
                foreach (var (kind, list) in new[] { ("available", balance?["available"]), ("pending", balance?["pending"]) })
                {
                    foreach (var amount in (list as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        var currency = Text(amount["currency"]) ?? "?";
                        var value = amount["amount"] is JsonValue number && number.TryGetValue<long>(out var cents) ? cents : 0;
                        var (available, pending) = byCurrency.GetValueOrDefault(currency);
                        byCurrency[currency] = kind == "available" ? (available + value, pending) : (available, pending + value);
                    }
                }

                foreach (var (currency, (available, pending)) in byCurrency)
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["currency"] = currency, ["available"] = available, ["pending"] = pending });
                }

                yield break;
            }

            if (Is(operation, CreateCustomerOperation))
            {
                var made = await SendAsync(client, token, HttpMethod.Post, api + "/customers", CustomerForm(parameters), cancellationToken);
                yield return CustomerRecord(made as JsonObject ?? new JsonObject());
                yield break;
            }

            var customerId = Customer(Values.Get(parameters, "customer"));
            if (Is(operation, GetCustomerOperation))
            {
                var found = await SendAsync(client, token, HttpMethod.Get, $"{api}/customers/{customerId}", null, cancellationToken);
                yield return CustomerRecord(found as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdateCustomerOperation))
            {
                var form = CustomerForm(parameters);
                if (form.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - email, name, phone, description or metadata.");
                }

                var changed = await SendAsync(client, token, HttpMethod.Post, $"{api}/customers/{customerId}", form, cancellationToken);
                yield return CustomerRecord(changed as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, DeleteCustomerOperation))
            {
                var removed = await SendAsync(client, token, HttpMethod.Delete, $"{api}/customers/{customerId}", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = customerId, ["deleted"] = removed?["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var gone) && gone });
                yield break;
            }

            throw new InvalidOperationException($"Stripe has no operation '{operation}' - customers, get_customer, create_customer, update_customer, delete_customer, charges or balance.");
        }

        // A customer's fields as Stripe's form: its email, name, phone, description and metadata[key], as given.
        private static List<KeyValuePair<string, string>> CustomerForm(IReadOnlyDictionary<string, string> parameters)
        {
            var form = new List<KeyValuePair<string, string>>();
            foreach (var field in new[] { "email", "name", "phone", "description" })
            {
                if (Values.Get(parameters, field) is { } value)
                {
                    if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"The {field} has a line break - one line only.");
                    }

                    if (field == "email" && (!value.Contains('@', StringComparison.Ordinal) || value.Any(char.IsWhiteSpace)))
                    {
                        throw new InvalidOperationException($"'{value}' is not an address.");
                    }

                    form.Add(new(field, value));
                }
            }

            if (Values.Get(parameters, "metadata") is { } metadata)
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(metadata);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("The metadata is not JSON - {\"order\": \"1001\"}.");
                }

                foreach (var (key, value) in parsed as JsonObject ?? throw new InvalidOperationException("The metadata is not a JSON object."))
                {
                    if (!MetadataKey.IsMatch(key))
                    {
                        throw new InvalidOperationException($"'{key}' is not a metadata key Stripe takes (letters, digits, _ . -; at most 40).");
                    }

                    form.Add(new($"metadata[{key}]", value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? ""));
                }
            }

            return form;
        }

        private static ConnectorRecord CustomerRecord(JsonObject customer) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(customer["id"]),
            ["email"] = Text(customer["email"]),
            ["name"] = Text(customer["name"]),
            ["phone"] = Text(customer["phone"]),
            ["description"] = Text(customer["description"]),
            ["balance"] = Number(customer["balance"]),
            ["currency"] = Text(customer["currency"]),
            ["delinquent"] = customer["delinquent"] is JsonValue delinquent && delinquent.TryGetValue<bool>(out var late) && late,
            ["created"] = Moment(customer["created"]),
            ["metadata"] = customer["metadata"]?.ToJsonString() ?? "{}",
        });

        private static ConnectorRecord ChargeRecord(JsonObject charge) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(charge["id"]),
            ["amount"] = Number(charge["amount"]),
            ["amount_captured"] = Number(charge["amount_captured"]),
            ["amount_refunded"] = Number(charge["amount_refunded"]),
            ["currency"] = Text(charge["currency"]),
            ["status"] = Text(charge["status"]),
            ["paid"] = charge["paid"] is JsonValue paid && paid.TryGetValue<bool>(out var isPaid) && isPaid,
            ["refunded"] = charge["refunded"] is JsonValue refunded && refunded.TryGetValue<bool>(out var isRefunded) && isRefunded,
            ["customer"] = Text(charge["customer"]),
            ["description"] = Text(charge["description"]),
            ["receipt_email"] = Text(charge["receipt_email"]),
            ["payment_intent"] = Text(charge["payment_intent"]),
            ["created"] = Moment(charge["created"]),
        });

        // Stripe's seconds since 1970 as a time in UTC.
        private static string? Moment(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<long>(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null;

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static string Customer(string? given) =>
            given is not null && CustomerId.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a Stripe customer's id (cus_...).");

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The Stripe connection has no key - a secret or restricted key.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Stripe's answer as JSON, the form sent as Stripe takes it; a throttled call waits as Stripe says; a failure thrown with its
        // status and Stripe's message.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, List<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
        {
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (form is not null)
                {
                    request.Content = new FormUrlEncodedContent(form);
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
                throw new InvalidOperationException($"Stripe answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = json is JsonObject error ? Text(error["error"]?["message"]) : null;
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json;
        }
    }
}
