# The MCP server

Nova Globen Stomme can publish a site's resources and tools to AI clients over the Model Context Protocol (MCP). Modules -
Stomme's own and your app's - contribute what is published; the server lists and runs only what the calling user may
see and use on the site the request reached, and writes every call to an audit ledger. It is off by default.

## How it works

### The switches

1. `Mcp:Server:Enabled` (configuration, default `false`) decides whether the endpoint EXISTS. It is read while the host
   is composed: with it off, no endpoint is mapped, no contributor is looked for, and no catalog is registered. Changing
   it needs a restart.
2. The feature flag `mcp.server` (default on) is the runtime half: with it off for a tenant, the catalog lists nothing
   and runs nothing, and each refusal is still written to the ledger. A flag is per tenant, so on an installation where
   every site shares one tenant it is installation-wide.
3. Each module's own feature flag, when it declares one (for example `datasets.mcp` and `dashboards.mcp`, both default
   off), and each entry's permission key.

### The endpoint

- Route: `Mcp:Server:Path`, default `/mcp/v1`. It must start with `/`; any other value stops the host at startup with an
  `InvalidOperationException` naming the key.
- Transport: MCP Streamable HTTP, STATELESS - each POST is answered on its own, no session, no `initialize` needed before
  `tools/list`. Several host instances behind one host name work for that reason.
- It is mapped after tenant resolution, so every call is answered for the site the request's host name resolves to.
- It requires an authenticated user (`RequireAuthorization()` against the default scheme, the site's sign-in cookie).
- Answers to an anonymous caller, per verb: `POST` 401; `GET` and `DELETE` 404, because a stateless transport maps
  neither verb (an SDK client's anonymous failure therefore shows as 404, not as an authorization answer).
- A rate limit per caller and per site: `Mcp:Server:RequestsPerMinute` (default 60) in a fixed one-minute window,
  answered 429 with `{"error":"Too many requests."}` (and a `Retry-After` header when the limiter supplies one). `0`
  removes the limit. Anonymous requests are partitioned by remote address. The window is
  held per host instance.
- The verbs served: `tools/list`, `tools/call`, `resources/list`, `resources/templates/list`, `resources/read`. A
  resource whose URI is a template (contains `{`) is listed by `resources/templates/list`, never by `resources/list`.
- Unknown and refused give one answer: a tool call answers a result with `isError: true` and the text "No such tool, or
  it is not available to you."; a resource read answers a JSON-RPC error "No such resource, or it is not available to
  you." Both arrive with HTTP 200. The endpoint does not reveal what exists. A tool's exception text is logged and never
  returned; the caller gets `{"error":"The tool failed."}`.
- A POST needs the session cookie only: no antiforgery token is checked on this route.
- Every refused or unknown `resources/read` writes an Error line to the server log from the MCP library
  (`ReadResource "..." threw an unhandled exception`, an `McpException` with the message above). That is the refusal
  travelling, not a fault.

### Authentication today

The endpoint accepts the site's sign-in cookie only. An MCP client must carry a signed-in session of a user of that site.
There is no `WWW-Authenticate` challenge and no OAuth flow for MCP clients: token authentication for external MCP
clients is not built. (Stomme's own OpenID Connect provider issues tokens that its `/api/` routes accept while
`OpenIddict:Enabled` is on; the MCP route is not under `/api/`.)

### Contributors

What is published comes from contributors. The contract is in `Stomme.Shared`, namespace `Stomme.Mcp`:

- `IMcpContributor` - one unit holding descriptors AND their handlers:
  - `string ContributorId` - stable, for logs and the ledger, for example `myapp.notes`. Compared ignoring case.
  - `IReadOnlyCollection<McpResourceDescriptor> Resources`
  - `IReadOnlyCollection<McpToolDescriptor> Tools`
  - `IMcpResourceReader? ReaderFor(string uriTemplate)` - the reader for a resource THIS contributor published.
  - `IMcpToolInvoker? InvokerFor(string toolName)` - the invoker for a tool THIS contributor published.
- `IMcpResourceReader.ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken)` returns an
  `McpResourceContent(uri, mimeType, text)`, or null for "no such resource".
- `IMcpToolInvoker.InvokeAsync(string argumentsJson, CancellationToken)` returns an
  `McpToolResult(contentJson, isError)`. The arguments arrive as the JSON text of the call's `arguments` object, or an
  empty string when there were none. The result's `ContentJson` is sent to the client as one text block.
- `McpResourceDescriptor(uri, name, description, mimeType, requires)` - the URI has a scheme (`myapp://notes`) and no
  surrounding white space; it may be a template with whole-segment parameters (`myapp://notes/{noteId}`).
- `McpToolDescriptor(name, description, parametersJsonSchema, requires, approval)` - the arguments' JSON Schema as text;
  an empty one becomes `{"type":"object"}`, and text that is not JSON is listed as the empty object schema.
- `McpAccessGate(feature, permission)` - one condition; `McpAccessGate.ForPermission(...)` and
  `McpAccessGate.ForFeature(...)` name one axis. A gate naming neither throws.
- `requires` is a list of gates, ALL of which must pass. An empty list throws at construction: there is no "visible to
  everyone".

### Tool names and approval

`McpToolNames` holds the rules:

- A name is 1 to 64 characters of lower-case ASCII letters, digits, `_`, `-` and `.`, starting with a letter. Anything
  else throws at construction; nothing is rewritten.
- A name starting with `create_`, `update_`, `delete_`, `execute_`, `run_`, `export_` or `send_` says the tool changes
  something (`McpToolNames.IsMutating`). Such a tool cannot be constructed as `Auto`.

`McpToolApproval`:

| Value | Effect today |
|---|---|
| `Auto` | Runs when the gates pass. |
| `Confirm` | DENIED. There is no approval pipeline; the call is refused, written to the ledger as `ConfirmDenied`, and logged at Error. Listed in `tools/list`. |
| `Deny` | Never runs, answered like an unknown tool, and not listed. |

A site may override a tool's approval with the Site setting `mcp.tool.{name}.approval` (`Auto`, `Confirm` or `Deny`, by
name). The override can tighten any tool and can open a `Confirm` tool whose name is not mutating; it cannot open a
`Deny` tool and cannot make a mutating-named tool `Auto` (that override is ignored and logged). A value that is not one
of the three names - a typo, a number - reads as `Deny`.

Consequence: **a tool that changes data does not run over MCP today.** Publish read-only tools as `Auto`; a mutating
tool may be published as `Confirm` so it is listed and its attempts are audited, ready for the day an approval pipeline
exists.

### Discovery and registration

Two ways for a contributor to reach the catalog, and both land in the same registry:

1. **Explicit registration** in the module's `IServerStartup.ConfigureServices`, for a contributor with constructor
   dependencies (the usual case - it needs the mediator or a repository). Register it as scoped, and only when the
   server half is composed:

   ```csharp
   if (McpSettings.ServerEnabled(HostConfiguration.Resolve(services)))
   {
       services.AddScoped<Stomme.Mcp.IMcpContributor, NotesMcpContributor>();
   }
   ```

   `McpSettings` is in `Stomme.Modules.Mcp.Startup`; `HostConfiguration` is in `Stomme.Infrastructure`.
2. **Convention discovery**, while the server half is on: every concrete, non-generic `IMcpContributor` with a public
   parameterless constructor, in a loaded assembly whose name contains `Stomme`, is registered as a singleton. The
   app's projects are named `*.Stomme` (see the `AssemblyName` in each `.csproj`), so the convention reaches them.

### Duplicate names are rejected on every side

The registry is built once at startup (and logged then) and again per request. It drops, and does NOT pick a winner:

- both contributors when two claim the same `ContributorId`;
- every contributor's tool when two publish the same tool name, and every later one of the same name;
- every contributor's resource when two publish the same URI;
- a plain URI and a template that matches it (for example `myapp://notes/list` and `myapp://notes/{noteId}`), both;
- a contributor with a blank `ContributorId`, a null descriptor, or a descriptor its contributor has no reader or
  invoker for.

The host still starts. Each rejection is logged once at startup at **Error**:
`MCP contribution REJECTED - {Kind} '{Name}' from {Contributors}: {Reason} It is not served. Remove or rename the
conflicting contribution to restore it.` The rejected name is simply not published. Stomme's own contributors are not
privileged: a module that publishes `stomme://site` removes the framework's resource too.

### The catalog, per call

For each verb the catalog (`IMcpServerCatalog`, namespace `Stomme.Mcp`):

1. names the caller from the request: the resolved site, the tenant and an authenticated user with a user id. With any of
   them missing it lists nothing and runs nothing, and writes nothing to the ledger;
2. checks the `mcp.server` flag for the tenant;
3. checks every gate of the descriptor through `IAccessGuard` - each gate's feature flag on and its permission held by
   the caller on this site;
4. for a tool, applies the effective approval;
5. dispatches to the reader or invoker of the contributor that published the descriptor, and no other;
6. writes a ledger row for every `tools/call` and `resources/read` - `Succeeded`, `Failed`, `Denied` or
   `ConfirmDenied`. `Denied` covers both a caller who fails a gate and a name that does not exist; a reader that
   answers null and a tool result with `isError` are written as `Failed`. Listing is not audited. A row keeps the subject, the user, the site, the time and, for a tool, the
   SHA-256 and length of the arguments - never the arguments. A failure to write the row fails the call.

### What Stomme itself publishes

| Contributor | Resources | Tools | Gates |
|---|---|---|---|
| `stomme` | `stomme://site` (the site's name, render mode, runtime) | none | `mcp:view` |
| `stomme.datasets` | `stomme://datasets`, `stomme://datasets/{datasetId}/summary` | `query_dataset` | flag `datasets.mcp` and `datasets:view` (resources) or `datasets:preview` (tool) |
| `stomme.dashboards` | `stomme://dashboards`, `stomme://dashboards/{dashboardId}` | `get_widget_data` | flag `dashboards.mcp` and `dashboards:view` |
| `stomme.pipelines` | `stomme://pipelines`, `stomme://pipelines/{definitionId}`, `stomme://pipelines/runs/{runId}`, `stomme://pipeline-steps` | read-only tools such as `list_pipelines` and `get_pipeline_run` (`Auto`); `run_pipeline`, `send_pipeline_event` and the other tools that start or stop runs are `Confirm`, so they are denied today | `pipelines:view`, `pipelines:manage` or `pipelines:run`; no feature flag |

The Datasets, Dashboards and Pipelines contributors exist only while their modules are composed. The pipelines'
contributor names its own resource `stomme://pipeline-steps` rather than `stomme://pipelines/steps` because the second
would be matched by its template `stomme://pipelines/{definitionId}` - the shadowing rule above.

### Permissions and the admin page

`Stomme.Mcp.McpPermissions` (in `Stomme.Shared`), all three granted to Administrators only on a new site:

| Key | Allows |
|---|---|
| `mcp:view` | See what the site publishes (as the caller may use it); the gate on `stomme://site`. |
| `mcp:view-audit` | Read the site's MCP ledger - every user's calls. |
| `mcp:manage` | Register and manage external MCP servers (see [the MCP client](mcp-client.md)). |

The admin page `/admin/mcp` exists while either `Mcp:Server:Enabled` or `Mcp:Enabled` is on (it is added to existing
sites at the next start). Its main control lists external servers (`mcp:manage`); with only `Mcp:Server:Enabled` on it
says the client half is not enabled. Two more controls are module actions of the same page: **Catalog** (`mcp:view`) -
what the site publishes, as the viewer may use it, with each tool's effective approval - and **Audit**
(`mcp:view-audit`) - the ledger for the last 1, 7, 30 or 90 days (default 7). The page has no link to these two today;
open them by the action address, `/admin/mcp/*/{moduleId}/Catalog` or `/admin/mcp/*/{moduleId}/Audit`, where
`{moduleId}` is the id of the MCP module on that page (`GET api/Module?siteid={siteId}` lists every module with its
`moduleId`, `pageId` and `moduleDefinitionName`; the MCP one is `Stomme.Modules.Mcp, Stomme.Client`).

The API behind them, under `api/Mcp`: `GET api/Mcp/published` (`mcp:view`, 404 while the server half is off) and
`GET api/Mcp/audit?days=7` (`mcp:view-audit`, served whatever the switches say, because the ledger outlives them).

## Using it from an app

Publish a module's read operations as MCP resources and tools: declare a feature flag, gate every descriptor on the flag
and a permission key, read through the module's own permission-gated queries, and register the contributor while the
server half is on. The full task with complete code is the skill
[stomme-expose-module-to-mcp](../../.claude/skills/stomme-expose-module-to-mcp/SKILL.md).

The shape of a contributor (an outline, not a complete file - the skill has the complete, compiled one):

```csharp
public sealed class NotesMcpContributor(IMediator mediator) : IMcpContributor, IMcpResourceReader
{
    private static readonly IReadOnlyList<McpAccessGate> Gate = new[] { new McpAccessGate("notes.mcp", NotesPermissions.View) };

    public string ContributorId => "myapp.notes";

    public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } = new[]
    {
        new McpResourceDescriptor("myapp://notes", "Notes", "The site's notes: id and title.", "application/json", Gate),
    };

    public IReadOnlyCollection<McpToolDescriptor> Tools { get; } = Array.Empty<McpToolDescriptor>();

    public IMcpResourceReader? ReaderFor(string uriTemplate) => uriTemplate == "myapp://notes" ? this : null;

    public IMcpToolInvoker? InvokerFor(string toolName) => null;

    // ReadAsync(...) reads through the mediator, so the module's [Requires] keys decide a second time.
}
```

### Calling the endpoint by hand

With a signed-in browser session's cookie, a JSON-RPC POST lists the tools the user may use on that site:

```
POST /mcp/v1
Content-Type: application/json
Accept: application/json, text/event-stream

{"jsonrpc":"2.0","id":1,"method":"tools/list"}
```

The answer is either plain JSON or one server-sent event whose `data:` line carries the JSON. `tools/call` takes
`{"name": "...", "arguments": {...}}`; `resources/read` takes `{"uri": "..."}`.

## Configuration

| Key | Where | Default | Effect |
|---|---|---|---|
| `Mcp:Server:Enabled` | configuration | `false` | The endpoint, the catalog and contributor discovery exist. Restart after changing it. |
| `Mcp:Server:Path` | configuration | `/mcp/v1` | The route; must start with `/`. |
| `Mcp:Server:RequestsPerMinute` | configuration | `60` | Per caller and site; `0` = no limit; a negative or unreadable value reads as 60. |
| `mcp.server` | feature flag (per tenant) | on | The runtime switch for publishing. |
| `datasets.mcp`, `dashboards.mcp`, your module's flag | feature flags (per tenant) | off | Each module's MCP surface. |
| `mcp.tool.{name}.approval` | Site setting | the tool's own approval | Tighten (or open a non-mutating `Confirm`) one tool on one site. |
| `McpAuditRetention` | Site setting | `90` days | Ledger retention, applied by the framework's purge job; values below 7 read as 7. |

Feature flags are set on the Feature Flags page (`/admin/features`, Host to change) or in the `FeatureManagement`
section of configuration (for example `Server/features.json`), which wins over the page. A change to
`Server/features.json` applies to the next call without a restart. See
[configuration](../architecture/configuration.md).

## Rules and pitfalls

- **Nothing appears and nothing errors.** Check in order: `Mcp:Server:Enabled` (restart after setting it), the
  `mcp.server` flag, the module's own flag (off by default), the caller's permission key on THIS site, the tool's
  approval. A listing that is empty because of a gate looks exactly like one with nothing registered.
- **A permission key no module declares denies everyone.** A gate on a key that no `ModuleDefinition.PermissionNames`
  declares has no permission rows, so it is never satisfied. Declare your key in the module's client `ModuleInfo`.
- **The MCP keys are Administrators-only on a new site.** To let other users read `stomme://site` or the Catalog page,
  grant `mcp:view` to their role.
- **A name collision is silent to the caller.** The tool or resource is not served; the only trace is the Error line at
  startup. Prefix your URIs with your app's own scheme (`myapp://`), never `stomme://`, and give tool names a module
  prefix (`notes_search`, not `search`).
- **A template and a plain URI that it matches cancel each other.** Keep list URIs out of a template's shape: use
  `myapp://notes` for the list and `myapp://notes/{noteId}`, not `myapp://notes/all` beside it.
- **A mutating tool does not run.** `Confirm` is a denial today. Do not dodge the prefix rule with a name like
  `add_note` on an `Auto` tool that writes: the prefixes are what keep a tool that changes data from running unattended.
- **Template arguments arrive raw.** `McpUriTemplate` matches one whole segment per parameter, refuses an empty segment,
  `.` and `..`, and does not URL-decode. Parse and validate each argument (a `Guid.TryParse`, a length bound).
- **Return errors as results.** A malformed call should return `new McpToolResult(json, isError: true)` with a message;
  an exception becomes "The tool failed." and an Error log line.
- **The catalog's gates are not your module's only check.** Read through the module's own `[Requires]`-gated queries so
  the key is decided again, and so the site is the request's. Never return another site's rows.
- **Do not register a contributor while the server half is off.** Guard the registration with
  `McpSettings.ServerEnabled(HostConfiguration.Resolve(services))`, as Stomme's own modules do.
- **Cookie only.** A desktop MCP client that cannot present the site's sign-in cookie cannot connect today.
- **Switching the server half off does not delete the ledger**, and the ledger's API keeps answering.

## Related

- [stomme-expose-module-to-mcp](../../.claude/skills/stomme-expose-module-to-mcp/SKILL.md) - the task
- [The MCP client](mcp-client.md) - external servers a site registers
- [The AI assistant](ai-assistant.md)
- [Identity and permissions](../security/identity-and-permissions.md) - permission keys and declaration
- [Configuration](../architecture/configuration.md) - switches and feature flags
- [Application layer](../modules/application-layer.md) - the mediator and `[Requires]`
