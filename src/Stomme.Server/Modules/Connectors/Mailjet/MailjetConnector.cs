#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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

namespace Stomme.Modules.Connectors.Mailjet
{
    /// <summary>
    /// Mailjet (n8n's Mailjet node: an e-mail sent, with a body or a template): Mailjet's Send API v3.1 with the account's
    /// API key and secret key as HTTP Basic, over the host's egress. A test names the account's sender addresses. Mailjet's SMS
    /// takes a token of its own: the <c>mailjetsms</c> connector.
    /// </summary>
    /// <remarks>
    /// <c>send_email</c>: a sender (<c>Kafé &lt;kafe@example.com&gt;</c>), recipients, copies and blind copies (comma-separated, a
    /// name with each when given), a reply-to, a subject, a text or HTML body as written - or <c>send_template</c>, a stored
    /// template's number and its language - with variables as a JSON object, open and click tracking, a priority and a campaign;
    /// Mailjet's word for each message read, an error among them a failure that says so, the message's id and address kept.
    /// <c>templates</c>: the account's templates, page by page by offset; <c>message</c>: a sent message's state by its id. Every
    /// address, number and kind held to its shape before anything is sent. A throttled call waits as Mailjet says; a failure is
    /// thrown with its status and Mailjet's error messages.
    /// </remarks>
    public sealed class MailjetConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.mailjet.com";

        public const string SendEmailOperation = "send_email";

        public const string SendTemplateOperation = "send_template";

        public const string TemplatesOperation = "templates";

        public const string MessageOperation = "message";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Named = new("^(?<name>.*?)\\s*<(?<email>[^<>\\s]+)>$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s,<>]+@[^@\\s,<>]+\\.[^@\\s,<>]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Number = new("^[0-9]{1,19}$", RegexOptions.CultureInvariant);

        private static readonly Regex Campaign = new("^[^\\x00-\\x1F]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SendEmailOperation, SendTemplateOperation, TemplatesOperation, MessageOperation };

        private static readonly string[] Tracking = { "account_default", "disabled", "enabled" };

        public string ConnectorType => "mailjet";

        public string DisplayName => "Mailjet";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "API key (Account settings > REST API > API key management)",
            ["password"] = "Secret key (the same page)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.mailjet.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://dev.mailjet.com/email/reference/send-emails/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigField[] Message(bool template) => new[]
        {
            Field("from", "From", "Kafé <kafe@example.com> - an address Mailjet has verified.", required: true),
            Field("to", "To", "Ada <ada@example.com>, lin@example.com.", required: true),
            Field("cc", "Copy", "Addresses, comma-separated."),
            Field("bcc", "Blind copy", "Addresses, comma-separated."),
            Field("reply_to", "Reply to", "An address."),
            Field("subject", "Subject", template ? "One line - the template's own when empty." : "One line.", required: !template),
        }.Concat(template
            ? new[] { Field("template", "Template", "The template's number.", required: true), Field("template_language", "Template language", "true: the template's variables and logic are used.", ConnectorConfigFieldType.Boolean) }
            : new[] { Field("text", "Text", "The body as text.", ConnectorConfigFieldType.Multiline), Field("html", "HTML", "The body as HTML.", ConnectorConfigFieldType.Multiline) })
        .Concat(new[]
        {
            Field("variables", "Variables", "A JSON object - {\"order\": \"1001\"}.", ConnectorConfigFieldType.Multiline),
            Field("track_opens", "Track opens", "account_default, disabled or enabled."),
            Field("track_clicks", "Track clicks", "account_default, disabled or enabled."),
            Field("priority", "Priority", "0 to 3 - 3 the most urgent.", ConnectorConfigFieldType.Integer),
            Field("campaign", "Campaign", "A campaign's name to file it under."),
            Field("deduplicate", "Once per campaign", "true: an address in the campaign gets it once.", ConnectorConfigFieldType.Boolean),
        }).ToArray();

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SendEmailOperation, "Send an e-mail", "With a text or HTML body - n8n's Mailjet Email Send.") { Parameters = new ConnectorConfigSchema(Message(template: false)) },
            new ConnectorOperation(SendTemplateOperation, "Send a template", "A stored template with its variables - n8n's Mailjet Email Send Template.") { Parameters = new ConnectorConfigSchema(Message(template: true)) },
            new ConnectorOperation(TemplatesOperation, "Get templates", "The account's templates, their numbers and names.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) } }),
            },
            new ConnectorOperation(MessageOperation, "Get a sent message", "Its state - sent, opened, bounced - by its id.") { Parameters = new ConnectorConfigSchema(new[] { Field("id", "Message", "The message's id, as a send gave it.", required: true) }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var senders = (await session.SendAsync(HttpMethod.Get, "/v3/REST/sender?Limit=10", null, cancellationToken))?["Data"] as JsonArray ?? new JsonArray();
                var first = senders.OfType<JsonObject>().Select(sender => Text(sender["Email"])).FirstOrDefault(email => email is not null);
                return ConnectionTestResult.Ok(first is null ? "Mailjet: signed in - no sender yet." : $"Mailjet: signed in - {first}{(senders.Count > 1 ? $" and {senders.Count - 1} more sender{(senders.Count == 2 ? "" : "s")}" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SendEmailOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Mailjet has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            using var client = session.Client;

            if (operation == TemplatesOperation)
            {
                var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;

                // Mailjet pages by offset: the size fixed at the start, its total or a short page the end.
                var size = Math.Min(PageSize, max);
                var read = 0;
                for (var offset = 0; ; offset += size)
                {
                    var answer = await session.SendAsync(HttpMethod.Get, $"/v3/REST/template?Limit={size}&Offset={offset}", null, cancellationToken);
                    var items = (answer?["Data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    var total = answer?["Total"] is JsonValue count && count.TryGetValue<long>(out var all) ? all : (long?)null;
                    if (items.Count < size || (total is not null && offset + items.Count >= total))
                    {
                        yield break;
                    }
                }
            }

            if (operation == MessageOperation)
            {
                var id = Values.Get(p, "id") is { } given ? Number.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a message's id - its number (id).") : throw new InvalidOperationException("Give the message's id (id).");
                var answer = await session.SendAsync(HttpMethod.Get, "/v3/REST/message/" + id, null, cancellationToken);
                yield return Record((answer?["Data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException("Mailjet has no message with that id."));
                yield break;
            }

            // A message, everything held to its shape before it is sent.
            var template = operation == SendTemplateOperation;
            var message = new JsonObject { ["From"] = Address(Values.Get(p, "from") ?? throw new InvalidOperationException("Give the sender (from)."), "from") };
            message["To"] = Addresses(Values.Get(p, "to") ?? throw new InvalidOperationException("Give the recipients (to)."), "to");
            foreach (var (name, field) in new[] { ("cc", "Cc"), ("bcc", "Bcc") })
            {
                if (Values.Get(p, name) is { } list)
                {
                    message[field] = Addresses(list, name);
                }
            }

            if (Values.Get(p, "reply_to") is { } replyTo)
            {
                message["ReplyTo"] = Address(replyTo, "reply_to");
            }

            if (Values.Get(p, "subject") is { } subject)
            {
                message["Subject"] = subject.Contains('\n') ? throw new InvalidOperationException("A subject is one line (subject).") : subject;
            }
            else if (!template)
            {
                throw new InvalidOperationException("Give the subject (subject).");
            }

            if (template)
            {
                message["TemplateID"] = Values.Get(p, "template") is { } number ? long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new InvalidOperationException($"'{number}' is not a template's number (template).") : throw new InvalidOperationException("Give the template's number (template).");
                if (Flag(p, "template_language") is { } language)
                {
                    message["TemplateLanguage"] = language;
                }
            }
            else
            {
                var text = Values.Get(p, "text");
                var html = Values.Get(p, "html");
                if (text is null && html is null)
                {
                    throw new InvalidOperationException("Give a body - text or HTML (text, html).");
                }

                if (text is not null)
                {
                    message["TextPart"] = text;
                }

                if (html is not null)
                {
                    message["HTMLPart"] = html;
                }
            }

            if (Values.Get(p, "variables") is { } variables)
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(variables);
                }
                catch (JsonException)
                {
                    parsed = null;
                }

                message["Variables"] = parsed as JsonObject ?? throw new InvalidOperationException("The variables are not a JSON object - {\"order\": \"1001\"} (variables).");
            }

            foreach (var (name, field) in new[] { ("track_opens", "TrackOpens"), ("track_clicks", "TrackClicks") })
            {
                if (Values.Get(p, name) is { } given)
                {
                    message[field] = Tracking.FirstOrDefault(kind => string.Equals(kind, given, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{given}' is not account_default, disabled or enabled ({name}).");
                }
            }

            if (Values.Get(p, "priority") is { } priority)
            {
                message["Priority"] = int.TryParse(priority, NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level <= 3 ? level : throw new InvalidOperationException($"'{priority}' is not 0 to 3 (priority).");
            }

            if (Values.Get(p, "campaign") is { } campaign)
            {
                message["CustomCampaign"] = Campaign.IsMatch(campaign) ? campaign : throw new InvalidOperationException($"'{campaign}' is not a campaign's name - 255 characters at most, one line (campaign).");
            }

            if (Flag(p, "deduplicate") is { } deduplicate)
            {
                message["DeduplicateCampaign"] = deduplicate;
            }

            var sent = await session.SendAsync(HttpMethod.Post, "/v3.1/send", new JsonObject { ["Messages"] = new JsonArray(message) }, cancellationToken);
            var result = (sent?["Messages"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException("Mailjet answered with no message.");
            if (Text(result["Status"]) != "success")
            {
                // Mailjet answers each message on its own: one it refused is a failure, whatever the HTTP status said.
                throw new InvalidOperationException("Mailjet refused the message: " + Errors(result));
            }

            var to = (result["To"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "success",
                ["message_id"] = to.Select(recipient => Value(recipient["MessageID"])).FirstOrDefault(),
                ["message_uuid"] = to.Select(recipient => Text(recipient["MessageUUID"])).FirstOrDefault(),
                ["to"] = string.Join(", ", to.Select(recipient => Text(recipient["Email"])).OfType<string>()),
            });
        }

        // An address as Mailjet takes one: "Name <email>" or the e-mail alone.
        private static JsonObject Address(string given, string name)
        {
            var match = Named.Match(given.Trim());
            var email = match.Success ? match.Groups["email"].Value : given.Trim();
            if (!Email.IsMatch(email))
            {
                throw new InvalidOperationException($"'{given}' is not an address - Kafé <kafe@example.com> or kafe@example.com ({name}).");
            }

            var address = new JsonObject { ["Email"] = email };
            if (match.Success && match.Groups["name"].Value.Trim() is { Length: > 0 } person)
            {
                address["Name"] = person;
            }

            return address;
        }

        private static JsonArray Addresses(string given, string name) =>
            new(given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(part => (JsonNode)Address(part, name)).ToArray() is { Length: > 0 } all
                ? all
                : throw new InvalidOperationException($"Give at least one address ({name})."));

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

        private static string Errors(JsonObject message)
        {
            var errors = (message["Errors"] as JsonArray)?.OfType<JsonObject>().Select(error => Text(error["ErrorMessage"]) is { } said ? said + (error["ErrorRelatedTo"] is JsonArray related && related.Count > 0 ? " (" + string.Join(", ", related.Select(field => Text(field)).OfType<string>()) + ")" : "") : null).OfType<string>().ToList() ?? new List<string>();
            var said = errors.Count == 0 ? "it gave no reason." : string.Join("; ", errors);
            return said.Length > 300 ? said[..300] : said;
        }

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Mailjet's API with the API key and secret key as HTTP Basic; a throttled call waiting as Mailjet
        // says; a failure thrown with its status and Mailjet's error messages.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string basic;

            private Session(string baseUrl, string basic, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.basic = basic;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var key = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Mailjet connection has no API key.");
                var secret = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Mailjet connection has no secret key.");
                var baseUrl = Values.BaseUrl(endpoint, DefaultEndpoint);
                foreach (var version in new[] { "/v3.1", "/v3", "/v4" })
                {
                    baseUrl = baseUrl.EndsWith(version, StringComparison.OrdinalIgnoreCase) ? baseUrl[..^version.Length] : baseUrl;
                }

                return new Session(baseUrl, Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret)), egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Mailjet answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    // A refused send names its errors per message; another call its ErrorMessage.
                    var messages = (json?["Messages"] as JsonArray)?.OfType<JsonObject>().ToList();
                    var said = messages is { Count: > 0 } ? Errors(messages[0]) : Text(json?["ErrorMessage"]) ?? "";
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said.Length == 0 ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
