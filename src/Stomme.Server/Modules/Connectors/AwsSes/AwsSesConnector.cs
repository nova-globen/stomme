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

namespace Stomme.Modules.Connectors.AwsSes
{
    /// <summary>
    /// Amazon SES (BL-229, n8n's AWS SES node: e-mail sent, plain or from a template, its templates and its custom verification
    /// e-mails): an account's Simple Email Service over the SES API v2 (<c>https://email.{region}.amazonaws.com</c>), every call
    /// signed with AWS Signature Version 4 by an IAM user's access key and secret, over the host's egress. A test names the
    /// account's sending state and quota.
    /// </summary>
    /// <remarks>
    /// <c>send_email</c> (a sender, up to 50 recipients over to, cc and bcc, reply-to addresses, a subject and a text or HTML body
    /// or both, a configuration set, tags); <c>send_template</c> (a stored template and its data, a JSON object); <c>templates</c>,
    /// <c>get_template</c>, <c>create_template</c>, <c>update_template</c> (what is not given read first and kept) and
    /// <c>delete_template</c>; <c>verification_templates</c>, <c>get_verification_template</c>,
    /// <c>create_verification_template</c>, <c>update_verification_template</c>, <c>delete_verification_template</c> and
    /// <c>send_verification</c>. Every address, name and URL held to its shape before anything is sent. A throttled call waits as
    /// AWS says; a failure is thrown with its status, AWS's error type and its message.
    /// </remarks>
    public sealed class AwsSesConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const string SendEmailOperation = "send_email";

        public const string SendTemplateOperation = "send_template";

        public const string TemplatesOperation = "templates";

        public const string GetTemplateOperation = "get_template";

        public const string CreateTemplateOperation = "create_template";

        public const string UpdateTemplateOperation = "update_template";

        public const string DeleteTemplateOperation = "delete_template";

        public const string VerificationTemplatesOperation = "verification_templates";

        public const string GetVerificationTemplateOperation = "get_verification_template";

        public const string CreateVerificationTemplateOperation = "create_verification_template";

        public const string UpdateVerificationTemplateOperation = "update_verification_template";

        public const string DeleteVerificationTemplateOperation = "delete_verification_template";

        public const string SendVerificationOperation = "send_verification";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private const int MostRecipients = 50;

        private static readonly Regex Region = new("^[a-z]{2}(-gov)?-[a-z]+-[0-9]$", RegexOptions.CultureInvariant);

        private static readonly Regex TemplateName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex TagName = new("^[A-Za-z0-9_-]{1,256}$", RegexOptions.CultureInvariant);

        private static readonly Regex Mailbox = new("^\\s*(?:\"?(?<name>[^\"<]*?)\"?\\s*<(?<email>[^<>\\s]+)>|(?<email>[^<>\\s]+))\\s*$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly char[] Specials = { '(', ')', '<', '>', '[', ']', ':', ';', '@', '\\', ',', '.', '"' };

        private static readonly string[] Known =
        {
            SendEmailOperation, SendTemplateOperation, TemplatesOperation, GetTemplateOperation, CreateTemplateOperation, UpdateTemplateOperation, DeleteTemplateOperation,
            VerificationTemplatesOperation, GetVerificationTemplateOperation, CreateVerificationTemplateOperation, UpdateVerificationTemplateOperation, DeleteVerificationTemplateOperation, SendVerificationOperation,
        };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awsses";

        public string DisplayName => "Amazon SES";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://email.eu-north-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/ses/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/ses/latest/APIReference-V2/Welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region SES sends from - eu-north-1. The address is SES's in it when left empty."),
        });

        private static readonly ConnectorConfigField TemplateField = new("template", "Template", ConnectorConfigFieldType.Text, Required: true, Description: "Its name - letters, digits, - and _.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] Recipients() => new[]
        {
            new ConnectorConfigField("from", "From", ConnectorConfigFieldType.Text, Required: true, Description: "A verified sender - shop@example.com, or Café Shop <shop@example.com>."),
            new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: true, Description: "Comma-separated - up to 50 recipients in all."),
            new ConnectorConfigField("cc", "Cc", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
            new ConnectorConfigField("bcc", "Bcc", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
            new ConnectorConfigField("reply_to", "Reply to", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
            new ConnectorConfigField("configuration_set", "Configuration set", ConnectorConfigFieldType.Text, Required: false, Description: "Its name."),
            new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "A JSON object of names and values - {\"campaign\": \"autumn\"}."),
        };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SendEmailOperation, "Send an e-mail", "An e-mail with a subject and a text or HTML body - n8n's email Send.")
            {
                Parameters = Schema(Recipients().Concat(new[]
                {
                    new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: false, Description: "The plain text body."),
                    new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Multiline, Required: false, Description: "The HTML body."),
                }).ToArray()),
            },
            new ConnectorOperation(SendTemplateOperation, "Send a templated e-mail", "An e-mail from a stored template and its data - n8n's email Send Template.")
            {
                Parameters = Schema(new[] { TemplateField, new ConnectorConfigField("data", "Data", ConnectorConfigFieldType.Multiline, Required: false, Description: "The template's values, a JSON object - {\"name\": \"Ada\"}.") }.Concat(Recipients()).ToArray()),
            },
            new ConnectorOperation(TemplatesOperation, "Get templates", "The account's e-mail templates - n8n's template Get Many.") { Parameters = Schema(Max()) },
            new ConnectorOperation(GetTemplateOperation, "Get a template", "One template - n8n's template Get.") { Parameters = Schema(TemplateField) },
            new ConnectorOperation(CreateTemplateOperation, "Create a template", "Stores a template - n8n's template Create.") { Parameters = Schema(TemplateParts(create: true)) },
            new ConnectorOperation(UpdateTemplateOperation, "Update a template", "Changes a template - what is not given kept; n8n's template Update.") { Parameters = Schema(TemplateParts(create: false)) },
            new ConnectorOperation(DeleteTemplateOperation, "Delete a template", "Deletes a template - n8n's template Delete.") { Parameters = Schema(TemplateField) },
            new ConnectorOperation(VerificationTemplatesOperation, "Get verification templates", "The custom verification e-mail templates - n8n's custom verification email Get Many.") { Parameters = Schema(Max()) },
            new ConnectorOperation(GetVerificationTemplateOperation, "Get a verification template", "One custom verification e-mail template.") { Parameters = Schema(TemplateField) },
            new ConnectorOperation(CreateVerificationTemplateOperation, "Create a verification template", "Stores a custom verification e-mail template.") { Parameters = Schema(VerificationParts(create: true)) },
            new ConnectorOperation(UpdateVerificationTemplateOperation, "Update a verification template", "Changes a custom verification e-mail template - what is not given kept.") { Parameters = Schema(VerificationParts(create: false)) },
            new ConnectorOperation(DeleteVerificationTemplateOperation, "Delete a verification template", "Deletes a custom verification e-mail template.") { Parameters = Schema(TemplateField) },
            new ConnectorOperation(SendVerificationOperation, "Send a verification e-mail", "Asks an address to verify itself with a custom template - n8n's custom verification email Send.")
            {
                Parameters = Schema(
                    TemplateField,
                    new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: true, Description: "ada@example.com."),
                    new ConnectorConfigField("configuration_set", "Configuration set", ConnectorConfigFieldType.Text, Required: false, Description: "Its name.")),
            },
        };

        private static ConnectorConfigField[] TemplateParts(bool create) => new[]
        {
            TemplateField,
            new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: create, Description: "One line - {{name}} for the data's values."),
            new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: false, Description: "The plain text part."),
            new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Multiline, Required: false, Description: "The HTML part."),
        };

        private static ConnectorConfigField[] VerificationParts(bool create) => new[]
        {
            TemplateField,
            new ConnectorConfigField("from", "From", ConnectorConfigFieldType.Text, Required: create, Description: "A verified sender - shop@example.com."),
            new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("content", "Content", ConnectorConfigFieldType.Multiline, Required: create, Description: "The e-mail's HTML."),
            new ConnectorConfigField("success_url", "After verifying", ConnectorConfigFieldType.Text, Required: create, Description: "Where a verified address goes - https://example.com/thanks."),
            new ConnectorConfigField("failure_url", "After failing", ConnectorConfigFieldType.Text, Required: create, Description: "Where a failed one goes - https://example.com/sorry."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                using var client = session.Client;
                var account = await session.SendAsync(HttpMethod.Get, "/v2/email/account", null, cancellationToken) as JsonObject ?? new JsonObject();
                var quota = account["SendQuota"] as JsonObject ?? new JsonObject();
                var sending = account["SendingEnabled"] is JsonValue on && on.TryGetValue<bool>(out var enabled) && enabled;
                var production = account["ProductionAccessEnabled"] is JsonValue live && live.TryGetValue<bool>(out var isLive) && isLive;
                return ConnectionTestResult.Ok($"SES in {session.Region}: sending {(sending ? "on" : "off")}, {(production ? "out of the sandbox" : "in the sandbox")}, {Number(quota["SentLast24Hours"])} of {Number(quota["Max24HourSend"])} sent in the last day.");
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
                ?? throw new InvalidOperationException($"Amazon SES has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed or sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var template = operation is TemplatesOperation or VerificationTemplatesOperation or SendEmailOperation ? null
                : Values.Get(p, "template") is { } named ? TemplateName.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a template's name - letters, digits, - and _, up to 64.")
                : throw new InvalidOperationException("Give the template - its name.");
            JsonObject? body = null;
            Action<JsonObject>? change = null;
            switch (operation)
            {
                case SendEmailOperation:
                case SendTemplateOperation:
                    {
                        body = Envelope(p);
                        if (operation == SendEmailOperation)
                        {
                            var subject = OneLine(Values.Get(p, "subject") ?? throw new InvalidOperationException("Give the e-mail's subject."), "subject");
                            var text = Raw(p, "text");
                            var html = Raw(p, "html");
                            if (text is null && html is null)
                            {
                                throw new InvalidOperationException("Give the e-mail's text or HTML.");
                            }

                            var parts = new JsonObject();
                            if (text is not null)
                            {
                                parts["Text"] = new JsonObject { ["Data"] = text, ["Charset"] = "UTF-8" };
                            }

                            if (html is not null)
                            {
                                parts["Html"] = new JsonObject { ["Data"] = html, ["Charset"] = "UTF-8" };
                            }

                            body["Content"] = new JsonObject { ["Simple"] = new JsonObject { ["Subject"] = new JsonObject { ["Data"] = subject, ["Charset"] = "UTF-8" }, ["Body"] = parts } };
                        }
                        else
                        {
                            var data = Values.Get(p, "data") is { } given ? ObjectOf(given, "data are", "{\"name\": \"Ada\"}") : new JsonObject();
                            body["Content"] = new JsonObject { ["Template"] = new JsonObject { ["TemplateName"] = template, ["TemplateData"] = data.ToJsonString(Wire) } };
                        }

                        break;
                    }

                case CreateTemplateOperation:
                case UpdateTemplateOperation:
                    {
                        var content = new JsonObject();
                        if (Values.Get(p, "subject") is { } subject)
                        {
                            content["Subject"] = OneLine(subject, "subject");
                        }
                        else if (operation == CreateTemplateOperation)
                        {
                            throw new InvalidOperationException("Give the template's subject.");
                        }

                        if (Raw(p, "text") is { } text)
                        {
                            content["Text"] = text;
                        }

                        if (Raw(p, "html") is { } html)
                        {
                            content["Html"] = html;
                        }

                        if (operation == CreateTemplateOperation)
                        {
                            if (content["Text"] is null && content["Html"] is null)
                            {
                                throw new InvalidOperationException("Give the template's text or HTML.");
                            }

                            body = new JsonObject { ["TemplateName"] = template, ["TemplateContent"] = content };
                            break;
                        }

                        if (content.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - a subject, a text or an HTML part.");
                        }

                        change = found =>
                        {
                            var kept = found["TemplateContent"] as JsonObject ?? new JsonObject();
                            var merged = new JsonObject();
                            foreach (var part in new[] { "Subject", "Text", "Html" })
                            {
                                if ((content[part] ?? kept[part]) is { } value)
                                {
                                    merged[part] = value.DeepClone();
                                }
                            }

                            body = new JsonObject { ["TemplateContent"] = merged };
                        };
                        break;
                    }

                case CreateVerificationTemplateOperation:
                case UpdateVerificationTemplateOperation:
                    {
                        var given = new JsonObject();
                        foreach (var (name, key, what) in new[] { ("from", "FromEmailAddress", "sender"), ("subject", "TemplateSubject", "subject"), ("content", "TemplateContent", "content"), ("success_url", "SuccessRedirectionURL", "address after verifying"), ("failure_url", "FailureRedirectionURL", "address after failing") })
                        {
                            var value = name == "content" ? Raw(p, name) : Values.Get(p, name);
                            if (value is null)
                            {
                                if (operation == CreateVerificationTemplateOperation)
                                {
                                    throw new InvalidOperationException($"Give the template's {what}.");
                                }

                                continue;
                            }

                            given[key] = name switch
                            {
                                "from" => Address(value, "sender"),
                                "subject" => OneLine(value, "subject"),
                                "success_url" or "failure_url" => Uri.TryCreate(value, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp) ? value : throw new InvalidOperationException($"'{value}' is not an address ({what}) - https://example.com/thanks."),
                                _ => value,
                            };
                        }

                        if (operation == CreateVerificationTemplateOperation)
                        {
                            given["TemplateName"] = template;
                            body = given;
                            break;
                        }

                        if (given.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - a sender, a subject, the content or an address after verifying or failing.");
                        }

                        change = found =>
                        {
                            body = new JsonObject();
                            foreach (var key in new[] { "FromEmailAddress", "TemplateSubject", "TemplateContent", "SuccessRedirectionURL", "FailureRedirectionURL" })
                            {
                                if ((given[key] ?? found[key]) is { } value)
                                {
                                    body[key] = value.DeepClone();
                                }
                            }
                        };
                        break;
                    }

                case SendVerificationOperation:
                    body = new JsonObject
                    {
                        ["EmailAddress"] = Address(Values.Get(p, "email") ?? throw new InvalidOperationException("Give the e-mail address to verify."), "e-mail address"),
                        ["TemplateName"] = template,
                    };
                    if (Values.Get(p, "configuration_set") is { } set)
                    {
                        body["ConfigurationSetName"] = TagName.IsMatch(set) ? set : throw new InvalidOperationException($"'{set}' is not a configuration set's name.");
                    }

                    break;
            }

            var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            using var client = session.Client;
            if (operation is TemplatesOperation or VerificationTemplatesOperation)
            {
                var (path, root) = operation == TemplatesOperation ? ("/v2/email/templates", "TemplatesMetadata") : ("/v2/email/custom-verification-email-templates", "CustomVerificationEmailTemplates");
                var read = 0;
                string? next = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await session.SendAsync(HttpMethod.Get, $"{path}?PageSize={Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)}{(next is null ? "" : "&NextToken=" + AwsSigV4.Encode(next))}", null, cancellationToken);
                    foreach (var one in (page?[root] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(one);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    next = Text(page?["NextToken"]) is { Length: > 0 } more ? more : null;
                }
                while (next is not null);

                yield break;
            }

            var encoded = template is null ? null : AwsSigV4.Encode(template);
            switch (operation)
            {
                case SendEmailOperation:
                case SendTemplateOperation:
                    {
                        var sent = await session.SendAsync(HttpMethod.Post, "/v2/email/outbound-emails", body, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["message_id"] = Text(sent["MessageId"]) });
                        yield break;
                    }

                case GetTemplateOperation:
                    yield return TemplateRecord(await session.SendAsync(HttpMethod.Get, $"/v2/email/templates/{encoded}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case GetVerificationTemplateOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Get, $"/v2/email/custom-verification-email-templates/{encoded}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case UpdateTemplateOperation:
                    change!(await session.SendAsync(HttpMethod.Get, $"/v2/email/templates/{encoded}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    await session.SendAsync(HttpMethod.Put, $"/v2/email/templates/{encoded}", body, cancellationToken);
                    break;
                case UpdateVerificationTemplateOperation:
                    change!(await session.SendAsync(HttpMethod.Get, $"/v2/email/custom-verification-email-templates/{encoded}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    await session.SendAsync(HttpMethod.Put, $"/v2/email/custom-verification-email-templates/{encoded}", body, cancellationToken);
                    break;
                case CreateTemplateOperation:
                    await session.SendAsync(HttpMethod.Post, "/v2/email/templates", body, cancellationToken);
                    break;
                case CreateVerificationTemplateOperation:
                    await session.SendAsync(HttpMethod.Post, "/v2/email/custom-verification-email-templates", body, cancellationToken);
                    break;
                case DeleteTemplateOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/v2/email/templates/{encoded}", null, cancellationToken);
                    break;
                case DeleteVerificationTemplateOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/v2/email/custom-verification-email-templates/{encoded}", null, cancellationToken);
                    break;
                default:
                    {
                        var sent = await session.SendAsync(HttpMethod.Post, "/v2/email/outbound-custom-verification-emails", body, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["message_id"] = Text(sent["MessageId"]), ["email"] = Text(body!["EmailAddress"]) });
                        yield break;
                    }
            }

            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["template"] = template, ["done"] = true });
        }

        // An e-mail's sender, recipients - 50 at most in all - reply-to addresses, configuration set and tags.
        private static JsonObject Envelope(IReadOnlyDictionary<string, string> p)
        {
            var body = new JsonObject { ["FromEmailAddress"] = Address(Values.Get(p, "from") ?? throw new InvalidOperationException("Give the sender."), "sender") };
            var destination = new JsonObject();
            var count = 0;
            foreach (var (name, key) in new[] { ("to", "ToAddresses"), ("cc", "CcAddresses"), ("bcc", "BccAddresses") })
            {
                if (Values.Get(p, name) is { } list)
                {
                    var addresses = Addresses(list, name);
                    count += addresses.Count;
                    destination[key] = new JsonArray(addresses.Select(address => (JsonNode)address).ToArray());
                }
                else if (name == "to")
                {
                    throw new InvalidOperationException("Give the recipients - to.");
                }
            }

            if (count > MostRecipients)
            {
                throw new InvalidOperationException($"An e-mail goes to {MostRecipients} recipients at most - {count} given.");
            }

            body["Destination"] = destination;
            if (Values.Get(p, "reply_to") is { } replyTo)
            {
                body["ReplyToAddresses"] = new JsonArray(Addresses(replyTo, "reply to").Select(address => (JsonNode)address).ToArray());
            }

            if (Values.Get(p, "configuration_set") is { } set)
            {
                body["ConfigurationSetName"] = TagName.IsMatch(set) ? set : throw new InvalidOperationException($"'{set}' is not a configuration set's name.");
            }

            if (Values.Get(p, "tags") is { } tags)
            {
                var given = ObjectOf(tags, "tags are", "{\"campaign\": \"autumn\"}");
                body["EmailTags"] = new JsonArray(given.Select(tag => (JsonNode)new JsonObject
                {
                    ["Name"] = TagName.IsMatch(tag.Key) ? tag.Key : throw new InvalidOperationException($"'{tag.Key}' is not a tag's name - letters, digits, - and _."),
                    ["Value"] = tag.Value is JsonValue value && value.TryGetValue<string>(out var text) && TagName.IsMatch(text) ? text : throw new InvalidOperationException($"The tag {tag.Key}'s value is not letters, digits, - and _."),
                }).ToArray());
            }

            return body;
        }

        // Addresses split at the commas between them - never at one inside a quoted name.
        private static List<string> Addresses(string given, string what)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            foreach (var c in given)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                }

                if (c == ',' && !quoted)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            parts.Add(current.ToString());
            return parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => Address(part.Trim(), what)).ToList();
        }

        // An address as SES takes it - ada@example.com, or Ada <ada@example.com>, a name with a comma quoted.
        private static string Address(string given, string what)
        {
            var match = Mailbox.Match(given);
            var email = match.Success ? match.Groups["email"].Value : "";
            if (!match.Success || email.IndexOf('@') <= 0 || email.IndexOf('@') != email.LastIndexOf('@') || email.IndexOf('@') == email.Length - 1)
            {
                throw new InvalidOperationException($"'{given}' is not an e-mail address ({what}).");
            }

            var name = match.Groups["name"].Success ? match.Groups["name"].Value.Trim() : "";
            return name.Length == 0 ? email : $"{Friendly(name)} <{email}>";
        }

        // A display name as SES takes it: one with letters beyond ASCII in MIME encoded-word (RFC 2047), one with specials quoted,
        // a backslash in it escaped (a quote never is in it - the address's shape keeps it out).
        private static string Friendly(string name) =>
            name.Any(c => c > 127) ? "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name)) + "?="
                : name.IndexOfAny(Specials) >= 0 ? "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"" : name;

        private static ConnectorRecord TemplateRecord(JsonObject template) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = Text(template["TemplateName"]),
            ["subject"] = Text(template["TemplateContent"]?["Subject"]),
            ["text"] = Text(template["TemplateContent"]?["Text"]),
            ["html"] = Text(template["TemplateContent"]?["Html"]),
        });

        // A listed template or a verification template: SES's values by their own names, in snake case.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item)
            {
                var key = string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 && !char.IsUpper(name[i - 1]) ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
                fields[key switch { "template_name" => "name", _ => key }] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => DateTimeOffset.FromUnixTimeMilliseconds((long)(d * 1000)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        private static string? Raw(IReadOnlyDictionary<string, string> p, string key) => p.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

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

        private static string Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number.ToString("N0", CultureInfo.InvariantCulture) : "?";

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: SES's address in the region, every request signed with the key pair; a throttled call waiting as
        // AWS says; a failure thrown with its status, AWS's error type and its message.
        private sealed class Session
        {
            private readonly string api;
            private readonly string accessKey;
            private readonly string secretKey;
            private readonly TimeProvider time;

            private Session(string api, string region, string accessKey, string secretKey, TimeProvider time, HttpClient client)
            {
                this.api = api;
                Region = region;
                this.accessKey = accessKey;
                this.secretKey = secretKey;
                this.time = time;
                Client = client;
            }

            public string Region { get; }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, TimeProvider time)
            {
                var region = Values.Get(settings, "region") ?? throw new InvalidOperationException("Give the AWS region - eu-north-1.");
                if (!AwsSesConnector.Region.IsMatch(region))
                {
                    throw new InvalidOperationException($"'{region}' is not an AWS region - eu-north-1.");
                }

                var api = Values.BaseUrl(endpoint, $"https://email.{region}.amazonaws.com");
                var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The Amazon SES connection has no access key.");
                var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The Amazon SES connection has no secret key.");
                return new Session(api, region, accessKey, secretKey, time, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToJsonString(Wire));
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    if (body is not null)
                    {
                        request.Content = new ByteArrayContent(payload);
                        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    }

                    AwsSigV4.Sign(request, payload, Region, "ses", accessKey, secretKey, null, time.GetUtcNow());
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
                    throw new InvalidOperationException($"Amazon SES answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var type = response.Headers.TryGetValues("x-amzn-ErrorType", out var types) ? types.First().Split(':')[0] : null;
                    var said = json is JsonObject error ? Text(error["message"]) ?? Text(error["Message"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(type is null ? "" : " " + type)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
