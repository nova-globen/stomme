#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.InvoiceNinja
{
    /// <summary>
    /// Invoice Ninja (n8n's Invoice Ninja node) over Invoice Ninja 5's API (<c>{address}/api/v1</c>) by an API token -
    /// from Invoice Ninja's settings, as its <c>X-API-TOKEN</c> header, never in the address. A test names the token's company.
    /// </summary>
    /// <remarks>
    /// n8n's resources: clients, invoices, quotes, payments, expenses, tasks and bank transactions - each listed, read, made and
    /// deleted as n8n's node does; an invoice or a quote e-mailed; a bank transaction matched to a payment. Measured against
    /// Invoice Ninja 5.13.43 (<c>scripts/invoiceninja-test-server.ps1</c>), where n8n's node sends version 4's names: an invoice's
    /// or a quote's <c>invoice_date</c> and an expense's <c>expense_date</c> are ignored without a word and the item dated today
    /// (version 5 calls them <c>date</c>); its e-mail (<c>GET /invoices/{id}/email</c>) answers 400, "the requested action email is
    /// not available" (version 5 e-mails by <c>POST /emails</c>); its "is deleted" option filters nothing (<c>status=deleted</c>
    /// does), and an unknown status filters nothing either; a bank transaction without a bank integration is refused, though n8n
    /// has it optional. Invoice Ninja takes what it should not: a payment on an invoice already paid is booked as applied and
    /// changes nothing, and a payment type it has not got is kept - so a payment is held to its invoice's balance, and a payment
    /// type, a currency and a country are looked up by name, code or number in Invoice Ninja's own lists. A payment's delete
    /// answers with the payment as it was before (not deleted, its invoice paid) though it is deleted and its invoice unpaid
    /// again, so a delete whose answer does not say deleted reads the item again. A list goes page by
    /// page by number - the next page's address Invoice Ninja gives is built from its own idea of its address, which need not be
    /// the connection's - and a list holds deleted items unless a status is given, as Invoice Ninja's does.
    /// </remarks>
    public sealed class InvoiceNinjaConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 100; // n8n's, for every item

        private const int MostText = 65536;

        private const int MostLines = 500;

        private enum Kind { Text, Long, Bool, Date, Id, Money, Rate, Country, Currency, PaymentType, BaseType, Items, Contacts, Moment, Hours }

        private sealed record Field(string Name, string Wire, Kind Kind, bool Required, string Description);

        private sealed record Resource(string Name, string Plural, string Route, Field[] Fields, string[] Lists);

        private static readonly Regex IdPattern = new("^[A-Za-z0-9]{1,32}$", RegexOptions.CultureInvariant);

        private static readonly Regex Words = new("^[a-z_]+(\\.[a-z_]+)*(,[a-z_]+(\\.[a-z_]+)*)*$", RegexOptions.CultureInvariant);

        private static readonly string[] Statuses = { "active", "archived", "deleted" };

        private static readonly string[] ClientStatuses = { "all", "paid", "unpaid", "overdue" };

        private static Field F(string name, string wire, Kind kind, bool required, string description) => new(name, wire, kind, required, description);

        private static Field[] Address(string prefix, string label) => new[]
        {
            F(prefix + "address1", prefix + "address1", Kind.Text, false, $"The {label} street - Storgatan 1."), F(prefix + "address2", prefix + "address2", Kind.Text, false, $"The {label} second line."),
            F(prefix + "city", prefix + "city", Kind.Text, false, "Lund."), F(prefix + "state", prefix + "state", Kind.Text, false, "Skåne."),
            F(prefix + "postal_code", prefix + "postal_code", Kind.Text, false, "222 22."), F(prefix + "country", prefix + "country_id", Kind.Country, false, "Its code - SE - its name or its number, 752."),
        };

        private static Field[] Taxes() => new[]
        {
            F("tax_name1", "tax_name1", Kind.Text, false, "Moms."), F("tax_rate1", "tax_rate1", Kind.Rate, false, "25 - a percentage."),
            F("tax_name2", "tax_name2", Kind.Text, false, "A second tax's name."), F("tax_rate2", "tax_rate2", Kind.Rate, false, "A second tax's percentage."),
        };

        private static Field[] Custom() => new[] { F("custom_value1", "custom_value1", Kind.Text, false, "The first custom field."), F("custom_value2", "custom_value2", Kind.Text, false, "The second custom field.") };

        private static Field[] Document(string kind) => new[]
        {
            F("client", "client_id", Kind.Id, true, "The client's id."), F("date", "date", Kind.Date, false, $"The {kind}'s date - today by default, 2026-10-21."),
            F("due_date", "due_date", Kind.Date, false, "2026-11-20."), F("number", "number", Kind.Text, false, $"The {kind}'s number - Invoice Ninja's next by default."),
            F("po_number", "po_number", Kind.Text, false, "The purchase order's number."), F("public_notes", "public_notes", Kind.Long, false, "Notes the client sees."),
            F("private_notes", "private_notes", Kind.Long, false, "Notes only you see."), F("discount", "discount", Kind.Rate, false, "A percentage, or an amount with is_amount_discount."),
            F("is_amount_discount", "is_amount_discount", Kind.Bool, false, "The discount an amount, true or false."), F("partial", "partial", Kind.Money, false, "A deposit due first."),
            F("partial_due_date", "partial_due_date", Kind.Date, false, "When the deposit is due."),
            F("items", "line_items", Kind.Items, false, "Its lines as JSON - [{\"product_key\": \"kaffe\", \"notes\": \"Bryggkaffe\", \"cost\": 120, \"quantity\": 3}] - from the step's Query box."),
        }.Concat(Taxes()).Concat(Custom()).ToArray();

        private static readonly string[] Since = { "created_since", "updated_since" };

        private static readonly Resource[] Resources =
        {
            new("client", "clients", "/clients", new[]
            {
                F("name", "name", Kind.Text, false, "Kafé Lund."), F("id_number", "id_number", Kind.Text, false, "Your number for the client."),
                F("vat_number", "vat_number", Kind.Text, false, "SE000000000001."), F("work_phone", "phone", Kind.Text, false, "040-628 04 03."),
                F("website", "website", Kind.Text, false, "https://kafe.example."), F("private_notes", "private_notes", Kind.Long, false, "Notes only you see."),
                F("contact_first_name", "first_name", Kind.Text, false, "Its contact's first name."), F("contact_last_name", "last_name", Kind.Text, false, "Its contact's last name."),
                F("contact_email", "email", Kind.Text, false, "Its contact's e-mail."), F("contact_phone", "phone", Kind.Text, false, "Its contact's phone."),
                F("contacts", "contacts", Kind.Contacts, false, "Several contacts as JSON - [{\"first_name\": \"Ada\", \"email\": \"ada@kafe.example\"}] - from the step's Query box."),
            }.Concat(Address(string.Empty, "billing")).Concat(Address("shipping_", "shipping")).ToArray(), Since),
            new("invoice", "invoices", "/invoices", Document("invoice").Concat(new[]
            {
                F("mark_sent", "mark_sent", Kind.Bool, false, "Marked sent when made, true or false."), F("paid", "amount_paid", Kind.Money, false, "An amount paid when made."),
                F("send_email", "send_email", Kind.Bool, false, "E-mailed to the client when made, true or false."),
            }).ToArray(), Since.Concat(new[] { "number", "client_status" }).ToArray()),
            new("quote", "quotes", "/quotes", Document("quote"), Since.Concat(new[] { "number" }).ToArray()),
            new("payment", "payments", "/payments", new[]
            {
                F("invoice", "invoice_id", Kind.Id, true, "The invoice's id - its client and balance are read from it."), F("amount", "amount", Kind.Money, true, "360 - at most the invoice's balance."),
                F("type", "type_id", Kind.PaymentType, false, "Bank Transfer, Swish, Cash - its name or its number."), F("date", "date", Kind.Date, false, "Today by default, 2026-10-21."),
                F("transaction_reference", "transaction_reference", Kind.Text, false, "The bank's reference."), F("private_notes", "private_notes", Kind.Long, false, "Notes only you see."),
            }, Since),
            new("expense", "expenses", "/expenses", new[]
            {
                F("amount", "amount", Kind.Money, true, "99.50."), F("date", "date", Kind.Date, false, "The expense's date - today by default, 2026-10-21."),
                F("client", "client_id", Kind.Id, false, "The client's id."), F("vendor", "vendor_id", Kind.Id, false, "The vendor's id."),
                F("category", "category_id", Kind.Id, false, "The expense category's id."), F("billable", "should_be_invoiced", Kind.Bool, false, "To be invoiced, true or false."),
                F("currency", "currency_id", Kind.Currency, false, "Its code - SEK - or its number."), F("payment_date", "payment_date", Kind.Date, false, "When it was paid."),
                F("payment_type", "payment_type_id", Kind.PaymentType, false, "How it was paid - its name or its number."), F("transaction_reference", "transaction_reference", Kind.Text, false, "The bank's reference."),
                F("public_notes", "public_notes", Kind.Long, false, "Notes."), F("private_notes", "private_notes", Kind.Long, false, "Notes only you see."),
            }.Concat(Taxes()).Concat(Custom()).ToArray(), Since),
            new("task", "tasks", "/tasks", new[]
            {
                F("description", "description", Kind.Long, false, "What was done."), F("client", "client_id", Kind.Id, false, "The client's id."),
                F("project", "project_id", Kind.Id, false, "The project's id."), F("start", "start", Kind.Moment, false, "When it started - 2026-10-21T08:00, in UTC unless it says."),
                F("end", "end", Kind.Moment, false, "When it ended - or hours."), F("hours", "hours", Kind.Hours, false, "How long, from its start - 1.5."),
            }.Concat(Custom()).ToArray(), Since),
            new("bank_transaction", "bank_transactions", "/bank_transactions", new[]
            {
                F("bank_integration", "bank_integration_id", Kind.Id, true, "The bank account's id - Invoice Ninja requires it."), F("amount", "amount", Kind.Money, true, "360."),
                F("base_type", "base_type", Kind.BaseType, false, "CREDIT (a deposit) or DEBIT (a withdrawal) - CREDIT by default."), F("date", "date", Kind.Date, false, "2026-10-21."),
                F("currency", "currency_id", Kind.Currency, false, "Its code - SEK - or its number."), F("description", "description", Kind.Long, false, "The bank's text."),
            }, Since),
        };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "invoiceninja";

        public string DisplayName => "Invoice Ninja";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API token (Settings - Account Management - API Tokens)" };

        public string EndpointExample => "https://invoicing.co";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Commerce };

        public Uri WebsiteUrl { get; } = new("https://invoiceninja.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://api-docs.invoicing.co/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField IncludeField = new("include", "Include", ConnectorConfigFieldType.Text, Required: false, Description: "What to include with it, by commas - client, payments.");

        private static readonly Dictionary<string, ConnectorConfigField> ListFields = new(StringComparer.Ordinal)
        {
            ["created_since"] = new("created_since", "Made since", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or a moment - 2026-10-01T08:00."),
            ["updated_since"] = new("updated_since", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or a moment."),
            ["number"] = new("number", "Number", ConnectorConfigFieldType.Text, Required: false, Description: "Its number - F-1001."),
            ["client_status"] = new("client_status", "Paid", ConnectorConfigFieldType.Text, Required: false, Description: "all, paid, unpaid or overdue."),
        };

        private static ConnectorConfigField Parameter(Field field, bool required) => new(field.Name, Label(field.Name), field.Kind switch
        {
            Kind.Long or Kind.Items or Kind.Contacts => ConnectorConfigFieldType.Multiline,
            Kind.Bool => ConnectorConfigFieldType.Boolean,
            _ => ConnectorConfigFieldType.Text,
        }, Required: required, Description: field.Description + (field.Kind is Kind.Long or Kind.Items or Kind.Contacts ? " Over several lines from the step's Query box." : string.Empty));

        private static string Label(string name) => char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' ');

        public IReadOnlyList<ConnectorOperation> Operations { get; } = BuildOperations();

        private static IReadOnlyList<ConnectorOperation> BuildOperations()
        {
            var operations = new List<ConnectorOperation>();
            foreach (var resource in Resources)
            {
                var label = resource.Name.Replace('_', ' ');
                var id = new ConnectorConfigField(resource.Name, Label(resource.Name), ConnectorConfigFieldType.Text, Required: true, Description: "Its id - VolejRejNm.");
                var lists = new[]
                {
                    new ConnectorConfigField("search", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "What Invoice Ninja's own search finds."),
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "active, archived or deleted, by commas - every one by default."),
                }.Concat(resource.Lists.Select(name => ListFields[name])).Concat(new[] { IncludeField, MaxField }).ToArray();
                operations.Add(new ConnectorOperation(resource.Plural, $"Get {resource.Plural.Replace('_', ' ')}", $"n8n's {label} - Get Many.") { Parameters = new(lists) });
                operations.Add(new ConnectorOperation(resource.Name, $"Get a {label}", $"n8n's {label} - Get.") { Parameters = new(new[] { id, IncludeField }) });
                operations.Add(new ConnectorOperation(resource.Name + "_create", $"Create a {label}", $"n8n's {label} - Create.") { Parameters = new(resource.Fields.Select(field => Parameter(field, field.Required)).ToArray()) });
                operations.Add(new ConnectorOperation(resource.Name + "_delete", $"Delete a {label}", $"n8n's {label} - Delete: Invoice Ninja marks it deleted.") { Parameters = new(new[] { id }) });
            }

            foreach (var kind in new[] { "invoice", "quote" })
            {
                operations.Add(new ConnectorOperation(kind + "_email", $"E-mail a {kind}", $"n8n's {kind} - Email: to the client's contacts, by Invoice Ninja's own template.") { Parameters = new(new[] { new ConnectorConfigField(kind, Label(kind), ConnectorConfigFieldType.Text, Required: true, Description: "Its id.") }) });
            }

            operations.Add(new ConnectorOperation("bank_transaction_match", "Match a bank transaction to a payment", "n8n's bank transaction - Match Payment.")
            {
                Parameters = new(new[]
                {
                    new ConnectorConfigField("bank_transaction", "Bank transaction", ConnectorConfigFieldType.Text, Required: true, Description: "Its id."),
                    new ConnectorConfigField("payment", "Payment", ConnectorConfigFieldType.Text, Required: true, Description: "The payment's id."),
                }),
            });
            return operations;
        }

        private static readonly string[] Known = BuildOperations().Select(operation => operation.Name).ToArray();

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var company = (await session.SendAsync(HttpMethod.Get, "/companies?per_page=1", null, cancellationToken))?["data"]?[0];
                return ConnectionTestResult.Ok($"Invoice Ninja answers: the token reaches the company {Text(company?["settings"]?["name"]) ?? "it has"}.");
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
            var name = (context.OperationName ?? "clients").Trim().ToLowerInvariant();
            if (!Known.Contains(name))
            {
                throw new InvalidOperationException($"Invoice Ninja has no operation '{context.OperationName}' - for each of {string.Join(", ", Resources.Select(resource => resource.Name))}: its list ({Resources[0].Plural}), get ({Resources[0].Name}), _create and _delete; invoice_email and quote_email; bank_transaction_match.");
            }

            var p = context.Parameters;
            if (name is "invoice_email" or "quote_email")
            {
                var kind = name[..^"_email".Length];
                var id = Id(p, kind);
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                // Version 5 e-mails by /emails: n8n's GET /invoices/{id}/email answers 400, "the requested action email is not available".
                yield return Record(await Data(session, HttpMethod.Post, "/emails", new JsonObject { ["entity"] = kind, ["entity_id"] = id, ["template"] = "email_template_" + kind }, cancellationToken));
                yield break;
            }

            if (name == "bank_transaction_match")
            {
                var transaction = Id(p, "bank_transaction");
                var payment = Id(p, "payment");
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.SendAsync(HttpMethod.Post, "/bank_transactions/match", new JsonObject { ["transactions"] = new JsonArray(new JsonObject { ["id"] = transaction, ["payment_id"] = payment }) }, cancellationToken);
                foreach (var item in (answer?["data"] as JsonArray ?? throw new InvalidOperationException("Invoice Ninja answered without the transaction - check the address.")).OfType<JsonObject>())
                {
                    yield return Record(item);
                }

                yield break;
            }

            // bank_transaction is no prefix of another name, nor any name of one: the longest that fits is the resource.
            var resource = Resources.Where(item => name == item.Plural || name == item.Name || name.StartsWith(item.Name + "_", StringComparison.Ordinal)).OrderByDescending(item => item.Name.Length).First();
            var verb = name == resource.Plural ? "list" : name == resource.Name ? "get" : name[(resource.Name.Length + 1)..];
            switch (verb)
            {
                case "list":
                    {
                        var query = ListQuery(resource, p);
                        var max = Count(p, "max", DefaultMax);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var found = 0;
                        for (var page = 1; found < max; page++)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, $"{resource.Route}?{query}{(query.Length == 0 ? string.Empty : "&")}per_page={PageSize}&page={page}", null, cancellationToken);
                            var items = answer?["data"] as JsonArray ?? throw new InvalidOperationException("Invoice Ninja answered with what is not a list - check the address: a sign-in page or a proxy answers so.");
                            foreach (var item in items.OfType<JsonObject>().Take(max - found))
                            {
                                found++;
                                yield return Record(item);
                            }

                            // Page by number: the next page's address Invoice Ninja gives is built from its own idea of its address.
                            var pages = answer["meta"]?["pagination"]?["total_pages"]?.GetValue<int>() ?? page;
                            if (page >= pages || items.Count == 0)
                            {
                                break;
                            }
                        }

                        break;
                    }

                case "get":
                    {
                        var id = Id(p, resource.Name);
                        var include = Include(p);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        yield return Record(await Data(session, HttpMethod.Get, $"{resource.Route}/{id}{(include is null ? string.Empty : "?include=" + include)}", null, cancellationToken));
                        break;
                    }

                case "create":
                    {
                        var given = Gather(resource, p);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var (body, query) = await BodyAsync(session, resource, given, cancellationToken);
                        yield return Record(await Data(session, HttpMethod.Post, resource.Route + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query)), body, cancellationToken));
                        break;
                    }

                default:
                    {
                        var id = Id(p, resource.Name);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

                        // A payment's delete answers with the payment as it was before: read again, it is deleted.
                        var deleted = await Data(session, HttpMethod.Delete, $"{resource.Route}/{id}", null, cancellationToken);
                        yield return Record(deleted["is_deleted"] is JsonValue flag && flag.TryGetValue<bool>(out var done) && done ? deleted : await Data(session, HttpMethod.Get, $"{resource.Route}/{id}", null, cancellationToken));
                        break;
                    }
            }
        }

        // A list's filters by Invoice Ninja's names - n8n's "is deleted" as the status that filters, a status held to the three it knows.
        private static string ListQuery(Resource resource, IReadOnlyDictionary<string, string> p)
        {
            var query = new List<string>();
            if (Values.Get(p, "search") is { } search)
            {
                query.Add("filter=" + Uri.EscapeDataString(search.Length <= 200 && !search.Any(char.IsControl) ? search : throw new InvalidOperationException("A search is one line (search).")));
            }

            if (Values.Get(p, "status") is { } status)
            {
                var statuses = status.Split(',').Select(part => part.Trim().ToLowerInvariant()).Where(part => part.Length > 0).ToArray();
                query.Add("status=" + (statuses.Length > 0 && statuses.All(Statuses.Contains) ? string.Join(",", statuses.Distinct()) : throw new InvalidOperationException($"'{status}' is not active, archived or deleted, by commas (status).")));
            }

            foreach (var since in Since.Where(key => resource.Lists.Contains(key)))
            {
                if (Values.Get(p, since) is { } given)
                {
                    query.Add((since == "created_since" ? "created_at=" : "updated_at=") + Moment(given, since).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                }
            }

            if (resource.Lists.Contains("number") && Values.Get(p, "number") is { } number)
            {
                query.Add("number=" + Uri.EscapeDataString(OneLine(number, "number")));
            }

            if (resource.Lists.Contains("client_status") && Values.Get(p, "client_status") is { } paid)
            {
                query.Add("client_status=" + (ClientStatuses.Contains(paid.Trim().ToLowerInvariant()) ? paid.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{paid}' is not all, paid, unpaid or overdue (client_status).")));
            }

            if (Include(p) is { } include)
            {
                query.Add("include=" + include);
            }

            return string.Join("&", query);
        }

        private static string? Include(IReadOnlyDictionary<string, string> p) =>
            Values.Get(p, "include") is { } include
                ? Words.IsMatch(include.Replace(" ", string.Empty, StringComparison.Ordinal)) ? include.Replace(" ", string.Empty, StringComparison.Ordinal) : throw new InvalidOperationException($"'{include}' is not names by commas - client, payments (include).")
                : null;

        // What was given, held to its shape, by Invoice Ninja's names - a required one missing refused.
        private static List<(Field Field, object Value)> Gather(Resource resource, IReadOnlyDictionary<string, string> p)
        {
            var given = new List<(Field, object)>();
            foreach (var field in resource.Fields)
            {
                var value = Values.Get(p, field.Name) ?? (field == FromQuery(resource) ? Values.Get(p, "query") : null);
                if (value is null)
                {
                    if (field.Required)
                    {
                        throw new InvalidOperationException($"Give the {field.Name.Replace('_', ' ')} - {field.Description} ({field.Name}).");
                    }

                    continue;
                }

                given.Add((field, field.Kind switch
                {
                    Kind.Text => OneLine(value, field.Name),
                    Kind.Long => value.Length <= MostText ? value : throw new InvalidOperationException($"A {field.Name.Replace('_', ' ')} is at most 64 kilobytes ({field.Name})."),
                    Kind.Bool => bool.TryParse(value, out var flag) ? flag : throw new InvalidOperationException($"'{value}' is not true or false ({field.Name})."),
                    Kind.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : throw new InvalidOperationException($"'{value}' is not a date - 2026-10-21 ({field.Name})."),
                    Kind.Id => IdOf(value, field.Name),
                    Kind.Money => Amount(value, field.Name, field.Required),
                    Kind.Rate => Amount(value, field.Name, false) is var rate && (field.Name == "discount" || rate <= 100) ? rate : throw new InvalidOperationException($"'{value}' is not a percentage - 0 to 100 ({field.Name})."),
                    Kind.Hours => Amount(value, field.Name, true) is var hours && hours <= 24 * 366 ? hours : throw new InvalidOperationException($"'{value}' is not hours - 1.5 ({field.Name})."),
                    Kind.Moment => Moment(value, field.Name),
                    Kind.BaseType => value.Trim().ToUpperInvariant() switch
                    {
                        "CREDIT" or "DEPOSIT" => "CREDIT",
                        "DEBIT" or "WITHDRAWAL" => "DEBIT",
                        _ => throw new InvalidOperationException($"'{value}' is not CREDIT or DEBIT ({field.Name})."),
                    },
                    Kind.Items => Items(value, field.Name),
                    Kind.Contacts => Contacts(value, field.Name),
                    _ => OneLine(value, field.Name), // a country, a currency, a payment type: looked up in Invoice Ninja's lists
                }));
            }

            if (resource.Name == "client" && !given.Any(pair => pair.Item1.Name is "name" or "contacts" or "contact_first_name" or "contact_last_name" or "contact_email"))
            {
                throw new InvalidOperationException("Give the client's name, or a contact's name or e-mail (name).");
            }

            if (resource.Name == "task" && given.Any(pair => pair.Item1.Name is "end" or "hours") && !given.Any(pair => pair.Item1.Name == "start"))
            {
                throw new InvalidOperationException("Give the task's start with its end or hours (start).");
            }

            if (given.Any(pair => pair.Item1.Name == "end") && given.Any(pair => pair.Item1.Name == "hours"))
            {
                throw new InvalidOperationException("Give the task's end or its hours, not both (hours).");
            }

            return given;
        }

        // The one field the step's Query box fills: its lines or contacts, else its first text over several lines.
        private static Field? FromQuery(Resource resource) => resource.Fields.FirstOrDefault(field => field.Kind is Kind.Items or Kind.Contacts) ?? resource.Fields.FirstOrDefault(field => field.Kind == Kind.Long);

        // The body and the query: names Invoice Ninja's lists hold looked up, a task's time as its log, a payment held to its invoice.
        private static async Task<(JsonObject Body, List<string> Query)> BodyAsync(Session session, Resource resource, List<(Field Field, object Value)> given, CancellationToken cancellationToken)
        {
            var body = new JsonObject();
            var query = new List<string>();
            JsonObject? contact = null;
            DateTimeOffset? start = null;
            foreach (var (field, value) in given)
            {
                switch (field.Kind)
                {
                    case Kind.Country:
                    case Kind.Currency:
                    case Kind.PaymentType:
                        body[field.Wire] = await session.LookUpAsync(field.Kind == Kind.Country ? "countries" : field.Kind == Kind.Currency ? "currencies" : "payment_types", (string)value, field.Name, cancellationToken);
                        continue;
                    case Kind.Moment when field.Name == "start":
                        start = (DateTimeOffset)value;
                        continue;
                }

                if (resource.Name == "client" && field.Name.StartsWith("contact_", StringComparison.Ordinal))
                {
                    contact ??= new JsonObject();
                    contact[field.Wire] = (string)value;
                    continue;
                }

                if (field.Name is "mark_sent" or "send_email" or "paid")
                {
                    query.Add(field.Wire + "=" + (value is bool flag ? (flag ? "true" : "false") : ((decimal)value).ToString(CultureInfo.InvariantCulture)));
                    continue;
                }

                if (field.Name is "end" or "hours")
                {
                    var end = value is DateTimeOffset moment ? moment : start!.Value.AddSeconds((double)((decimal)value * 3600));
                    if (end <= start)
                    {
                        throw new InvalidOperationException("The task's end is before its start (end).");
                    }

                    body["time_log"] = $"[[{start!.Value.ToUnixTimeSeconds()},{end.ToUnixTimeSeconds()}]]";
                    continue;
                }

                body[field.Wire] = value switch
                {
                    DateOnly day => JsonValue.Create(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    decimal number => JsonValue.Create(number),
                    bool flag => JsonValue.Create(flag),
                    JsonArray list => list,
                    string text => JsonValue.Create(text),
                    _ => null,
                };
            }

            if (start is { } begun && body["time_log"] is null)
            {
                body["time_log"] = $"[[{begun.ToUnixTimeSeconds()},0]]"; // started and running, as Invoice Ninja keeps a running task
            }

            if (contact is not null)
            {
                body["contacts"] = body["contacts"] is JsonArray several ? throw new InvalidOperationException("Give one contact by its fields or several as JSON, not both (contacts).") : new JsonArray(contact);
            }

            if (resource.Name == "bank_transaction" && body["base_type"] is null)
            {
                body["base_type"] = "CREDIT";
            }

            if (resource.Name == "payment")
            {
                // Invoice Ninja books a payment on an invoice already paid as applied, and changes nothing: held to the balance here.
                var invoiceId = (string)body["invoice_id"]!;
                body.Remove("invoice_id");
                var invoice = await Data(session, HttpMethod.Get, $"/invoices/{invoiceId}", null, cancellationToken);
                var amount = (decimal)body["amount"]!;
                var balance = invoice["balance"]?.GetValue<decimal>() ?? 0;
                if (amount > balance)
                {
                    throw new InvalidOperationException(balance <= 0
                        ? $"The invoice {Text(invoice["number"]) ?? invoiceId} is paid - Invoice Ninja would book the payment and change nothing (invoice)."
                        : $"The invoice {Text(invoice["number"]) ?? invoiceId}'s balance is {balance.ToString(CultureInfo.InvariantCulture)}, less than {amount.ToString(CultureInfo.InvariantCulture)} (amount).");
                }

                body["client_id"] = invoice["client_id"]?.DeepClone();
                body["invoices"] = new JsonArray(new JsonObject { ["invoice_id"] = invoiceId, ["amount"] = amount });
            }

            return (body, query);
        }

        private static JsonArray Items(string value, string name)
        {
            JsonArray list;
            try
            {
                list = JsonNode.Parse(value) as JsonArray ?? throw new InvalidOperationException($"The {name} are a JSON list - [{{\"notes\": \"Kaffe\", \"cost\": 120, \"quantity\": 3}}] ({name}).");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {name} are a JSON list - [{{\"notes\": \"Kaffe\", \"cost\": 120, \"quantity\": 3}}] ({name}).");
            }

            if (list.Count is 0 or > MostLines)
            {
                throw new InvalidOperationException($"Give 1 to {MostLines} lines ({name}).");
            }

            var lines = new JsonArray();
            foreach (var node in list)
            {
                var line = node as JsonObject ?? throw new InvalidOperationException($"Each line is a JSON object ({name}).");
                var item = new JsonObject();
                foreach (var (key, part) in line)
                {
                    // n8n's names for a line - service, description, hours - and Invoice Ninja's own.
                    var wire = key switch { "service" => "product_key", "description" => "notes", "hours" => "quantity", var other => other };
                    item[wire] = wire switch
                    {
                        "product_key" or "notes" or "tax_name1" or "tax_name2" or "tax_name3" => part is JsonValue text && text.TryGetValue<string>(out var said) && said.Length <= MostText ? said : throw new InvalidOperationException($"A line's {key} is text ({name})."),
                        "cost" or "quantity" or "tax_rate1" or "tax_rate2" or "tax_rate3" or "discount" => part is JsonValue number && (number.TryGetValue<decimal>(out var amount) || (number.TryGetValue<string>(out var written) && decimal.TryParse(written, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount))) ? amount : throw new InvalidOperationException($"A line's {key} is a number ({name})."),
                        _ => throw new InvalidOperationException($"A line has no '{key}' - product_key, notes, cost, quantity, tax_name1, tax_rate1, tax_name2, tax_rate2, discount ({name})."),
                    };
                }

                lines.Add(item);
            }

            return lines;
        }

        private static JsonArray Contacts(string value, string name)
        {
            JsonArray list;
            try
            {
                list = JsonNode.Parse(value) as JsonArray ?? throw new InvalidOperationException($"The {name} are a JSON list - [{{\"first_name\": \"Ada\", \"email\": \"ada@kafe.example\"}}] ({name}).");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {name} are a JSON list - [{{\"first_name\": \"Ada\", \"email\": \"ada@kafe.example\"}}] ({name}).");
            }

            var contacts = new JsonArray();
            foreach (var node in list.Count is > 0 and <= 50 ? list : throw new InvalidOperationException($"Give 1 to 50 contacts ({name})."))
            {
                var contact = new JsonObject();
                foreach (var (key, part) in node as JsonObject ?? throw new InvalidOperationException($"Each contact is a JSON object ({name})."))
                {
                    var wire = key switch { "firstName" => "first_name", "lastName" => "last_name", var other => other };
                    contact[wire] = wire is "first_name" or "last_name" or "email" or "phone"
                        ? part is JsonValue text && text.TryGetValue<string>(out var said) ? OneLine(said, name) : throw new InvalidOperationException($"A contact's {key} is text ({name}).")
                        : throw new InvalidOperationException($"A contact has no '{key}' - first_name, last_name, email, phone ({name}).");
                }

                contacts.Add(contact);
            }

            return contacts;
        }

        private static string OneLine(string value, string name) =>
            value.Length <= 250 && !value.Any(char.IsControl) ? value : throw new InvalidOperationException($"A {name.Replace('_', ' ')} is one line of at most 250 characters ({name}).");

        private static decimal Amount(string value, string name, bool aboveZero) =>
            decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && (aboveZero ? amount > 0 : amount >= 0) && amount < 1_000_000_000_000m
                ? amount
                : throw new InvalidOperationException($"'{value}' is not an amount{(aboveZero ? " above 0" : string.Empty)} - 99.50 ({name}).");

        private static readonly string[] Moments = { "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.fffK" };

        private static DateTimeOffset Moment(string value, string name) =>
            DateTimeOffset.TryParseExact(value.Trim(), Moments, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment
                : throw new InvalidOperationException($"'{value}' is not a moment - 2026-10-21T08:00, or a day ({name}).");

        private static string Id(IReadOnlyDictionary<string, string> p, string name) =>
            IdOf(Values.Get(p, name) ?? throw new InvalidOperationException($"Give the {name.Replace('_', ' ')} - its id, VolejRejNm ({name})."), name);

        private static string IdOf(string given, string name) =>
            IdPattern.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id of Invoice Ninja's - letters and digits, VolejRejNm ({name}).");

        private static int Count(IReadOnlyDictionary<string, string> p, string name, int fallback) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{given}' is not a number above 0 ({name}).")
                : fallback;

        private static async Task<JsonObject> Data(Session session, HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken) =>
            (await session.SendAsync(method, route, body, cancellationToken))?["data"] as JsonObject
                ?? throw new InvalidOperationException("Invoice Ninja answered without the item - check the address: a sign-in page or a proxy answers so.");

        private static ConnectorRecord Record(JsonObject data) => new(data.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string token;
            private JsonNode? statics;

            private Session(HttpClient client, string api, string token)
            {
                this.client = client;
                this.api = api;
                this.token = token;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give Invoice Ninja's address - https://invoicing.co, or your own.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                foreach (var suffix in new[] { "/api/v1", "/api" })
                {
                    if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        root = root[..^suffix.Length]; // the API's own address given: Invoice Ninja's
                        break;
                    }
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Invoice Ninja's address - https://invoicing.co, with no query.");
                }

                var token = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Invoice Ninja connection has no API token.");
                return new Session(egress.CreateHttpClient(timeout), root + "/api/v1", token);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A name, code or number from Invoice Ninja's own lists - its countries, currencies and payment types - as its id.</summary>
            public async Task<JsonNode> LookUpAsync(string list, string given, string name, CancellationToken cancellationToken)
            {
                statics ??= await SendAsync(HttpMethod.Get, "/statics", null, cancellationToken);
                var items = (statics?[list] as JsonArray ?? throw new InvalidOperationException($"Invoice Ninja answered without its {list.Replace('_', ' ')} - check the address.")).OfType<JsonObject>().ToArray();
                bool Is(JsonObject item, params string[] keys) => keys.Any(key => string.Equals(Text(item[key]) ?? item[key]?.ToString(), given.Trim(), StringComparison.OrdinalIgnoreCase));
                var found = items.FirstOrDefault(item => list switch
                {
                    "countries" => Is(item, "id", "iso_3166_2", "iso_3166_3", "name"),
                    "currencies" => Is(item, "id", "code", "name"),
                    _ => Is(item, "id", "name"),
                }) ?? throw new InvalidOperationException($"Invoice Ninja has no {list switch { "countries" => "country", "currencies" => "currency", _ => "payment type" }} '{given}' ({name}).");
                var id = Text(found["id"]) ?? found["id"]?.ToString() ?? string.Empty;
                return list == "countries" ? JsonValue.Create(id) : long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(id);
            }

            /// <summary>A call with the token as Invoice Ninja's header; its JSON, or its status and messages.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.TryAddWithoutValidation("X-API-TOKEN", token);
                    request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json ?? throw new InvalidOperationException("Invoice Ninja answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }

                // Invoice Ninja's messages: {"message": "...", "errors": {"field": ["..."]}}; a missing item is a 400 naming its model.
                var message = Text(json?["message"]) ?? string.Empty;
                var status = (int)response.StatusCode;
                var model = Regex.Match(message, @"^No query results for model \[App\\Models\\(\w+)\]", RegexOptions.CultureInvariant);
                var said = model.Success
                    ? $"it has no such {Regex.Replace(model.Groups[1].Value, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant()}"
                    : json?["errors"] is JsonObject errors && errors.Count > 0
                        ? string.Join("; ", errors.Select(pair => pair.Key + ": " + string.Join(" ", (pair.Value as JsonArray ?? new JsonArray()).Select(item => Text(item)))))
                        : message;
                said = said.Length > 300 ? said[..300] : said;
                throw new InvalidOperationException($"Invoice Ninja answered HTTP {status}{(said.Length == 0 ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + (status is 401 or 403 && message.Contains("token", StringComparison.OrdinalIgnoreCase) ? " - check the API token." : string.Empty));
            }
        }
    }
}
