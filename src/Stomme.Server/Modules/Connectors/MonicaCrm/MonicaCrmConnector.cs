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
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.MonicaCrm
{
    /// <summary>
    /// Monica (n8n's Monica CRM node) over Monica 4's API (<c>{address}/api</c>) by an API token - a personal access
    /// token from Monica's settings - as a bearer token, never in the address. A test names the user the token is.
    /// </summary>
    /// <remarks>
    /// n8n's resources: contacts, activities, calls, contact fields, a contact's tags, conversations and their messages, journal
    /// entries, notes, reminders, tags and tasks - each listed, read, made, changed and deleted as n8n's node does. Measured
    /// against Monica 4.1.2 (<c>scripts/monica-test-server.ps1</c>): n8n's Contact Field - Get Many calls
    /// <c>/contact/{id}/contactfields</c>, a route Monica does not have (a 404 naming it), where the connector reads
    /// <c>/contacts/{id}/contactfields</c>; a change replaces the item and refuses one missing what it requires (a note without
    /// its contact, a task without completed), so a change reads the item and sends it back whole with the fields given; a
    /// date that is no date (31 February) is stored as another (3 March), so a date is held to the calendar here; and a tag
    /// made under a name Monica already has is made a second time, so a tag's name is looked up first and a taken one refused.
    /// A contact's gender by its number or its name. Every id, date and choice held to Monica's shape; Monica's own messages.
    /// </remarks>
    internal static class MonicaJson
    {
        /// <summary>The body with one more field: how a read item is put back together.</summary>
        public static JsonObject With(this JsonObject body, string key, JsonNode? value)
        {
            body[key] = value;
            return body;
        }
    }

    public sealed class MonicaCrmConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 100; // Monica's own most

        private const int MostText = 1048576;

        private enum Kind { Text, Long, Bool, Date, Id, Ids, Number, Frequency, Gender }

        private sealed record Field(string Name, string Wire, Kind Kind, bool Required, string Description);

        // A resource: its name, its plural (its list), its route, what it is made of, and how a read item is sent back whole.
        private sealed record Resource(string Name, string Plural, string Route, Field[] Fields, Func<JsonObject, JsonObject> Back, bool Listed = true, string? ListBy = null);

        private static readonly string[] Frequencies = { "one_time", "week", "month", "year" };

        private static Field F(string name, string wire, Kind kind, bool required, string description) => new(name, wire, kind, required, description);

        private static Field Contact(bool required = true) => F("contact", "contact_id", Kind.Id, required, "The contact's number.");

        private static readonly Resource[] Resources =
        {
            new("contact", "contacts", "/contacts", new[]
            {
                F("first_name", "first_name", Kind.Text, true, "Ada."), F("last_name", "last_name", Kind.Text, false, "Lund."), F("nickname", "nickname", Kind.Text, false, "Adis."),
                F("description", "description", Kind.Long, false, "About them."), F("gender", "gender_id", Kind.Gender, false, "Its name - Woman - or its number."),
                F("is_partial", "is_partial", Kind.Bool, false, "A partial contact, true or false."), F("birthdate", "birthdate", Kind.Date, false, "1990-03-14."),
                F("deceased", "is_deceased", Kind.Bool, false, "Deceased, true or false."), F("deceased_date", "deceased_date", Kind.Date, false, "2026-01-02."),
            }, BackContact),
            new("activity", "activities", "/activities", new[]
            {
                F("activity_type", "activity_type_id", Kind.Id, true, "The activity type's number."), F("summary", "summary", Kind.Text, true, "Fika på Kafé."),
                F("description", "description", Kind.Long, false, "What happened."), F("happened_at", "happened_at", Kind.Date, true, "2026-10-20."),
                F("contacts", "contacts", Kind.Ids, true, "The contacts' numbers, by commas."),
            }, item => Pick(item, ("summary", "summary"), ("description", "description"), ("happened_at", "happened_at"))
                .With("activity_type_id", item["activity_type"]?["id"]?.DeepClone())
                .With("contacts", new JsonArray((item["attendees"]?["contacts"] as JsonArray ?? new JsonArray()).Select(contact => contact?["id"]?.DeepClone()).ToArray()))),
            new("call", "calls", "/calls", new[] { Contact(), F("called_at", "called_at", Kind.Date, true, "2026-10-21."), F("content", "content", Kind.Long, true, "What was said.") },
                item => Pick(item, ("content", "content")).With("contact_id", item["contact"]?["id"]?.DeepClone()).With("called_at", Day(item["called_at"]))),
            new("contact_field", "contact_fields", "/contactfields", new[] { Contact(), F("contact_field_type", "contact_field_type_id", Kind.Id, true, "The field type's number - an e-mail, a phone."), F("data", "data", Kind.Text, true, "ada@kafe.example.") },
                item => new JsonObject { ["data"] = item["content"]?.DeepClone(), ["contact_id"] = item["contact"]?["id"]?.DeepClone(), ["contact_field_type_id"] = item["contact_field_type"]?["id"]?.DeepClone() },
                ListBy: "/contacts/{0}/contactfields"),
            new("conversation", "conversations", "/conversations", new[] { Contact(), F("contact_field_type", "contact_field_type_id", Kind.Id, true, "How - the field type's number."), F("happened_at", "happened_at", Kind.Date, true, "2026-10-21.") },
                item => new JsonObject { ["happened_at"] = Day(item["happened_at"]), ["contact_field_type_id"] = item["contact_field_type"]?["id"]?.DeepClone(), ["contact_id"] = item["contact"]?["id"]?.DeepClone() }, Listed: false),
            new("journal_entry", "journal_entries", "/journal", new[] { F("title", "title", Kind.Text, true, "Torsdag."), F("post", "post", Kind.Long, true, "What happened.") },
                item => Pick(item, ("title", "title"), ("post", "post"))),
            new("note", "notes", "/notes", new[] { Contact(), F("body", "body", Kind.Long, true, "The note."), F("is_favorited", "is_favorited", Kind.Bool, false, "A favourite, true or false.") },
                item => Pick(item, ("body", "body"), ("is_favorited", "is_favorited")).With("contact_id", item["contact"]?["id"]?.DeepClone())),
            new("reminder", "reminders", "/reminders", new[]
            {
                Contact(), F("title", "title", Kind.Text, true, "Ringa Ada."), F("initial_date", "initial_date", Kind.Date, true, "2026-11-01."),
                F("frequency_type", "frequency_type", Kind.Frequency, true, "one_time, week, month or year."), F("frequency_number", "frequency_number", Kind.Number, false, "Every how many - 1 by default."),
                F("description", "description", Kind.Long, false, "About it."),
            }, item => Pick(item, ("title", "title"), ("description", "description"), ("frequency_type", "frequency_type"), ("frequency_number", "frequency_number"))
                .With("initial_date", Day(item["initial_date"])).With("contact_id", item["contact"]?["id"]?.DeepClone())),
            new("tag", "tags", "/tags", new[] { F("name", "name", Kind.Text, true, "stamkund.") }, item => Pick(item, ("name", "name"))),
            new("task", "tasks", "/tasks", new[]
            {
                Contact(), F("title", "title", Kind.Text, true, "Ring Ada."), F("description", "description", Kind.Long, false, "About it."),
                F("completed", "completed", Kind.Bool, false, "Done, true or false - false by default."),
            }, item => Pick(item, ("title", "title"), ("description", "description"), ("completed", "completed")).With("contact_id", item["contact"]?["id"]?.DeepClone())),
        };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "monicacrm";

        public string DisplayName => "Monica CRM";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API token (Settings - API - personal access token)" };

        public string EndpointExample => "https://monica.kafe.example";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.monicahq.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://www.monicahq.com/api");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField Parameter(Field field, bool required) => new(field.Name, Label(field.Name), field.Kind switch
        {
            Kind.Long => ConnectorConfigFieldType.Multiline,
            Kind.Bool => ConnectorConfigFieldType.Boolean,
            Kind.Id or Kind.Number => ConnectorConfigFieldType.Integer,
            _ => ConnectorConfigFieldType.Text,
        }, Required: required, Description: field.Description + (field.Kind == Kind.Long ? " Over several lines from the step's Query box." : string.Empty));

        private static string Label(string name) => char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' ');

        public IReadOnlyList<ConnectorOperation> Operations { get; } = BuildOperations();

        private static IReadOnlyList<ConnectorOperation> BuildOperations()
        {
            var operations = new List<ConnectorOperation>();
            foreach (var resource in Resources)
            {
                var label = resource.Name.Replace('_', ' ');
                var id = new ConnectorConfigField(resource.Name, Label(resource.Name), ConnectorConfigFieldType.Integer, Required: true, Description: "Its number.");
                if (resource.Listed)
                {
                    var listing = resource.ListBy is null
                        ? resource.Name == "contact"
                            ? new[] { new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Contacts whose names hold it."), new ConnectorConfigField("sort", "Sort", ConnectorConfigFieldType.Text, Required: false, Description: "created_at, -created_at, updated_at or -updated_at."), MaxField }
                            : new[] { MaxField }
                        : new[] { Parameter(Contact(), true), MaxField };
                    operations.Add(new ConnectorOperation(resource.Plural, $"Get {resource.Plural.Replace('_', ' ')}", $"n8n's {label} - Get Many.") { Parameters = new(listing) });
                }

                operations.Add(new ConnectorOperation(resource.Name, $"Get a {label}", $"n8n's {label} - Get.") { Parameters = new(new[] { id }) });
                operations.Add(new ConnectorOperation(resource.Name + "_create", $"Create a {label}", $"n8n's {label} - Create.") { Parameters = new(resource.Fields.Select(field => Parameter(field, field.Required)).ToArray()) });
                operations.Add(new ConnectorOperation(resource.Name + "_update", $"Update a {label}", $"n8n's {label} - Update; it is read and sent back whole with what is given.") { Parameters = new(new[] { id }.Concat(resource.Fields.Select(field => Parameter(field, false))).ToArray()) });
                operations.Add(new ConnectorOperation(resource.Name + "_delete", $"Delete a {label}", $"n8n's {label} - Delete.") { Parameters = new(new[] { id }) });
            }

            var conversation = new ConnectorConfigField("conversation", "Conversation", ConnectorConfigFieldType.Integer, Required: true, Description: "Its number.");
            var message = new[] { F("content", "content", Kind.Long, true, "What was written."), F("written_at", "written_at", Kind.Date, true, "2026-10-21."), F("written_by_me", "written_by_me", Kind.Bool, true, "Written by you, true or false.") };
            operations.Add(new ConnectorOperation("message_create", "Add a message", "n8n's Conversation Message - Add.") { Parameters = new(new[] { conversation }.Concat(message.Select(field => Parameter(field, field.Required))).ToArray()) });
            operations.Add(new ConnectorOperation("message_update", "Update a message", "n8n's Conversation Message - Update.") { Parameters = new(new[] { conversation, new ConnectorConfigField("message", "Message", ConnectorConfigFieldType.Integer, Required: true, Description: "Its number.") }.Concat(message.Select(field => Parameter(field, false))).ToArray()) });
            var tags = new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: true, Description: "By commas - stamkund, kaffe.");
            operations.Add(new ConnectorOperation("contact_tags_add", "Add tags to a contact", "n8n's Contact Tag - Add: a tag Monica does not have is made.") { Parameters = new(new[] { Parameter(Contact(), true), tags }) });
            operations.Add(new ConnectorOperation("contact_tags_remove", "Remove tags from a contact", "n8n's Contact Tag - Remove: by name or number.") { Parameters = new(new[] { Parameter(Contact(), true), tags }) });
            return operations;
        }

        private static readonly string[] Known = BuildOperations().Select(operation => operation.Name).ToArray();

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var me = (await session.SendAsync(HttpMethod.Get, "/me", null, cancellationToken))?["data"];
                return ConnectionTestResult.Ok($"Monica answers: the token is {Text(me?["name"]) ?? Text(me?["email"]) ?? "a user"}'s.");
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
            var name = (context.OperationName ?? "contacts").Trim().ToLowerInvariant();
            if (!Known.Contains(name))
            {
                throw new InvalidOperationException($"Monica has no operation '{context.OperationName}' - for each of {string.Join(", ", Resources.Select(resource => resource.Name))}: its list ({Resources[0].Plural}), get ({Resources[0].Name}), _create, _update and _delete; message_create and message_update; contact_tags_add and contact_tags_remove.");
            }

            var p = context.Parameters;
            var max = Count(p, "max", DefaultMax);

            if (name is "contact_tags_add" or "contact_tags_remove")
            {
                var contact = Id(p, "contact");
                var tags = (Values.Get(p, "tags") ?? throw new InvalidOperationException("Give the tags - by commas, stamkund, kaffe (tags).")).Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToArray();
                if (tags.Length == 0 || tags.Any(tag => tag.Length > 250 || tag.Any(char.IsControl)))
                {
                    throw new InvalidOperationException("Give the tags - by commas, each one line (tags).");
                }

                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                JsonNode? answer;
                if (name == "contact_tags_add")
                {
                    answer = await session.SendAsync(HttpMethod.Post, $"/contacts/{contact}/setTags", new JsonObject { ["tags"] = new JsonArray(tags.Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray()) }, cancellationToken);
                }
                else
                {
                    // Monica removes a tag by its number: a name is looked up among its tags.
                    var known = await AllAsync(session, "/tags", int.MaxValue, cancellationToken);
                    var ids = tags.Select(tag => long.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number
                        : known.FirstOrDefault(item => string.Equals(Text(item["name"]), tag, StringComparison.OrdinalIgnoreCase))?["id"]?.GetValue<long>() ?? throw new InvalidOperationException($"Monica has no tag '{tag}' (tags).")).ToArray();
                    answer = await session.SendAsync(HttpMethod.Post, $"/contacts/{contact}/unsetTag", new JsonObject { ["tags"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }, cancellationToken);
                }

                yield return Record(answer?["data"] as JsonObject ?? throw new InvalidOperationException("Monica answered without the contact - check the address."));
                yield break;
            }

            if (name.StartsWith("message_", StringComparison.Ordinal))
            {
                var conversation = Id(p, "conversation");
                var update = name == "message_update";
                var message = update ? Id(p, "message") : 0;
                var given = Gather(new[] { F("content", "content", Kind.Long, true, string.Empty), F("written_at", "written_at", Kind.Date, true, string.Empty), F("written_by_me", "written_by_me", Kind.Bool, true, string.Empty) }, p, create: !update);
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

                // A message is the conversation's contact's: read from the conversation, as n8n does.
                var held = await session.SendAsync(HttpMethod.Get, $"/conversations/{conversation}", null, cancellationToken);
                var body = new JsonObject { ["contact_id"] = held?["data"]?["contact"]?["id"]?.DeepClone() };
                if (update)
                {
                    var current = (held?["data"]?["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<long>() == message)
                        ?? throw new InvalidOperationException($"The conversation has no message {message} (message).");
                    body["content"] = current["content"]?.DeepClone();
                    body["written_at"] = Day(current["written_at"]);
                    body["written_by_me"] = current["written_by_me"]?.DeepClone();
                }

                await PutAsync(session, body, given, cancellationToken);

                var answer = await session.SendAsync(update ? HttpMethod.Put : HttpMethod.Post, update ? $"/conversations/{conversation}/messages/{message}" : $"/conversations/{conversation}/messages", body, cancellationToken);
                yield return Record(answer?["data"] as JsonObject ?? throw new InvalidOperationException("Monica answered without the conversation - check the address."));
                yield break;
            }

            // contact is a prefix of contact_field: the longest name that fits is the resource.
            var resource = Resources.Where(item => name == item.Plural || name == item.Name || name.StartsWith(item.Name + "_", StringComparison.Ordinal)).OrderByDescending(item => item.Name.Length).First();
            var verb = name == resource.Plural ? "list" : name == resource.Name ? "get" : name[(resource.Name.Length + 1)..];
            switch (verb)
            {
                case "list":
                    {
                        string route;
                        if (resource.ListBy is { } by)
                        {
                            route = string.Format(CultureInfo.InvariantCulture, by, Id(p, "contact")); // n8n's /contact/{id}/contactfields is no route of Monica's
                        }
                        else
                        {
                            var query = new List<string>();
                            if (resource.Name == "contact" && Values.Get(p, "query") is { } search)
                            {
                                query.Add("query=" + Uri.EscapeDataString(search.Length <= 200 && !search.Any(char.IsControl) ? search : throw new InvalidOperationException("A search is one line (query).")));
                            }

                            if (resource.Name == "contact" && Values.Get(p, "sort") is { } sort)
                            {
                                query.Add("sort=" + (sort is "created_at" or "-created_at" or "updated_at" or "-updated_at" ? sort : throw new InvalidOperationException($"'{sort}' is not created_at, -created_at, updated_at or -updated_at (sort).")));
                            }

                            route = resource.Route + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));
                        }

                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        foreach (var item in await AllAsync(session, route, max, cancellationToken))
                        {
                            yield return Record(item);
                        }

                        break;
                    }

                case "get":
                    {
                        var id = Id(p, resource.Name);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        yield return Record(await Data(session, HttpMethod.Get, $"{resource.Route}/{id}", null, cancellationToken));
                        break;
                    }

                case "create":
                    {
                        var given = Gather(resource.Fields, p, create: true);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var body = new JsonObject();
                        if (resource.Name == "contact")
                        {
                            body["is_birthdate_known"] = false;
                            body["is_deceased"] = false;
                            body["is_deceased_date_known"] = false;
                        }
                        else if (resource.Name == "reminder")
                        {
                            body["frequency_number"] = 1;
                        }
                        else if (resource.Name == "task")
                        {
                            body["completed"] = false;
                        }
                        else if (resource.Name == "tag")
                        {
                            // Monica makes a second tag under a name it has: refused here instead.
                            var named = (string)given.First(pair => pair.Key == "name").Value!;
                            if ((await AllAsync(session, "/tags", int.MaxValue, cancellationToken)).FirstOrDefault(item => string.Equals(Text(item["name"]), named, StringComparison.OrdinalIgnoreCase)) is { } taken)
                            {
                                throw new InvalidOperationException($"Monica already has a tag '{Text(taken["name"])}' ({taken["id"]}); it would make a second one (name).");
                            }
                        }

                        await PutAsync(session, body, given, cancellationToken);
                        yield return Record(await Data(session, HttpMethod.Post, resource.Route, body, cancellationToken));
                        break;
                    }

                case "update":
                    {
                        var id = Id(p, resource.Name);
                        var given = Gather(resource.Fields, p, create: false);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

                        // Monica replaces the item and refuses one missing what it requires: read, and sent back whole.
                        var body = resource.Back(await Data(session, HttpMethod.Get, $"{resource.Route}/{id}", null, cancellationToken));
                        await PutAsync(session, body, given, cancellationToken);
                        yield return Record(await Data(session, HttpMethod.Put, $"{resource.Route}/{id}", body, cancellationToken));
                        break;
                    }

                default:
                    {
                        var id = Id(p, resource.Name);
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var answer = await session.SendAsync(HttpMethod.Delete, $"{resource.Route}/{id}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = (long)id, ["deleted"] = answer?["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var done) && done, ["done"] = resource.Name + "_delete" });
                        break;
                    }
            }
        }

        // The given fields onto a body: a gender's name looked up, a birthdate or a deceased date as Monica's day, month and year.
        private static async Task PutAsync(Session session, JsonObject body, List<KeyValuePair<string, object?>> given, CancellationToken cancellationToken)
        {
            foreach (var (key, value) in given)
            {
                switch (key)
                {
                    case "gender_id" when value is string gender && !long.TryParse(gender, NumberStyles.None, CultureInfo.InvariantCulture, out _):
                        {
                            var genders = await AllAsync(session, "/genders", int.MaxValue, cancellationToken);
                            body[key] = genders.FirstOrDefault(item => string.Equals(Text(item["name"]), gender, StringComparison.OrdinalIgnoreCase))?["id"]?.DeepClone()
                                ?? throw new InvalidOperationException($"Monica has no gender '{gender}' - it has {string.Join(", ", genders.Select(item => Text(item["name"])))} (gender).");
                            break;
                        }

                    case "gender_id":
                        body[key] = long.Parse((string)value!, CultureInfo.InvariantCulture);
                        break;
                    case "birthdate":
                        {
                            var day = (DateOnly)value!;
                            body["is_birthdate_known"] = true;
                            body["birthdate_day"] = day.Day;
                            body["birthdate_month"] = day.Month;
                            body["birthdate_year"] = day.Year;
                            body["birthdate_is_age_based"] = false;
                            break;
                        }

                    case "deceased_date":
                        {
                            var day = (DateOnly)value!;
                            body["is_deceased"] = true;
                            body["is_deceased_date_known"] = true;
                            body["deceased_date_day"] = day.Day;
                            body["deceased_date_month"] = day.Month;
                            body["deceased_date_year"] = day.Year;
                            body["deceased_date_is_age_based"] = false;
                            break;
                        }

                    default:
                        body[key] = value switch
                        {
                            DateOnly day => JsonValue.Create(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                            long number => JsonValue.Create(number),
                            bool flag => JsonValue.Create(flag),
                            long[] ids => new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                            string text => JsonValue.Create(text),
                            _ => null,
                        };
                        break;
                }
            }
        }

        // What was given, held to its shape, by Monica's names - a required one missing refused on a create.
        private static List<KeyValuePair<string, object?>> Gather(Field[] fields, IReadOnlyDictionary<string, string> p, bool create)
        {
            var given = new List<KeyValuePair<string, object?>>();
            foreach (var field in fields)
            {
                var value = field.Kind == Kind.Long ? Values.Get(p, field.Name) ?? Values.Get(p, "query") : Values.Get(p, field.Name);
                if (value is null)
                {
                    if (create && field.Required)
                    {
                        throw new InvalidOperationException(field.Kind == Kind.Id
                            ? $"Give the {field.Name.Replace('_', ' ')} - its number, 42 ({field.Name})."
                            : $"Give the {field.Name.Replace('_', ' ')} - {field.Description} ({field.Name}).");
                    }

                    continue;
                }

                given.Add(new(field.Wire, field.Kind switch
                {
                    Kind.Text => value.Length <= 250 && !value.Any(char.IsControl) ? value : throw new InvalidOperationException($"A {field.Name.Replace('_', ' ')} is one line of at most 250 characters ({field.Name})."),
                    Kind.Long => value.Length <= MostText ? value : throw new InvalidOperationException($"A {field.Name.Replace('_', ' ')} is at most a megabyte ({field.Name})."),
                    Kind.Bool => bool.TryParse(value, out var flag) ? flag : throw new InvalidOperationException($"'{value}' is not true or false ({field.Name})."),
                    Kind.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : throw new InvalidOperationException($"'{value}' is not a date - 2026-10-21 ({field.Name})."),
                    Kind.Id => Id(p, field.Name),
                    Kind.Number => (long)Count(p, field.Name, 1),
                    Kind.Ids => value.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).Select(part => long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new InvalidOperationException($"'{value}' is not numbers by commas - 1, 4 ({field.Name}).")).ToArray() is { Length: > 0 } ids ? ids : throw new InvalidOperationException($"Give at least one number ({field.Name})."),
                    Kind.Frequency => Frequencies.Contains(value) ? value : throw new InvalidOperationException($"'{value}' is not one_time, week, month or year ({field.Name})."),
                    Kind.Gender => value.Length <= 100 && !value.Any(char.IsControl) ? value : throw new InvalidOperationException($"'{value}' is not a gender's name or number ({field.Name})."),
                    _ => value,
                }));
            }

            return given.Count > 0 || create ? given : throw new InvalidOperationException("Give something to change.");
        }

        private static async Task<JsonObject> Data(Session session, HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken) =>
            (await session.SendAsync(method, route, body, cancellationToken))?["data"] as JsonObject
                ?? throw new InvalidOperationException("Monica answered without the item - check the address: a sign-in page or a proxy answers so.");

        // A list, page by page as Monica's meta says, up to max.
        private static async Task<List<JsonObject>> AllAsync(Session session, string route, int max, CancellationToken cancellationToken)
        {
            var found = new List<JsonObject>();
            for (var page = 1; found.Count < max; page++)
            {
                var answer = await session.SendAsync(HttpMethod.Get, $"{route}{(route.Contains('?') ? "&" : "?")}limit={PageSize}&page={page}", null, cancellationToken);
                var items = answer?["data"] as JsonArray ?? throw new InvalidOperationException("Monica answered with what is not a list - check the address: a sign-in page or a proxy answers so.");
                found.AddRange(items.OfType<JsonObject>().Take(max - found.Count));
                var current = answer["meta"]?["current_page"]?.GetValue<int>() ?? page;
                var last = answer["meta"]?["last_page"]?.GetValue<int>() ?? page;
                if (current >= last || items.Count == 0)
                {
                    break;
                }
            }

            return found;
        }

        private static JsonObject BackContact(JsonObject item)
        {
            var body = Pick(item, ("first_name", "first_name"), ("last_name", "last_name"), ("nickname", "nickname"), ("description", "description"), ("is_partial", "is_partial"));
            body["is_deceased"] = item["is_dead"]?.DeepClone() ?? false;
            foreach (var (date, prefix, known) in new[] { ("birthdate", "birthdate", "is_birthdate_known"), ("deceased_date", "deceased_date", "is_deceased_date_known") })
            {
                var held = item["information"]?["dates"]?[date];
                if (Text(held?["date"]) is { Length: >= 10 } text && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    body[known] = true;
                    body[prefix + "_day"] = day.Day;
                    body[prefix + "_month"] = day.Month;
                    if (held?["is_year_unknown"] is not JsonValue unknown || !unknown.TryGetValue<bool>(out var noYear) || !noYear)
                    {
                        body[prefix + "_year"] = day.Year;
                    }

                    body[prefix + "_is_age_based"] = held?["is_age_based"] is JsonValue aged && aged.TryGetValue<bool>(out var age) && age;
                }
                else
                {
                    body[known] = false;
                }
            }

            return body;
        }

        private static JsonObject Pick(JsonObject item, params (string From, string To)[] names)
        {
            var body = new JsonObject();
            foreach (var (from, to) in names)
            {
                body[to] = item[from]?.DeepClone();
            }

            return body;
        }

        private static JsonNode? Day(JsonNode? node) => Text(node) is { Length: >= 10 } text ? JsonValue.Create(text[..10]) : null;

        private static long Id(IReadOnlyDictionary<string, string> p, string name)
        {
            var given = Values.Get(p, name) ?? throw new InvalidOperationException($"Give the {name.Replace('_', ' ')} - its number, 42 ({name}).");
            return long.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new InvalidOperationException($"'{given}' is not a {name.Replace('_', ' ')}'s number - 42 ({name}).");
        }

        private static int Count(IReadOnlyDictionary<string, string> p, string name, int fallback) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{given}' is not a number above 0 ({name}).")
                : fallback;

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
                    throw new InvalidOperationException("Give Monica's address - https://monica.kafe.example, or https://app.monicahq.com.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    root = root[..^"/api".Length]; // the API's own address given: Monica's
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Monica's address - https://monica.kafe.example, with no query.");
                }

                var token = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Monica connection has no API token.");
                return new Session(egress.CreateHttpClient(timeout), root + "/api", token);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with the token as a bearer; Monica's JSON, or its status and messages.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
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
                    return json ?? throw new InvalidOperationException("Monica answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }

                // Monica's messages: {"error": {"message": [..] or "..."}}, or Laravel's {"message": "..."}.
                var said = json?["error"]?["message"] is JsonArray list ? string.Join(" ", list.Select(item => Text(item)))
                    : Text(json?["error"]?["message"]) ?? Text(json?["message"]) ?? string.Empty;
                said = said.Length > 300 ? said[..300] : said;
                var status = (int)response.StatusCode;
                throw new InvalidOperationException($"Monica answered HTTP {status}{(said.Length == 0 ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + (status == 401 ? " - check the API token." : string.Empty));
            }
        }
    }
}
