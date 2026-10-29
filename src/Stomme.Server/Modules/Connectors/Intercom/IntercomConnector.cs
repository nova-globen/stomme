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

namespace Stomme.Modules.Connectors.Intercom
{
    /// <summary>
    /// Intercom (n8n's Intercom node: its users, leads and companies): a workspace over Intercom's API 2.x
    /// (<c>https://api.intercom.io</c>, or the EU's and Australia's), an access token as a bearer, Intercom's API version in its
    /// <c>Intercom-Version</c> header. A test names the admin and the workspace.
    /// </summary>
    /// <remarks>
    /// A contact - a user or a lead, n8n's two - is its id, its e-mail address or its external id (the last two looked up); a
    /// company its id, or its own company id or name (looked up); a tag its id or its name (looked up) - each held to its shape
    /// before anything is sent. <c>contacts</c>: the workspace's contacts, or those a role, an address and Intercom's own query
    /// keep (a search), page by page by Intercom's cursor up to <c>max</c> (1,000); <c>get_contact</c>, <c>create_contact</c> (a
    /// user by default, a user needing an address or an external id), <c>update_contact</c>, <c>delete_contact</c> - an address, an
    /// external id, a phone, a name, an owner, whether unsubscribed, when signed up and last seen, custom attributes as a JSON
    /// object. Each contact a record: its fields, its times in UTC, its location's city and country, its tags' and companies'
    /// ids, each custom attribute a field of its own (<c>custom_</c> before one named as a field is). <c>companies</c> page by
    /// page, <c>get_company</c>, <c>upsert_company</c> (made, or changed when its company id is known - n8n's Create and Update),
    /// <c>delete_company</c>, <c>company_contacts</c>; <c>attach_company</c> and <c>detach_company</c>; <c>tags</c>,
    /// <c>tag_contact</c>, <c>untag_contact</c>; <c>add_note</c>. A throttled call waits as Intercom says; a failure is thrown with
    /// its status and Intercom's codes and messages.
    /// </remarks>
    public sealed class IntercomConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.intercom.io";

        public const string ApiVersion = "2.11";

        public const string ContactsOperation = "contacts";

        public const string GetContactOperation = "get_contact";

        public const string CreateContactOperation = "create_contact";

        public const string UpdateContactOperation = "update_contact";

        public const string DeleteContactOperation = "delete_contact";

        public const string CompaniesOperation = "companies";

        public const string GetCompanyOperation = "get_company";

        public const string UpsertCompanyOperation = "upsert_company";

        public const string DeleteCompanyOperation = "delete_company";

        public const string CompanyContactsOperation = "company_contacts";

        public const string AttachCompanyOperation = "attach_company";

        public const string DetachCompanyOperation = "detach_company";

        public const string TagsOperation = "tags";

        public const string TagContactOperation = "tag_contact";

        public const string UntagContactOperation = "untag_contact";

        public const string AddNoteOperation = "add_note";

        public const int DefaultMax = 1000;

        private const int ContactPage = 150;

        private const int CompanyPage = 60;

        private static readonly Regex Id = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Numeric = new("^[0-9]{1,18}$", RegexOptions.CultureInvariant);

        private static readonly string[] Roles = { "user", "lead" };

        private static readonly HashSet<string> StandardFields = new(StringComparer.Ordinal)
        {
            "id", "external_id", "role", "email", "phone", "name", "owner_id", "unsubscribed_from_emails", "has_hard_bounced", "marked_email_as_spam",
            "created_at", "updated_at", "signed_up_at", "last_seen_at", "last_replied_at", "last_contacted_at", "browser", "os", "city", "country", "tag_ids", "company_ids",
        };

        // JSON as written: names, notes and their accents and markup, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            ContactsOperation, GetContactOperation, CreateContactOperation, UpdateContactOperation, DeleteContactOperation,
            CompaniesOperation, GetCompanyOperation, UpsertCompanyOperation, DeleteCompanyOperation, CompanyContactsOperation, AttachCompanyOperation, DetachCompanyOperation,
            TagsOperation, TagContactOperation, UntagContactOperation, AddNoteOperation,
        };

        public string ConnectorType => "intercom";

        public string DisplayName => "Intercom";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.intercom.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.intercom.com/docs/references/rest-api/api.intercom.io");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField ContactField = new("contact", "Contact", ConnectorConfigFieldType.Text, Required: true, Description: "Its id, its e-mail address, or external: and its external id - external:4f2a.");

        private static readonly ConnectorConfigField CompanyField = new("company", "Company", ConnectorConfigFieldType.Text, Required: true, Description: "Its Intercom id, or company: and its own company id - company:acme-ab.");

        private static readonly ConnectorConfigField TagField = new("tag", "Tag", ConnectorConfigFieldType.Text, Required: true, Description: "Its id or its name.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] ContactWrite(bool create)
        {
            var fields = new List<ConnectorConfigField>();
            if (create)
            {
                fields.Add(new("role", "Role", ConnectorConfigFieldType.Text, Required: false, Description: "user or lead.") { Default = "user" });
            }

            fields.AddRange(new[]
            {
                new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: false, Description: "ada@example.com - a user's, or its external id."),
                new ConnectorConfigField("external_id", "External id", ConnectorConfigFieldType.Text, Required: false, Description: "Your own id for it."),
                new ConnectorConfigField("phone", "Phone", ConnectorConfigFieldType.Text, Required: false, Description: "+46 8 465 004 01."),
                new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Ada Lovelace."),
                new ConnectorConfigField("owner_id", "Owner", ConnectorConfigFieldType.Text, Required: false, Description: "An admin's id."),
                new ConnectorConfigField("unsubscribed_from_emails", "Unsubscribed", ConnectorConfigFieldType.Text, Required: false, Description: "true or false."),
                new ConnectorConfigField("signed_up_at", "Signed up", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or 2026-10-01T09:00:00+02:00."),
                new ConnectorConfigField("last_seen_at", "Last seen", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
                new ConnectorConfigField("custom_attributes", "Custom attributes", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object - {\"plan\": \"Pro\", \"seats\": 5}."),
            });
            return fields.ToArray();
        }

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ContactsOperation, "Get contacts", "The workspace's contacts, or those a role, an address and a query keep - n8n's user and lead Get Many.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("role", "Role", ConnectorConfigFieldType.Text, Required: false, Description: "user or lead."),
                    new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: false, Description: "Only the contacts with this address."),
                    new ConnectorConfigField("query", "Query", ConnectorConfigFieldType.Multiline, Required: false, Description: "Intercom's search query, a JSON object - {\"field\": \"custom_attributes.plan\", \"operator\": \"=\", \"value\": \"Pro\"}."),
                    Max()),
            },
            new ConnectorOperation(GetContactOperation, "Get a contact", "One contact - n8n's user and lead Get.") { Parameters = Schema(ContactField) },
            new ConnectorOperation(CreateContactOperation, "Create a contact", "Adds a user or a lead - n8n's user and lead Create.") { Parameters = Schema(ContactWrite(create: true)) },
            new ConnectorOperation(UpdateContactOperation, "Update a contact", "Changes a contact - n8n's user and lead Update.") { Parameters = Schema(new[] { ContactField }.Concat(ContactWrite(create: false)).ToArray()) },
            new ConnectorOperation(DeleteContactOperation, "Delete a contact", "Deletes a contact - n8n's user and lead Delete.") { Parameters = Schema(ContactField) },
            new ConnectorOperation(CompaniesOperation, "Get companies", "The workspace's companies - n8n's company Get Many.") { Parameters = Schema(Max()) },
            new ConnectorOperation(GetCompanyOperation, "Get a company", "One company - n8n's company Get.") { Parameters = Schema(CompanyField with { Description = "Its Intercom id, company: and its own company id, or name: and its name." }) },
            new ConnectorOperation(UpsertCompanyOperation, "Create or update a company", "Adds a company, or changes it when its company id is known - n8n's company Create and Update.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("company_id", "Company id", ConnectorConfigFieldType.Text, Required: true, Description: "Your own id for it - acme-ab."),
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Acme AB."),
                    new ConnectorConfigField("plan", "Plan", ConnectorConfigFieldType.Text, Required: false, Description: "Pro."),
                    new ConnectorConfigField("size", "Size", ConnectorConfigFieldType.Text, Required: false, Description: "How many people - 120."),
                    new ConnectorConfigField("website", "Website", ConnectorConfigFieldType.Text, Required: false, Description: "https://acme.example."),
                    new ConnectorConfigField("industry", "Industry", ConnectorConfigFieldType.Text, Required: false, Description: "Retail."),
                    new ConnectorConfigField("monthly_spend", "Monthly spend", ConnectorConfigFieldType.Text, Required: false, Description: "What it pays a month - 499."),
                    new ConnectorConfigField("custom_attributes", "Custom attributes", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object - {\"region\": \"EU\"}.")),
            },
            new ConnectorOperation(DeleteCompanyOperation, "Delete a company", "Deletes a company.") { Parameters = Schema(CompanyField) },
            new ConnectorOperation(CompanyContactsOperation, "Get a company's contacts", "The contacts a company has - n8n's company Users.") { Parameters = Schema(CompanyField, Max()) },
            new ConnectorOperation(AttachCompanyOperation, "Add a contact to a company", "Attaches a contact to a company.") { Parameters = Schema(ContactField, CompanyField) },
            new ConnectorOperation(DetachCompanyOperation, "Remove a contact from a company", "Detaches a contact from a company.") { Parameters = Schema(ContactField, CompanyField) },
            new ConnectorOperation(TagsOperation, "Get tags", "The workspace's tags.") { Parameters = Schema(Max()) },
            new ConnectorOperation(TagContactOperation, "Tag a contact", "Puts a tag on a contact.") { Parameters = Schema(ContactField, TagField) },
            new ConnectorOperation(UntagContactOperation, "Untag a contact", "Takes a tag off a contact.") { Parameters = Schema(ContactField, TagField) },
            new ConnectorOperation(AddNoteOperation, "Add a note", "Notes something on a contact.")
            {
                Parameters = Schema(
                    ContactField,
                    new ConnectorConfigField("body", "Note", ConnectorConfigFieldType.Multiline, Required: true, Description: "What to note - HTML is kept."),
                    new ConnectorConfigField("admin_id", "Admin", ConnectorConfigFieldType.Text, Required: false, Description: "The admin it is from.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await new Session(client, api, Token(context.Credentials)).SendAsync(HttpMethod.Get, "/me", null, cancellationToken) as JsonObject ?? new JsonObject();
                var workspace = Text(me["app"]?["name"]);
                return ConnectionTestResult.Ok($"Signed in as {Text(me["name"]) ?? Text(me["email"]) ?? "the token's admin"}{(workspace is null ? "" : $" - {workspace}")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? ContactsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Intercom has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var contact = operation is GetContactOperation or UpdateContactOperation or DeleteContactOperation or AttachCompanyOperation or DetachCompanyOperation or TagContactOperation or UntagContactOperation or AddNoteOperation
                ? ContactRef(Values.Get(p, "contact"))
                : (Ref?)null;
            var company = operation is GetCompanyOperation or DeleteCompanyOperation or CompanyContactsOperation or AttachCompanyOperation or DetachCompanyOperation
                ? CompanyRef(Values.Get(p, "company"), allowName: operation == GetCompanyOperation)
                : (Ref?)null;
            var tag = operation is TagContactOperation or UntagContactOperation
                ? Values.Get(p, "tag") is { } named ? Numeric.IsMatch(named) ? new Ref("id", named) : new Ref("name", OneLine(named, "tag's name")) : throw new InvalidOperationException("Give the tag - its id or its name.")
                : (Ref?)null;
            JsonObject? body = null;
            JsonObject? search = null;
            switch (operation)
            {
                case ContactsOperation:
                    {
                        var conditions = new JsonArray();
                        if (Values.Get(p, "role") is { } role)
                        {
                            conditions.Add(Condition("role", Choice(role, "a role", Roles)));
                        }

                        if (Values.Get(p, "email") is { } email)
                        {
                            conditions.Add(Condition("email", IsAddress(email) ? email : throw new InvalidOperationException($"'{email}' is not an e-mail address.")));
                        }

                        if (Values.Get(p, "query") is { } query)
                        {
                            conditions.Add(ObjectOf(query, "query is", "{\"field\": \"custom_attributes.plan\", \"operator\": \"=\", \"value\": \"Pro\"}"));
                        }

                        search = conditions.Count switch
                        {
                            0 => null,
                            1 => conditions[0]!.DeepClone().AsObject(),
                            _ => new JsonObject { ["operator"] = "AND", ["value"] = conditions },
                        };
                        break;
                    }

                case CreateContactOperation:
                case UpdateContactOperation:
                    {
                        body = new JsonObject();
                        if (operation == CreateContactOperation)
                        {
                            body["role"] = Choice(Values.Get(p, "role") ?? "user", "a role", Roles);
                        }

                        if (Values.Get(p, "email") is { } email)
                        {
                            body["email"] = IsAddress(email) ? email : throw new InvalidOperationException($"'{email}' is not an e-mail address.");
                        }

                        foreach (var (name, what) in new[] { ("external_id", "external id"), ("phone", "phone"), ("name", "name") })
                        {
                            if (Values.Get(p, name) is { } given)
                            {
                                body[name] = OneLine(given, what);
                            }
                        }

                        if (Values.Get(p, "owner_id") is { } owner)
                        {
                            body["owner_id"] = Numeric.IsMatch(owner) ? long.Parse(owner, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{owner}' is not an admin's id.");
                        }

                        if (Values.Get(p, "unsubscribed_from_emails") is { } unsubscribed)
                        {
                            body["unsubscribed_from_emails"] = unsubscribed.ToLowerInvariant() switch { "true" => true, "false" => false, _ => throw new InvalidOperationException($"'{unsubscribed}' is not true or false (unsubscribed).") };
                        }

                        foreach (var (name, what) in new[] { ("signed_up_at", "signed up"), ("last_seen_at", "last seen") })
                        {
                            if (Values.Get(p, name) is { } given)
                            {
                                body[name] = Unix(given, what);
                            }
                        }

                        if (Values.Get(p, "custom_attributes") is { } custom)
                        {
                            body["custom_attributes"] = ObjectOf(custom, "custom attributes are", "{\"plan\": \"Pro\"}");
                        }

                        if (operation == CreateContactOperation && body["role"]!.GetValue<string>() == "user" && !body.ContainsKey("email") && !body.ContainsKey("external_id"))
                        {
                            throw new InvalidOperationException("Give a user's e-mail address or external id.");
                        }

                        if (operation == UpdateContactOperation && body.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - an address, an external id, a phone, a name, an owner, whether unsubscribed, when signed up or last seen, or custom attributes.");
                        }

                        break;
                    }

                case UpsertCompanyOperation:
                    {
                        body = new JsonObject { ["company_id"] = OneLine(Values.Get(p, "company_id") ?? throw new InvalidOperationException("Give the company's own id - its company id."), "company id") };
                        foreach (var (name, what) in new[] { ("name", "name"), ("plan", "plan"), ("industry", "industry") })
                        {
                            if (Values.Get(p, name) is { } given)
                            {
                                body[name] = OneLine(given, what);
                            }
                        }

                        if (Values.Get(p, "website") is { } website)
                        {
                            body["website"] = Stomme.Shared.Utilities.TryCreateAbsoluteUri(website, out var site) && (site.Scheme == Uri.UriSchemeHttp || site.Scheme == Uri.UriSchemeHttps) ? website : throw new InvalidOperationException($"'{website}' is not a website's address - https://acme.example.");
                        }

                        if (Values.Get(p, "size") is { } size)
                        {
                            body["size"] = Numeric.IsMatch(size) ? long.Parse(size, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{size}' is not a number of people (size).");
                        }

                        if (Values.Get(p, "monthly_spend") is { } spend)
                        {
                            body["monthly_spend"] = double.TryParse(spend, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ? amount : throw new InvalidOperationException($"'{spend}' is not an amount (monthly spend) - 499.50.");
                        }

                        if (Values.Get(p, "custom_attributes") is { } custom)
                        {
                            body["custom_attributes"] = ObjectOf(custom, "custom attributes are", "{\"region\": \"EU\"}");
                        }

                        break;
                    }

                case AddNoteOperation:
                    body = new JsonObject { ["body"] = Values.Get(p, "body") ?? throw new InvalidOperationException("Give the note.") };
                    if (Values.Get(p, "admin_id") is { } admin)
                    {
                        body["admin_id"] = Numeric.IsMatch(admin) ? admin : throw new InvalidOperationException($"'{admin}' is not an admin's id.");
                    }

                    break;
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            var session = new Session(client, api, token);
            switch (operation)
            {
                case ContactsOperation:
                    {
                        var read = 0;
                        string? cursor = null;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var size = Math.Min(ContactPage, max - read);
                            JsonNode? page;
                            if (search is null)
                            {
                                page = await session.SendAsync(HttpMethod.Get, $"/contacts?per_page={size.ToString(CultureInfo.InvariantCulture)}{(cursor is null ? "" : "&starting_after=" + Uri.EscapeDataString(cursor))}", null, cancellationToken);
                            }
                            else
                            {
                                var pagination = new JsonObject { ["per_page"] = size };
                                if (cursor is not null)
                                {
                                    pagination["starting_after"] = cursor;
                                }

                                page = await session.SendAsync(HttpMethod.Post, "/contacts/search", new JsonObject { ["query"] = search.DeepClone(), ["pagination"] = pagination }, cancellationToken);
                            }

                            foreach (var item in (page?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return ContactRecord(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            cursor = Text(page?["pages"]?["next"]?["starting_after"]);
                            if (cursor is null)
                            {
                                yield break;
                            }
                        }
                    }

                case CompaniesOperation:
                case CompanyContactsOperation:
                    {
                        var path = operation == CompaniesOperation ? "/companies" : $"/companies/{await session.CompanyIdAsync(company!.Value, cancellationToken)}/contacts";
                        var size = Math.Min(CompanyPage, max);
                        var read = 0;
                        for (var number = 1; ; number++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var page = await session.SendAsync(HttpMethod.Get, $"{path}?page={number.ToString(CultureInfo.InvariantCulture)}&per_page={size.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
                            var items = page?["data"] as JsonArray ?? new JsonArray();
                            foreach (var item in items.OfType<JsonObject>())
                            {
                                yield return operation == CompaniesOperation ? CompanyRecord(item) : ContactRecord(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count == 0 || !(Long(page?["pages"]?["total_pages"]) is { } total && number < total))
                            {
                                yield break;
                            }
                        }
                    }

                case TagsOperation:
                    {
                        var read = 0;
                        foreach (var item in ((await session.SendAsync(HttpMethod.Get, "/tags", null, cancellationToken))?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(item["id"]), ["name"] = Text(item["name"]) });
                            if (++read >= max)
                            {
                                yield break;
                            }
                        }

                        yield break;
                    }

                case GetContactOperation:
                    yield return ContactRecord(await session.SendAsync(HttpMethod.Get, "/contacts/" + await session.ContactIdAsync(contact!.Value, cancellationToken), null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case CreateContactOperation:
                    yield return ContactRecord(await session.SendAsync(HttpMethod.Post, "/contacts", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case UpdateContactOperation:
                    yield return ContactRecord(await session.SendAsync(HttpMethod.Put, "/contacts/" + await session.ContactIdAsync(contact!.Value, cancellationToken), body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case DeleteContactOperation:
                    {
                        var gone = await session.SendAsync(HttpMethod.Delete, "/contacts/" + await session.ContactIdAsync(contact!.Value, cancellationToken), null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(gone?["id"]), ["deleted"] = gone?["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var yes) && yes });
                        yield break;
                    }

                case GetCompanyOperation:
                    yield return CompanyRecord(await session.CompanyAsync(company!.Value, cancellationToken));
                    yield break;
                case UpsertCompanyOperation:
                    yield return CompanyRecord(await session.SendAsync(HttpMethod.Post, "/companies", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case DeleteCompanyOperation:
                    {
                        var gone = await session.SendAsync(HttpMethod.Delete, "/companies/" + await session.CompanyIdAsync(company!.Value, cancellationToken), null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(gone?["id"]), ["deleted"] = gone?["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var yes) && yes });
                        yield break;
                    }

                case AttachCompanyOperation:
                case DetachCompanyOperation:
                    {
                        var who = await session.ContactIdAsync(contact!.Value, cancellationToken);
                        var which = await session.CompanyIdAsync(company!.Value, cancellationToken);
                        var linked = operation == AttachCompanyOperation
                            ? await session.SendAsync(HttpMethod.Post, $"/contacts/{who}/companies", new JsonObject { ["id"] = which }, cancellationToken)
                            : await session.SendAsync(HttpMethod.Delete, $"/contacts/{who}/companies/{which}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["contact"] = who, ["company"] = Text(linked?["id"]) ?? which, ["attached"] = operation == AttachCompanyOperation });
                        yield break;
                    }

                case TagContactOperation:
                case UntagContactOperation:
                    {
                        var who = await session.ContactIdAsync(contact!.Value, cancellationToken);
                        var which = await session.TagIdAsync(tag!.Value, cancellationToken);
                        var tagged = operation == TagContactOperation
                            ? await session.SendAsync(HttpMethod.Post, $"/contacts/{who}/tags", new JsonObject { ["id"] = which }, cancellationToken)
                            : await session.SendAsync(HttpMethod.Delete, $"/contacts/{who}/tags/{which}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["contact"] = who, ["tag"] = Text(tagged?["name"]) ?? which, ["tagged"] = operation == TagContactOperation });
                        yield break;
                    }

                default:
                    {
                        var note = await session.SendAsync(HttpMethod.Post, $"/contacts/{await session.ContactIdAsync(contact!.Value, cancellationToken)}/notes", body, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(note?["id"]), ["created"] = Time(note?["created_at"]), ["body"] = Text(note?["body"]) });
                        yield break;
                    }
            }
        }

        // A contact: its fields, its times in UTC, its location's city and country, its tags' and companies' ids, each custom
        // attribute a field of its own - custom_ before one named as one of its fields is.
        private static ConnectorRecord ContactRecord(JsonObject contact)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(contact["id"]),
                ["external_id"] = Text(contact["external_id"]),
                ["role"] = Text(contact["role"]),
                ["email"] = Text(contact["email"]),
                ["phone"] = Text(contact["phone"]),
                ["name"] = Text(contact["name"]),
                ["owner_id"] = Long(contact["owner_id"]),
                ["unsubscribed_from_emails"] = Flag(contact["unsubscribed_from_emails"]),
                ["has_hard_bounced"] = Flag(contact["has_hard_bounced"]),
                ["created_at"] = Time(contact["created_at"]),
                ["updated_at"] = Time(contact["updated_at"]),
                ["signed_up_at"] = Time(contact["signed_up_at"]),
                ["last_seen_at"] = Time(contact["last_seen_at"]),
                ["city"] = Text(contact["location"]?["city"]),
                ["country"] = Text(contact["location"]?["country"]),
                ["tag_ids"] = string.Join(", ", (contact["tags"]?["data"] as JsonArray ?? new JsonArray()).Select(item => Text(item?["id"]))),
                ["company_ids"] = string.Join(", ", (contact["companies"]?["data"] as JsonArray ?? new JsonArray()).Select(item => Text(item?["id"]))),
            };
            foreach (var (name, value) in contact["custom_attributes"] as JsonObject ?? new JsonObject())
            {
                fields[StandardFields.Contains(name) ? "custom_" + name : name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        private static ConnectorRecord CompanyRecord(JsonObject company)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(company["id"]),
                ["company_id"] = Text(company["company_id"]),
                ["name"] = Text(company["name"]),
                ["plan"] = Text(company["plan"]?["name"]) ?? Text(company["plan"]),
                ["size"] = Long(company["size"]),
                ["website"] = Text(company["website"]),
                ["industry"] = Text(company["industry"]),
                ["monthly_spend"] = company["monthly_spend"] is JsonValue spend && spend.TryGetValue<double>(out var amount) ? amount : null,
                ["user_count"] = Long(company["user_count"]),
                ["created_at"] = Time(company["created_at"]),
                ["updated_at"] = Time(company["updated_at"]),
            };
            foreach (var (name, value) in company["custom_attributes"] as JsonObject ?? new JsonObject())
            {
                fields[fields.ContainsKey(name) ? "custom_" + name : name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        // A contact, a company or a tag as given: an id, or what finds it.
        private readonly record struct Ref(string Kind, string Value);

        private static Ref ContactRef(string? given) =>
            given is null ? throw new InvalidOperationException("Give the contact - its id, its e-mail address or external: and its external id.")
            : given.StartsWith("external:", StringComparison.OrdinalIgnoreCase) && given[9..].Trim() is { Length: > 0 } external ? new Ref("external_id", OneLine(external, "external id"))
            : IsAddress(given) ? new Ref("email", given)
            : Id.IsMatch(given) ? new Ref("id", given)
            : throw new InvalidOperationException($"'{given}' is not a contact - its id, its e-mail address or external: and its external id.");

        private static Ref CompanyRef(string? given, bool allowName) =>
            given is null ? throw new InvalidOperationException("Give the company - its Intercom id or company: and its own company id.")
            : given.StartsWith("company:", StringComparison.OrdinalIgnoreCase) && given[8..].Trim() is { Length: > 0 } own ? new Ref("company_id", OneLine(own, "company id"))
            : allowName && given.StartsWith("name:", StringComparison.OrdinalIgnoreCase) && given[5..].Trim() is { Length: > 0 } name ? new Ref("name", OneLine(name, "company's name"))
            : Id.IsMatch(given) ? new Ref("id", given)
            : throw new InvalidOperationException($"'{given}' is not a company - its Intercom id{(allowName ? ", company: and its own company id, or name: and its name" : " or company: and its own company id")}.");

        // The workspace's API: the calls, and the lookups of a contact by its address or external id, a company by its own id or
        // name and a tag by its name.
        private sealed class Session(HttpClient client, string api, string token)
        {
            private JsonArray? tags;

            public async Task<string> ContactIdAsync(Ref contact, CancellationToken cancellationToken)
            {
                if (contact.Kind == "id")
                {
                    return contact.Value;
                }

                var found = await SendAsync(HttpMethod.Post, "/contacts/search", new JsonObject { ["query"] = Condition(contact.Kind, contact.Value), ["pagination"] = new JsonObject { ["per_page"] = 1 } }, cancellationToken);
                return (found?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(item => Text(item["id"])).FirstOrDefault(id => id is not null)
                    ?? throw new InvalidOperationException($"No contact has the {(contact.Kind == "email" ? "address" : "external id")} {contact.Value}.");
            }

            public async Task<JsonObject> CompanyAsync(Ref company, CancellationToken cancellationToken) =>
                await SendAsync(HttpMethod.Get, company.Kind == "id" ? "/companies/" + company.Value : $"/companies?{company.Kind}={Uri.EscapeDataString(company.Value)}", null, cancellationToken) as JsonObject
                    ?? throw new InvalidOperationException($"No company has the {company.Kind.Replace('_', ' ')} {company.Value}.");

            public async Task<string> CompanyIdAsync(Ref company, CancellationToken cancellationToken) =>
                company.Kind == "id" ? company.Value : Text((await CompanyAsync(company, cancellationToken))["id"]) ?? throw new InvalidOperationException($"No company has the {company.Kind.Replace('_', ' ')} {company.Value}.");

            public async Task<string> TagIdAsync(Ref tag, CancellationToken cancellationToken)
            {
                if (tag.Kind == "id")
                {
                    return tag.Value;
                }

                tags ??= (await SendAsync(HttpMethod.Get, "/tags", null, cancellationToken))?["data"] as JsonArray ?? new JsonArray();
                return tags.OfType<JsonObject>().Where(item => string.Equals(Text(item["name"]), tag.Value, StringComparison.OrdinalIgnoreCase)).Select(item => Text(item["id"])).FirstOrDefault(id => id is not null)
                    ?? throw new InvalidOperationException($"No tag is named '{tag.Value}'.");
            }

            // Intercom's answer as JSON (an empty answer nothing), the token as a bearer and its API version, a throttled call
            // waiting as Intercom says; a failure thrown with its status and Intercom's codes and messages.
            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
            {
                var content = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Add("Intercom-Version", ApiVersion);
                    request.Headers.Accept.ParseAdd("application/json");
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
                    throw new InvalidOperationException($"Intercom answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errors = (json?["errors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Select(error => Text(error["code"]) is { } code ? code + (Text(error["message"]) is { Length: > 0 } message ? ": " + message : "") : Text(error["message"]))
                        .Where(error => error is not null).ToList();
                    var said = errors.Count > 0 ? string.Join("; ", errors) : Text(json?["message"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : " " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }

        private static JsonObject Condition(string field, string value) => new() { ["field"] = field, ["operator"] = "=", ["value"] = value };

        private static long Unix(string given, string what) =>
            DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment.ToUnixTimeSeconds()
                : throw new InvalidOperationException($"'{given}' is not a time ({what}) - 2026-10-01, or 2026-10-01T09:00:00+02:00.");

        // Intercom's times - seconds since 1970 - as times in UTC.
        private static string? Time(JsonNode? node) =>
            Long(node) is { } seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null;

        private static bool IsAddress(string given) =>
            !given.Any(char.IsWhiteSpace) && given.IndexOf('@') > 0 && given.IndexOf('@') == given.LastIndexOf('@') && given.IndexOf('@') < given.Length - 1;

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

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Intercom connection has no access token.");

        private static long? Long(JsonNode? node) => node is JsonValue value ? value.TryGetValue<long>(out var number) ? number : value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null : null;

        private static bool? Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

        private static string? Text(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null
            : null;
    }
}
