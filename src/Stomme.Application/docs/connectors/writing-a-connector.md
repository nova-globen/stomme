# Writing a connector

A connector is a class in your app's `Server` project that implements `IConnector` (and `IConnectorExtractor` when it
offers operations), registered as a singleton from an `IServerStartup`. Once registered it is in the site's connector
catalogue: administrators create connections to it on the Connections page, pipelines run its operations, and your code
calls it through the extraction port. This document explains the contract. The
[stomme-write-connector](../../.claude/skills/stomme-write-connector/SKILL.md) skill builds one end to end.

## How it works

### Registration and discovery

1. Your `Server` project's assembly name ends in `.Stomme`, so the host's startup scan loads it and runs every
   `IServerStartup` (`Stomme.Infrastructure`) it contains. Each needs a public parameterless constructor.
2. Your startup calls `services.AddSingleton<IConnector, YourConnector>()`.
3. The host's `IConnectorCatalog` collects every registered `IConnector` by `ConnectorType` (case-insensitive). The
   catalogue is built the first time something resolves it; a type key registered twice makes that build throw,
   naming both classes. The host still starts: the Connections page and every `api/Connections` read then fail with
   500 (the write and test routes answer 400 or 404), a connection step faults its run, and the event log
   (Admin > Event Log) carries the message.

The connector is a **singleton shared by every site and tenant** of the installation. Keep no per-call state in fields.
Everything that belongs to one call arrives in the context.

### The contract (`Stomme.Connectivity`, in the `Stomme.Shared` package)

`IConnector`:

| Member | Default | Meaning |
| --- | --- | --- |
| `string ConnectorType` | required | The stable key stored on every connection. Lower case, never changed once shipped. |
| `string DisplayName` | required | Shown in the picker and the list. |
| `ConnectorConfigSchema ConfigSchema` | empty | The settings a connection carries (not secrets). |
| `AuthenticationType AuthType` | `None` | Decides the credential field names (see [the overview](overview.md#credentials-and-authentication-schemes)). |
| `ConnectorEndpointKind EndpointKind` | `Url` | `Url`: absolute `http`/`https` only. `ConnectionString`: anything else. |
| `string? EndpointExample` | `null` | The placeholder in the connection form. It must be an endpoint the kind accepts. |
| `bool HostOnly` | `false` | `true` when the connector reaches the host itself (a process, the file system): only the Host role may create or edit its connections. |
| `IReadOnlyList<ConnectorCategory> Categories` | empty | Picker groups (`DataAndFiles`, `Messaging`, `Development`, `Productivity`, `Commerce`, `Crm`, `Marketing`, ...). Empty lists it under Uncategorized. |
| `Uri? WebsiteUrl`, `Uri? ApiDocumentationUrl` | `null` | Links in the form. |
| `string? Icon` | `null` | An inline SVG rendered as markup. `null` gives the neutral monogram. |
| `IReadOnlyDictionary<string, string>? CredentialLabels` | `null` | Labels for the scheme's fields in the service's words, keyed by the scheme's field name. |
| `ConnectorCapabilities Capabilities` | computed | `TestConnection`, plus `Extract` when the class implements `IConnectorExtractor`. Do not override. |
| `Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext, CancellationToken)` | required | Proves the endpoint, credentials and settings work. |

`IConnectorExtractor`:

| Member | Meaning |
| --- | --- |
| `IReadOnlyList<ConnectorOperation> Operations` | What the connector can do. The first one is the default when a caller names none. |
| `IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext, CancellationToken)` | Runs `context.OperationName` with `context.Parameters` and yields records. |

Despite the name, an operation may write: create a row, send a message, upload a file. It yields the records that
describe the result (an id, a status).

### The contexts

`ConnectorTestContext(Endpoint, Settings, Timeout, Egress)` with `Credentials`, and
`ConnectorExtractContext(Endpoint, Settings, Timeout, Egress, OperationName, Parameters)` with `Credentials`,
`FileSink` and `Files`:

- `Endpoint`: the connection's endpoint as stored, trimmed.
- `Settings`: the connection's settings, keyed by field name. Absent keys are absent; defaults are not applied.
- `Credentials`: the scheme's fields resolved from the site's secret store, keyed by field name (`api-key`,
  `username`, ...).
- `Timeout`: the connection's timeout.
- `Egress`: the site's `IConnectorEgress`. The only network you may use.
- `OperationName`: the operation as the caller wrote it. The host has already checked it case-insensitively against
  `Operations`; compare it case-insensitively yourself.
- `Parameters`: the operation's parameters, keyed by name. **The host does not validate them against the
  operation's schema**; your connector must.
- `FileSink` (`IConnectorFileSink`): stores a file the operation produced into a folder of the site, through the
  host's upload checks, and returns the file's id. `MaxBytes` is the per-file cap.
- `Files` (`IConnectorFiles`): inside a pipeline run, the run's folder: `OpenReadAsync(path)` to upload a file,
  `WriteAsync(path, stream)` to save a download, paths relative to the folder. `null` outside a run; an operation that
  needs it must refuse.

### Settings and parameters: `ConnectorConfigSchema`

```csharp
new ConnectorConfigSchema(new[]
{
    new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Select, Required: true, Description: "The data centre.")
    {
        Options = new[] { new ConnectorConfigFieldOption("eu", "Europe"), new ConnectorConfigFieldOption("us", "United States") },
        Default = "eu",
    },
    new ConnectorConfigField("page_size", "Page size", ConnectorConfigFieldType.Integer) { Default = "100" },
});
```

Field types: `Text`, `Secret`, `Integer`, `Boolean`, `Select`, `Url`, `Multiline`, `Number`, `Duration` (whole seconds
or `hh:mm:ss`). `Group`, `Placeholder`, `HelpText` and `VisibleWhen` (show while another field equals a value) shape the
form. The host validates a connection's **settings** against `ConfigSchema` on create and update. A `Secret` setting is
never shown by the form: put secrets in the authentication scheme, not in settings.

An operation is `new ConnectorOperation(name, displayName, description) { Parameters = new ConnectorConfigSchema(...) }`.
The pipeline editor and the catalogue show its parameters.

### Records

`ConnectorRecord(IReadOnlyDictionary<string, object?> Fields)`: named scalar fields. Use `string`, `long`, `int`,
`double`, `decimal`, `bool`, `DateTime`, `DateTimeOffset`, `Guid` or `null`. Consumers turn records into JSON with
`ConnectorRecordJson.Serialize`, which writes those (and `short` and `float`) as JSON values and anything else as its
`ToString()`. Put a nested
object or list in a field as its JSON text.

### The egress rules

1. **HTTP:** `using var client = context.Egress.CreateHttpClient(context.Timeout);`. Never `new HttpClient()`, a
   handler of your own, a socket, a DNS lookup or a process. The egress client refuses private and metadata addresses
   unless the site allowlisted the host, does not follow redirects, and times out at the timeout you pass.
2. **Drivers** (a database client, an LDAP or AMQP library): parse the host and port from the endpoint, call
   `await context.Egress.ResolveAndPinAsync(host, port, cancellationToken)`, and make the driver dial **that address**
   (rewrite the host in the connection string to the returned address, keeping the original name for TLS validation
   where the driver allows it). When it returns `null`, throw `ConnectorEgressDeniedException`. A driver that resolves
   the name again on its own reopens the gap the check closed.
3. **A driver with no timeout of its own:** call `context.Egress.ReachAsync(address, port, host, timeout, "Service
   name", cancellationToken)` before the driver connects. It throws `InvalidOperationException` when nothing answers in
   time, and the egress's denial when the address is refused.
4. **A refusal is the host's, not yours.** Catch exceptions only `when (!context.Egress.IsDenial(ex))`. A denial must
   propagate so the host reports `DeniedByPolicy`; folded into a `Fail` it reads as a broken remote.

### Test connection

Return `ConnectionTestResult.Ok(message)` or `ConnectionTestResult.Fail(message)`. Make the test exercise the
credentials, not only reachability: an authenticated, cheap read (who am I, list one item). The host:

- decides `CredentialsMissing` and `CredentialsUnreadable` before calling you;
- cancels your call when the connection's timeout elapses (`TimedOut`);
- maps `Ok` to `Succeeded`, `Fail` to `Failed`, a denial to `DeniedByPolicy`, a `TimeoutException` or
  `TaskCanceledException` to `TimedOut`, `ConnectorHostKeyUntrustedException` to `HostKeyUntrusted`, and any other
  exception to `Faulted`;
- **discards your message.** It reaches neither the page nor the log. Write it for your own tests.

A success answer that is not the format you expect (a sign-in page or a proxy answering HTML with HTTP 200) is a
failure, not an empty success.

### Extraction

The host's extraction port resolves the connection, the operation (the first declared when none is named), the
credentials and the egress, then reads your records **up to the caller's cap plus one** and stops enumerating. Yield
records as you read them; honour the cancellation token. An exception from your enumerator becomes a category
(`DeniedByPolicy`, `HostKeyUntrusted`, `TimedOut` or `Faulted`); its message is discarded and the log names its type.
The `TaskCanceledException` an `HttpClient` throws when its timeout elapses is categorised too: since Nova Globen Stomme 10.73.0
the port reports a cancellation the caller did not ask for as `TimedOut`, and only a cancellation of the caller's own
token passes through as an exception. (Before 10.73.0 the port let that `TaskCanceledException` propagate to its
caller, and a pipeline's Extract from connection step faulted with the exception's own message.) A connector that turns
its timeout into a `TimeoutException` itself is harmless but no longer needed; the port reports a `TimeoutException` as `TimedOut` as well:

```csharp
private static async Task<HttpResponseMessage> TimeoutAsTimeoutException(Task<HttpResponseMessage> sending, CancellationToken cancellationToken)
{
    try
    {
        return await sending;
    }
    catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
    {
        throw new TimeoutException("The service did not answer within the connection's timeout.", ex);
    }
}

// using var response = await TimeoutAsTimeoutException(client.SendAsync(request, cancellationToken), cancellationToken);
```

### Helpers you may use

The framework's own connectors share public helpers in `Stomme.Modules.Connectors.Shared` (in the `Stomme.Server`
package). They are optional; your connector depends only on the contract if you do not use them.

| Type | Use |
| --- | --- |
| `Values.Get(dict, key)` | The trimmed value, or `null` when absent or blank. `GetOrDefault`, `GetBool` beside it. |
| `Values.BaseUrl(endpoint, fallback)` | The endpoint as an absolute `http(s)` base URL without a trailing slash; throws `InvalidOperationException` otherwise. |
| `RetryAfterPolicy.Default.SendAsync(client, () => request, ct)` | Sends a fresh request until the answer is not 429 or 503, honouring `Retry-After`; three retries, delays capped at 30 seconds. Takes a factory because a request cannot be sent twice. Use it for requests that are safe to repeat. |
| `JsonAnswers.NotJsonAsync(response, "Service", ct)` | A failure message when a success answer is not JSON; `null` when it is JSON or empty. |
| `JsonRecords.Parse(payload, parameters)` | A JSON array (top level, under the `records_path` parameter, or under `values`/`value`/`data`) as records; integers stay `long`. |
| `OAuth2TokenProvider` | Client-credentials and refresh-token grants through the egress, with a cache per credential set. Keep one instance in a field of your singleton. |
| `BearerClient.Create(egress, timeout, token)` | An egress client with a bearer token set. |

### Catalogue metadata and the brand rule

Give every connector at least one category, an `https` `WebsiteUrl` for a vendor product, and an `EndpointExample`
that its `EndpointKind` accepts. Leave `Icon` `null`: the catalogue then draws a neutral monogram from the display name
and the first category's colour. Do not embed a vendor's logo unless you hold the right to use it on this surface; the
page already prints a notice that product names identify the products and imply no affiliation. `Icon` is rendered as
raw markup, so it must be a constant you wrote, never text built from input.

## Using it from an app

A complete connector, its startup and its tests are in the
[stomme-write-connector](../../.claude/skills/stomme-write-connector/SKILL.md) skill. The minimal shape:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace Stomme.Application.Server.Connectors.Ping
{
    public sealed class PingConnector : IConnector
    {
        public string ConnectorType => "app-ping";

        public string DisplayName => "Ping";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!Uri.TryCreate(context.Endpoint, UriKind.Absolute, out var uri))
            {
                return ConnectionTestResult.Fail("The endpoint is not an absolute URL.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            try
            {
                using var response = await client.GetAsync(uri, cancellationToken);
                return response.IsSuccessStatusCode
                    ? ConnectionTestResult.Ok($"HTTP {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail($"HTTP {(int)response.StatusCode}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }
    }
}
```

```csharp
using Stomme.Application.Server.Connectors.Ping;
using Stomme.Connectivity;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Application.Server.Connectors
{
    public class ConnectorsStartup : IServerStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IConnector, PingConnector>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }
    }
}
```

### Testing a connector

A connector depends only on its context, so a unit test hands it a stub `IConnectorEgress` whose `CreateHttpClient`
returns an `HttpClient` over a fake `HttpMessageHandler`. Assert the request the handler saw (URL, headers, body), the
records yielded, `Fail` for a bad status, and that an exception the stub egress calls a denial propagates. The skill
has a complete test class. Then drive it for real: create a connection on the Connections page, press Test, and run an
operation from a pipeline's Extract from connection step (pipelines need `Pipelines:Enabled` on) or from your own
code through the extraction port.

## Rules and pitfalls

- **Prefix your type key** (for example `myapp-orders`). A later Stomme release that ships a connector with the same
  key makes the catalogue throw on your installation, and the Connections page and every connection read answer 500.
- **Never change a shipped key.** Existing connections keep the old key and show as not installed.
- **No network of your own.** A `new HttpClient()` works on your machine and bypasses the site's policy: it can reach
  the installation's own network and the cloud metadata endpoint.
- **Do not swallow the denial.** `catch (HttpRequestException)` without `when (!context.Egress.IsDenial(ex))` turns a
  policy refusal into `Failed`, and the operator looks for a fault in the remote.
- **No `yield return` inside a `try` with a `catch`.** C# forbids it. Validate before you start yielding, and let
  failures inside the loop throw: the host categorises them.
- **Validate parameters yourself** and give a clear exception; the host does not check them.
- **The connection timeout is not a cancellation during extraction.** Only a test is cancelled at the timeout. The
  egress `HttpClient` you created with `context.Timeout` times out each request; a driver must apply the timeout itself.
- **Credentials can be absent.** A connection created without a credential has no stored row and no missing one, so
  the host calls you with an empty `Credentials`. Check and fail with a message that says which field to enter.
- **Do not retry non-idempotent writes** with `RetryAfterPolicy`: a 503 after the remote accepted a create would
  create twice.
- **The connector is a singleton.** Inject only singletons into its constructor; a cache must be keyed by everything
  that identifies the caller (the endpoint and the credentials), because one instance serves every site.
- **Use `Url` only for `http(s)`.** Any other scheme needs `ConnectionString`, or every connection to it is refused.

## Related

- [Connectors and connections](overview.md)
- [Using connections in code](using-connections-in-code.md)
- [Startup and discovery](../architecture/startup-and-discovery.md)
- [Security posture](../security/security-posture.md)
- [Custom pipeline steps](../pipelines/custom-steps.md)
- Skill: [stomme-write-connector](../../.claude/skills/stomme-write-connector/SKILL.md)
