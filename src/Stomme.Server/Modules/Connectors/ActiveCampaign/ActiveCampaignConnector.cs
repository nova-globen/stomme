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

namespace Stomme.Modules.Connectors.ActiveCampaign
{
    /// <summary>
    /// ActiveCampaign (n8n's ActiveCampaign node: its contacts, lists, tags, deals, accounts and e-commerce records): an
    /// account over its API v3 (<c>{the account's API URL}/api/3</c>), its API key sent in ActiveCampaign's <c>Api-Token</c> header.
    /// A test names the user the key belongs to.
    /// </summary>
    /// <remarks>
    /// A contact is its id or its e-mail address (looked up), a list and a tag their id or their name (looked up), a deal, an
    /// account, a note and an account contact their id. <c>contacts</c>: the contacts a search, an address, a list, a tag, a
    /// segment, a status and creation and change times keep, in an order, page by page (100 a page, a page no bigger than what is
    /// left) up to <c>max</c> (1,000); more of ActiveCampaign's filters as name=value pairs. <c>get_contact</c>: the contact, its
    /// lists' and tags' ids and each custom field by its personalization tag. <c>create_contact</c>, <c>sync_contact</c> (made, or
    /// changed when the address is known - n8n's Update If Exists), <c>update_contact</c>, <c>delete_contact</c> - custom fields a
    /// JSON object, each field by its personalization tag or id. <c>lists</c>, <c>add_to_list</c>, <c>remove_from_list</c>;
    /// <c>tags</c>, <c>get_tag</c>, <c>create_tag</c>, <c>update_tag</c>, <c>delete_tag</c>, <c>add_tag</c>, <c>remove_tag</c> (by
    /// the contact and the tag, or n8n's contact tag id); <c>fields</c>; <c>deals</c>, <c>get_deal</c>, <c>create_deal</c>,
    /// <c>update_deal</c>, <c>delete_deal</c>, <c>add_deal_note</c>, <c>update_deal_note</c> (a value in cents);
    /// <c>accounts</c>, <c>get_account</c>, <c>create_account</c>, <c>update_account</c>, <c>delete_account</c>,
    /// <c>add_account_contact</c>, <c>update_account_contact</c>, <c>remove_account_contact</c>; and any other collection of the API
    /// - its e-commerce connections, customers, orders and order products among them - through <c>records</c>, <c>get_record</c>,
    /// <c>create_record</c>, <c>update_record</c> and <c>delete_record</c>. Each record ActiveCampaign's fields as it names them, its
    /// links left out. A throttled call waits as ActiveCampaign says; a failure is thrown with its status and ActiveCampaign's words.
    /// </remarks>
    public sealed class ActiveCampaignConnector : IConnector, IConnectorExtractor
    {
        public const string ContactsOperation = "contacts";

        public const string GetContactOperation = "get_contact";

        public const string CreateContactOperation = "create_contact";

        public const string SyncContactOperation = "sync_contact";

        public const string UpdateContactOperation = "update_contact";

        public const string DeleteContactOperation = "delete_contact";

        public const string ListsOperation = "lists";

        public const string AddToListOperation = "add_to_list";

        public const string RemoveFromListOperation = "remove_from_list";

        public const string TagsOperation = "tags";

        public const string GetTagOperation = "get_tag";

        public const string CreateTagOperation = "create_tag";

        public const string UpdateTagOperation = "update_tag";

        public const string DeleteTagOperation = "delete_tag";

        public const string AddTagOperation = "add_tag";

        public const string RemoveTagOperation = "remove_tag";

        public const string FieldsOperation = "fields";

        public const string DealsOperation = "deals";

        public const string GetDealOperation = "get_deal";

        public const string CreateDealOperation = "create_deal";

        public const string UpdateDealOperation = "update_deal";

        public const string DeleteDealOperation = "delete_deal";

        public const string AddDealNoteOperation = "add_deal_note";

        public const string UpdateDealNoteOperation = "update_deal_note";

        public const string AccountsOperation = "accounts";

        public const string GetAccountOperation = "get_account";

        public const string CreateAccountOperation = "create_account";

        public const string UpdateAccountOperation = "update_account";

        public const string DeleteAccountOperation = "delete_account";

        public const string AddAccountContactOperation = "add_account_contact";

        public const string UpdateAccountContactOperation = "update_account_contact";

        public const string RemoveAccountContactOperation = "remove_account_contact";

        public const string RecordsOperation = "records";

        public const string GetRecordOperation = "get_record";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordOperation = "update_record";

        public const string DeleteRecordOperation = "delete_record";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Numeric = new("^[1-9][0-9]{0,17}$", RegexOptions.CultureInvariant);

        private static readonly Regex Cents = new("^[0-9]{1,15}$", RegexOptions.CultureInvariant);

        private static readonly Regex Currency = new("^[A-Za-z]{3}$", RegexOptions.CultureInvariant);

        private static readonly Regex QueryName = new("^[A-Za-z_][A-Za-z0-9_]{0,59}(\\[[A-Za-z0-9_]{1,60}\\])?$", RegexOptions.CultureInvariant);

        private static readonly Regex ResourcePath = new("^[A-Za-z]{2,60}(/[1-9][0-9]{0,17}/[A-Za-z]{2,60})?$", RegexOptions.CultureInvariant);

        private static readonly Regex RootName = new("^[A-Za-z]{2,60}$", RegexOptions.CultureInvariant);

        private static readonly string[] Orders = { "cdate", "email", "first_name", "last_name", "name", "score" };

        // A contact's status as ActiveCampaign numbers it.
        private static readonly (string Name, string Value)[] Statuses = { ("any", "-1"), ("unconfirmed", "0"), ("active", "1"), ("unsubscribed", "2"), ("bounced", "3") };

        // A deal's status as ActiveCampaign numbers it.
        private static readonly (string Name, string Value)[] DealStatuses = { ("open", "0"), ("won", "1"), ("lost", "2") };

        // JSON as written: names, notes and their accents and markup, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            ContactsOperation, GetContactOperation, CreateContactOperation, SyncContactOperation, UpdateContactOperation, DeleteContactOperation,
            ListsOperation, AddToListOperation, RemoveFromListOperation,
            TagsOperation, GetTagOperation, CreateTagOperation, UpdateTagOperation, DeleteTagOperation, AddTagOperation, RemoveTagOperation, FieldsOperation,
            DealsOperation, GetDealOperation, CreateDealOperation, UpdateDealOperation, DeleteDealOperation, AddDealNoteOperation, UpdateDealNoteOperation,
            AccountsOperation, GetAccountOperation, CreateAccountOperation, UpdateAccountOperation, DeleteAccountOperation, AddAccountContactOperation, UpdateAccountContactOperation, RemoveAccountContactOperation,
            RecordsOperation, GetRecordOperation, CreateRecordOperation, UpdateRecordOperation, DeleteRecordOperation,
        };

        public string ConnectorType => "activecampaign";

        public string DisplayName => "ActiveCampaign";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => "https://youraccount.api-us1.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing, ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.activecampaign.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.activecampaign.com/reference");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField ContactField = new("contact", "Contact", ConnectorConfigFieldType.Text, Required: true, Description: "The contact's id or e-mail address.");

        private static readonly ConnectorConfigField ListField = new("list", "List", ConnectorConfigFieldType.Text, Required: true, Description: "The list's id or name.");

        private static readonly ConnectorConfigField TagField = new("tag", "Tag", ConnectorConfigFieldType.Text, Required: true, Description: "The tag's id or name.");

        private static readonly ConnectorConfigField DealField = new("deal", "Deal", ConnectorConfigFieldType.Text, Required: true, Description: "The deal's id.");

        private static readonly ConnectorConfigField AccountField = new("account", "Account", ConnectorConfigFieldType.Text, Required: true, Description: "The account's id.");

        private static readonly ConnectorConfigField AccountContactField = new("account_contact", "Account contact", ConnectorConfigFieldType.Text, Required: true, Description: "The id of the link between an account and a contact.");

        private static readonly ConnectorConfigField SearchField = new("search", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words to look for.");

        private static readonly ConnectorConfigField ResourceField = new("resource", "Resource", ConnectorConfigFieldType.Text, Required: true, Description: "Its path under /api/3 - ecomOrders, or ecomOrders/12/orderProducts.");

        private static readonly ConnectorConfigField RecordIdField = new("id", "Record", ConnectorConfigFieldType.Text, Required: true, Description: "The record's id.");

        private static readonly ConnectorConfigField RootField = new("root", "Record name", ConnectorConfigFieldType.Text, Required: false, Description: "The record's name in the body - ecomOrder. The resource's, without its s, when empty.");

        private static readonly ConnectorConfigField QueryField = new("query", "More filters", ConnectorConfigFieldType.Text, Required: false, Description: "ActiveCampaign's query parameters as name=value pairs, comma-separated - filters[status]=1, orders[cdate]=DESC.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] ContactWrite(bool create) => new[]
        {
            new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: create, Description: "ada@example.com."),
            new ConnectorConfigField("first_name", "First name", ConnectorConfigFieldType.Text, Required: false, Description: "Ada."),
            new ConnectorConfigField("last_name", "Last name", ConnectorConfigFieldType.Text, Required: false, Description: "Lovelace."),
            new ConnectorConfigField("phone", "Phone", ConnectorConfigFieldType.Text, Required: false, Description: "+46 8 465 004 01."),
            new ConnectorConfigField("fields", "Custom fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object, each field by its personalization tag or its id - {\"COMPANY\": \"Acme\", \"7\": \"Gold\"}; a list of values for a field holding several."),
        };

        private static ConnectorConfigField[] DealWrite(bool create) => new[]
        {
            new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: create, Description: "Acme renewal."),
            new ConnectorConfigField("contact", "Contact", ConnectorConfigFieldType.Text, Required: create, Description: "The deal's contact - its id or e-mail address."),
            new ConnectorConfigField("value", "Value in cents", ConnectorConfigFieldType.Text, Required: create, Description: "45000 for 450.00."),
            new ConnectorConfigField("currency", "Currency", ConnectorConfigFieldType.Text, Required: create, Description: "Three letters - eur, usd.") { Default = create ? "usd" : null },
            new ConnectorConfigField("pipeline", "Pipeline", ConnectorConfigFieldType.Text, Required: false, Description: "The pipeline's id."),
            new ConnectorConfigField("stage", "Stage", ConnectorConfigFieldType.Text, Required: false, Description: "The stage's id."),
            new ConnectorConfigField("owner", "Owner", ConnectorConfigFieldType.Text, Required: false, Description: "The owning user's id."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "What the deal is."),
            new ConnectorConfigField("percent", "Probability", ConnectorConfigFieldType.Text, Required: false, Description: "0 to 100."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "open, won or lost."),
        };

        private static ConnectorConfigField[] AccountWrite(bool create) => new[]
        {
            new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: create, Description: "Acme AB."),
            new ConnectorConfigField("url", "Website", ConnectorConfigFieldType.Text, Required: false, Description: "https://acme.example."),
            new ConnectorConfigField("fields", "Custom fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object, each account field by its id - {\"3\": \"Retail\"}."),
        };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ContactsOperation, "Get contacts", "The contacts a search and filters keep, in an order - n8n's contact Get Many.")
            {
                Parameters = Schema(
                    SearchField,
                    new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: false, Description: "Only the contact with this address."),
                    new ConnectorConfigField("list", "List", ConnectorConfigFieldType.Text, Required: false, Description: "Only a list's contacts - its id or name."),
                    new ConnectorConfigField("tag", "Tag", ConnectorConfigFieldType.Text, Required: false, Description: "Only the contacts with a tag - its id or name."),
                    new ConnectorConfigField("segment", "Segment", ConnectorConfigFieldType.Text, Required: false, Description: "Only a segment's contacts - its id."),
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "active, unconfirmed, unsubscribed, bounced or any."),
                    new ConnectorConfigField("created_after", "Made after", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or 2026-10-01T09:00:00+02:00."),
                    new ConnectorConfigField("created_before", "Made before", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
                    new ConnectorConfigField("updated_after", "Changed after", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
                    new ConnectorConfigField("updated_before", "Changed before", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
                    new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "cdate, email, first_name, last_name, name or score, then asc or desc - cdate desc."),
                    QueryField,
                    Max()),
            },
            new ConnectorOperation(GetContactOperation, "Get a contact", "One contact, its lists' and tags' ids and its custom fields by their personalization tags - n8n's contact Get.") { Parameters = Schema(ContactField) },
            new ConnectorOperation(CreateContactOperation, "Create a contact", "Adds a contact - n8n's contact Create.") { Parameters = Schema(ContactWrite(create: true)) },
            new ConnectorOperation(SyncContactOperation, "Create or update a contact", "Adds a contact, or changes it when its address is known - n8n's contact Create with Update If Exists.") { Parameters = Schema(ContactWrite(create: true)) },
            new ConnectorOperation(UpdateContactOperation, "Update a contact", "Changes a contact - n8n's contact Update.") { Parameters = Schema(new[] { ContactField }.Concat(ContactWrite(create: false)).ToArray()) },
            new ConnectorOperation(DeleteContactOperation, "Delete a contact", "Deletes a contact - n8n's contact Delete.") { Parameters = Schema(ContactField) },
            new ConnectorOperation(ListsOperation, "Get lists", "The account's lists - n8n's list Get Many.") { Parameters = Schema(new ConnectorConfigField("search", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Lists whose names hold it."), Max()) },
            new ConnectorOperation(AddToListOperation, "Add a contact to a list", "Subscribes a contact to a list - n8n's contact list Add.") { Parameters = Schema(ContactField, ListField) },
            new ConnectorOperation(RemoveFromListOperation, "Remove a contact from a list", "Unsubscribes a contact from a list - n8n's contact list Remove.") { Parameters = Schema(ContactField, ListField) },
            new ConnectorOperation(TagsOperation, "Get tags", "The account's tags - n8n's tag Get Many.") { Parameters = Schema(SearchField, Max()) },
            new ConnectorOperation(GetTagOperation, "Get a tag", "One tag - n8n's tag Get.") { Parameters = Schema(TagField) },
            new ConnectorOperation(CreateTagOperation, "Create a tag", "Adds a tag - n8n's tag Create.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "VIP."),
                    new ConnectorConfigField("type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "contact or template.") { Default = "contact" },
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Text, Required: false, Description: "What it marks.")),
            },
            new ConnectorOperation(UpdateTagOperation, "Update a tag", "Renames a tag or changes its description - n8n's tag Update.")
            {
                Parameters = Schema(
                    TagField,
                    new ConnectorConfigField("name", "New name", ConnectorConfigFieldType.Text, Required: false, Description: "VIP customer."),
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Text, Required: false, Description: "What it marks.")),
            },
            new ConnectorOperation(DeleteTagOperation, "Delete a tag", "Deletes a tag - n8n's tag Delete.") { Parameters = Schema(TagField) },
            new ConnectorOperation(AddTagOperation, "Add a tag to a contact", "Tags a contact - n8n's contact tag Add.") { Parameters = Schema(ContactField, TagField) },
            new ConnectorOperation(RemoveTagOperation, "Remove a tag from a contact", "Takes a tag off a contact - n8n's contact tag Remove.")
            {
                Parameters = Schema(
                    ContactField with { Required = false },
                    TagField with { Required = false },
                    new ConnectorConfigField("contact_tag", "Contact's tag", ConnectorConfigFieldType.Text, Required: false, Description: "Its id - n8n's Contact Tag ID - in place of the contact and the tag.")),
            },
            new ConnectorOperation(FieldsOperation, "Get custom fields", "The contacts' custom fields - their ids, titles, personalization tags and types.") { Parameters = Schema(Max()) },
            new ConnectorOperation(DealsOperation, "Get deals", "The deals a search, a pipeline, a stage, an owner and a status keep - n8n's deal Get Many.")
            {
                Parameters = Schema(
                    SearchField,
                    new ConnectorConfigField("pipeline", "Pipeline", ConnectorConfigFieldType.Text, Required: false, Description: "The pipeline's id."),
                    new ConnectorConfigField("stage", "Stage", ConnectorConfigFieldType.Text, Required: false, Description: "The stage's id."),
                    new ConnectorConfigField("owner", "Owner", ConnectorConfigFieldType.Text, Required: false, Description: "The owning user's id."),
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "open, won or lost."),
                    Max()),
            },
            new ConnectorOperation(GetDealOperation, "Get a deal", "One deal - n8n's deal Get.") { Parameters = Schema(DealField) },
            new ConnectorOperation(CreateDealOperation, "Create a deal", "Adds a deal - n8n's deal Create.") { Parameters = Schema(DealWrite(create: true)) },
            new ConnectorOperation(UpdateDealOperation, "Update a deal", "Changes a deal - n8n's deal Update.") { Parameters = Schema(new[] { DealField }.Concat(DealWrite(create: false)).ToArray()) },
            new ConnectorOperation(DeleteDealOperation, "Delete a deal", "Deletes a deal - n8n's deal Delete.") { Parameters = Schema(DealField) },
            new ConnectorOperation(AddDealNoteOperation, "Add a note to a deal", "Notes something on a deal - n8n's deal Create Note.")
            {
                Parameters = Schema(DealField, new ConnectorConfigField("note", "Note", ConnectorConfigFieldType.Multiline, Required: true, Description: "What to note.")),
            },
            new ConnectorOperation(UpdateDealNoteOperation, "Update a deal's note", "Rewrites a note on a deal - n8n's deal Update Deal Note.")
            {
                Parameters = Schema(
                    DealField,
                    new ConnectorConfigField("note_id", "Note", ConnectorConfigFieldType.Text, Required: true, Description: "The note's id."),
                    new ConnectorConfigField("note", "Text", ConnectorConfigFieldType.Multiline, Required: true, Description: "The note as it should read.")),
            },
            new ConnectorOperation(AccountsOperation, "Get accounts", "The accounts - companies - a search keeps - n8n's account Get Many.") { Parameters = Schema(SearchField, Max()) },
            new ConnectorOperation(GetAccountOperation, "Get an account", "One account - n8n's account Get.") { Parameters = Schema(AccountField) },
            new ConnectorOperation(CreateAccountOperation, "Create an account", "Adds an account - n8n's account Create.") { Parameters = Schema(AccountWrite(create: true)) },
            new ConnectorOperation(UpdateAccountOperation, "Update an account", "Changes an account - n8n's account Update.") { Parameters = Schema(new[] { AccountField }.Concat(AccountWrite(create: false)).ToArray()) },
            new ConnectorOperation(DeleteAccountOperation, "Delete an account", "Deletes an account - n8n's account Delete.") { Parameters = Schema(AccountField) },
            new ConnectorOperation(AddAccountContactOperation, "Add a contact to an account", "Links a contact to an account - n8n's account contact Create.")
            {
                Parameters = Schema(AccountField, ContactField, new ConnectorConfigField("job_title", "Job title", ConnectorConfigFieldType.Text, Required: false, Description: "Buyer.")),
            },
            new ConnectorOperation(UpdateAccountContactOperation, "Update an account's contact", "Changes the contact's job title at the account - n8n's account contact Update.")
            {
                Parameters = Schema(AccountContactField, new ConnectorConfigField("job_title", "Job title", ConnectorConfigFieldType.Text, Required: true, Description: "Buyer.")),
            },
            new ConnectorOperation(RemoveAccountContactOperation, "Remove a contact from an account", "Unlinks a contact from an account - n8n's account contact Delete.") { Parameters = Schema(AccountContactField) },
            new ConnectorOperation(RecordsOperation, "Get records", "Any collection of the API - e-commerce orders, customers and connections among them - n8n's Get Many of those.") { Parameters = Schema(ResourceField, QueryField, Max()) },
            new ConnectorOperation(GetRecordOperation, "Get a record", "One record of any collection - n8n's Get of those.") { Parameters = Schema(ResourceField, RecordIdField) },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "Adds a record to any collection - n8n's Create of those.")
            {
                Parameters = Schema(ResourceField, new ConnectorConfigField("values", "Values", ConnectorConfigFieldType.Multiline, Required: true, Description: "A JSON object - {\"email\": \"ada@example.com\", \"connectionid\": 1}."), RootField),
            },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "Changes a record of any collection - n8n's Update of those.")
            {
                Parameters = Schema(ResourceField, RecordIdField, new ConnectorConfigField("values", "Values", ConnectorConfigFieldType.Multiline, Required: true, Description: "A JSON object of what changes."), RootField),
            },
            new ConnectorOperation(DeleteRecordOperation, "Delete a record", "Deletes a record of any collection - n8n's Delete of those.") { Parameters = Schema(ResourceField, RecordIdField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                var key = Key(context.Credentials);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = (await new Session(client, api, key).SendAsync(HttpMethod.Get, "/users/me", null, cancellationToken))?["user"] as JsonObject ?? new JsonObject();
                var name = string.Join(" ", new[] { Text(me["firstName"]), Text(me["lastName"]) }.Where(part => !string.IsNullOrWhiteSpace(part)));
                return ConnectionTestResult.Ok($"Signed in as {(name.Length > 0 ? name : Text(me["email"]) ?? "the key's user")}{(Text(me["username"]) is { Length: > 0 } user ? $" ({user})" : "")}.");
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
            var key = Key(context.Credentials);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? CreateContactOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"ActiveCampaign has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");

            // Everything held to its shape before anything is sent.
            var plan = Plan.Of(operation, context.Parameters);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            var session = new Session(client, api, key);
            await foreach (var record in RunAsync(operation, plan, session, cancellationToken))
            {
                yield return record;
            }
        }

        private static async IAsyncEnumerable<ConnectorRecord> RunAsync(string operation, Plan plan, Session session, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            switch (operation)
            {
                case ContactsOperation:
                    {
                        var query = new List<string>(plan.Query);
                        if (plan.ListRef is { } list)
                        {
                            query.Add("listid=" + await session.ListIdAsync(list, cancellationToken));
                        }

                        if (plan.Tag is { } tag)
                        {
                            query.Add("tagid=" + await session.TagIdAsync(tag, cancellationToken));
                        }

                        await foreach (var contact in session.PagesAsync(Path("/contacts", query), "contacts", plan.Max, cancellationToken))
                        {
                            yield return Record(contact);
                        }

                        yield break;
                    }

                case ListsOperation:
                case TagsOperation:
                case FieldsOperation:
                case DealsOperation:
                case AccountsOperation:
                case RecordsOperation:
                    {
                        var (path, root) = operation switch
                        {
                            ListsOperation => ("/lists", "lists"),
                            TagsOperation => ("/tags", "tags"),
                            FieldsOperation => ("/fields", "fields"),
                            DealsOperation => ("/deals", "deals"),
                            AccountsOperation => ("/accounts", "accounts"),
                            _ => ("/" + plan.Resource, plan.Resource!.Split('/')[^1]),
                        };
                        await foreach (var item in session.PagesAsync(Path(path, plan.Query), root, plan.Max, cancellationToken))
                        {
                            yield return Record(item);
                        }

                        yield break;
                    }

                case GetContactOperation:
                    {
                        var id = await session.ContactIdAsync(plan.Contact!.Value, cancellationToken);
                        var answer = await session.SendAsync(HttpMethod.Get, "/contacts/" + id, null, cancellationToken) as JsonObject ?? new JsonObject();
                        var fields = Fields(answer["contact"] as JsonObject ?? new JsonObject());
                        fields["lists"] = string.Join(", ", (answer["contactLists"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(link => Scalar(link["status"]) == "1").Select(link => Scalar(link["list"])));
                        fields["tags"] = string.Join(", ", (answer["contactTags"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(link => Scalar(link["tag"])));
                        var values = (answer["fieldValues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                        if (values.Count > 0)
                        {
                            var tags = await session.FieldTagsAsync(cancellationToken);
                            foreach (var value in values)
                            {
                                var field = Scalar(value["field"]) ?? "";
                                fields[tags.TryGetValue(field, out var tag) ? tag : "field_" + field] = Scalar(value["value"]);
                            }
                        }

                        yield return new ConnectorRecord(fields);
                        yield break;
                    }

                case CreateContactOperation:
                case SyncContactOperation:
                case UpdateContactOperation:
                    {
                        var contact = plan.Body!;
                        if (plan.Custom is { } custom)
                        {
                            contact["fieldValues"] = await session.FieldValuesAsync(custom, cancellationToken);
                        }

                        var body = new JsonObject { ["contact"] = contact };
                        var answer = operation == UpdateContactOperation
                            ? await session.SendAsync(HttpMethod.Put, "/contacts/" + await session.ContactIdAsync(plan.Contact!.Value, cancellationToken), body, cancellationToken)
                            : await session.SendAsync(HttpMethod.Post, operation == SyncContactOperation ? "/contact/sync" : "/contacts", body, cancellationToken);
                        yield return Written(answer, "contact");
                        yield break;
                    }

                case DeleteContactOperation:
                    await session.SendAsync(HttpMethod.Delete, "/contacts/" + await session.ContactIdAsync(plan.Contact!.Value, cancellationToken), null, cancellationToken);
                    yield return Done();
                    yield break;

                case AddToListOperation:
                case RemoveFromListOperation:
                    {
                        var body = new JsonObject
                        {
                            ["contactList"] = new JsonObject
                            {
                                ["list"] = await session.ListIdAsync(plan.ListRef!.Value, cancellationToken),
                                ["contact"] = await session.ContactIdAsync(plan.Contact!.Value, cancellationToken),
                                ["status"] = operation == AddToListOperation ? 1 : 2,
                            },
                        };
                        yield return Written(await session.SendAsync(HttpMethod.Post, "/contactLists", body, cancellationToken), "contactList");
                        yield break;
                    }

                case GetTagOperation:
                case DeleteTagOperation:
                case UpdateTagOperation:
                    {
                        var id = await session.TagIdAsync(plan.Tag!.Value, cancellationToken);
                        if (operation == DeleteTagOperation)
                        {
                            await session.SendAsync(HttpMethod.Delete, "/tags/" + id, null, cancellationToken);
                            yield return Done();
                        }
                        else
                        {
                            var answer = operation == GetTagOperation
                                ? await session.SendAsync(HttpMethod.Get, "/tags/" + id, null, cancellationToken)
                                : await session.SendAsync(HttpMethod.Put, "/tags/" + id, new JsonObject { ["tag"] = plan.Body }, cancellationToken);
                            yield return Written(answer, "tag");
                        }

                        yield break;
                    }

                case AddTagOperation:
                    {
                        var body = new JsonObject
                        {
                            ["contactTag"] = new JsonObject
                            {
                                ["contact"] = await session.ContactIdAsync(plan.Contact!.Value, cancellationToken),
                                ["tag"] = await session.TagIdAsync(plan.Tag!.Value, cancellationToken),
                            },
                        };
                        yield return Written(await session.SendAsync(HttpMethod.Post, "/contactTags", body, cancellationToken), "contactTag");
                        yield break;
                    }

                case RemoveTagOperation:
                    {
                        var link = plan.Id;
                        if (link is null)
                        {
                            var contact = await session.ContactIdAsync(plan.Contact!.Value, cancellationToken);
                            var tag = await session.TagIdAsync(plan.Tag!.Value, cancellationToken);
                            var links = await session.SendAsync(HttpMethod.Get, $"/contacts/{contact}/contactTags", null, cancellationToken);
                            link = (links?["contactTags"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(found => Scalar(found["tag"]) == tag).Select(found => Scalar(found["id"])).FirstOrDefault();
                            if (link is null)
                            {
                                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["removed"] = false }); // it had no such tag
                                yield break;
                            }
                        }

                        await session.SendAsync(HttpMethod.Delete, "/contactTags/" + link, null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["removed"] = true });
                        yield break;
                    }

                case CreateDealOperation:
                case UpdateDealOperation:
                    {
                        var deal = plan.Body!;
                        if (plan.Contact is { } contact)
                        {
                            deal["contact"] = await session.ContactIdAsync(contact, cancellationToken);
                        }

                        var body = new JsonObject { ["deal"] = deal };
                        yield return Written(operation == CreateDealOperation
                            ? await session.SendAsync(HttpMethod.Post, "/deals", body, cancellationToken)
                            : await session.SendAsync(HttpMethod.Put, "/deals/" + plan.Id, body, cancellationToken), "deal");
                        yield break;
                    }

                case AddAccountContactOperation:
                    {
                        var link = plan.Body!;
                        link["contact"] = await session.ContactIdAsync(plan.Contact!.Value, cancellationToken);
                        yield return Written(await session.SendAsync(HttpMethod.Post, "/accountContacts", new JsonObject { ["accountContact"] = link }, cancellationToken), "accountContact");
                        yield break;
                    }

                default:
                    {
                        // The rest is one call: a record got, written or deleted at a path the plan holds.
                        var answer = await session.SendAsync(plan.Method, plan.Path!, plan.Body is null ? null : new JsonObject { [plan.Root!] = plan.Body }, cancellationToken);
                        yield return plan.Method == HttpMethod.Delete ? Done() : Written(answer, plan.Root!);
                        yield break;
                    }
            }
        }

        // What is asked, held to its shape: the references and values each operation needs, and the one call most of them make.
        private sealed class Plan
        {
            public int Max { get; private set; } = DefaultMax;

            public List<string> Query { get; } = new();

            public Ref? Contact { get; private set; }

            public Ref? ListRef { get; private set; }

            public Ref? Tag { get; private set; }

            public string? Id { get; private set; }

            public string? Resource { get; private set; }

            public JsonObject? Body { get; private set; }

            public JsonObject? Custom { get; private set; }

            public HttpMethod Method { get; private set; } = HttpMethod.Get;

            public string? Path { get; private set; }

            public string? Root { get; private set; }

            public static Plan Of(string operation, IReadOnlyDictionary<string, string> p)
            {
                var plan = new Plan();
                if (Values.Get(p, "max") is { } most)
                {
                    plan.Max = int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).");
                }

                string Need(string name, string what) => Values.Get(p, name) ?? throw new InvalidOperationException($"Give {what}.");
                string IdOf(string name, string what) => Values.Get(p, name) is not { } given ? throw new InvalidOperationException($"Give {what}'s id.") : Numeric.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what}'s id.");
                void Filter(string name, string query, Func<string, string> shape)
                {
                    if (Values.Get(p, name) is { } given)
                    {
                        plan.Query.Add(Uri.EscapeDataString(query) + "=" + Uri.EscapeDataString(shape(given)));
                    }
                }

                string Itself(string given) => given;
                string IdShape(string given) => Numeric.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id.");

                switch (operation)
                {
                    case ContactsOperation:
                        Filter("search", "search", Itself);
                        Filter("email", "email", given => IsAddress(given) ? given : throw new InvalidOperationException($"'{given}' is not an e-mail address."));
                        plan.ListRef = Values.Get(p, "list") is { } list ? Ref.Named(list, "list") : null;
                        plan.Tag = Values.Get(p, "tag") is { } tag ? Ref.Named(tag, "tag") : null;
                        Filter("segment", "segmentid", IdShape);
                        Filter("status", "status", given => Choice(given, "a contact's status", Statuses));
                        Filter("created_after", "filters[created_after]", given => Time(given, "made after"));
                        Filter("created_before", "filters[created_before]", given => Time(given, "made before"));
                        Filter("updated_after", "filters[updated_after]", given => Time(given, "changed after"));
                        Filter("updated_before", "filters[updated_before]", given => Time(given, "changed before"));
                        if (Values.Get(p, "order") is { } order)
                        {
                            var parts = order.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            var field = parts.Length is 1 or 2 ? Orders.FirstOrDefault(known => string.Equals(known, parts[0], StringComparison.OrdinalIgnoreCase)) : null;
                            var direction = parts.Length == 2 ? parts[1].ToUpperInvariant() : "ASC";
                            plan.Query.Add(field is not null && direction is "ASC" or "DESC"
                                ? $"orders%5B{field}%5D={direction}"
                                : throw new InvalidOperationException($"'{order}' is not an order - {string.Join(", ", Orders)}, then asc or desc."));
                        }

                        plan.More(p);
                        break;
                    case ListsOperation:
                        Filter("search", "filters[name]", Itself);
                        break;
                    case TagsOperation:
                    case AccountsOperation:
                        Filter("search", "search", Itself);
                        break;
                    case DealsOperation:
                        Filter("search", "filters[search]", Itself);
                        Filter("pipeline", "filters[group]", IdShape);
                        Filter("stage", "filters[stage]", IdShape);
                        Filter("owner", "filters[owner]", IdShape);
                        Filter("status", "filters[status]", given => Choice(given, "a deal's status", DealStatuses));
                        break;
                    case RecordsOperation:
                        plan.Resource = ResourceOf(p);
                        plan.More(p);
                        break;
                    case GetContactOperation:
                    case DeleteContactOperation:
                        plan.Contact = Ref.Contact(Values.Get(p, "contact"));
                        break;
                    case CreateContactOperation:
                    case SyncContactOperation:
                    case UpdateContactOperation:
                        {
                            var body = new JsonObject();
                            if (operation == UpdateContactOperation)
                            {
                                plan.Contact = Ref.Contact(Values.Get(p, "contact"));
                            }

                            if (Values.Get(p, "email") is not null || operation != UpdateContactOperation)
                            {
                                body["email"] = Values.Get(p, "email") is not { } address ? throw new InvalidOperationException("Give the contact's e-mail address.") : IsAddress(address) ? address : throw new InvalidOperationException($"'{address}' is not an e-mail address.");
                            }

                            foreach (var (name, field) in new[] { ("first_name", "firstName"), ("last_name", "lastName"), ("phone", "phone") })
                            {
                                if (Values.Get(p, name) is { } given)
                                {
                                    body[field] = OneLine(given, name.Replace('_', ' '));
                                }
                            }

                            plan.Custom = Values.Get(p, "fields") is { } custom ? ObjectOf(custom, "custom fields", "{\"COMPANY\": \"Acme\"}") : null;
                            if (body.Count == 0 && plan.Custom is null)
                            {
                                throw new InvalidOperationException("Give something to change - an address, a name, a phone or custom fields.");
                            }

                            plan.Body = body;
                            break;
                        }

                    case AddToListOperation:
                    case RemoveFromListOperation:
                        plan.Contact = Ref.Contact(Values.Get(p, "contact"));
                        plan.ListRef = Ref.Named(Need("list", "the list - its id or name"), "list");
                        break;
                    case GetTagOperation:
                    case DeleteTagOperation:
                        plan.Tag = Ref.Named(Need("tag", "the tag - its id or name"), "tag");
                        break;
                    case CreateTagOperation:
                        plan.Body = new JsonObject
                        {
                            ["tag"] = OneLine(Need("name", "the tag's name"), "tag's name"),
                            ["tagType"] = Choice(Values.Get(p, "type") ?? "contact", "a tag's type", ("contact", "contact"), ("template", "template")),
                        };
                        if (Values.Get(p, "description") is { } about)
                        {
                            plan.Body["description"] = about;
                        }

                        plan.Method = HttpMethod.Post;
                        plan.Path = "/tags";
                        plan.Root = "tag";
                        break;
                    case UpdateTagOperation:
                        plan.Tag = Ref.Named(Need("tag", "the tag - its id or name"), "tag");
                        plan.Body = new JsonObject();
                        if (Values.Get(p, "name") is { } renamed)
                        {
                            plan.Body["tag"] = OneLine(renamed, "tag's name");
                        }

                        if (Values.Get(p, "description") is { } description)
                        {
                            plan.Body["description"] = description;
                        }

                        if (plan.Body.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - a name or a description.");
                        }

                        break;
                    case AddTagOperation:
                        plan.Contact = Ref.Contact(Values.Get(p, "contact"));
                        plan.Tag = Ref.Named(Need("tag", "the tag - its id or name"), "tag");
                        break;
                    case RemoveTagOperation:
                        if (Values.Get(p, "contact_tag") is not null)
                        {
                            plan.Id = IdOf("contact_tag", "a contact tag");
                        }
                        else
                        {
                            plan.Contact = Ref.Contact(Values.Get(p, "contact"));
                            plan.Tag = Ref.Named(Need("tag", "the tag - its id or name - or the contact tag's id"), "tag");
                        }

                        break;
                    case GetDealOperation:
                    case DeleteDealOperation:
                        plan.Path = "/deals/" + IdOf("deal", "a deal");
                        plan.Method = operation == GetDealOperation ? HttpMethod.Get : HttpMethod.Delete;
                        plan.Root = "deal";
                        break;
                    case CreateDealOperation:
                    case UpdateDealOperation:
                        {
                            var create = operation == CreateDealOperation;
                            if (!create)
                            {
                                plan.Id = IdOf("deal", "a deal");
                            }

                            var deal = new JsonObject();
                            if (Values.Get(p, "title") is not null || create)
                            {
                                deal["title"] = OneLine(Need("title", "the deal's title"), "title");
                            }

                            if (Values.Get(p, "contact") is not null || create)
                            {
                                plan.Contact = Ref.Contact(Values.Get(p, "contact"));
                            }

                            if (Values.Get(p, "value") is not null || create)
                            {
                                deal["value"] = Values.Get(p, "value") is not { } cents ? throw new InvalidOperationException("Give the deal's value in cents - 45000 for 450.00.") : Cents.IsMatch(cents) ? long.Parse(cents, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{cents}' is not a value in cents - 45000 for 450.00.");
                            }

                            if (Values.Get(p, "currency") is not null || create)
                            {
                                var code = Values.Get(p, "currency") ?? "usd";
                                deal["currency"] = Currency.IsMatch(code) ? code.ToLowerInvariant() : throw new InvalidOperationException($"'{code}' is not a currency - three letters, eur.");
                            }

                            foreach (var (name, field, what) in new[] { ("pipeline", "group", "a pipeline"), ("stage", "stage", "a stage"), ("owner", "owner", "an owner") })
                            {
                                if (Values.Get(p, name) is not null)
                                {
                                    deal[field] = IdOf(name, what);
                                }
                            }

                            if (Values.Get(p, "description") is { } told)
                            {
                                deal["description"] = told;
                            }

                            if (Values.Get(p, "percent") is { } percent)
                            {
                                deal["percent"] = int.TryParse(percent, NumberStyles.None, CultureInfo.InvariantCulture, out var chance) && chance <= 100 ? chance : throw new InvalidOperationException($"'{percent}' is not a probability - 0 to 100.");
                            }

                            if (Values.Get(p, "status") is { } status)
                            {
                                deal["status"] = int.Parse(Choice(status, "a deal's status", DealStatuses), CultureInfo.InvariantCulture);
                            }

                            if (deal.Count == 0 && plan.Contact is null)
                            {
                                throw new InvalidOperationException("Give something to change - a title, a contact, a value, a currency, a pipeline, a stage, an owner, a description, a probability or a status.");
                            }

                            plan.Body = deal;
                            break;
                        }

                    case AddDealNoteOperation:
                        plan.Path = $"/deals/{IdOf("deal", "a deal")}/notes";
                        plan.Method = HttpMethod.Post;
                        plan.Body = new JsonObject { ["note"] = Need("note", "the note") };
                        plan.Root = "note";
                        break;
                    case UpdateDealNoteOperation:
                        plan.Path = $"/deals/{IdOf("deal", "a deal")}/notes/{IdOf("note_id", "a note")}";
                        plan.Method = HttpMethod.Put;
                        plan.Body = new JsonObject { ["note"] = Need("note", "the note") };
                        plan.Root = "note";
                        break;
                    case GetAccountOperation:
                    case DeleteAccountOperation:
                        plan.Path = "/accounts/" + IdOf("account", "an account");
                        plan.Method = operation == GetAccountOperation ? HttpMethod.Get : HttpMethod.Delete;
                        plan.Root = "account";
                        break;
                    case CreateAccountOperation:
                    case UpdateAccountOperation:
                        {
                            var create = operation == CreateAccountOperation;
                            plan.Path = create ? "/accounts" : "/accounts/" + IdOf("account", "an account");
                            plan.Method = create ? HttpMethod.Post : HttpMethod.Put;
                            plan.Root = "account";
                            var account = new JsonObject();
                            if (Values.Get(p, "name") is not null || create)
                            {
                                account["name"] = OneLine(Need("name", "the account's name"), "account's name");
                            }

                            if (Values.Get(p, "url") is { } url)
                            {
                                account["accountUrl"] = Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var site) && (site.Scheme == Uri.UriSchemeHttp || site.Scheme == Uri.UriSchemeHttps) ? url : throw new InvalidOperationException($"'{url}' is not a website's address - https://acme.example.");
                            }

                            if (Values.Get(p, "fields") is { } fields)
                            {
                                var values = new JsonArray();
                                foreach (var (id, value) in ObjectOf(fields, "custom fields", "{\"3\": \"Retail\"}"))
                                {
                                    values.Add(new JsonObject
                                    {
                                        ["customFieldId"] = Numeric.IsMatch(id) ? long.Parse(id, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{id}' is not an account field's id."),
                                        ["fieldValue"] = FieldValue(value),
                                    });
                                }

                                account["fields"] = values;
                            }

                            if (account.Count == 0)
                            {
                                throw new InvalidOperationException("Give something to change - a name, a website or custom fields.");
                            }

                            plan.Body = account;
                            break;
                        }

                    case AddAccountContactOperation:
                        plan.Body = new JsonObject { ["account"] = IdOf("account", "an account") };
                        plan.Contact = Ref.Contact(Values.Get(p, "contact"));
                        if (Values.Get(p, "job_title") is { } job)
                        {
                            plan.Body["jobTitle"] = OneLine(job, "job title");
                        }

                        break;
                    case UpdateAccountContactOperation:
                        plan.Path = "/accountContacts/" + IdOf("account_contact", "an account contact");
                        plan.Method = HttpMethod.Put;
                        plan.Body = new JsonObject { ["jobTitle"] = OneLine(Need("job_title", "the job title"), "job title") };
                        plan.Root = "accountContact";
                        break;
                    case RemoveAccountContactOperation:
                        plan.Path = "/accountContacts/" + IdOf("account_contact", "an account contact");
                        plan.Method = HttpMethod.Delete;
                        plan.Root = "accountContact";
                        break;
                    case GetRecordOperation:
                    case DeleteRecordOperation:
                    case CreateRecordOperation:
                    case UpdateRecordOperation:
                        {
                            plan.Resource = ResourceOf(p);
                            var last = plan.Resource.Split('/')[^1];
                            plan.Root = Values.Get(p, "root") is { } root
                                ? RootName.IsMatch(root) ? root : throw new InvalidOperationException($"'{root}' is not a record's name - ecomOrder.")
                                : last.EndsWith("ies", StringComparison.Ordinal) ? last[..^3] + "y" : last.EndsWith('s') ? last[..^1] : last;
                            plan.Path = "/" + plan.Resource + (operation == CreateRecordOperation ? "" : "/" + IdOf("id", "a record"));
                            plan.Method = operation switch
                            {
                                GetRecordOperation => HttpMethod.Get,
                                DeleteRecordOperation => HttpMethod.Delete,
                                CreateRecordOperation => HttpMethod.Post,
                                _ => HttpMethod.Put,
                            };
                            if (operation is CreateRecordOperation or UpdateRecordOperation)
                            {
                                plan.Body = ObjectOf(Need("values", "the values - a JSON object"), "values", "{\"email\": \"ada@example.com\"}");
                            }

                            break;
                        }
                }

                return plan;
            }

            // ActiveCampaign's own query parameters, name=value pairs, comma-separated.
            private void More(IReadOnlyDictionary<string, string> p)
            {
                if (Values.Get(p, "query") is not { } more)
                {
                    return;
                }

                foreach (var pair in more.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var equals = pair.IndexOf('=');
                    var name = equals > 0 ? pair[..equals].Trim() : "";
                    Query.Add(QueryName.IsMatch(name)
                        ? Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(pair[(equals + 1)..].Trim())
                        : throw new InvalidOperationException($"'{pair}' is not a filter - name=value, filters[status]=1."));
                }
            }

            private static string ResourceOf(IReadOnlyDictionary<string, string> p) =>
                Values.Get(p, "resource") is { } resource && ResourcePath.IsMatch(resource.Trim('/')) ? resource.Trim('/') : throw new InvalidOperationException($"'{Values.Get(p, "resource")}' is not a resource - its path under /api/3, ecomOrders or ecomOrders/12/orderProducts.");
        }

        // A contact, a list or a tag: its id, or what finds it - an address, a name.
        private readonly record struct Ref(string? Id, string? Name)
        {
            public static Ref Contact(string? given) =>
                given is null ? throw new InvalidOperationException("Give the contact - its id or its e-mail address.")
                : Numeric.IsMatch(given) ? new Ref(given, null)
                : IsAddress(given) ? new Ref(null, given)
                : throw new InvalidOperationException($"'{given}' is not a contact - its id or its e-mail address.");

            public static Ref Named(string given, string what) => Numeric.IsMatch(given) ? new Ref(given, null) : new Ref(null, OneLine(given, what + "'s name"));
        }

        // The account's API: the calls, their pages, and the lookups of a contact by its address, a list and a tag by their name and
        // a custom field by its personalization tag.
        private sealed class Session
        {
            private readonly HttpClient client;

            private readonly string api;

            private readonly string key;

            private Dictionary<string, string>? fieldTags;

            public Session(HttpClient client, string api, string key)
            {
                this.client = client;
                this.api = api;
                this.key = key;
            }

            public async IAsyncEnumerable<JsonObject> PagesAsync(string path, string root, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var read = 0;
                var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(PageSize, max - read);
                    var page = await SendAsync(HttpMethod.Get, $"{path}{separator}limit={size.ToString(CultureInfo.InvariantCulture)}&offset={read.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken) as JsonObject ?? new JsonObject();
                    var items = page[root] as JsonArray ?? page.Select(pair => pair.Value).OfType<JsonArray>().FirstOrDefault() ?? new JsonArray();
                    foreach (var item in items.OfType<JsonObject>())
                    {
                        yield return item;
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    if (items.Count < size || (long.TryParse(Scalar(page["meta"]?["total"]), NumberStyles.None, CultureInfo.InvariantCulture, out var total) && read >= total))
                    {
                        yield break;
                    }
                }
            }

            public async Task<string> ContactIdAsync(Ref contact, CancellationToken cancellationToken)
            {
                if (contact.Id is { } id)
                {
                    return id;
                }

                var found = await SendAsync(HttpMethod.Get, "/contacts?email=" + Uri.EscapeDataString(contact.Name!) + "&limit=1", null, cancellationToken);
                return (found?["contacts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(item => Scalar(item["id"])).FirstOrDefault(item => item is not null)
                    ?? throw new InvalidOperationException($"No contact has the address {contact.Name}.");
            }

            public Task<string> ListIdAsync(Ref list, CancellationToken cancellationToken) => NamedAsync(list, "/lists?filters%5Bname%5D=", "lists", "name", "list", cancellationToken);

            public Task<string> TagIdAsync(Ref tag, CancellationToken cancellationToken) => NamedAsync(tag, "/tags?search=", "tags", "tag", "tag", cancellationToken);

            // A custom field's id by its personalization tag or its title; an id as it is.
            public async Task<JsonArray> FieldValuesAsync(JsonObject custom, CancellationToken cancellationToken)
            {
                var values = new JsonArray();
                foreach (var (name, value) in custom)
                {
                    string field;
                    if (Numeric.IsMatch(name))
                    {
                        field = name;
                    }
                    else
                    {
                        var tags = await FieldTagsAsync(cancellationToken);
                        field = tags.Where(tag => string.Equals(tag.Value, name.Trim().Trim('%'), StringComparison.OrdinalIgnoreCase)).Select(tag => tag.Key).FirstOrDefault()
                            ?? throw new InvalidOperationException($"No custom field has the personalization tag {name} - its id, or one of {string.Join(", ", tags.Values.OrderBy(tag => tag, StringComparer.Ordinal))}.");
                    }

                    values.Add(new JsonObject { ["field"] = field, ["value"] = FieldValue(value) });
                }

                return values;
            }

            // The custom fields' personalization tags by their ids, read once.
            public async Task<Dictionary<string, string>> FieldTagsAsync(CancellationToken cancellationToken)
            {
                if (fieldTags is null)
                {
                    fieldTags = new Dictionary<string, string>(StringComparer.Ordinal);
                    await foreach (var field in PagesAsync("/fields", "fields", 1000, cancellationToken))
                    {
                        if (Scalar(field["id"]) is { } id && Scalar(field["perstag"]) is { Length: > 0 } tag)
                        {
                            fieldTags[id] = tag;
                        }
                    }
                }

                return fieldTags;
            }

            private async Task<string> NamedAsync(Ref named, string search, string root, string nameField, string what, CancellationToken cancellationToken)
            {
                if (named.Id is { } id)
                {
                    return id;
                }

                var found = await SendAsync(HttpMethod.Get, search + Uri.EscapeDataString(named.Name!) + "&limit=100", null, cancellationToken);
                return (found?[root] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Where(item => string.Equals(Scalar(item[nameField]), named.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(item => Scalar(item["id"])).FirstOrDefault(item => item is not null)
                    ?? throw new InvalidOperationException($"No {what} is named '{named.Name}'.");
            }

            // ActiveCampaign's answer as JSON (an empty answer nothing), the key in its Api-Token header, a throttled call waiting as it
            // says; a failure thrown with its status and ActiveCampaign's words.
            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
            {
                var content = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Add("Api-Token", key);
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
                    throw new InvalidOperationException($"ActiveCampaign answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errors = (json?["errors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Select(error => Text(error["title"]) is { Length: > 0 } title ? title + (Text(error["detail"]) is { Length: > 0 } detail ? $" ({detail})" : "") : null)
                        .Where(error => error is not null).ToList();
                    var said = errors.Count > 0 ? string.Join("; ", errors) : Text(json?["message"]) ?? Text(json?["error"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }

        private static string Path(string path, IReadOnlyCollection<string> query) => query.Count == 0 ? path : path + "?" + string.Join("&", query);

        // A written record as ActiveCampaign answers it, under its name; done, when it answers nothing.
        private static ConnectorRecord Written(JsonNode? answer, string root) => answer?[root] is JsonObject record ? Record(record) : Done();

        private static ConnectorRecord Done() => new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["done"] = true });

        private static ConnectorRecord Record(JsonObject item) => new(Fields(item));

        // A record's fields as ActiveCampaign names them, its links left out; what is not a single value as its JSON.
        private static Dictionary<string, object?> Fields(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item)
            {
                if (name == "links")
                {
                    continue;
                }

                fields[name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    _ => value.ToJsonString(Wire),
                };
            }

            return fields;
        }

        // A custom field's value as ActiveCampaign keeps it: text; several values between double bars, ||Gold||Silver||.
        private static string FieldValue(JsonNode? value) => value switch
        {
            null => "",
            JsonArray several => "||" + string.Join("||", several.Select(one => Scalar(one) ?? "")) + "||",
            _ => Scalar(value) ?? value.ToJsonString(Wire),
        };

        private static string Api(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("Give the account's API URL - https://youraccount.api-us1.com, under Settings > Developer.");
            }

            var url = Values.BaseUrl(endpoint, "");
            return (url.EndsWith("/api/3", StringComparison.OrdinalIgnoreCase) ? url[..^6] : url) + "/api/3";
        }

        private static string Time(string given, string what) =>
            DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time ({what}) - 2026-10-01, or 2026-10-01T09:00:00+02:00.");

        private static bool IsAddress(string given) =>
            !given.Any(char.IsWhiteSpace) && given.IndexOf('@') > 0 && given.IndexOf('@') == given.LastIndexOf('@') && given.IndexOf('@') < given.Length - 1;

        private static string Choice(string given, string what, params (string Name, string Value)[] allowed) =>
            allowed.Where(choice => string.Equals(choice.Name, given.Trim(), StringComparison.OrdinalIgnoreCase)).Select(choice => choice.Value).FirstOrDefault()
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1].Select(choice => choice.Name))} or {allowed[^1].Name}.");

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

            return parsed as JsonObject ?? throw new InvalidOperationException($"The {what} are not a JSON object - {example}.");
        }

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static string Key(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The ActiveCampaign connection has no API key.");

        // A single value as text - ActiveCampaign writes its ids and numbers as text.
        private static string? Scalar(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : value.TryGetValue<double>(out var real) ? real.ToString(CultureInfo.InvariantCulture) : value.TryGetValue<bool>(out var flag) ? (flag ? "true" : "false") : null
            : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
