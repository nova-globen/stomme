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

namespace Stomme.Modules.Connectors.Mailgun
{
    /// <summary>
    /// Mailgun (BL-229, n8n's Mailgun node: an e-mail sent): a sending domain over Mailgun's API (<c>https://api.mailgun.net/v3</c>,
    /// or the EU's <c>https://api.eu.mailgun.net/v3</c>), its API key as HTTP Basic (<c>api</c> and the key), the domain the
    /// <c>domain</c> setting. A test names the domain and its state.
    /// </summary>
    /// <remarks>
    /// <c>send_email</c>: a sender and recipients (<c>Shop &lt;shop@mg.example.com&gt;</c>, comma-separated), copies, a reply-to, a
    /// subject and a text or HTML body as written - or a stored template and its variables - with up to three tags, headers of
    /// its own, a delivery time and Mailgun's test mode; sent as a form, the message's id and Mailgun's word said. <c>events</c>: the
    /// domain's events - of a kind, to a recipient, between two times, oldest or newest first - page by page by Mailgun's own next
    /// link (followed only while it stays under the domain's events) up to <c>max</c> (1,000); each a record of its id, kind, time
    /// in UTC, recipient, message id, subject, sender, severity, reason, delivery status and tags. <c>bounces</c>,
    /// <c>unsubscribes</c> and <c>complaints</c> - the domain's suppressions - and <c>delete_suppression</c>; <c>domains</c>. Every
    /// address, name and time held to its shape before anything is sent. A throttled call waits as Mailgun says; a failure is
    /// thrown with its status and Mailgun's message.
    /// </remarks>
    public sealed class MailgunConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.mailgun.net/v3";

        public const string SendEmailOperation = "send_email";

        public const string EventsOperation = "events";

        public const string BouncesOperation = "bounces";

        public const string UnsubscribesOperation = "unsubscribes";

        public const string ComplaintsOperation = "complaints";

        public const string DeleteSuppressionOperation = "delete_suppression";

        public const string DomainsOperation = "domains";

        public const int DefaultMax = 1000;

        private const int EventPage = 300;

        private const int SuppressionPage = 1000;

        private static readonly Regex Named = new("^(?<name>.*?)\\s*<(?<email>[^<>\\s]+)>$", RegexOptions.CultureInvariant);

        private static readonly Regex DomainName = new("^(?=.{1,253}$)([A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\.)+[A-Za-z]{2,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex HeaderName = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex TemplateName = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly string[] EventKinds = { "accepted", "rejected", "delivered", "failed", "opened", "clicked", "unsubscribed", "complained", "stored" };

        private static readonly string[] Suppressions = { BouncesOperation, UnsubscribesOperation, ComplaintsOperation };

        private static readonly string[] Known = { SendEmailOperation, EventsOperation, BouncesOperation, UnsubscribesOperation, ComplaintsOperation, DeleteSuppressionOperation, DomainsOperation };

        // JSON as written: variables and their accents, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "mailgun";

        public string DisplayName => "Mailgun";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.mailgun.com");

        public Uri ApiDocumentationUrl { get; } = new("https://documentation.mailgun.com/docs/mailgun/api-reference/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("domain", "Sending domain", ConnectorConfigFieldType.Text, Required: true, Description: "The domain you send from in Mailgun - mg.example.com. In the EU, the endpoint is https://api.eu.mailgun.net/v3."),
        });

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SendEmailOperation, "Send an e-mail", "Sends an e-mail from the domain, with a body or a stored template - n8n's Mailgun.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("from", "From", ConnectorConfigFieldType.Text, Required: true, Description: "Shop <shop@mg.example.com>."),
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: true, Description: "Ada <ada@example.com>, lin@example.com."),
                    new ConnectorConfigField("cc", "Copy", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("bcc", "Blind copy", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("reply_to", "Reply to", ConnectorConfigFieldType.Text, Required: false, Description: "An address."),
                    new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: false, Description: "One line. A template's own, when empty."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: false, Description: "The body as text."),
                    new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Multiline, Required: false, Description: "The body as HTML."),
                    new ConnectorConfigField("template", "Template", ConnectorConfigFieldType.Text, Required: false, Description: "A stored template's name, in place of a body."),
                    new ConnectorConfigField("variables", "Template variables", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object - {\"order\": \"1001\"}."),
                    new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Up to three, comma-separated."),
                    new ConnectorConfigField("headers", "Headers", ConnectorConfigFieldType.Multiline, Required: false, Description: "Headers of its own, a JSON object - {\"X-Order\": \"1001\"}."),
                    new ConnectorConfigField("delivery_time", "Send at", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-20T09:00:00+02:00 - within three days."),
                    new ConnectorConfigField("test_mode", "Test mode", ConnectorConfigFieldType.Text, Required: false, Description: "true to have Mailgun accept it and send nothing."),
                }),
            },
            new ConnectorOperation(EventsOperation, "Get events", "The domain's events - delivered, failed, opened and the rest - newest first.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("event", "Kind", ConnectorConfigFieldType.Text, Required: false, Description: "accepted, rejected, delivered, failed, opened, clicked, unsubscribed, complained or stored."),
                    new ConnectorConfigField("recipient", "Recipient", ConnectorConfigFieldType.Text, Required: false, Description: "Only this address's."),
                    new ConnectorConfigField("begin", "From", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or 2026-10-01T09:00:00+02:00."),
                    new ConnectorConfigField("end", "To", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
                    new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "desc - newest first - or asc.") { Default = "desc" },
                    Max(),
                }),
            },
            new ConnectorOperation(BouncesOperation, "Get bounces", "The addresses that bounced, and why.") { Parameters = new ConnectorConfigSchema(new[] { Max() }) },
            new ConnectorOperation(UnsubscribesOperation, "Get unsubscribes", "The addresses that unsubscribed.") { Parameters = new ConnectorConfigSchema(new[] { Max() }) },
            new ConnectorOperation(ComplaintsOperation, "Get complaints", "The addresses that marked a message as spam.") { Parameters = new ConnectorConfigSchema(new[] { Max() }) },
            new ConnectorOperation(DeleteSuppressionOperation, "Delete a suppression", "Lets an address be sent to again.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("list", "List", ConnectorConfigFieldType.Text, Required: true, Description: "bounces, unsubscribes or complaints."),
                    new ConnectorConfigField("address", "Address", ConnectorConfigFieldType.Text, Required: true, Description: "ada@example.com."),
                }),
            },
            new ConnectorOperation(DomainsOperation, "Get domains", "The account's sending domains and their state.") { Parameters = new ConnectorConfigSchema(new[] { Max() }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var key = Key(context.Credentials);
                var domain = Domain(context.Settings);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var answer = await SendAsync(client, key, HttpMethod.Get, $"{api}/domains/{domain}", null, cancellationToken);
                var found = answer?["domain"] as JsonObject ?? new JsonObject();
                return ConnectionTestResult.Ok($"Signed in - {Text(found["name"]) ?? domain}{(Text(found["state"]) is { } state ? $" ({state})" : "")}.");
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
            var key = Key(context.Credentials);
            var domain = Domain(context.Settings);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SendEmailOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Mailgun has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var form = new List<KeyValuePair<string, string>>();
            var query = new List<string>();
            string? suppression = null;
            switch (operation)
            {
                case SendEmailOperation:
                    {
                        form.Add(new("from", Mailboxes(Values.Get(p, "from") ?? throw new InvalidOperationException("Give the sender - Shop <shop@mg.example.com>."), "sender", one: true)));
                        form.Add(new("to", Mailboxes(Values.Get(p, "to") ?? throw new InvalidOperationException("Give the recipients - Ada <ada@example.com>."), "recipient", one: false)));
                        foreach (var name in new[] { "cc", "bcc" })
                        {
                            if (Values.Get(p, name) is { } copies)
                            {
                                form.Add(new(name, Mailboxes(copies, name, one: false)));
                            }
                        }

                        var template = Values.Get(p, "template") is { } stored ? TemplateName.IsMatch(stored) ? stored : throw new InvalidOperationException($"'{stored}' is not a template's name.") : null;
                        var subject = Values.Get(p, "subject") is { } line ? OneLine(line, "subject") : null;
                        var text = p.TryGetValue("text", out var plain) && !string.IsNullOrWhiteSpace(plain) ? plain : null;
                        var html = p.TryGetValue("html", out var marked) && !string.IsNullOrWhiteSpace(marked) ? marked : null;
                        if (template is null && text is null && html is null)
                        {
                            throw new InvalidOperationException("Give a body - text or HTML - or a template.");
                        }

                        if (template is null && subject is null)
                        {
                            throw new InvalidOperationException("Give the subject.");
                        }

                        if (subject is not null)
                        {
                            form.Add(new("subject", subject));
                        }

                        if (text is not null)
                        {
                            form.Add(new("text", text));
                        }

                        if (html is not null)
                        {
                            form.Add(new("html", html));
                        }

                        if (template is not null)
                        {
                            form.Add(new("template", template));
                        }

                        if (Values.Get(p, "variables") is { } variables)
                        {
                            form.Add(new("t:variables", ObjectOf(variables, "template variables are", "{\"order\": \"1001\"}").ToJsonString(Wire)));
                        }

                        if (Values.Get(p, "reply_to") is { } replyTo)
                        {
                            form.Add(new("h:Reply-To", Mailboxes(replyTo, "reply-to", one: true)));
                        }

                        if (Values.Get(p, "headers") is { } headers)
                        {
                            foreach (var (name, value) in ObjectOf(headers, "headers are", "{\"X-Order\": \"1001\"}"))
                            {
                                form.Add(new("h:" + (HeaderName.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a header's name - X-Order.")),
                                    OneLine(value is JsonValue single && single.TryGetValue<string>(out var s) ? s : value?.ToJsonString(Wire) ?? "", $"header {name}")));
                            }
                        }

                        if (Values.Get(p, "tags") is { } tags)
                        {
                            var each = tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (each.Length > 3)
                            {
                                throw new InvalidOperationException("Give at most three tags - Mailgun keeps no more.");
                            }

                            form.AddRange(each.Select(tag => new KeyValuePair<string, string>("o:tag", tag)));
                        }

                        if (Values.Get(p, "delivery_time") is { } at)
                        {
                            form.Add(new("o:deliverytime", DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                                ? moment.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture)
                                : throw new InvalidOperationException($"'{at}' is not a time (send at) - 2026-10-20T09:00:00+02:00.")));
                        }

                        if (Values.Get(p, "test_mode") is { } testMode)
                        {
                            if (Flag(testMode, "test mode"))
                            {
                                form.Add(new("o:testmode", "yes"));
                            }
                        }

                        break;
                    }

                case EventsOperation:
                    if (Values.Get(p, "event") is { } kind)
                    {
                        query.Add("event=" + Choice(kind, "an event", EventKinds));
                    }

                    if (Values.Get(p, "recipient") is { } recipient)
                    {
                        query.Add("recipient=" + Uri.EscapeDataString(IsAddress(recipient) ? recipient : throw new InvalidOperationException($"'{recipient}' is not an e-mail address.")));
                    }

                    foreach (var name in new[] { "begin", "end" })
                    {
                        if (Values.Get(p, name) is { } time)
                        {
                            query.Add(name + "=" + (DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                                ? moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                                : throw new InvalidOperationException($"'{time}' is not a time ({(name == "begin" ? "from" : "to")}) - 2026-10-01, or 2026-10-01T09:00:00+02:00.")));
                        }
                    }

                    query.Add("ascending=" + (Choice(Values.Get(p, "order") ?? "desc", "an order", "desc", "asc") == "asc" ? "yes" : "no"));
                    break;
                case DeleteSuppressionOperation:
                    suppression = Choice(Values.Get(p, "list") ?? throw new InvalidOperationException("Give the list - bounces, unsubscribes or complaints."), "a list", Suppressions);
                    suppression += "/" + Uri.EscapeDataString(Values.Get(p, "address") is { } address ? IsAddress(address) ? address : throw new InvalidOperationException($"'{address}' is not an e-mail address.") : throw new InvalidOperationException("Give the address."));
                    break;
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            switch (operation)
            {
                case SendEmailOperation:
                    {
                        var sent = await SendAsync(client, key, HttpMethod.Post, $"{api}/{domain}/messages", form, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(sent?["id"]), ["message"] = Text(sent?["message"]) });
                        yield break;
                    }

                case DeleteSuppressionOperation:
                    {
                        var gone = await SendAsync(client, key, HttpMethod.Delete, $"{api}/{domain}/{suppression}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["address"] = Text(gone?["address"]), ["message"] = Text(gone?["message"]) });
                        yield break;
                    }

                case DomainsOperation:
                    {
                        var read = 0;
                        var answer = await SendAsync(client, key, HttpMethod.Get, $"{api}/domains?limit={Math.Min(SuppressionPage, max).ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
                        foreach (var item in (answer?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["name"] = Text(item["name"]),
                                ["state"] = Text(item["state"]),
                                ["type"] = Text(item["type"]),
                                ["created_at"] = Text(item["created_at"]),
                            });
                            if (++read >= max)
                            {
                                yield break;
                            }
                        }

                        yield break;
                    }

                default:
                    {
                        // The events or a suppression list, page by page by Mailgun's own next link - followed only while it stays under
                        // the same list of the same domain.
                        var list = operation == EventsOperation ? "events" : operation;
                        var under = $"{api}/{domain}/{list}";
                        var size = Math.Min(operation == EventsOperation ? EventPage : SuppressionPage, max);
                        string? url = $"{under}?{string.Join("&", query.Append("limit=" + size.ToString(CultureInfo.InvariantCulture)))}";
                        var read = 0;
                        while (url is not null)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var page = await SendAsync(client, key, HttpMethod.Get, url, null, cancellationToken);
                            var items = page?["items"] as JsonArray ?? new JsonArray();
                            foreach (var item in items.OfType<JsonObject>())
                            {
                                yield return operation == EventsOperation ? EventRecord(item) : SuppressionRecord(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            var next = Text(page?["paging"]?["next"]);
                            // The events page by a path (events/{page}), the suppressions by a query (bounces?page=next&address=...).
                            url = items.Count > 0 && next is not null && (next.StartsWith(under + "/", StringComparison.Ordinal) || next.StartsWith(under + "?", StringComparison.Ordinal)) ? next : null;
                        }

                        yield break;
                    }
            }
        }

        private static ConnectorRecord EventRecord(JsonObject item)
        {
            var headers = item["message"]?["headers"] as JsonObject ?? new JsonObject();
            var status = item["delivery-status"] as JsonObject ?? new JsonObject();
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(item["id"]),
                ["event"] = Text(item["event"]),
                ["time"] = item["timestamp"] is JsonValue stamp && stamp.TryGetValue<double>(out var seconds)
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : null,
                ["recipient"] = Text(item["recipient"]),
                ["message_id"] = Text(headers["message-id"]),
                ["subject"] = Text(headers["subject"]),
                ["from"] = Text(headers["from"]),
                ["severity"] = Text(item["severity"]),
                ["reason"] = Text(item["reason"]),
                ["status_code"] = Long(status["code"]),
                ["status"] = Text(status["description"]) is { Length: > 0 } description ? description : Text(status["message"]),
                ["tags"] = string.Join(", ", (item["tags"] as JsonArray ?? new JsonArray()).Select(Text).Where(tag => tag is not null)),
                ["url"] = Text(item["url"]),
                ["ip"] = Text(item["ip"]),
            });
        }

        private static ConnectorRecord SuppressionRecord(JsonObject item) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["address"] = Text(item["address"]),
            ["code"] = Text(item["code"]),
            ["error"] = Text(item["error"]),
            ["tags"] = string.Join(", ", (item["tags"] as JsonArray ?? new JsonArray()).Select(Text).Where(tag => tag is not null)),
            ["created_at"] = Text(item["created_at"]),
        });

        // Addresses, comma-separated, each "Name <address>" or an address alone - checked, and sent as given; one when one is asked.
        private static string Mailboxes(string given, string what, bool one)
        {
            var entries = given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var match = Named.Match(entry);
                if (!IsAddress(match.Success ? match.Groups["email"].Value : entry))
                {
                    throw new InvalidOperationException($"'{entry}' is not an address ({what}) - Ada <ada@example.com>, or ada@example.com.");
                }
            }

            return entries.Length == 0 ? throw new InvalidOperationException($"Give an address ({what}).")
                : one && entries.Length > 1 ? throw new InvalidOperationException($"Give one address ({what}).")
                : string.Join(", ", entries);
        }

        private static string Domain(IReadOnlyDictionary<string, string> settings) =>
            Values.Get(settings, "domain") is { } domain ? DomainName.IsMatch(domain) ? domain.ToLowerInvariant() : throw new InvalidOperationException($"'{domain}' is not a domain - mg.example.com.")
            : throw new InvalidOperationException("Give the sending domain - mg.example.com.");

        private static bool IsAddress(string given) =>
            !given.Any(char.IsWhiteSpace) && given.IndexOf('@') > 0 && given.IndexOf('@') == given.LastIndexOf('@') && given.IndexOf('@') < given.Length - 1;

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static JsonObject ObjectOf(string given, string what, string example)
        {
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
            }

            return parsed as JsonObject ?? throw new InvalidOperationException($"The {what} not a JSON object - {example}.");
        }

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static string Key(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Mailgun connection has no API key.");

        private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static string? Text(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null
            : null;

        // Mailgun's answer as JSON, the key as HTTP Basic (api and the key), a form as a form; a throttled call waiting as Mailgun says;
        // a failure thrown with its status and Mailgun's message.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string key, HttpMethod method, string url, IReadOnlyList<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + key));
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
                request.Headers.Accept.ParseAdd("application/json");
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
                throw new InvalidOperationException($"Mailgun answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = Text(json?["message"]) ?? (payload.Length is > 0 and <= 200 && json is null ? payload.Trim() : null);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            return json;
        }
    }
}
