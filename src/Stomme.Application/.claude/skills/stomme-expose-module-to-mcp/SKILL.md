---
name: stomme-expose-module-to-mcp
description: Use when a module's data or operations should be available to AI clients over MCP (Model Context Protocol) - publishing MCP resources or tools, an IMcpContributor, an MCP feature flag such as <module>.mcp, gating MCP access by permission, or when a published tool or resource does not show up in tools/list.
---

# Expose a module to MCP

You will have published a module's read operations as MCP resources and a read-only MCP tool, behind a feature flag that
is off by default and the module's own permission key, registered only while the installation's MCP server is on, and
audited by Nova Globen Stomme on every call. Read [the MCP server](../../../docs/ai/mcp-server.md) first for how the catalog,
gates, approval and duplicate-name rule work.

## Before you start

The steps assume the module named **Notes** built with the `stomme-create-module` skill, with:

- in `Shared/`: the permission key class `NotesPermissions` (namespace `Stomme.Application.Modules.Notes`) with
  `View = "notes:view"`, declared in the client `ModuleInfo` through `PermissionKeys.Declare(...)`, and the model
  `NoteModel` (namespace `Stomme.Application.Modules.Notes.Models`) with `Guid Id`, `string Title` and `string Body`;
- in `Server/`: two mediator queries gated with `[Requires(NotesPermissions.View)]`, in namespace
  `Stomme.Application.Modules.Notes.Application` - `ListNotesQuery` returning `IReadOnlyList<NoteModel>` and
  `GetNoteQuery(Guid NoteId)` returning `NoteModel?` - with their handlers registered explicitly in
  `NotesServerStartup.ConfigureServices` (`Server/Modules/Notes/Startup/NotesServerStartup.cs`; see the
  `stomme-add-command-or-query` skill). Do not call `AddMediator` in a module.

Rename `Notes`, `NoteModel`, the queries, the key and the namespaces to your module's. If your module has no read queries
yet, add them first: MCP must read through the same permission-gated path as the module's own API, never through a
repository directly.

## Steps

1. **Choose the names.** All lower-case, and unique across the installation:
   - the feature flag: `notes.mcp`;
   - the contributor id: `myapp.notes` (your app's prefix, then the module);
   - the URI scheme: your app's own (`myapp://`), never `stomme://`;
   - the resource list `myapp://notes` and the item template `myapp://notes/{noteId}` - the list URI must NOT have the
     template's shape (`myapp://notes/all` would be matched by the template, and both would be dropped);
   - tool names: a module prefix and no mutating prefix for a read-only tool - `notes_search`. Names starting with
     `create_`, `update_`, `delete_`, `execute_`, `run_`, `export_` or `send_` cannot be `Auto` and do not run today.

2. **Create `Server/Modules/Notes/Mcp/NotesMcpContributor.cs`** with the flag's declaration and the contributor:

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Linq;
   using System.Text.Json;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Application.Modules.Notes.Application;
   using Stomme.FeatureManagement.Application;
   using Stomme.Mcp;
   using Stomme.Mediator;

   namespace Stomme.Application.Modules.Notes.Mcp
   {
       /// <summary>The module's MCP flag, found by convention (public parameterless constructor). Off by default.</summary>
       public sealed class NotesFeatureContributor : IFeatureContributor
       {
           public const string Mcp = "notes.mcp";

           public IReadOnlyCollection<FeatureDescriptor> Features { get; } = new[]
           {
               new FeatureDescriptor(Mcp, "Expose the site's notes to MCP clients: the note list, one note, and the notes_search tool.", "MCP", DefaultEnabled: false),
           };
       }

       /// <summary>The Notes module's MCP surface: two resources and one read-only tool, each gated on the flag AND notes:view.</summary>
       public sealed class NotesMcpContributor(IMediator mediator) : IMcpContributor, IMcpResourceReader
       {
           public const string ListUri = "myapp://notes";
           public const string NoteTemplate = "myapp://notes/{noteId}";
           public const string SearchTool = "notes_search";
           public const int MaximumQueryLength = 200;
           public const int MaximumResults = 20;

           private static readonly IReadOnlyList<McpAccessGate> ViewGate = new[] { new McpAccessGate(NotesFeatureContributor.Mcp, NotesPermissions.View) };
           private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

           public string ContributorId => "myapp.notes";

           public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } = new[]
           {
               new McpResourceDescriptor(ListUri, "Notes", "The site's notes: id, title and address - no bodies.", "application/json", ViewGate),
               new McpResourceDescriptor(NoteTemplate, "Note", "One note by id, with its body.", "application/json", ViewGate),
           };

           public IReadOnlyCollection<McpToolDescriptor> Tools { get; } = new[]
           {
               new McpToolDescriptor(SearchTool, "Search the site's notes by words in their title or body; answers the best matches with each note's address.",
                   """{"type":"object","properties":{"query":{"type":"string","description":"The words to look for."}},"required":["query"]}""",
                   ViewGate, McpToolApproval.Auto),
           };

           public IMcpResourceReader? ReaderFor(string uriTemplate) => uriTemplate is ListUri or NoteTemplate ? this : null;

           public IMcpToolInvoker? InvokerFor(string toolName) => toolName == SearchTool ? new SearchInvoker(this) : null;

           public async Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
           {
               if (string.Equals(uri, ListUri, StringComparison.Ordinal))
               {
                   var notes = await mediator.Send(new ListNotesQuery(), ct);
                   return Content(uri, notes.Select(note => new { id = note.Id, title = note.Title, uri = ListUri + "/" + note.Id }).ToList());
               }

               // A template argument arrives exactly as sent: parse it, and answer null (= "no such resource") when it is not a note id.
               if (arguments.TryGetValue("noteId", out var raw) && Guid.TryParse(raw, out var noteId))
               {
                   var note = await mediator.Send(new GetNoteQuery(noteId), ct);
                   return note is null ? null : Content(uri, new { id = note.Id, title = note.Title, body = note.Body });
               }

               return null;
           }

           internal async Task<McpToolResult> SearchAsync(string query, CancellationToken ct)
           {
               var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
               var matches = (await mediator.Send(new ListNotesQuery(), ct))
                   .Where(note => words.All(word => note.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || note.Body.Contains(word, StringComparison.OrdinalIgnoreCase)))
                   .Take(MaximumResults)
                   .Select(note => new { id = note.Id, title = note.Title, uri = ListUri + "/" + note.Id })
                   .ToList();
               return new McpToolResult(JsonSerializer.Serialize(new { query, results = matches }, Json));
           }

           private static McpResourceContent Content(string uri, object payload) => new(uri, "application/json", JsonSerializer.Serialize(payload, Json));

           private static McpToolResult Error(string message) => new(JsonSerializer.Serialize(new { error = message }, Json), isError: true);

           /// <summary>A malformed call is an MCP error result with a reason, never an exception.</summary>
           private sealed class SearchInvoker(NotesMcpContributor contributor) : IMcpToolInvoker
           {
               public async Task<McpToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
               {
                   string? query = null;
                   try
                   {
                       using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                       if (document.RootElement.ValueKind == JsonValueKind.Object
                           && document.RootElement.TryGetProperty("query", out var value) && value.ValueKind == JsonValueKind.String)
                       {
                           query = value.GetString()?.Trim();
                       }
                   }
                   catch (JsonException)
                   {
                       return Error("The arguments must be a JSON object.");
                   }

                   if (string.IsNullOrEmpty(query) || query.Length > MaximumQueryLength)
                   {
                       return Error($"The arguments need a 'query' of 1 to {MaximumQueryLength} characters.");
                   }

                   return await contributor.SearchAsync(query, ct);
               }
           }
       }
   }
   ```

   `NotesPermissions` needs no `using`: its namespace, `Stomme.Application.Modules.Notes`, encloses this one. If your
   module's names differ, change the `using` of the queries' namespace. Keep the bodies out of the list resource: an AI
   client reads the list first, and a list is cheaper to read and safer to hand out than every body.

3. **Register the contributor in `NotesServerStartup.ConfigureServices`**
   (`Server/Modules/Notes/Startup/NotesServerStartup.cs`, the module's `IServerStartup`), only while the MCP server half
   is composed. Put it before the line that registers `INotesService`, which stays last:

   ```csharp
   // at the top of the file (Stomme.Infrastructure is already there)
   using Stomme.Application.Modules.Notes.Mcp;
   using Stomme.Modules.Mcp.Startup;

   // inside ConfigureServices(IServiceCollection services)
   if (McpSettings.ServerEnabled(HostConfiguration.Resolve(services)))
   {
       services.AddScoped<Stomme.Mcp.IMcpContributor, NotesMcpContributor>();
   }
   ```

   Do NOT register `NotesFeatureContributor`: it has a public parameterless constructor and is found by convention, in an
   assembly whose name contains `Stomme` - the app's projects are named `*.Stomme` for that reason; keep their
   `AssemblyName` as the template made it.

4. **Switch the server half on** for your development install. Put this in the installation's
   `appsettings.override.json` (`Server/Data/` by default) or in `Server/appsettings.json`, then restart:

   ```json
   {
     "Mcp": {
       "Server": {
         "Enabled": true
       }
     }
   }
   ```

5. **Turn the module's flag on.** Either on the Feature Flags page (`/admin/features`, as the Host), or in
   `Server/features.json`, which wins over the page:

   ```json
   {
     "FeatureManagement": {
       "notes.mcp": true
     }
   }
   ```

   A change to `features.json` takes effect without a restart. While the key is in `features.json`, it wins over the
   Feature Flags page: a toggle there changes the stored flag, not what the site does. Ship the app with
   the flag off (absent) unless the product decision is that every site publishes the module.

6. **Update the docs.** Add the module's flag, URIs and tool to the app's own documentation of the module, and to any
   list of switches the app keeps.

## Verify

Run the app (`cd Server`, `dotnet run`) and sign in as an administrator in a browser.

1. The startup log has NO line starting `MCP contribution REJECTED`. If it has one naming your contributor, a name
   collided; rename it.
2. From the signed-in browser tab, open the developer console and POST to the endpoint (the session cookie goes with it):

   ```js
   const call = async (method, params) => {
     const r = await fetch('/mcp/v1', {
       method: 'POST',
       headers: { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream' },
       body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params })
     });
     const text = await r.text();
     const json = text.includes('data:') ? text.split('\n').filter(l => l.startsWith('data:')).map(l => l.slice(5)).join('') : text;
     return [r.status, JSON.parse(json)];
   };
   await call('tools/list');
   await call('resources/list');
   await call('resources/templates/list');
   await call('resources/read', { uri: 'myapp://notes' });
   await call('tools/call', { name: 'notes_search', arguments: { query: 'hello' } });
   ```

   Expect `notes_search` in the tool list, `myapp://notes` (beside Stomme's `stomme://site`) in the resource list,
   `myapp://notes/{noteId}` in the template list, the notes in the read, and matches in the call. Every answer is 200;
   the JSON-RPC body says whether it succeeded. No `initialize` and no antiforgery header are needed. Use the site's own
   host name: the endpoint answers for the site the request's host resolves to.
3. Read one note through the template with a real id, and with a random GUID: the random one answers a JSON-RPC error
   "No such resource, or it is not available to you." Call `notes_search` with `arguments: {}`: the result has
   `isError: true` and your message.
4. Turn `notes.mcp` off and repeat `tools/list`: your tool is gone, Stomme's own entries are not. Turn it back on.
5. Sign in as a user who does NOT hold `notes:view` and repeat: `tools/list` and both resource lists are empty of your
   entries, a `resources/read` of `myapp://notes` answers the "No such resource" error, and a `tools/call` of
   `notes_search` answers a result with `isError: true` and the text "No such tool, or it is not available to you."
   The Notes module grants `notes:view` to Registered Users, so first take it away: as the Host, open
   **Admin > Module Management** (`/admin/modules`), edit Notes, and on its Permissions tab clear Registered Users for
   `notes:view`; then create a user and sign in as them. Give the permission back afterwards.
6. As an administrator, open the MCP page's Catalog and Audit actions - `/admin/mcp/*/{moduleId}/Catalog` and
   `/admin/mcp/*/{moduleId}/Audit`, where `{moduleId}` is the id of the MCP module on `/admin/mcp` (the page has no
   link to them) - or call `GET /api/Mcp/published` and `GET /api/Mcp/audit?days=1`. The catalog lists your three
   entries, `notes_search` as `Auto`. Each `tools/call` and `resources/read` above is an audit row: `Succeeded` for the
   allowed ones, `Denied` for the user without the key, and `Failed` for the random GUID and the call without a query
   (a reader that answers null and a tool result with `isError` are both written as `Failed`).
7. Sign out and POST again: 401.

A refused or unknown `resources/read` also writes an Error line to the server log from the MCP library
(`ReadResource "..." threw an unhandled exception` with `McpException: No such resource...`). That is how the refusal
travels, not a fault in your contributor.

Steps 4, 5 and 7 are what prove the gates; a green step 2 alone proves only that the contributor is registered.

## Common mistakes

- **`tools/list` is empty or lacks your tool.** Cause, in the order to check: `Mcp:Server:Enabled` not on (or no
  restart), the `notes.mcp` flag off, the user lacks `notes:view` on this site, the key is not declared in the client
  `ModuleInfo` (an undeclared key denies everyone), a site override `mcp.tool.notes_search.approval` set to `Deny`, or a
  rejected collision. Fix the first that applies.
- **Turning `notes.mcp` off on the Feature Flags page changes nothing.** The key is in `features.json`, which wins over
  the page. Change it there, or remove it from the file to manage it on the page.
- **`McpToolDescriptor` throws at startup: "may not be Auto".** The tool's name starts with a mutating prefix. Rename a
  read-only tool, or make a tool that really changes data `Confirm` - and know that it will be denied until Stomme has
  an approval pipeline.
- **`ArgumentException` "A tool name is 1 to 64 lower-case ASCII letters..."** Upper case or a space in the name. Names
  are refused, not rewritten.
- **"declares no gate" at startup.** A descriptor was built with an empty `requires` list. Give it the flag and the key.
- **The startup log says `REJECTED ... Two contributors publish this tool`.** Another module (or Stomme) publishes the
  same name; both are dropped. Prefix names with the module.
- **`This address is also matched by the template`.** A plain URI has the shape of one of the templates. Change the list
  URI so it has fewer or more segments than the template.
- **The host fails to start after adding the contributor.** A constructor dependency of the contributor is not
  registered (the host validates its container at start). Register what it needs, or depend on `IMediator`.
- **The call answers "The tool failed." and the log says `No handler registered for ListNotesQuery`.** The query's
  handler is not registered. Register every handler explicitly in `NotesServerStartup`; do not add `AddMediator` to a
  module to fix it.
- **A tool returns another site's data.** The contributor read a repository directly or cached data across requests.
  Read through the module's gated queries on every call; the contributor is scoped for that reason.
- **The call answers "The tool failed."** The invoker threw; the exception is in the log at Error. Return
  `McpToolResult(..., isError: true)` with a reason for anything the caller can fix.
- **An MCP desktop client cannot connect.** The endpoint accepts the site's sign-in cookie only; token authentication
  for MCP clients is not built. Drive it from a signed-in session as above.
