---
name: stomme-use-connection
description: Use when module code must read from or write to an external system through one of the site's connections (Admin > Connections) - calling a connector's operation from a service, an API endpoint or a background job, importing records from another system, choosing a connection in module settings, or when asked to "use the connection", "pull data from X", "import from X", "call the API with the stored credentials" or "integrate with a configured system".
---

# Use a connection from module code

You will have, in the Notes module, a server service that runs an operation of one of the site's connections through
Nova Globen Stomme's extraction port, an API endpoint behind permission keys that previews the records and imports them as notes,
and the same read made safe for a background job. The code never touches a credential, an address policy or the
connection row. Read [using connections in code](../../../docs/connectors/using-connections-in-code.md) for the port
and its outcomes, and [connectors and connections](../../../docs/connectors/overview.md) for connections, credentials
and the egress. The Notes module itself is the one the
[stomme-create-module](../stomme-create-module/SKILL.md) skill builds.

The example reads notes from an HTTP API (`GET /v1/notes` answering `{ "notes": [ { "title": ..., "body": ... } ] }`)
through a connection of the generic REST connector (`rest`, a bearer token). For another connector, use its operation
and parameter names from `GET api/Connections/connectors`.

## Steps

1. **Create the connection first.** On `/admin/connections`, as an administrator, create a connection of the connector
   you need, enter its credentials and press **Test** until it says "Connection succeeded.". The REST connector's test
   sends a GET to the endpoint plus its **Health check path** setting, so set that to an address the API answers with
   2xx. A private or loopback host needs the Host to add it, exactly as the endpoint writes it, under **Private
   hosts**. Note the operation and parameters the connector declares: `GET api/Connections/connectors` lists them
   (`operations[].parameters`).

2. **Write the service.** Create `Server/Modules/Notes/Services/NoteImportReader.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Connectivity;

   namespace Stomme.Application.Modules.Notes.Services
   {
       /// <summary>Reads notes from an external API through one of the site's connections; the credentials stay in the site's secret store.</summary>
       public sealed class NoteImportReader(IConnectorExtractionService extraction)
       {
           public const int MaxNotes = 500;

           public Task<ConnectorExtractionResult> ReadAsync(Guid connectionId, CancellationToken ct = default)
           {
               var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
               {
                   ["path"] = "/v1/notes",
                   ["records_path"] = "notes",
               };

               return extraction.ExtractAsync(connectionId, "read", parameters, MaxNotes, ct);
           }
       }
   }
   ```

   `read` is the REST connector's operation: it sends `method` (GET by default) to `path` resolved against the
   connection's endpoint and reads the JSON array under `records_path`.

3. **Add a gated endpoint.** Create `Server/Modules/Notes/Controllers/NoteImportController.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Application.Modules.Notes.Application;
   using Stomme.Application.Modules.Notes.Services;
   using Stomme.Connectivity;
   using Stomme.Controllers;
   using Stomme.Infrastructure;
   using Stomme.Infrastructure.Security;
   using Stomme.Mediator;
   using Stomme.Shared;
   using FluentValidation;
   using Microsoft.AspNetCore.Http;
   using Microsoft.AspNetCore.Mvc;

   namespace Stomme.Application.Modules.Notes.Controllers
   {
       /// <summary>
       /// api/NoteImport/{connectionId}: GET shows what an import would bring, POST creates a note per record. Both need the
       /// notes:edit key and connections:view, because the connection id unlocks the site's stored credentials.
       /// </summary>
       [Route(ControllerRoutes.ApiRoute)]
       public sealed class NoteImportController : ModuleControllerBase
       {
           private readonly NoteImportReader _reader;
           private readonly IMediator _mediator;

           public NoteImportController(NoteImportReader reader, IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
               : base(logger, accessor)
           {
               _reader = reader;
               _mediator = mediator;
           }

           // GET api/NoteImport/{connectionId}: the records the import would read, as a JSON array.
           [HttpGet("{connectionId:guid}")]
           [RequiresPermissionKey(NotesPermissions.Edit)]
           [RequiresPermissionKey(ConnectionPermissions.View)]
           public async Task<IActionResult> Preview(Guid connectionId, CancellationToken ct)
           {
               var (result, refusal) = await ReadAsync(connectionId, ct);
               if (refusal is not null)
               {
                   return refusal;
               }

               Response.Headers["X-Truncated"] = result!.Truncated ? "true" : "false";
               return Content(ConnectorRecordJson.Serialize(result.Records), "application/json");
           }

           // POST api/NoteImport/{connectionId}: a note per record with a title; a title the site already has is skipped.
           [HttpPost("{connectionId:guid}")]
           [RequiresPermissionKey(NotesPermissions.Edit)]
           [RequiresPermissionKey(ConnectionPermissions.View)]
           public async Task<IActionResult> Import(Guid connectionId, CancellationToken ct)
           {
               var (result, refusal) = await ReadAsync(connectionId, ct);
               if (refusal is not null)
               {
                   return refusal;
               }

               var imported = 0;
               var skipped = 0;
               foreach (var record in result!.Records)
               {
                   var title = record.Fields.TryGetValue("title", out var t) ? t?.ToString() : null;
                   var body = record.Fields.TryGetValue("body", out var b) ? b?.ToString() : null;
                   try
                   {
                       await _mediator.Send(new CreateNoteCommand(title ?? string.Empty, body ?? string.Empty), ct);
                       imported++;
                   }
                   catch (Exception ex) when (ex is NoteConflictException or ValidationException)
                   {
                       skipped++; // a title the site already has, or a record without a usable title
                   }
               }

               return Ok(new { imported, skipped, truncated = result.Truncated });
           }

           private async Task<(ConnectorExtractionResult? Result, IActionResult? Refusal)> ReadAsync(Guid connectionId, CancellationToken ct)
           {
               // A failure is a result with an outcome, never an exception: an HTTP timeout inside the connector is
               // TimedOut (since Stomme 10.73.0). Only a cancellation of ct itself is thrown.
               var result = await _reader.ReadAsync(connectionId, ct);
               if (result.Outcome == ConnectorOutcome.ConnectionNotFound)
               {
                   return (null, NotFound());
               }

               if (!result.Success)
               {
                   return (null, StatusCode(StatusCodes.Status502BadGateway, new { outcome = result.Outcome.ToString(), error = result.Message }));
               }

               return (result, null);
           }
       }
   }
   ```

   The port checks no permission, so the endpoint must, and it needs both keys: the module's own (`notes:edit`, which
   creating a note needs anyway) and `connections:view`, because whoever names a connection id uses its stored
   credentials. Two `[RequiresPermissionKey]` attributes both apply. The import sends the module's own
   `CreateNoteCommand`, so its validation, its duplicate-title check and its author stamping hold for imported notes too.
   An operation that writes to the remote system belongs on a POST gated with a manage-level key.

4. **Register the service.** In `Server/Modules/Notes/Startup/NotesServerStartup.cs`, add this line to
   `ConfigureServices`, before the `INotesService` registration that stays last:

   ```csharp
   services.AddScoped<NoteImportReader>();
   ```

   and `using Stomme.Application.Modules.Notes.Services;` at the top if it is not there. Controllers in the `Server`
   assembly are discovered by the host; nothing else to register.

5. **Outside a request, enter the site first.** A background job or hosted service has no request, so no site and no
   tenant. Create a scope, set the site's alias, then resolve the service from that scope. Create
   `Server/Modules/Notes/Services/NoteImportInSite.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Connectivity;
   using Stomme.Infrastructure;
   using Microsoft.Extensions.DependencyInjection;

   namespace Stomme.Application.Modules.Notes.Services
   {
       public static class NoteImportInSite
       {
           /// <summary>Reads one site's connection from code that runs outside a request (a job, a hosted service).</summary>
           public static async Task<ConnectorExtractionResult> ReadAsync(IServiceScopeFactory scopes, int tenantId, int siteId, Guid connectionId, CancellationToken ct)
           {
               await using var scope = scopes.CreateAsyncScope();
               await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(tenantId, siteId);
               return await scope.ServiceProvider.GetRequiredService<NoteImportReader>().ReadAsync(connectionId, ct);
           }
       }
   }
   ```

   A job can READ this way, but it cannot create the notes through the mediator: `CreateNoteCommand` carries
   `[Requires]`, which needs a signed-in user, and a job has none - the send throws `AccessDeniedException`. Import from
   a request (the endpoint above), or give the job a write path of its own that does not go through `[Requires]`
   requests. How a job finds its tenants and sites is in [jobs](../../../docs/services/jobs.md).

6. **Let an administrator choose the connection.** Store the connection id in your module's settings, picked from
   `IConnectionsService.ListAsync()` (`Stomme.Modules.Connectivity.Services`) filtered to the connector types your
   code supports (`ConnectionView.ConnectorType`). If your module stores the id, implement `IConnectionUsageProvider`
   (`Stomme.Connectivity`) and register it with `services.AddScoped<IConnectionUsageProvider, YourUsageProvider>()`
   so the connection cannot be deleted while it is in use.

## Verify

1. `dotnet build` is clean. Run the app and sign in as an administrator.
2. Copy the connection's id: open `/api/Connections` in the signed-in browser tab and find it by name.
3. Open `/api/NoteImport/<id>` in the same tab. It must return the remote's notes as a JSON array, with no credential
   in it.
4. Send `POST /api/NoteImport/<id>` from the signed-in session (with the antiforgery header, see
   [API and client services](../../../docs/modules/api-and-client-services.md)). It answers
   `{"imported":N,"skipped":0,...}` and the notes appear on the Notes page with you as the author. Send it again: every
   record is `skipped`, nothing is duplicated. The server log then carries one `fail` entry per skipped record
   ("Unhandled exception while processing CreateNoteCommand ... NoteConflictException"): the mediator logs every
   exception a handler throws, including the ones the endpoint catches. Those are expected; any other `fail` is not. A
   record skipped for having no usable title logs nothing: a validation failure is not logged as an error.
5. Signed out, the same URL answers 403. A Registered user (who holds `notes:edit` but not `connections:view`) also gets
   403 on both GET and POST - never data.
6. Open it with a random Guid. It must answer 404.
7. Point a connection's endpoint at a LAN or loopback address that is not on Private hosts (for example `localhost`
   when only `127.0.0.1` is listed) and call again. It must answer 502 with outcome `DeniedByPolicy`, and the remote
   must see no request.
8. If you use the job path, run it from a real job or hosted service (not from a scope you create inside a controller
   action, see Common mistakes) and check that it reads the records. A `ConnectionNotFound` there means the site was
   not entered.

## Common mistakes

- **`ConnectionNotFound` from a job for a connection that exists.** The cause: no site or tenant in the scope. The
  fix: `SetAliasAsync(tenantId, siteId)` in the scope before resolving the service.
- **`AccessDeniedException` from a job that sends the module's commands.** The cause: a `[Requires]` request needs a
  signed-in user, and a job has none. The fix: import from a request, or write without the mediator.
- **The job path works when you try it from a controller and fails in the real job.** The cause: a scope created inside
  a request still sees that request's `HttpContext` and user. The fix: test the job path in a real background job.
- **`OperationUndefined`.** The cause: the operation name is not one the connector declares (`read`, not `get`). The
  fix: read `operations` from `GET api/Connections/connectors`.
- **No records, no error.** The cause: a parameter name the connector does not know is ignored, so the filter or path
  was never applied. The fix: copy parameter names from the operation's declared `parameters`.
- **`DeniedByPolicy`.** The cause: the endpoint resolves to a private or loopback address. The fix: the Host adds the
  exact host the endpoint names under Private hosts on the Connections page.
- **A REST connection's Test fails though the reads work.** The cause: the test GETs the endpoint plus the Health check
  path, and that address answers 404 or 401. The fix: set Health check path to an address the API answers with 2xx.
- **A 500 when the remote is slow.** The cause: a Stomme older than 10.73.0, whose extraction port let an HTTP timeout
  inside the connector propagate as a `TaskCanceledException`. From 10.73.0 the port returns `TimedOut`, which the
  endpoint above answers as a 502 with outcome `TimedOut`. The fix: upgrade Stomme; on an older one, catch
  `OperationCanceledException` when `ct` was not cancelled and treat it as a timeout.
- **`CredentialsUnreadable` after a move or a restore to another machine.** The cause: the Data Protection key ring
  changed. The fix: re-enter the credentials on the connection.
- **An endpoint anyone can call.** The cause: the extraction port checks no permission. The fix: a
  `[RequiresPermissionKey]` for `connections:view` (or a key of your own) on every action that calls it.
- **Calling the connection-management commands from a job fails with access denied.** The cause:
  on the server `IConnectionsService` sends the Connectivity module's commands, whose `[Requires]` checks the signed-in
  user's keys, and a job has none. The fix: use the extraction
  port for runs; manage connections from pages and the API.
