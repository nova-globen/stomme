#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

namespace Stomme.Modules.Connectors.Mautic
{
    /// <summary>
    /// Mautic (BL-229, n8n's Mautic node: contacts, companies, segments, campaigns and emails): a Mautic instance's REST API
    /// with a user's name and password (Basic), over the host's egress. A test names whose the credentials are.
    /// </summary>
    /// <remarks>
    /// Contacts read (searched, ordered, page by page by Mautic's total up to <c>max</c>), got, created, updated, deleted, their
    /// points added or taken, their do-not-contact on a channel set and lifted, an email sent to one; companies read, got,
    /// created, updated, deleted, a contact added and removed; segments, campaigns and emails read, a contact added to and
    /// removed from a segment or a campaign, a segment email sent. A contact or a company is its fields as Mautic gives them
    /// (<c>fields.all</c>) or, raw, the whole of it - a list or an object as its JSON. Every id, e-mail, field, tag, time,
    /// address, order and channel held to its shape before anything is sent; an error Mautic answers with HTTP 200 a failure
    /// too. A throttled call waits as Mautic says; a failure is thrown with its status and Mautic's word.
    /// </remarks>
    public sealed class MauticConnector : IConnector, IConnectorExtractor
    {
        public const string ContactsOperation = "contacts";

        public const string ContactOperation = "contact";

        public const string CreateContactOperation = "create_contact";

        public const string UpdateContactOperation = "update_contact";

        public const string DeleteContactOperation = "delete_contact";

        public const string PointsOperation = "points";

        public const string AddDoNotContactOperation = "add_do_not_contact";

        public const string RemoveDoNotContactOperation = "remove_do_not_contact";

        public const string SendEmailOperation = "send_email";

        public const string CompaniesOperation = "companies";

        public const string CompanyOperation = "company";

        public const string CreateCompanyOperation = "create_company";

        public const string UpdateCompanyOperation = "update_company";

        public const string DeleteCompanyOperation = "delete_company";

        public const string AddToCompanyOperation = "add_to_company";

        public const string RemoveFromCompanyOperation = "remove_from_company";

        public const string SegmentsOperation = "segments";

        public const string AddToSegmentOperation = "add_to_segment";

        public const string RemoveFromSegmentOperation = "remove_from_segment";

        public const string CampaignsOperation = "campaigns";

        public const string AddToCampaignOperation = "add_to_campaign";

        public const string RemoveFromCampaignOperation = "remove_from_campaign";

        public const string EmailsOperation = "emails";

        public const string SendSegmentEmailOperation = "send_segment_email";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Column = new("^[A-Za-z][A-Za-z0-9_.]{0,63}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            ContactsOperation, ContactOperation, CreateContactOperation, UpdateContactOperation, DeleteContactOperation, PointsOperation, AddDoNotContactOperation, RemoveDoNotContactOperation, SendEmailOperation,
            CompaniesOperation, CompanyOperation, CreateCompanyOperation, UpdateCompanyOperation, DeleteCompanyOperation, AddToCompanyOperation, RemoveFromCompanyOperation,
            SegmentsOperation, AddToSegmentOperation, RemoveFromSegmentOperation, CampaignsOperation, AddToCampaignOperation, RemoveFromCampaignOperation, EmailsOperation, SendSegmentEmailOperation,
        };

        // A do-not-contact's reason as Mautic numbers it.
        private static readonly Dictionary<string, int> Reasons = new(StringComparer.Ordinal) { ["unsubscribed"] = 1, ["bounced"] = 2, ["manual"] = 3 };

        // A contact's own fields as parameters, by the alias Mautic gives each.
        private static readonly (string Name, string Alias)[] ContactFields =
        {
            ("email", "email"), ("firstname", "firstname"), ("lastname", "lastname"), ("company", "company"), ("position", "position"), ("title", "title"), ("phone", "phone"), ("mobile", "mobile"),
        };

        public string ConnectorType => "mautic";

        public string DisplayName => "Mautic";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://mautic.example.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.mautic.org/");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.mautic.org/#rest-api");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField ContactField = Field("contact", "Contact", "The contact's id.", required: true);

        private static readonly ConnectorConfigField CompanyField = Field("company", "Company", "The company's id.", required: true);

        private static readonly ConnectorConfigField RawField = Field("raw", "Raw", "true: the whole of it as Mautic gives it; otherwise its fields.", ConnectorConfigFieldType.Boolean);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField SearchField = Field("search", "Search", "Mautic's search - words, or email:ada@example.com, tag:fika, is:mine.");

        private static readonly ConnectorConfigField FieldsField = Field("fields", "Other fields", "A JSON object of other fields by their aliases - {\"city\": \"Malmö\", \"interests\": [\"fika\"]}.", ConnectorConfigFieldType.Multiline);

        private static readonly ConnectorConfigField OverwriteField = Field("overwrite_with_blank", "Overwrite with blank", "true: a field given empty is emptied.", ConnectorConfigFieldType.Boolean);

        private static ConnectorConfigField[] Listing(bool contacts) => new[]
        {
            SearchField,
            Field("order_by", "Order by", "A column - email, last_active, id."),
            Field("order_dir", "Order", "asc or desc."),
        }.Concat(contacts ? new[] { Field("published_only", "Published only", "true: only published ones.", ConnectorConfigFieldType.Boolean), RawField } : Array.Empty<ConnectorConfigField>()).Append(MaxField).ToArray();

        private static ConnectorConfigField[] ContactInputs(params ConnectorConfigField[] first) => first.Concat(new[]
        {
            Field("email", "E-mail", "Their e-mail."),
            Field("firstname", "First name", "Their first name."),
            Field("lastname", "Last name", "Their last name."),
            Field("company", "Company", "Their company's name."),
            Field("position", "Position", "Their position."),
            Field("title", "Title", "Their title - Mr, Ms, Dr."),
            Field("phone", "Phone", "Their phone."),
            Field("mobile", "Mobile", "Their mobile."),
            Field("tags", "Tags", "Comma-separated tags - one starting with - is taken off."),
            Field("owner", "Owner", "The owning user's id."),
            Field("stage", "Stage", "The stage's id."),
            Field("ip_address", "IP address", "An address they came from."),
            Field("last_active", "Last active", "A time - 2026-10-13 10:00:00, UTC when it names no offset."),
            FieldsField,
            OverwriteField,
            RawField,
        }).ToArray();

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static ConnectorOperation Membership(string name, string label, string what, bool add) =>
            new(name, label, $"A contact {(add ? "added to" : "removed from")} a {what} - n8n's Mautic {char.ToUpperInvariant(what[0]) + what[1..]} Contact {(add ? "Add" : "Remove")}.") { Parameters = Schema(ContactField, Field(what, char.ToUpperInvariant(what[0]) + what[1..], $"The {what}'s id.", required: true)) };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ContactsOperation, "Get contacts", "Contacts, searched and ordered - n8n's Mautic Contact Get Many.") { Parameters = Schema(Listing(contacts: true)) },
            new ConnectorOperation(ContactOperation, "Get a contact", "n8n's Mautic Contact Get.") { Parameters = Schema(ContactField, RawField) },
            new ConnectorOperation(CreateContactOperation, "Create a contact", "n8n's Mautic Contact Create.") { Parameters = Schema(ContactInputs()) },
            new ConnectorOperation(UpdateContactOperation, "Update a contact", "Changes what is given - n8n's Mautic Contact Update.") { Parameters = Schema(ContactInputs(ContactField)) },
            new ConnectorOperation(DeleteContactOperation, "Delete a contact", "n8n's Mautic Contact Delete.") { Parameters = Schema(ContactField, RawField) },
            new ConnectorOperation(PointsOperation, "Change a contact's points", "Points added, or taken when below 0 - n8n's Mautic Contact Edit Contact Points.")
            {
                Parameters = Schema(ContactField, new ConnectorConfigField("points", "Points", ConnectorConfigFieldType.Integer, Required: true, Description: "How many - 5 adds five, -5 takes five.")),
            },
            new ConnectorOperation(AddDoNotContactOperation, "Do not contact", "A contact not to be contacted on a channel - n8n's Mautic Contact Edit Do Not Contact List, Add.")
            {
                Parameters = Schema(ContactField, Field("channel", "Channel", "email (the default) or sms."), Field("reason", "Reason", "unsubscribed, bounced or manual (the default)."), Field("comments", "Comments", "Why.")),
            },
            new ConnectorOperation(RemoveDoNotContactOperation, "Contact again", "A contact's do-not-contact on a channel lifted - n8n's Mautic Contact Edit Do Not Contact List, Remove.")
            {
                Parameters = Schema(ContactField, Field("channel", "Channel", "email (the default) or sms.")),
            },
            new ConnectorOperation(SendEmailOperation, "Send an email", "A template email sent to a contact - n8n's Mautic Contact Send Email.") { Parameters = Schema(ContactField, Field("email_id", "Email", "The email's id.", required: true)) },
            new ConnectorOperation(CompaniesOperation, "Get companies", "Companies, searched and ordered - n8n's Mautic Company Get Many.") { Parameters = Schema(Listing(contacts: false).SkipLast(1).Append(RawField).Append(MaxField).ToArray()) },
            new ConnectorOperation(CompanyOperation, "Get a company", "n8n's Mautic Company Get.") { Parameters = Schema(CompanyField, RawField) },
            new ConnectorOperation(CreateCompanyOperation, "Create a company", "n8n's Mautic Company Create.") { Parameters = Schema(Field("name", "Name", "Its name.", required: true), Field("fields", "Other fields", "A JSON object of other fields by their aliases - {\"companyemail\": \"info@kafe.example\", \"companycity\": \"Malmö\"}.", ConnectorConfigFieldType.Multiline), OverwriteField, RawField) },
            new ConnectorOperation(UpdateCompanyOperation, "Update a company", "Changes what is given - n8n's Mautic Company Update.") { Parameters = Schema(CompanyField, Field("name", "Name", "Its new name."), Field("fields", "Other fields", "A JSON object of other fields by their aliases.", ConnectorConfigFieldType.Multiline), OverwriteField, RawField) },
            new ConnectorOperation(DeleteCompanyOperation, "Delete a company", "n8n's Mautic Company Delete.") { Parameters = Schema(CompanyField, RawField) },
            Membership(AddToCompanyOperation, "Add a contact to a company", "company", add: true),
            Membership(RemoveFromCompanyOperation, "Remove a contact from a company", "company", add: false),
            new ConnectorOperation(SegmentsOperation, "Get segments", "The segments.") { Parameters = Schema(SearchField, MaxField) },
            Membership(AddToSegmentOperation, "Add a contact to a segment", "segment", add: true),
            Membership(RemoveFromSegmentOperation, "Remove a contact from a segment", "segment", add: false),
            new ConnectorOperation(CampaignsOperation, "Get campaigns", "The campaigns.") { Parameters = Schema(SearchField, MaxField) },
            Membership(AddToCampaignOperation, "Add a contact to a campaign", "campaign", add: true),
            Membership(RemoveFromCampaignOperation, "Remove a contact from a campaign", "campaign", add: false),
            new ConnectorOperation(EmailsOperation, "Get emails", "The emails - templates and segment emails.") { Parameters = Schema(SearchField, MaxField) },
            new ConnectorOperation(SendSegmentEmailOperation, "Send a segment email", "A segment email sent to its segments - n8n's Mautic Segment Email Send.") { Parameters = Schema(Field("email_id", "Email", "The segment email's id.", required: true)) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var user = await session.SendAsync(HttpMethod.Get, "/users/self", null, cancellationToken) as JsonObject;
                var name = string.Join(" ", new[] { Text(user?["firstName"]), Text(user?["lastName"]) }.Where(part => !string.IsNullOrWhiteSpace(part)));
                return ConnectionTestResult.Ok($"Mautic: the credentials are {(name.Length > 0 ? name : "a user")}'s{(Text(user?["username"]) is { } username ? $" ({username})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? ContactsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Mautic has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? IdOf(string name, string what) => Values.Get(p, name) is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id ({name}).") : throw new InvalidOperationException($"Give the {what}'s id.");
            string? Flag(string name) => Values.Get(p, name) is { } flag ? flag.ToLowerInvariant() is "true" or "false" ? flag.ToLowerInvariant() : throw new InvalidOperationException($"'{flag}' is not true or false ({name}).") : null;
            var raw = Flag("raw") == "true";
            var contact = operation is ContactOperation or UpdateContactOperation or DeleteContactOperation or PointsOperation or AddDoNotContactOperation or RemoveDoNotContactOperation or SendEmailOperation
                or AddToCompanyOperation or RemoveFromCompanyOperation or AddToSegmentOperation or RemoveFromSegmentOperation or AddToCampaignOperation or RemoveFromCampaignOperation ? IdOf("contact", "contact") : null;
            var company = operation is CompanyOperation or UpdateCompanyOperation or DeleteCompanyOperation or AddToCompanyOperation or RemoveFromCompanyOperation ? IdOf("company", "company") : null;
            var segment = operation is AddToSegmentOperation or RemoveFromSegmentOperation ? IdOf("segment", "segment") : null;
            var campaign = operation is AddToCampaignOperation or RemoveFromCampaignOperation ? IdOf("campaign", "campaign") : null;
            var email = operation is SendEmailOperation or SendSegmentEmailOperation ? IdOf("email_id", "email") : null;
            var channel = Values.Get(p, "channel")?.ToLowerInvariant() is { } chosen ? chosen is "email" or "sms" ? chosen : throw new InvalidOperationException($"'{Values.Get(p, "channel")}' is not email or sms (channel).") : "email";

            JsonObject Other(string fields)
            {
                var parsed = Parse(fields) as JsonObject ?? throw new InvalidOperationException("The other fields are not a JSON object - {\"city\": \"Malmö\"}.");
                foreach (var (key, value) in parsed)
                {
                    if (value is JsonObject)
                    {
                        throw new InvalidOperationException($"'{key}' is an object - a field holds a text, a number, a list or null (fields).");
                    }
                }

                return parsed;
            }

            JsonObject? body = null;
            if (operation is CreateContactOperation or UpdateContactOperation)
            {
                body = Values.Get(p, "fields") is { } fields ? Other(fields) : new JsonObject();
                foreach (var (name, alias) in ContactFields)
                {
                    if (Values.Get(p, name) is { } value)
                    {
                        body[alias] = name == "email" && !Email.IsMatch(value) ? throw new InvalidOperationException($"'{value}' is not an e-mail (email).") : value;
                    }
                }

                if (Values.Get(p, "tags") is { } tags)
                {
                    body["tags"] = new JsonArray(tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
                }

                foreach (var name in new[] { "owner", "stage" })
                {
                    if (Values.Get(p, name) is { } id)
                    {
                        body[name] = Id.IsMatch(id) ? long.Parse(id, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{id}' is not an id ({name}).");
                    }
                }

                if (Values.Get(p, "ip_address") is { } address)
                {
                    // An IPv4 address in its four parts - .NET would read 192.0.2 as 192.0.0.2.
                    body["ipAddress"] = IPAddress.TryParse(address, out var ip) && (ip.AddressFamily == AddressFamily.InterNetworkV6 || address.Count(c => c == '.') == 3) ? address : throw new InvalidOperationException($"'{address}' is not an IP address (ip_address).");
                }

                if (Values.Get(p, "last_active") is { } when)
                {
                    body["lastActive"] = DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{when}' is not a time - 2026-10-13 10:00:00 (last_active).");
                }

                if (operation == UpdateContactOperation && body.Count == 0)
                {
                    throw new InvalidOperationException("Give what to change - a field, tags, the owner, the stage, an address or when they were last active.");
                }

                if (Flag("overwrite_with_blank") is { } overwrite)
                {
                    body["overwriteWithBlank"] = overwrite == "true";
                }
            }

            if (operation is CreateCompanyOperation or UpdateCompanyOperation)
            {
                body = Values.Get(p, "fields") is { } fields ? Other(fields) : new JsonObject();
                if (Values.Get(p, "name") is { } name)
                {
                    body["companyname"] = name;
                }
                else if (operation == CreateCompanyOperation)
                {
                    throw new InvalidOperationException("Give the company's name.");
                }

                if (body.Count == 0)
                {
                    throw new InvalidOperationException("Give what to change - its name or other fields.");
                }

                if (Flag("overwrite_with_blank") is { } overwrite)
                {
                    body["overwriteWithBlank"] = overwrite == "true";
                }
            }

            var points = 0;
            if (operation == PointsOperation)
            {
                points = Values.Get(p, "points") is { } given ? int.TryParse(given, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count) && count != 0 ? count : throw new InvalidOperationException($"'{given}' is not a number other than 0 (points).") : throw new InvalidOperationException("Give the points - 5 adds five, -5 takes five.");
            }

            if (operation == AddDoNotContactOperation)
            {
                var reason = Values.Get(p, "reason")?.ToLowerInvariant() is { } why ? Reasons.TryGetValue(why, out var number) ? number : throw new InvalidOperationException($"'{Values.Get(p, "reason")}' is not unsubscribed, bounced or manual (reason).") : 3;
                body = new JsonObject { ["reason"] = reason };
                if (Values.Get(p, "comments") is { } comments)
                {
                    body["comments"] = comments;
                }
            }

            var query = new List<string>();
            if (operation is ContactsOperation or CompaniesOperation or SegmentsOperation or CampaignsOperation or EmailsOperation)
            {
                if (Values.Get(p, "search") is { } search)
                {
                    query.Add("search=" + Uri.EscapeDataString(search));
                }

                if (Values.Get(p, "order_by") is { } column)
                {
                    query.Add("orderBy=" + (Column.IsMatch(column) ? column : throw new InvalidOperationException($"'{column}' is not a column (order_by).")));
                }

                if (Values.Get(p, "order_dir") is { } direction)
                {
                    query.Add("orderByDir=" + (direction.ToLowerInvariant() is "asc" or "desc" ? direction.ToUpperInvariant() : throw new InvalidOperationException($"'{direction}' is not asc or desc (order_dir).")));
                }

                if (Flag("published_only") is { } published)
                {
                    query.Add("publishedOnly=" + published);
                }
            }

            using var client = session.Client;
            ConnectorRecord Thing(JsonNode? answer, string key) => Record(answer?[key] as JsonObject ?? new JsonObject(), raw);
            ConnectorRecord Done(JsonNode? answer, params (string Name, object? Value)[] fields)
            {
                // Mautic says a change succeeded - 1 or true; anything else is not taken for it.
                if (answer?["success"] is not JsonValue success || !(success.TryGetValue<bool>(out var yes) ? yes : success.TryGetValue<long>(out var one) && one == 1))
                {
                    throw new InvalidOperationException("Mautic did not say it succeeded.");
                }

                return new ConnectorRecord(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
            }

            switch (operation)
            {
                case ContactOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Get, $"/contacts/{contact}", null, cancellationToken), "contact");
                    yield break;
                case CreateContactOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Post, "/contacts/new", body, cancellationToken), "contact");
                    yield break;
                case UpdateContactOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Patch, $"/contacts/{contact}/edit", body, cancellationToken), "contact");
                    yield break;
                case DeleteContactOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Delete, $"/contacts/{contact}/delete", null, cancellationToken), "contact");
                    yield break;
                case PointsOperation:
                    yield return Done(await session.SendAsync(HttpMethod.Post, $"/contacts/{contact}/points/{(points > 0 ? "plus" : "minus")}/{Math.Abs(points).ToString(CultureInfo.InvariantCulture)}", null, cancellationToken), ("contact", contact), ("points", (long)points));
                    yield break;
                case AddDoNotContactOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Post, $"/contacts/{contact}/dnc/{channel}/add", body, cancellationToken), "contact");
                    yield break;
                case RemoveDoNotContactOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Post, $"/contacts/{contact}/dnc/{channel}/remove", null, cancellationToken), "contact");
                    yield break;
                case SendEmailOperation:
                    yield return Done(await session.SendAsync(HttpMethod.Post, $"/emails/{email}/contact/{contact}/send", null, cancellationToken), ("contact", contact), ("email_id", email), ("sent", true));
                    yield break;
                case CompanyOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Get, $"/companies/{company}", null, cancellationToken), "company");
                    yield break;
                case CreateCompanyOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Post, "/companies/new", body, cancellationToken), "company");
                    yield break;
                case UpdateCompanyOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Patch, $"/companies/{company}/edit", body, cancellationToken), "company");
                    yield break;
                case DeleteCompanyOperation:
                    yield return Thing(await session.SendAsync(HttpMethod.Delete, $"/companies/{company}/delete", null, cancellationToken), "company");
                    yield break;
                case AddToCompanyOperation or RemoveFromCompanyOperation:
                    {
                        var add = operation == AddToCompanyOperation;
                        yield return Done(await session.SendAsync(HttpMethod.Post, $"/companies/{company}/contact/{contact}/{(add ? "add" : "remove")}", null, cancellationToken), ("contact", contact), ("company", company), (add ? "added" : "removed", true));
                        yield break;
                    }

                case AddToSegmentOperation or RemoveFromSegmentOperation:
                    {
                        var add = operation == AddToSegmentOperation;
                        yield return Done(await session.SendAsync(HttpMethod.Post, $"/segments/{segment}/contact/{contact}/{(add ? "add" : "remove")}", null, cancellationToken), ("contact", contact), ("segment", segment), (add ? "added" : "removed", true));
                        yield break;
                    }

                case AddToCampaignOperation or RemoveFromCampaignOperation:
                    {
                        var add = operation == AddToCampaignOperation;
                        yield return Done(await session.SendAsync(HttpMethod.Post, $"/campaigns/{campaign}/contact/{contact}/{(add ? "add" : "remove")}", null, cancellationToken), ("contact", contact), ("campaign", campaign), (add ? "added" : "removed", true));
                        yield break;
                    }

                case SendSegmentEmailOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Post, $"/emails/{email}/send", null, cancellationToken);
                        yield return Done(answer, ("email_id", email), ("sent", Number(answer?["sentCount"])), ("failed", Number(answer?["failedRecipients"])));
                        yield break;
                    }

                default:
                    {
                        // Mautic pages by an offset and says how many there are: the size fixed at the start, the total or an
                        // empty page the end - never a short page, which a Mautic capping its pages answers too.
                        var (path, key) = operation switch
                        {
                            ContactsOperation => ("/contacts", "contacts"),
                            CompaniesOperation => ("/companies", "companies"),
                            SegmentsOperation => ("/segments", "lists"),
                            CampaignsOperation => ("/campaigns", "campaigns"),
                            _ => ("/emails", "emails"),
                        };
                        var whole = operation is ContactsOperation or CompaniesOperation;
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        var start = 0L;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"start={start.ToString(CultureInfo.InvariantCulture)}").Append($"limit={size}")), null, cancellationToken);

                            // A list Mautic gives keyed by id - or as a list when it is empty.
                            var items = (answer?[key] switch
                            {
                                JsonObject byId => byId.Select(pair => pair.Value),
                                JsonArray list => list.AsEnumerable(),
                                _ => Enumerable.Empty<JsonNode?>(),
                            }).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return whole ? Record(item, raw) : Record(item, raw: true);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            start += items.Count;
                            if (items.Count == 0 || Number(answer?["total"]) is not { } total || start >= total)
                            {
                                yield break;
                            }
                        }
                    }
            }
        }

        private static JsonNode? Parse(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A record: a contact's or a company's fields as Mautic gives them (fields.all), or - raw - the whole of it; a list or
        // an object as its JSON.
        private static ConnectorRecord Record(JsonObject item, bool raw)
        {
            var fields = raw ? item : item["fields"]?["all"] as JsonObject ?? item;
            return new(fields.ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                null => null,
                JsonValue text when text.TryGetValue<string>(out var s) => s,
                JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                JsonValue real when real.TryGetValue<double>(out var d) => (object?)d,
                var other => other.ToJsonString(Wire),
            }, StringComparer.Ordinal));
        }

        // A number as Mautic gives it - a number, or a string of digits.
        private static long? Number(JsonNode? node) => node is JsonValue value ? value.TryGetValue<long>(out var number) ? number : value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // What Mautic said went wrong: each error's message with what it found wrong with each field.
        private static string? Said(JsonObject? answer)
        {
            static string Details(JsonNode? details) => details is JsonObject { Count: > 0 } each
                ? " - " + string.Join("; ", each.Select(pair => pair.Key + ": " + (pair.Value is JsonArray list ? string.Join(" ", list.Select(Text).Where(text => text is not null)) : Text(pair.Value))))
                : "";
            return answer?["errors"] switch
            {
                JsonArray { Count: > 0 } errors => string.Join("; ", errors.OfType<JsonObject>().Select(error => Text(error["message"]) + Details(error["details"]))),
                _ => answer?["error"] is JsonObject error ? Text(error["message"]) : null,
            };
        }

        // One connection's calls: the instance's API with the user's name and password; a throttled call waiting as Mautic
        // says; a failure - an error Mautic answers with HTTP 200 among them - thrown with its status and Mautic's word.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly AuthenticationHeaderValue authorization;

            private Session(string baseUrl, AuthenticationHeaderValue authorization, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.authorization = authorization;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give Mautic's address - https://mautic.example.com.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                baseUrl = baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^4] : baseUrl;
                var user = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Mautic connection has no user name.");
                var password = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Mautic connection has no password.");
                return new Session(baseUrl + "/api", new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password))), egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path) { Headers = { Authorization = authorization } };
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
                    // A page that is not Mautic's - a sign-in page, a proxy's - read as no contacts would hide that it never
                    // was.
                    throw new InvalidOperationException($"Mautic answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                // Mautic sometimes answers an error with HTTP 200: an answer carrying errors is a failure whatever its status.
                var said = Said(json as JsonObject);
                if (!response.IsSuccessStatusCode || (json as JsonObject)?["errors"] is JsonArray { Count: > 0 })
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
