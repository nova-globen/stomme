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

namespace Stomme.Modules.Connectors.Wekan
{
    /// <summary>
    /// Wekan (n8n's Wekan node: boards, lists, cards, comments, checklists and their items) over Wekan's REST API
    /// (<c>{address}/api</c>), signed in as n8n's credential does - a user's name or e-mail and password exchanged for a token
    /// (<c>POST /users/login</c>), sent as a bearer token - and signed out again when the run ends, so no token is left behind. A
    /// test names the user the connection signs in as.
    /// </summary>
    /// <remarks>
    /// Boards listed (a user's, the connection's own by default), read, made and deleted; a board's lists and swimlanes; lists
    /// read, made and deleted; cards listed from a list or a swimlane, read, made (in the board's first swimlane unless one is
    /// named), changed - moved to another list or swimlane of the board too - and deleted; a card's comments listed, read, added
    /// and deleted; checklists listed, read, made with their items and deleted; an item read, changed (its title, finished or
    /// not) and deleted. Measured against Wekan 12.45 (<c>scripts/wekan-test-server.ps1</c>), which says nothing of a wrong id
    /// where n8n's node passed it on: a card made in a list or a swimlane the board does not have is answered 200 and lost to
    /// the board's view, a card or a comment that is not there is answered 200 with nothing, and a colour or a permission it does
    /// not know is a 500. So every id is held to Wekan's shape, a card's list and swimlane are checked against the board before
    /// the card is made, a colour against Wekan's own lists, and nothing found is said. Above all, a comment is added only to a
    /// card checked to be on its board first: Wekan 12 DISABLES the signing-in user's account, and every token it has, at a
    /// comment sent to a card that is not on the board (its CommentBoundaryBleed, measured). A comment's author and a card's
    /// are the connection's user - Wekan takes them from the token, whatever n8n's node sent.
    /// </remarks>
    public sealed class WekanConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 100; // n8n's limit

        private const int MostText = 1048576; // a description or a comment: a megabyte is far beyond any

        private static readonly string[] Known =
        {
            "boards", "board", "board_create", "board_delete", "swimlanes",
            "lists", "list", "list_create", "list_delete",
            "cards", "card", "card_create", "card_update", "card_delete",
            "comments", "comment", "comment_create", "comment_delete",
            "checklists", "checklist", "checklist_create", "checklist_delete",
            "checklist_item", "checklist_item_update", "checklist_item_delete",
        };

        // Wekan 12's own lists (its board and card schemas); anything else is its 500.
        private static readonly string[] BoardColours =
        {
            "belize", "nephritis", "pomegranate", "pumpkin", "wisteria", "moderatepink", "strongcyan", "limegreen", "midnight", "dark",
            "relax", "corteza", "appleglasspastel", "clearblue", "cleargreen", "clearorange", "clearpink", "clearpurple", "clearred",
            "natural", "modern", "moderndark", "exodark", "cleandark", "cleanlight",
        };

        private static readonly string[] CardColours =
        {
            "white", "green", "yellow", "orange", "red", "purple", "blue", "sky", "lime", "pink", "black", "silver", "peachpuff",
            "crimson", "plum", "darkgreen", "slateblue", "magenta", "gold", "navy", "gray", "saddlebrown", "paleturquoise", "mistyrose",
            "indigo",
        };

        private static readonly string[] Permissions = { "private", "public" };

        // Board member flags n8n's Create Board takes - the owner's on the new board.
        private static readonly (string Name, string Wire)[] MemberFlags =
        {
            ("is_admin", "isAdmin"), ("is_active", "isActive"), ("is_comment_only", "isCommentOnly"), ("is_no_comments", "isNoComments"), ("is_worker", "isWorker"),
        };

        private static readonly (string Name, string Wire)[] CardDates = { ("due_at", "dueAt"), ("start_at", "startAt"), ("end_at", "endAt"), ("received_at", "receivedAt") };

        // A Meteor id (17 of 55 characters) - or an id imported from elsewhere, letters and digits all the same.
        private static readonly Regex IdPattern = new("^[0-9A-Za-z]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "wekan";

        public string DisplayName => "Wekan";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "Username or e-mail",
            ["password"] = "Password",
        };

        public string EndpointExample => "https://wekan.kafe.example";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://wekan.fi/");

        public Uri ApiDocumentationUrl { get; } = new("https://wekan.fi/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        // Over several lines: the step's Query box (the query parameter), when the field is not given.
        private static ConnectorConfigField Long(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Multiline, Required: required, Description: description + " Over several lines from the step's Query box.");

        private static ConnectorConfigField Flag(string name, string label, string description) => new(name, label, ConnectorConfigFieldType.Boolean, Required: false, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField Id(string name, string label, bool required = true) => Field(name, label, "Its id - from its address in Wekan, ar8JWfcYtNtCBjeLh.", required);

        private static readonly ConnectorConfigField BoardField = Id("board", "Board");

        private static readonly ConnectorConfigField ListField = Id("list", "List");

        private static readonly ConnectorConfigField CardField = Id("card", "Card");

        private static readonly ConnectorConfigField ChecklistField = Id("checklist", "Checklist");

        private static readonly ConnectorConfigField ItemField = Id("item", "Item");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("boards", "Get boards", "A user's boards - n8n's Board - Get Many.") { Parameters = new(new[] { Id("user", "User", required: false) with { Description = "Its id. Empty: the connection's own." }, MaxField }) },
            new ConnectorOperation("board", "Get a board", "n8n's Board - Get.") { Parameters = new(new[] { BoardField }) },
            new ConnectorOperation("board_create", "Create a board", "n8n's Board - Create; it comes with a first swimlane, Default.")
            {
                Parameters = new(new[]
                {
                    Field("title", "Title", "Kafe.", required: true), Id("owner", "Owner", required: false) with { Description = "Its id. Empty: the connection's own user." },
                    Field("color", "Colour", "belize, nephritis, pomegranate, pumpkin, wisteria, midnight - as Wekan names its board colours."), Field("permission", "Permission", "private (the default) or public."),
                    Flag("is_admin", "Admin", "The owner an admin of the board."), Flag("is_active", "Active", "The owner active on it."), Flag("is_comment_only", "Comment only", "The owner may only comment."),
                    Flag("is_no_comments", "No comments", "The owner may not comment."), Flag("is_worker", "Worker", "The owner a worker."),
                }),
            },
            new ConnectorOperation("board_delete", "Delete a board", "n8n's Board - Delete.") { Parameters = new(new[] { BoardField }) },
            new ConnectorOperation("swimlanes", "Get a board's swimlanes", "For a card's swimlane.") { Parameters = new(new[] { BoardField }) },
            new ConnectorOperation("lists", "Get lists", "A board's lists - n8n's List - Get Many.") { Parameters = new(new[] { BoardField, MaxField }) },
            new ConnectorOperation("list", "Get a list", "n8n's List - Get.") { Parameters = new(new[] { BoardField, ListField }) },
            new ConnectorOperation("list_create", "Create a list", "n8n's List - Create.") { Parameters = new(new[] { BoardField, Field("title", "Title", "Att göra.", required: true) }) },
            new ConnectorOperation("list_delete", "Delete a list", "n8n's List - Delete.") { Parameters = new(new[] { BoardField, ListField }) },
            new ConnectorOperation("cards", "Get cards", "A list's or a swimlane's cards - n8n's Card - Get Many.")
            {
                Parameters = new(new[] { BoardField, Id("list", "List", required: false) with { Description = "Its id - or give a swimlane." }, Id("swimlane", "Swimlane", required: false) with { Description = "Its id - or give a list." }, MaxField }),
            },
            new ConnectorOperation("card", "Get a card", "n8n's Card - Get.") { Parameters = new(new[] { BoardField, ListField, CardField }) },
            new ConnectorOperation("card_create", "Create a card", "n8n's Card - Create; its author is the connection's user.")
            {
                Parameters = new(new[]
                {
                    BoardField, ListField, Field("title", "Title", "Beställ kaffe.", required: true),
                    Id("swimlane", "Swimlane", required: false) with { Description = "Its id. Empty: the board's first swimlane." },
                    Long("description", "Description", "Markdown."), Field("assignees", "Assignees", "Users' ids, by commas."), Field("members", "Members", "Users' ids, by commas."),
                }),
            },
            new ConnectorOperation("card_update", "Update a card", "n8n's Card - Update; to_list or to_swimlane moves it within the board.")
            {
                Parameters = new(new[]
                {
                    BoardField, ListField, CardField, Field("title", "Title", "A new title."), Long("description", "Description", "A new description."),
                    Field("color", "Colour", "green, yellow, orange, red, purple, blue, sky - as Wekan names its card colours."),
                    Field("due_at", "Due", "2026-10-30T10:00:00Z."), Field("start_at", "Start", "2026-10-28T08:00:00Z."), Field("end_at", "End", "2026-10-30T16:00:00Z."), Field("received_at", "Received", "2026-10-22T08:15:00Z."),
                    Id("to_list", "Move to list", required: false), Id("to_swimlane", "Move to swimlane", required: false), Id("parent", "Parent card", required: false),
                    Field("assignees", "Assignees", "Users' ids, by commas - replacing the card's."), Field("members", "Members", "Users' ids, by commas - replacing the card's."),
                    Field("labels", "Labels", "The board's label ids, by commas - replacing the card's."),
                    Field("sort", "Sort", "Its place in the list - 0, 1.5."), Field("spent_time", "Spent time", "Hours - 2.5."), Flag("is_over_time", "Over time", "Over its time."),
                }),
            },
            new ConnectorOperation("card_delete", "Delete a card", "n8n's Card - Delete.") { Parameters = new(new[] { BoardField, ListField, CardField }) },
            new ConnectorOperation("comments", "Get comments", "A card's comments - n8n's Card Comment - Get Many.") { Parameters = new(new[] { BoardField, CardField }) },
            new ConnectorOperation("comment", "Get a comment", "n8n's Card Comment - Get.") { Parameters = new(new[] { BoardField, CardField, Id("comment", "Comment") }) },
            new ConnectorOperation("comment_create", "Add a comment", "n8n's Card Comment - Create; its author is the connection's user. The card is checked to be on the board first.")
            {
                Parameters = new(new[] { BoardField, CardField, Long("comment", "Comment", "Markdown.", required: true) }),
            },
            new ConnectorOperation("comment_delete", "Delete a comment", "n8n's Card Comment - Delete.") { Parameters = new(new[] { BoardField, CardField, Id("comment", "Comment") }) },
            new ConnectorOperation("checklists", "Get checklists", "A card's checklists, their items with them - n8n's Checklist - Get Many.") { Parameters = new(new[] { BoardField, CardField, MaxField }) },
            new ConnectorOperation("checklist", "Get a checklist", "n8n's Checklist - Get.") { Parameters = new(new[] { BoardField, CardField, ChecklistField }) },
            new ConnectorOperation("checklist_create", "Create a checklist", "n8n's Checklist - Create.")
            {
                Parameters = new(new[] { BoardField, CardField, Field("title", "Title", "Inköp.", required: true), Long("items", "Items", "One item a line.") }),
            },
            new ConnectorOperation("checklist_delete", "Delete a checklist", "n8n's Checklist - Delete.") { Parameters = new(new[] { BoardField, CardField, ChecklistField }) },
            new ConnectorOperation("checklist_item", "Get a checklist item", "n8n's Checklist Item - Get.") { Parameters = new(new[] { BoardField, CardField, ChecklistField, ItemField }) },
            new ConnectorOperation("checklist_item_update", "Update a checklist item", "n8n's Checklist Item - Update.")
            {
                Parameters = new(new[] { BoardField, CardField, ChecklistField, ItemField, Field("title", "Title", "A new title."), Flag("finished", "Finished", "Done or not.") }),
            },
            new ConnectorOperation("checklist_item_delete", "Delete a checklist item", "n8n's Checklist Item - Delete.") { Parameters = new(new[] { BoardField, CardField, ChecklistField, ItemField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                await using var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var user = await session.SendAsync(HttpMethod.Get, "/user", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Wekan answers: signed in as {Text(user?["username"]) ?? session.UserId}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "boards").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Wekan has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to Wekan's shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var plan = Plan(operation, p);

            await using var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            var board = plan.Board;
            switch (operation)
            {
                case "boards":
                    foreach (var item in Items(await session.SendAsync(HttpMethod.Get, $"/users/{plan.User ?? session.UserId}/boards", null, cancellationToken)).Take(max))
                    {
                        yield return Record(item);
                    }

                    break;
                case "board_create":
                    plan.Body!["owner"] ??= session.UserId;
                    yield return Record(await Made(session, "/boards", plan.Body, cancellationToken));
                    break;
                case "swimlanes":
                case "lists":
                case "cards":
                case "comments":
                case "checklists":
                    foreach (var item in Items(await session.SendAsync(HttpMethod.Get, plan.Route, null, cancellationToken)).Take(max))
                    {
                        yield return Record(item);
                    }

                    break;
                case "list_create":
                    yield return Record(await Made(session, $"/boards/{board}/lists", plan.Body!, cancellationToken));
                    break;
                case "card_create":
                    {
                        // Wekan keeps a card made in a list or a swimlane the board does not have - and the board never shows it.
                        await Found(session, $"/boards/{board}/lists/{plan.List}", "list", "the board has no such list", cancellationToken);
                        var swimlanes = Items(await session.SendAsync(HttpMethod.Get, $"/boards/{board}/swimlanes", null, cancellationToken)).Select(lane => Text(lane["_id"])).ToList();
                        var swimlane = plan.Swimlane ?? swimlanes.FirstOrDefault() ?? throw new InvalidOperationException("The board has no swimlane to put the card in - make one in Wekan (swimlane).");
                        if (!swimlanes.Contains(swimlane))
                        {
                            throw new InvalidOperationException($"The board has no swimlane '{swimlane}' (swimlane).");
                        }

                        plan.Body!["swimlaneId"] = swimlane;
                        plan.Body["authorId"] = session.UserId;
                        var made = await Made(session, $"/boards/{board}/lists/{plan.List}/cards", plan.Body, cancellationToken);
                        made["swimlaneId"] = swimlane;
                        yield return Record(made);
                        break;
                    }

                case "card_update":
                    {
                        // A move stays on the board: Wekan refuses another board's list, but keeps a swimlane the board does not have.
                        if (plan.Body!["swimlaneId"] is JsonValue lane && Text(lane) is { } to
                            && !Items(await session.SendAsync(HttpMethod.Get, $"/boards/{board}/swimlanes", null, cancellationToken)).Any(item => Text(item["_id"]) == to))
                        {
                            throw new InvalidOperationException($"The board has no swimlane '{to}' (to_swimlane).");
                        }

                        await session.SendAsync(HttpMethod.Put, plan.Route, plan.Body, cancellationToken);
                        yield return Record(await Found(session, $"/cards/{plan.Subject}", "card", "it is not on the board", cancellationToken));
                        break;
                    }

                case "comment_create":
                    {
                        // Wekan 12 disables the account at a comment sent to a card that is not on its board - so the card is read first.
                        await session.SendAsync(HttpMethod.Get, $"/boards/{board}/cards/{plan.Subject}/comments", null, cancellationToken);
                        plan.Body!["authorId"] = session.UserId;
                        yield return Record(await Made(session, $"/boards/{board}/cards/{plan.Subject}/comments", plan.Body, cancellationToken));
                        break;
                    }

                case "checklist_create":
                    yield return Record(await Made(session, $"/boards/{board}/cards/{plan.Subject}/checklists", plan.Body!, cancellationToken));
                    break;
                case "checklist_item_update":
                    await session.SendAsync(HttpMethod.Put, plan.Route, plan.Body, cancellationToken);
                    yield return Record(await Found(session, plan.Route, "checklist item", "it is not on that checklist", cancellationToken));
                    break;
                case "board_delete":
                case "list_delete":
                case "card_delete":
                case "comment_delete":
                case "checklist_delete":
                case "checklist_item_delete":
                    await session.SendAsync(HttpMethod.Delete, plan.Route, null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = plan.Subject, ["done"] = operation });
                    break;
                default: // one item read
                    yield return Record(await Found(session, plan.Route, plan.What!, plan.Missing!, cancellationToken));
                    break;
            }
        }

        private sealed record Call(string? Board, string Route = "", JsonObject? Body = null, string? Subject = null, string? List = null, string? Swimlane = null, string? User = null, string? What = null, string? Missing = null);

        private static Call Plan(string operation, IReadOnlyDictionary<string, string> p)
        {
            if (operation == "boards")
            {
                return new Call(null, User: Optional(p, "user"));
            }

            if (operation == "board_create")
            {
                var body = new JsonObject { ["title"] = Required(p, "title", "Give the board's title (title).", 500), ["owner"] = Optional(p, "owner") };
                if (Values.Get(p, "color") is not null)
                {
                    body["color"] = Choice(p, "color", BoardColours);
                }

                if (Values.Get(p, "permission") is not null)
                {
                    body["permission"] = Choice(p, "permission", Permissions);
                }

                foreach (var (name, wire) in MemberFlags)
                {
                    if (Bool(p, name) is { } flag)
                    {
                        body[wire] = flag;
                    }
                }

                return new Call(null, Body: body);
            }

            var board = Ident(p, "board", "the board");
            switch (operation)
            {
                case "board":
                    return new Call(board, $"/boards/{board}", What: "board", Missing: "no such board");
                case "board_delete":
                    return new Call(board, $"/boards/{board}", Subject: board);
                case "swimlanes":
                    return new Call(board, $"/boards/{board}/swimlanes");
                case "lists":
                    return new Call(board, $"/boards/{board}/lists");
                case "list":
                    {
                        var list = Ident(p, "list", "the list");
                        return new Call(board, $"/boards/{board}/lists/{list}", What: "list", Missing: "the board has no such list");
                    }

                case "list_create":
                    return new Call(board, Body: new JsonObject { ["title"] = Required(p, "title", "Give the list's title (title).", 500) });
                case "list_delete":
                    {
                        var list = Ident(p, "list", "the list");
                        return new Call(board, $"/boards/{board}/lists/{list}", Subject: list);
                    }

                case "cards":
                    {
                        var list = Optional(p, "list");
                        var swimlane = Optional(p, "swimlane");
                        return (list, swimlane) switch
                        {
                            (not null, null) => new Call(board, $"/boards/{board}/lists/{list}/cards"),
                            (null, not null) => new Call(board, $"/boards/{board}/swimlanes/{swimlane}/cards"),
                            _ => throw new InvalidOperationException("Give a list or a swimlane, one of them, whose cards are read (list, swimlane)."),
                        };
                    }

                case "card":
                    {
                        var list = Ident(p, "list", "the list");
                        var card = Ident(p, "card", "the card");
                        return new Call(board, $"/boards/{board}/lists/{list}/cards/{card}", What: "card", Missing: "it is not in that list on that board");
                    }

                case "card_create":
                    {
                        var body = new JsonObject { ["title"] = Required(p, "title", "Give the card's title (title).", 500) };
                        if (Text(p, "description", false) is { } description)
                        {
                            body["description"] = description;
                        }

                        Ids(body, p, "assignees", "assignees");
                        Ids(body, p, "members", "members");
                        return new Call(board, Body: body, List: Ident(p, "list", "the list"), Swimlane: Optional(p, "swimlane"));
                    }

                case "card_update":
                    {
                        var list = Ident(p, "list", "the list");
                        var card = Ident(p, "card", "the card");
                        var body = new JsonObject();
                        if (Line(p, "title", 500) is { } title)
                        {
                            body["title"] = title; // an empty value is no value: a title is changed or left
                        }

                        if (Text(p, "description", false) is { } description)
                        {
                            body["description"] = description;
                        }

                        if (Values.Get(p, "color") is not null)
                        {
                            body["color"] = Choice(p, "color", CardColours);
                        }

                        foreach (var (name, wire) in CardDates)
                        {
                            if (Values.Get(p, name) is not null)
                            {
                                body[wire] = Date(p, name);
                            }
                        }

                        Move(body, p, "to_list", "listId");
                        Move(body, p, "to_swimlane", "swimlaneId");
                        Move(body, p, "parent", "parentId");
                        Ids(body, p, "assignees", "assignees");
                        Ids(body, p, "members", "members");
                        Ids(body, p, "labels", "labelIds");
                        if (Values.Get(p, "sort") is { } sort)
                        {
                            body["sort"] = double.TryParse(sort, NumberStyles.Float, CultureInfo.InvariantCulture, out var place) && double.IsFinite(place) ? place : throw new InvalidOperationException($"'{sort}' is not a number - 0, 1.5 (sort).");
                        }

                        if (Values.Get(p, "spent_time") is { } spent)
                        {
                            body["spentTime"] = double.TryParse(spent, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && double.IsFinite(hours) && hours >= 0 ? hours : throw new InvalidOperationException($"'{spent}' is not hours, 0 or more - 2.5 (spent_time).");
                        }

                        if (Bool(p, "is_over_time") is { } over)
                        {
                            body["isOverTime"] = over;
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change on the card - a title, a description, a colour, a date, a list to move it to.");
                        }

                        return new Call(board, $"/boards/{board}/lists/{list}/cards/{card}", body, card);
                    }

                case "card_delete":
                    {
                        var list = Ident(p, "list", "the list");
                        var card = Ident(p, "card", "the card");
                        return new Call(board, $"/boards/{board}/lists/{list}/cards/{card}", Subject: card);
                    }

                case "comments":
                    return new Call(board, $"/boards/{board}/cards/{Ident(p, "card", "the card")}/comments");
                case "comment":
                case "comment_delete":
                    {
                        var card = Ident(p, "card", "the card");
                        var comment = Ident(p, "comment", "the comment");
                        return new Call(board, $"/boards/{board}/cards/{card}/comments/{comment}", Subject: comment, What: "comment", Missing: "it is not on that card");
                    }

                case "comment_create":
                    return new Call(board, Body: new JsonObject { ["comment"] = Text(p, "comment", true)! }, Subject: Ident(p, "card", "the card"));
                case "checklists":
                    return new Call(board, $"/boards/{board}/cards/{Ident(p, "card", "the card")}/checklists");
                case "checklist":
                case "checklist_delete":
                    {
                        var card = Ident(p, "card", "the card");
                        var checklist = Ident(p, "checklist", "the checklist");
                        return new Call(board, $"/boards/{board}/cards/{card}/checklists/{checklist}", Subject: checklist, What: "checklist", Missing: "it is not on that card");
                    }

                case "checklist_create":
                    {
                        var body = new JsonObject { ["title"] = Required(p, "title", "Give the checklist's title (title).", 500) };
                        if (Text(p, "items", false) is { } items)
                        {
                            var lines = items.Split('\n').Select(line => line.TrimEnd('\r').Trim()).Where(line => line.Length > 0).ToArray();
                            if (lines.Any(line => line.Length > 500 || line.Any(char.IsControl)))
                            {
                                throw new InvalidOperationException("A checklist item is one line of at most 500 characters (items).");
                            }

                            body["items"] = new JsonArray(lines.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray());
                        }

                        return new Call(board, Body: body, Subject: Ident(p, "card", "the card"));
                    }

                default: // the checklist items
                    {
                        var card = Ident(p, "card", "the card");
                        var checklist = Ident(p, "checklist", "the checklist");
                        var item = Ident(p, "item", "the item");
                        var route = $"/boards/{board}/cards/{card}/checklists/{checklist}/items/{item}";
                        if (operation != "checklist_item_update")
                        {
                            return new Call(board, route, Subject: item, What: "checklist item", Missing: "it is not on that checklist");
                        }

                        var body = new JsonObject();
                        if (Line(p, "title", 500) is { } title)
                        {
                            body["title"] = title;
                        }

                        if (Bool(p, "finished") is { } finished)
                        {
                            body["isFinished"] = finished;
                        }

                        return body.Count > 0 ? new Call(board, route, body, item) : throw new InvalidOperationException("Give something to change on the item - a title, or finished true or false.");
                    }
            }
        }

        // A made thing as Wekan answers it ({ _id }, a board with its first swimlane), with what was sent.
        private static async Task<JsonObject> Made(Session session, string route, JsonObject body, CancellationToken cancellationToken)
        {
            var answer = await session.SendAsync(HttpMethod.Post, route, body, cancellationToken) as JsonObject;
            if (Text(answer?["_id"]) is null)
            {
                throw new InvalidOperationException("Wekan answered without what it made - check the address: a sign-in page or a proxy answers so.");
            }

            var made = (JsonObject)body.DeepClone();
            foreach (var (key, value) in answer!)
            {
                made[key] = value?.DeepClone();
            }

            return made;
        }

        // One thing read; Wekan answers 200 with nothing for a card or a comment it does not have.
        private static async Task<JsonObject> Found(Session session, string route, string what, string missing, CancellationToken cancellationToken) =>
            await session.SendAsync(HttpMethod.Get, route, null, cancellationToken) as JsonObject is { Count: > 0 } found
                ? found
                : throw new InvalidOperationException($"Wekan found no such {what} - {missing}.");

        private static IEnumerable<JsonObject> Items(JsonNode? answer) =>
            answer is JsonArray items ? items.OfType<JsonObject>() : throw new InvalidOperationException("Wekan answered with what is not a list - check the address: a sign-in page or a proxy answers so.");

        private static string Ident(IReadOnlyDictionary<string, string> p, string name, string what) =>
            Optional(p, name) ?? throw new InvalidOperationException($"Give {what} - its id, ar8JWfcYtNtCBjeLh ({name}).");

        private static string? Optional(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? IdPattern.IsMatch(given) ? given : throw new InvalidOperationException($"'{(given.Length > 40 ? given[..40] + "..." : given)}' is not an id - letters and digits, ar8JWfcYtNtCBjeLh ({name}).") : null;

        private static void Move(JsonObject body, IReadOnlyDictionary<string, string> p, string name, string wire)
        {
            if (Optional(p, name) is { } id)
            {
                body[wire] = id;
            }
        }

        private static void Ids(JsonObject body, IReadOnlyDictionary<string, string> p, string name, string wire)
        {
            if (Values.Get(p, name) is not { } given)
            {
                return;
            }

            var ids = given.Split(',').Select(id => id.Trim()).Where(id => id.Length > 0).ToArray();
            body[wire] = ids.All(IdPattern.IsMatch) ? new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) : throw new InvalidOperationException($"'{given}' is not ids by commas - letters and digits ({name}).");
        }

        private static string Choice(IReadOnlyDictionary<string, string> p, string name, string[] known)
        {
            var given = Values.Get(p, name)!;
            return known.FirstOrDefault(choice => string.Equals(choice, given, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not one Wekan knows - {string.Join(", ", known.Take(8))}{(known.Length > 8 ? " ..." : string.Empty)} ({name}).");
        }

        private static bool? Bool(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? bool.TryParse(given, out var flag) ? flag : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

        private static string Date(IReadOnlyDictionary<string, string> p, string name)
        {
            var given = Values.Get(p, name)!;
            return DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
                ? when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a date - 2026-10-30T10:00:00Z ({name}).");
        }

        private static string? Line(IReadOnlyDictionary<string, string> p, string name, int most) =>
            Values.Get(p, name) is { } given ? given.Length <= most && !given.Any(char.IsControl) ? given : throw new InvalidOperationException($"'{(given.Length > 40 ? given[..40] + "..." : given)}' is more than {most} characters or more than one line ({name}).") : null;

        private static string Required(IReadOnlyDictionary<string, string> p, string name, string missing, int most) =>
            Line(p, name, most) is { Length: > 0 } given ? given : throw new InvalidOperationException(missing);

        private static string? Text(IReadOnlyDictionary<string, string> p, string name, bool required)
        {
            var given = Values.Get(p, name) ?? Values.Get(p, "query"); // over several lines: the Query box
            if (given is null)
            {
                return required ? throw new InvalidOperationException($"Give the {name} - here or in the step's Query box ({name}).") : null;
            }

            return given.Length <= MostText ? given : throw new InvalidOperationException($"A {name} is at most a megabyte ({name}).");
        }

        // An object as a record: its plain values as they are, a list or an object as JSON, its _id as id.
        private static ConnectorRecord Record(JsonObject data)
        {
            var fields = data.Where(pair => pair.Key != "_id").ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                JsonValue value when value.TryGetValue<long>(out var whole) => whole,
                JsonValue value when value.TryGetValue<double>(out var number) => number,
                var other => (object?)other.ToJsonString(Wire),
            }, StringComparer.Ordinal);
            fields["id"] = Text(data["_id"]);
            return new ConnectorRecord(fields);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IAsyncDisposable
        {
            private readonly HttpClient client;
            private readonly string root;
            private readonly string token;

            private Session(HttpClient client, string root, string token, string userId)
            {
                this.client = client;
                this.root = root;
                this.token = token;
                UserId = userId;
            }

            public string UserId { get; }

            public static async Task<Session> OpenAsync(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give Wekan's address - https://wekan.kafe.example.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    root = root[..^"/api".Length]; // the API's own address given: Wekan's
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Wekan's address - https://wekan.kafe.example, with no query.");
                }

                var user = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Wekan connection has no username.");
                var password = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Wekan connection has no password.");
                var client = egress.CreateHttpClient(timeout);
                try
                {
                    // n8n's sign-in: the user's name or e-mail and password for a token, sent as a bearer token on every call.
                    var body = new JsonObject { ["username"] = user, ["password"] = password }.ToJsonString(Wire);
                    using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, root + "/users/login") { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken);
                    var answer = Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"Wekan refused the sign-in (HTTP {(int)response.StatusCode}){Said(answer)} - check the username and the password; Wekan refuses a disabled account so too.");
                    }

                    return Text(answer?["token"]) is { Length: > 0 } token && Text(answer?["id"]) is { Length: > 0 } id
                        ? new Session(client, root, token, id)
                        : throw new InvalidOperationException("Wekan signed in without a token - check the address: a sign-in page or a proxy answers so.");
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }

            // Signed out again: the token this run made, and only it, ends - Wekan keeps every token a sign-in makes otherwise.
            public async ValueTask DisposeAsync()
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, root + "/users/logout");
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var response = await client.SendAsync(request, cancel.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    // best effort: the token lapses in Wekan's own time (90 days)
                }
                finally
                {
                    client.Dispose();
                }
            }

            /// <summary>A call with the token; Wekan's JSON, or its status and message.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, root + "/api" + route);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                var json = Parse(text);
                if (response.IsSuccessStatusCode)
                {
                    if (json is null && text.Trim().Length > 0)
                    {
                        throw new InvalidOperationException("Wekan answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                    }

                    return json;
                }

                var status = (int)response.StatusCode;
                throw new InvalidOperationException($"Wekan answered HTTP {status}{Said(json)}"
                    + (status == 401 ? " - check the username and password, and that the user may see the board." : status == 500 ? " - Wekan answers so to a value it does not take." : string.Empty));
            }

            private static JsonNode? Parse(string answer)
            {
                try
                {
                    return string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            // Wekan's message: its "error" and "reason" ({"error":"Board not found"}, {"error":"login-failed","reason":...}), or its "message".
            private static string Said(JsonNode? json)
            {
                var words = string.Join(": ", new[] { Text(json?["error"]), Text(json?["reason"]) ?? Text(json?["message"]) }.Where(part => !string.IsNullOrEmpty(part)));
                words = words.Length > 300 ? words[..300] : words;
                return words.Length == 0 ? "." : ": " + words + (words.EndsWith('.') ? string.Empty : ".");
            }
        }
    }
}
