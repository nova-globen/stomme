# Using connections in code

Your module's server code reads from (or writes to) an external system by naming one of the site's connections and an
operation. Nova Globen Stomme resolves the connection, its connector, its credentials and the site's egress, runs the operation
and hands back records. Your code never sees a credential, an address policy or the connection row.

## How it works

The port is `IConnectorExtractionService` (`Stomme.Connectivity`), a scoped service in the server's container:

```csharp
Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters,
    int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default);

Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters,
    int maxRecords, IConnectorFiles? files, CancellationToken cancellationToken);
```

One call:

1. Loads the connection. When the scope has a site (`SiteState.Alias`), a connection of another site is
   `ConnectionNotFound`. The connection must also belong to the scope's tenant.
2. Finds its connector in the catalogue (`ConnectorUnavailable` if none) and checks it extracts at all, that is
   implements `IConnectorExtractor` (`ExtractionUnsupported` if not).
3. Takes the operation you named, compared case-insensitively (`OperationUndefined` if the connector does not declare
   it), or the connector's first operation when you pass `null` or blank.
4. Reads the credentials from the site's secret store (`CredentialsMissing`, `CredentialsUnreadable`).
5. Builds the site's egress and runs the connector, reading at most `maxRecords` records. `maxRecords` is clamped to
   1 to 100,000 (`ConnectorExtractionLimits.Clamp`); the default is 10,000. It reads one record past the cap to know
   whether the cap cut the read short.

The answer is a `ConnectorExtractionResult(Success, Outcome, Message, Records, Truncated)`:

- `Success` and `Outcome` (`ConnectorOutcome`): see the table in [the overview](overview.md#testing-a-connection-and-outcomes).
- `Message`: fixed text for the outcome, or on success "Extracted N record(s)" and whether the cap was reached. Never
  the connector's or the remote's own words.
- `Records`: `ConnectorRecord` values, each a dictionary of named scalar fields.
- `Truncated`: `true` when more records were available than the cap.

A failure is a result, not an exception. Only a cancellation of your own token is thrown.

The `files` overload hands the connector a folder to read uploads from and write downloads to; a pipeline run passes its
run folder. Outside a run, pass none: an operation that needs files then refuses.

### What the parameters are

Each connector declares its operations and their parameters. Read them from the catalogue: `GET
api/Connections/connectors` answers `ConnectorView.Operations`, each with `Name`, `DisplayName`, `Description` and
`Parameters` (name, type, required, default, help). Parameter values are strings. Two conventions the built-in
connectors share: a SQL statement, an SSH command or a message body goes in the `query` parameter, and HTTP connectors
take extra headers as `headers` (one `Name: value` per line). For example, the generic REST connector (`rest`)
declares `read` (parameters `method`, `path`, `body`, `content_type`, `headers`, `records_path`) and `send`.

### Permissions

The port decides **no permission**. It is a building block: the caller decides who may run it. Gate the endpoint or
command that calls it with a permission key, either one of yours or an existing one such as
`ConnectionPermissions.View` / `ConnectionPermissions.Manage` (`connections:view`, `connections:manage`). See
[identity and permissions](../security/identity-and-permissions.md).

Listing and managing connections, rather than running them, goes through `IConnectionsService`
(`Stomme.Modules.Connectivity.Services`, in the `Stomme.Client` package). On the server it dispatches mediator
requests that require `connections:view` or `connections:manage` of the signed-in user; in the browser it calls
`api/Connections`. Because those requests check the signed-in user, they are refused in a background job that has
no user - as is every mediator request that carries `[Requires]`, your own module's commands included (a
`CreateNoteCommand` sent from a job scope throws `AccessDeniedException`).

### Site and tenant

The port works on the scope's site and tenant:

- **In a request** (a controller, a page), the site and tenant come from the request's alias. Nothing to do.
- **In a background job or a hosted service**, there is no request. Create a scope, enter the site with
  `ITenantManager.SetAliasAsync(tenantId, siteId)` (`Stomme.Infrastructure`), then resolve the port from that scope.
  Without it the connection is not visible to the scope's tenant and the answer is `ConnectionNotFound`. See
  [jobs](../services/jobs.md) for how a job walks tenants and sites.
- **In a pipeline step**, use the built-in Extract from connection step if it fits. A custom step must enter the run's
  site before calling the port; see [custom pipeline steps](../pipelines/custom-steps.md).

## Using it from an app

A service in the Notes module that reads notes from an external API through one of the site's connections
(`Server/Modules/Notes/Services/NoteImportReader.cs`):

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

Register it in the module's `NotesServerStartup.ConfigureServices`, before the `INotesService` line that comes last:

```csharp
services.AddScoped<NoteImportReader>();
```

The sample assumes a `rest` connection whose endpoint is the API's address. The REST connector resolves `path` against
the endpoint as a relative URL, so a path that starts with `/` replaces any path the endpoint has, and a non-blank path drops the endpoint's query
string (write the query inside `path`, or in the `query` parameter). `ConnectorRecordJson.Serialize(records)` (`Stomme.Connectivity`) turns records into a JSON array string, the
same shape pipeline steps pass on.

The [stomme-use-connection](../../.claude/skills/stomme-use-connection/SKILL.md) skill adds a gated API endpoint that
previews and imports the records as notes, and a job-safe read around this.

### Choosing the connection

Store the connection's id (a `Guid`) in your module's settings or aggregate, chosen by an administrator from the site's
connections. List them with `IConnectionsService.ListAsync()` in your settings component, and keep only those whose
`ConnectorType` your code supports. Do not search by name: names are editable.

Implement `IConnectionUsageProvider` (`Stomme.Connectivity`) when your module stores connection ids, and register it
in your startup (`services.AddScoped<IConnectionUsageProvider, YourUsageProvider>()`). The Connectivity module then
refuses to delete a connection your module still uses, with your description as the reason.

### Reacting to connection changes

The Connectivity module raises domain events (`Stomme.Modules.Connectivity.Domain`): `ConnectionCreatedEvent`,
`ConnectionReconfiguredEvent`, `ConnectionTestedEvent`, `ConnectionFailedEvent`, `ConnectionDeletedEvent`. Their
messages are the fixed outcome texts. See [events and messaging](../services/events-and-messaging.md) for handling a
domain event.

## Configuration

None of its own. The site's allowlist (the Private hosts panel, Host only) decides which private hosts a connection may
reach; see [the overview](overview.md#the-egress-where-a-connection-may-go).

## Rules and pitfalls

- **Gate every caller.** The port has no permission check. An endpoint that runs it ungated lets any signed-in user,
  or an anonymous one, read the external system with the site's stored credentials.
- **Never let a caller choose an arbitrary connection id without a permission check**: the id is the key to the
  credentials. Accept ids only from users who may use connections, or from your own stored settings.
- **Do not show the result message as the remote's error.** It is a category. For the cause, read the server log,
  which names the connection id and the exception type.
- **An HTTP timeout is the `TimedOut` outcome, not an exception** (since Stomme 10.73.0). When a connector's
  `HttpClient` times out during an extraction, the port returns a failed result with `TimedOut`; only a cancellation of
  your own token passes through as an `OperationCanceledException`. Before 10.73.0 the port let the connector's
  `TaskCanceledException` propagate, so code written for an older version may still catch
  `OperationCanceledException` when its own token was not cancelled - harmless, but no longer needed.
- **In a job, enter the site first.** Without `SetAliasAsync` the port answers `ConnectionNotFound` for a connection
  that exists.
- **Do not test the job path from inside a request.** A scope you create in a controller action still sees the
  request's `HttpContext` (it flows with the async context), so the signed-in user's commands pass where a real job's
  are refused, and a read without `SetAliasAsync` fails differently (here with "No database provider has been
  configured for this DbContext") instead of `ConnectionNotFound`. Test it from a real job or hosted service.
- **Cap what you read.** Records are held in memory; ask for what you need, and check `Truncated`.
- **Writes are operations too.** Operations such as `row_create` or `message` change the remote system. Make the
  calling endpoint a POST, gated with a manage-level key.
- **Parameter names are the connector's.** An unknown parameter is ignored by most connectors, so a typo reads as "no
  filter". Read the operation's declared parameters from the catalogue.

## Related

- [Connectors and connections](overview.md)
- [Writing a connector](writing-a-connector.md)
- [Datasets, dashboards and formats](../data/datasets-dashboards-formats.md)
- [Jobs](../services/jobs.md)
- [API and client services](../modules/api-and-client-services.md)
- Skill: [stomme-use-connection](../../.claude/skills/stomme-use-connection/SKILL.md)
