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

namespace Stomme.Modules.Connectors.HighLevel
{
    /// <summary>
    /// HighLevel (BL-229, n8n's HighLevel node, its second version: contacts, opportunities, tasks and a calendar's
    /// appointments) over HighLevel's API v2 (<c>https://services.leadconnectorhq.com</c>, its <c>Version: 2021-07-28</c>) by a
    /// sub-account's Private Integration token, its location the connection's. A test lists a contact of the location.
    /// </summary>
    /// <remarks>
    /// Contacts: <c>contacts</c> (by a search, newest or oldest first, by when added or updated, page by page by HighLevel's
    /// startAfter up to <c>max</c>), <c>contact</c>, <c>contact_upsert</c> (n8n's Create or Update: by its email or phone, with its
    /// names, address, site, time zone, Do Not Disturb, source, tags and custom fields, a note added after), <c>contact_update</c>
    /// and <c>contact_delete</c>. Opportunities: <c>opportunities</c> (by a search, pipeline, stage, status, owner, campaign and
    /// date), <c>opportunity</c>, <c>opportunity_create</c>, <c>opportunity_update</c>, <c>opportunity_delete</c>. A contact's
    /// tasks: <c>tasks</c>, <c>task</c>, <c>task_create</c>, <c>task_update</c> (its title and due date read first when not given,
    /// as HighLevel needs both, as n8n's node does), <c>task_delete</c>. A calendar's <c>free_slots</c> (each free slot a record)
    /// and <c>appointment_book</c>. Every id, address, number, time and choice held to its shape before anything is sent;
    /// HighLevel's status and message on a failure.
    /// </remarks>
    public sealed class HighLevelConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://services.leadconnectorhq.com";

        public const int DefaultMax = 100;

        private const string ApiVersion = "2021-07-28";

        private const int PageSize = 100; // HighLevel's most a page

        private static readonly string[] Known =
        {
            "contacts", "contact", "contact_upsert", "contact_update", "contact_delete",
            "opportunities", "opportunity", "opportunity_create", "opportunity_update", "opportunity_delete",
            "tasks", "task", "task_create", "task_update", "task_delete",
            "free_slots", "appointment_book",
        };

        private static readonly string[] Statuses = { "open", "won", "lost", "abandoned" };

        private static readonly string[] AppointmentStatuses = { "new", "confirmed", "cancelled", "showed", "noshow", "invalid" };

        private static readonly Regex Id = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s]{1,64}@[^@\\s]+\\.[^@\\s]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Phone = new("^\\+?[0-9][0-9 ().-]{4,24}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "highlevel";

        public string DisplayName => "HighLevel";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["token"] = "Private Integration token (the sub-account's Settings > Private Integrations)",
        };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm, ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.gohighlevel.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://highlevel.stoplight.io/docs/integrations/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("location", "Location", ConnectorConfigFieldType.Text, Required: true, Description: "The sub-account's location id - Settings > Business Profile."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField ContactField = Field("contact", "Contact", "Its id.", required: true);

        private static readonly ConnectorConfigField OpportunityField = Field("opportunity", "Opportunity", "Its id.", required: true);

        private static readonly ConnectorConfigField TaskField = Field("task", "Task", "Its id.", required: true);

        private static readonly ConnectorConfigField[] ContactFields =
        {
            Field("email", "Email", "ada@kafe.example."),
            Field("phone", "Phone", "+46 70 174 06 05."),
            Field("first_name", "First name", "Ada."),
            Field("last_name", "Last name", "Lovelace."),
            Field("name", "Name", "Its whole name, Ada Lovelace."),
            Field("address1", "Address", "Kassagatan 1."),
            Field("city", "City", "Göteborg."),
            Field("state", "State", "Västra Götaland."),
            Field("postal_code", "Postal code", "411 01."),
            Field("website", "Website", "https://kafe.example."),
            Field("timezone", "Time zone", "Europe/Stockholm."),
            Field("dnd", "Do Not Disturb", "true or false."),
            Field("tags", "Tags", "By commas - stamkund, kassa."),
            Field("custom_fields", "Custom fields", "A JSON object of their ids and values - {\"6dvNaf7VhkQ9snc5vnjJ\": \"guld\"}."),
        };

        private static readonly ConnectorConfigField[] OpportunityFields =
        {
            Field("pipeline", "Pipeline", "Its id."),
            Field("stage", "Stage", "The pipeline's stage - its id."),
            Field("name", "Name", "Kassan, nytt avtal."),
            Field("status", "Status", "open (the default), won, lost or abandoned."),
            Field("monetary_value", "Value", "1250.50."),
            Field("assigned_to", "Assigned to", "A user's id."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("contacts", "Get contacts", "n8n's Contact Get Many.")
            {
                Parameters = new(new[] { Field("query", "Search", "Its name, email, phone or company - ada."), Field("order", "Order", "desc (the default) or asc."), Field("sort_by", "Sort by", "date_added (the default) or date_updated."), MaxField }),
            },
            new ConnectorOperation("contact", "Get a contact", "n8n's Contact Get.") { Parameters = new(new[] { ContactField }) },
            new ConnectorOperation("contact_upsert", "Create or update a contact", "n8n's Contact Create or Update - by its email or phone.")
            {
                Parameters = new(ContactFields.Concat(new[] { Field("source", "Source", "Where it came from - Kassan."), Field("note", "Note", "A note added to it after.") }).ToArray()),
            },
            new ConnectorOperation("contact_update", "Update a contact", "n8n's Contact Update.") { Parameters = new(new[] { ContactField }.Concat(ContactFields).ToArray()) },
            new ConnectorOperation("contact_delete", "Delete a contact", "n8n's Contact Delete.") { Parameters = new(new[] { ContactField }) },
            new ConnectorOperation("opportunities", "Get opportunities", "n8n's Opportunity Get Many.")
            {
                Parameters = new(new[]
                {
                    Field("query", "Search", "Its name, the contact's name, email, phone, tags or company."),
                    Field("pipeline", "Pipeline", "Its id."),
                    Field("stage", "Stage", "Its id."),
                    Field("status", "Status", "open, won, lost, abandoned or all."),
                    Field("assigned_to", "Assigned to", "A user's id."),
                    Field("campaign", "Campaign", "Its id."),
                    Field("start", "From", "Created on or after - 2026-10-01."),
                    Field("end", "To", "Created on or before - 2026-10-31."),
                    MaxField,
                }),
            },
            new ConnectorOperation("opportunity", "Get an opportunity", "n8n's Opportunity Get.") { Parameters = new(new[] { OpportunityField }) },
            new ConnectorOperation("opportunity_create", "Create an opportunity", "n8n's Opportunity Create.")
            {
                Parameters = new(new[] { Field("contact", "Contact", "Its id.", required: true) }.Concat(OpportunityFields).Concat(new[] { Field("company_name", "Company", "Kafé Kassan AB."), Field("tags", "Tags", "By commas.") }).ToArray()),
            },
            new ConnectorOperation("opportunity_update", "Update an opportunity", "n8n's Opportunity Update.") { Parameters = new(new[] { OpportunityField }.Concat(OpportunityFields).ToArray()) },
            new ConnectorOperation("opportunity_delete", "Delete an opportunity", "n8n's Opportunity Delete.") { Parameters = new(new[] { OpportunityField }) },
            new ConnectorOperation("tasks", "Get a contact's tasks", "n8n's Task Get Many.") { Parameters = new(new[] { ContactField, MaxField }) },
            new ConnectorOperation("task", "Get a task", "n8n's Task Get.") { Parameters = new(new[] { ContactField, TaskField }) },
            new ConnectorOperation("task_create", "Create a task", "n8n's Task Create.")
            {
                Parameters = new(new[] { ContactField, Field("title", "Title", "Ring tillbaka.", required: true), Field("due", "Due", "2026-10-20T09:00:00+02:00.", required: true), Field("body", "Body", "What it is about."), Field("completed", "Completed", "true or false (the default)."), Field("assigned_to", "Assigned to", "A user's id.") }),
            },
            new ConnectorOperation("task_update", "Update a task", "n8n's Task Update.")
            {
                Parameters = new(new[] { ContactField, TaskField, Field("title", "Title", "Its new title."), Field("due", "Due", "Its new due time."), Field("body", "Body", "Its new body."), Field("completed", "Completed", "true or false."), Field("assigned_to", "Assigned to", "A user's id.") }),
            },
            new ConnectorOperation("task_delete", "Delete a task", "n8n's Task Delete.") { Parameters = new(new[] { ContactField, TaskField }) },
            new ConnectorOperation("free_slots", "Get free slots", "n8n's Calendar Get Free Slots - each a record.")
            {
                Parameters = new(new[] { Field("calendar", "Calendar", "Its id.", required: true), Field("start", "From", "2026-10-20 or milliseconds since 1970.", required: true), Field("end", "To", "2026-10-27 or milliseconds since 1970.", required: true), Field("timezone", "Time zone", "Europe/Stockholm."), Field("user", "User", "A user's id - theirs alone."), Field("users", "Users", "Users' ids, by commas.") }),
            },
            new ConnectorOperation("appointment_book", "Book an appointment", "n8n's Calendar Book Appointment.")
            {
                Parameters = new(new[]
                {
                    Field("calendar", "Calendar", "Its id.", required: true),
                    Field("contact", "Contact", "Its id.", required: true),
                    Field("start", "Starts", "2026-10-20T09:00:00+02:00.", required: true),
                    Field("end", "Ends", "2026-10-20T09:30:00+02:00. Empty: the calendar's slot."),
                    Field("title", "Title", "Kassagenomgång."),
                    Field("appointment_status", "Status", "new (the default), confirmed, cancelled, showed, noshow or invalid."),
                    Field("assigned_user", "Assigned to", "A user's id."),
                    Field("address", "Address", "Where, or a meeting link."),
                    Field("ignore_date_range", "Ignore the date range", "true: book outside the calendar's bookable range."),
                    Field("notify", "Notify", "true (the default) or false."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.SendAsync(HttpMethod.Get, "/contacts/", new() { ["locationId"] = session.Location, ["limit"] = "1" }, null, cancellationToken);
                var total = answer?["meta"]?["total"] is JsonValue count && count.TryGetValue<long>(out var all) ? all : (answer?["contacts"] as JsonArray)?.Count ?? 0;
                return ConnectionTestResult.Ok($"HighLevel answers - location {session.Location}, {total} contacts.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "contacts").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"HighLevel has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            var body = new JsonObject();
            string route;
            HttpMethod method;
            string? note = null;
            switch (operation)
            {
                case "contacts":
                    route = "/contacts/";
                    method = HttpMethod.Get;
                    Optional(p, "query", query, "query");
                    query["order"] = Choice(p, "order", "desc", "desc", "asc");
                    query["sortBy"] = Choice(p, "sort_by", "date_added", "date_added", "date_updated");
                    break;
                case "contact" or "contact_delete":
                    route = $"/contacts/{IdOf(p, "contact")}";
                    method = operation == "contact" ? HttpMethod.Get : HttpMethod.Delete;
                    break;
                case "contact_upsert" or "contact_update":
                    {
                        route = operation == "contact_upsert" ? "/contacts/upsert" : $"/contacts/{IdOf(p, "contact")}";
                        method = operation == "contact_upsert" ? HttpMethod.Post : HttpMethod.Put;
                        ContactBody(p, body, upsert: operation == "contact_upsert");
                        note = operation == "contact_upsert" ? Values.Get(p, "note") : null;
                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - email, phone, name, tags or another of its fields.");
                        }

                        break;
                    }

                case "opportunities":
                    route = "/opportunities/search";
                    method = HttpMethod.Get;
                    Optional(p, "query", query, "q");
                    foreach (var (name, key) in new[] { ("pipeline", "pipeline_id"), ("stage", "pipeline_stage_id"), ("assigned_to", "assigned_to"), ("campaign", "campaignId") })
                    {
                        if (Values.Get(p, name) is not null)
                        {
                            query[key] = IdOf(p, name);
                        }
                    }

                    if (Values.Get(p, "status") is not null)
                    {
                        query["status"] = Choice(p, "status", "all", Statuses.Append("all").ToArray());
                    }

                    if (Values.Get(p, "start") is { } from)
                    {
                        query["date"] = Day(from, "start");
                    }

                    if (Values.Get(p, "end") is { } to)
                    {
                        query["endDate"] = Day(to, "end");
                    }

                    break;
                case "opportunity" or "opportunity_delete":
                    route = $"/opportunities/{IdOf(p, "opportunity")}";
                    method = operation == "opportunity" ? HttpMethod.Get : HttpMethod.Delete;
                    break;
                case "opportunity_create" or "opportunity_update":
                    {
                        var create = operation == "opportunity_create";
                        route = create ? "/opportunities/" : $"/opportunities/{IdOf(p, "opportunity")}";
                        method = create ? HttpMethod.Post : HttpMethod.Put;
                        if (create)
                        {
                            body["contactId"] = IdOf(p, "contact");
                            body["name"] = Values.Get(p, "name") ?? throw new InvalidOperationException("Give the opportunity's name (name).");
                            body["pipelineId"] = Values.Get(p, "pipeline") is null ? throw new InvalidOperationException("Give the pipeline it is in - its id (pipeline).") : IdOf(p, "pipeline");
                            body["status"] = Choice(p, "status", "open", Statuses);
                        }
                        else
                        {
                            if (Values.Get(p, "name") is { } name)
                            {
                                body["name"] = name;
                            }

                            if (Values.Get(p, "pipeline") is not null)
                            {
                                body["pipelineId"] = IdOf(p, "pipeline");
                            }

                            if (Values.Get(p, "status") is not null)
                            {
                                body["status"] = Choice(p, "status", "open", Statuses);
                            }
                        }

                        if (Values.Get(p, "stage") is not null)
                        {
                            body["pipelineStageId"] = IdOf(p, "stage");
                        }

                        if (Values.Get(p, "monetary_value") is { } value)
                        {
                            body["monetaryValue"] = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var money) && money >= 0 ? money : throw new InvalidOperationException($"'{value}' is not an amount - 1250.50 (monetary_value).");
                        }

                        if (Values.Get(p, "assigned_to") is not null)
                        {
                            body["assignedTo"] = IdOf(p, "assigned_to");
                        }

                        if (create && Values.Get(p, "company_name") is { } company)
                        {
                            body["companyName"] = company;
                        }

                        if (create && Tags(p) is { } tags)
                        {
                            body["tags"] = tags;
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - name, status, stage, value or who it is assigned to.");
                        }

                        break;
                    }

                case "tasks":
                    route = $"/contacts/{IdOf(p, "contact")}/tasks";
                    method = HttpMethod.Get;
                    break;
                case "task" or "task_delete":
                    route = $"/contacts/{IdOf(p, "contact")}/tasks/{IdOf(p, "task")}";
                    method = operation == "task" ? HttpMethod.Get : HttpMethod.Delete;
                    break;
                case "task_create" or "task_update":
                    {
                        var create = operation == "task_create";
                        route = create ? $"/contacts/{IdOf(p, "contact")}/tasks" : $"/contacts/{IdOf(p, "contact")}/tasks/{IdOf(p, "task")}";
                        method = create ? HttpMethod.Post : HttpMethod.Put;
                        if (Values.Get(p, "title") is { } title)
                        {
                            body["title"] = title;
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give the task's title (title).");
                        }

                        if (Values.Get(p, "due") is { } due)
                        {
                            body["dueDate"] = Instant(due, "due");
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give when the task is due - 2026-10-20T09:00:00+02:00 (due).");
                        }

                        if (Values.Get(p, "body") is { } text)
                        {
                            body["body"] = text;
                        }

                        if (Bool(p, "completed") is { } completed)
                        {
                            body["completed"] = completed;
                        }
                        else if (create)
                        {
                            body["completed"] = false;
                        }

                        if (Values.Get(p, "assigned_to") is not null)
                        {
                            body["assignedTo"] = IdOf(p, "assigned_to");
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - title, due, body, completed or who it is assigned to.");
                        }

                        break;
                    }

                case "free_slots":
                    {
                        route = $"/calendars/{IdOf(p, "calendar")}/free-slots";
                        method = HttpMethod.Get;
                        var start = Milliseconds(Values.Get(p, "start") ?? throw new InvalidOperationException("Give the first day - 2026-10-20 (start)."), "start");
                        var end = Milliseconds(Values.Get(p, "end") ?? throw new InvalidOperationException("Give the last day - 2026-10-27 (end)."), "end");
                        if (end < start)
                        {
                            throw new InvalidOperationException("The end is before the start (start, end).");
                        }

                        query["startDate"] = start.ToString(CultureInfo.InvariantCulture);
                        query["endDate"] = end.ToString(CultureInfo.InvariantCulture);
                        Optional(p, "timezone", query, "timezone");
                        if (Values.Get(p, "user") is not null)
                        {
                            query["userId"] = IdOf(p, "user");
                        }

                        if (Values.Get(p, "users") is { } users)
                        {
                            query["userIds"] = string.Join(",", users.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(user => Id.IsMatch(user) ? user : throw new InvalidOperationException($"'{user}' is not an id (users).")));
                        }

                        break;
                    }

                default: // appointment_book
                    {
                        route = "/calendars/events/appointments";
                        method = HttpMethod.Post;
                        body["calendarId"] = IdOf(p, "calendar");
                        body["contactId"] = IdOf(p, "contact");
                        body["startTime"] = Instant(Values.Get(p, "start") ?? throw new InvalidOperationException("Give when it starts - 2026-10-20T09:00:00+02:00 (start)."), "start");
                        if (Values.Get(p, "end") is { } end)
                        {
                            body["endTime"] = Instant(end, "end");
                        }

                        foreach (var (name, key) in new[] { ("title", "title"), ("address", "address") })
                        {
                            if (Values.Get(p, name) is { } given)
                            {
                                body[key] = given;
                            }
                        }

                        if (Values.Get(p, "appointment_status") is not null)
                        {
                            body["appointmentStatus"] = Choice(p, "appointment_status", "new", AppointmentStatuses);
                        }

                        if (Values.Get(p, "assigned_user") is not null)
                        {
                            body["assignedUserId"] = IdOf(p, "assigned_user");
                        }

                        if (Bool(p, "ignore_date_range") is { } ignore)
                        {
                            body["ignoreDateRange"] = ignore;
                        }

                        if (Bool(p, "notify") is { } notify)
                        {
                            body["toNotify"] = notify;
                        }

                        break;
                    }
            }

            using var session = Session.Open(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "contacts" or "opportunities":
                    {
                        var list = operation;
                        query[operation == "contacts" ? "locationId" : "location_id"] = session.Location;
                        var read = 0;
                        while (true)
                        {
                            query["limit"] = Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture);
                            var answer = await session.SendAsync(method, route, query, null, cancellationToken);
                            var found = (answer?[list] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in found)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            var meta = answer?["meta"];
                            var total = meta?["total"] is JsonValue count && count.TryGetValue<long>(out var all) ? all : 0;
                            if (found.Count == 0 || read >= total || Text(meta?["startAfterId"]) is not { Length: > 0 } afterId)
                            {
                                yield break;
                            }

                            query["startAfterId"] = afterId;
                            query["startAfter"] = meta?["startAfter"] is JsonValue after ? after.ToJsonString().Trim('"') : string.Empty;
                        }
                    }

                case "contact" or "contact_upsert" or "contact_update":
                    {
                        if (operation == "contact_upsert")
                        {
                            body["locationId"] = session.Location;
                        }

                        var answer = await session.SendAsync(method, route, query, operation == "contact" ? null : body, cancellationToken);
                        var contact = answer?["contact"] as JsonObject ?? throw new InvalidOperationException("HighLevel answered with no contact - check the address.");
                        var fields = Record(contact).Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        if (operation == "contact_upsert")
                        {
                            fields["new"] = answer?["new"] is JsonValue made && made.TryGetValue<bool>(out var isNew) ? isNew : null;
                            if (note is not null && Text(contact["id"]) is { } contactId)
                            {
                                var added = await session.SendAsync(HttpMethod.Post, $"/contacts/{contactId}/notes", new(), new JsonObject { ["body"] = note }, cancellationToken);
                                fields["note_id"] = Text(added?["note"]?["id"]);
                            }
                        }

                        yield return new ConnectorRecord(fields);
                        break;
                    }

                case "opportunity" or "opportunity_create" or "opportunity_update":
                    {
                        if (operation == "opportunity_create")
                        {
                            body["locationId"] = session.Location;
                        }

                        var answer = await session.SendAsync(method, route, query, operation == "opportunity" ? null : body, cancellationToken);
                        yield return Record(answer?["opportunity"] as JsonObject ?? throw new InvalidOperationException("HighLevel answered with no opportunity - check the address."));
                        break;
                    }

                case "contact_delete" or "opportunity_delete" or "task_delete":
                    await session.SendAsync(method, route, query, null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [operation[..operation.IndexOf('_', StringComparison.Ordinal)]] = route[(route.LastIndexOf('/') + 1)..],
                        ["deleted"] = true,
                    });
                    break;
                case "tasks":
                    {
                        var answer = await session.SendAsync(method, route, query, null, cancellationToken);
                        foreach (var task in (answer?["tasks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                        {
                            yield return Record(task);
                        }

                        break;
                    }

                case "task" or "task_create" or "task_update":
                    {
                        if (operation == "task_update" && (!body.ContainsKey("title") || !body.ContainsKey("dueDate")))
                        {
                            // HighLevel needs both on an update: what is not given is read from the task, as n8n's node does.
                            var current = (await session.SendAsync(HttpMethod.Get, route, query, null, cancellationToken))?["task"];
                            if (!body.ContainsKey("title"))
                            {
                                body["title"] = Text(current?["title"]);
                            }

                            if (!body.ContainsKey("dueDate"))
                            {
                                body["dueDate"] = Text(current?["dueDate"]);
                            }
                        }

                        var answer = await session.SendAsync(method, route, query, operation == "task" ? null : body, cancellationToken);
                        yield return Record(answer?["task"] as JsonObject ?? throw new InvalidOperationException("HighLevel answered with no task - check the address."));
                        break;
                    }

                case "free_slots":
                    {
                        // Each day's slots under the day; the most counted across the days.
                        var answer = await session.SendAsync(method, route, query, null, cancellationToken) as JsonObject ?? new JsonObject();
                        var slots = answer.Where(pair => pair.Value is JsonObject).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .SelectMany(pair => (pair.Value?["slots"] as JsonArray ?? new JsonArray()).Select(slot => (Day: pair.Key, Slot: Text(slot))));
                        foreach (var (day, slot) in slots.Take(max))
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["date"] = day, ["slot"] = slot });
                        }

                        break;
                    }

                default:
                    {
                        body["locationId"] = session.Location;
                        var answer = await session.SendAsync(method, route, query, body, cancellationToken) as JsonObject;
                        yield return Record(answer?["appointment"] as JsonObject ?? answer ?? throw new InvalidOperationException("HighLevel answered with no appointment - check the address."));
                        break;
                    }
            }
        }

        // A contact's fields: n8n's names as HighLevel's, its tags a list, its custom fields as HighLevel's id and value pairs.
        private static void ContactBody(IReadOnlyDictionary<string, string> p, JsonObject body, bool upsert)
        {
            if (Values.Get(p, "email") is { } email)
            {
                body["email"] = Email.IsMatch(email) ? email : throw new InvalidOperationException($"'{email}' is not an email address (email).");
            }

            if (Values.Get(p, "phone") is { } phone)
            {
                body["phone"] = Phone.IsMatch(phone) ? phone : throw new InvalidOperationException($"'{phone}' is not a phone number - +46 70 174 06 05 (phone).");
            }

            if (upsert && !body.ContainsKey("email") && !body.ContainsKey("phone"))
            {
                throw new InvalidOperationException("Give the contact's email or phone - HighLevel finds or makes it by them (email, phone).");
            }

            foreach (var (name, key) in new[] { ("first_name", "firstName"), ("last_name", "lastName"), ("name", "name"), ("address1", "address1"), ("city", "city"), ("state", "state"), ("postal_code", "postalCode"), ("website", "website"), ("timezone", "timezone") })
            {
                if (Values.Get(p, name) is { } given)
                {
                    body[key] = given;
                }
            }

            if (upsert && Values.Get(p, "source") is { } source)
            {
                body["source"] = source;
            }

            if (Bool(p, "dnd") is { } dnd)
            {
                body["dnd"] = dnd;
            }

            if (Tags(p) is { } tags)
            {
                body["tags"] = tags;
            }

            if (Values.Get(p, "custom_fields") is { } custom)
            {
                JsonObject? fields;
                try
                {
                    fields = JsonNode.Parse(custom) as JsonObject;
                }
                catch (JsonException)
                {
                    fields = null;
                }

                if (fields is not { Count: > 0 } || fields.Any(pair => !Id.IsMatch(pair.Key)))
                {
                    throw new InvalidOperationException("The custom fields are a JSON object of their ids and values - {\"6dvNaf7VhkQ9snc5vnjJ\": \"guld\"} (custom_fields).");
                }

                body["customFields"] = new JsonArray(fields.Select(pair => (JsonNode?)new JsonObject { ["id"] = pair.Key, ["field_value"] = pair.Value?.DeepClone() }).ToArray());
            }
        }

        private static JsonArray? Tags(IReadOnlyDictionary<string, string> p) =>
            Values.Get(p, "tags") is { } given ? new JsonArray(given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Select(tag => (JsonNode?)tag).ToArray()) : null;

        private static void Optional(IReadOnlyDictionary<string, string> p, string name, Dictionary<string, string> query, string key)
        {
            if (Values.Get(p, name) is { } given)
            {
                query[key] = given;
            }
        }

        private static string IdOf(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given
                ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id ({name}).")
                : throw new InvalidOperationException($"Give the {name.Replace('_', ' ')}'s id ({name}).");

        private static string Choice(IReadOnlyDictionary<string, string> p, string name, string fallback, params string[] known) =>
            (Values.Get(p, name) ?? fallback).ToLowerInvariant() is var given && known.Contains(given) ? given : throw new InvalidOperationException($"'{Values.Get(p, name)}' is not {string.Join(", ", known[..^1])} or {known[^1]} ({name}).");

        private static bool? Bool(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? bool.TryParse(given, out var flag) ? flag : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

        // A moment as ISO 8601 with its offset, without fractions of a second - as HighLevel takes it.
        private static string Instant(string given, string name) =>
            DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-20T09:00:00+02:00 ({name}).");

        private static string Day(string given, string name) =>
            DateOnly.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a day - 2026-10-01 ({name}).");

        // A day or a moment as milliseconds since 1970 - or milliseconds given as they are.
        private static long Milliseconds(string given, string name) =>
            long.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && given.Length >= 10 ? ms
                : DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds()
                : throw new InvalidOperationException($"'{given}' is not a day, a time or milliseconds since 1970 - 2026-10-20 ({name}).");

        // An object as a record: its plain values as they are, a list or an object as JSON.
        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(),
        }, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string token;

            private Session(HttpClient client, string api, string token, string location)
            {
                this.client = client;
                this.api = api;
                this.token = token;
                Location = location;
            }

            public string Location { get; }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not HighLevel's address - its own, {DefaultEndpoint}.");
                }

                var location = Values.Get(settings, "location") ?? throw new InvalidOperationException("Give the sub-account's location id (location) - Settings > Business Profile.");
                if (!Id.IsMatch(location))
                {
                    throw new InvalidOperationException($"'{location}' is not a location's id (location).");
                }

                var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The HighLevel connection has no Private Integration token.");
                return new Session(egress.CreateHttpClient(timeout), root, token, location);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call's JSON, or HighLevel's status and message.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, Dictionary<string, string> query, JsonObject? body, CancellationToken cancellationToken)
            {
                var address = api + route + (query.Count > 0 ? "?" + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))) : string.Empty);
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, address);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Add("Version", ApiVersion);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
                    throw new InvalidOperationException("HighLevel answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json is JsonObject ? json : throw new InvalidOperationException("HighLevel answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                }

                // HighLevel says it as a message - text, or a list of them - with its status code.
                var said = json?["message"] switch
                {
                    JsonValue value when value.TryGetValue<string>(out var text) => text,
                    JsonArray list => string.Join("; ", list.Select(item => item is JsonValue entry && entry.TryGetValue<string>(out var text) ? text : item?.ToJsonString())),
                    _ => Text(json?["error"]),
                };
                var status = (int)response.StatusCode;
                throw new InvalidOperationException($"HighLevel answered HTTP {status}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}"
                    + (status is 401 or 403 ? " - check the Private Integration token, its scopes and the location." : string.Empty));
            }
        }
    }
}
