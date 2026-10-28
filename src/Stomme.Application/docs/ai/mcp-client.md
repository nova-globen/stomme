# The MCP client

A site can register external MCP servers, store an API key for each, and discover the tools each one offers. Today this
half is a registry with discovery: it lists what external servers offer, and nothing in Nova Globen Stomme calls those tools yet -
the AI assistant does not use them. It is off by default and independent of the [MCP server](mcp-server.md).

## How it works

### The switch

`Mcp:Enabled` (configuration, default `false`) composes the client half: the registration service, the outbound
connector, the endpoint guard and the tool source. It is read while the host is composed; restart after changing it.
It is a different switch from `Mcp:Server:Enabled` and neither implies the other. The registrations are stored whatever
either switch says, so switching a half off keeps its data.

### A registration

A registration belongs to ONE site (and its tenant). It holds:

- a name, 1 to 100 characters, unique on the site;
- the endpoint: an absolute `http` or `https` URL of at most 2,048 characters, with no user name or password in it. Only
  the Streamable HTTP transport exists; there is no stdio transport, so no registration can start a program on the host.
  The endpoint cannot be changed - a different address is a different registration;
- an optional requests-per-minute value (positive when set). It is stored and shown; because nothing calls external
  tools yet, it limits nothing today;
- whether an API key is stored. The key itself is in the per-site secret store under a name derived from the
  registration's id. It is never read back to any page or API, and removing the registration removes its key first;
- the trust state - `Unverified` until a refresh succeeds, then `Verified` - the time of the last refresh, whether it is
  enabled, and the discovered tools.

### Registering does not dial

A new registration is `Unverified` with no tools. Only an explicit **Refresh** (key `mcp:manage`) connects to the server:

1. It builds an HTTP client through the framework's single outbound guard. The guard decides at CONNECT time, against the
   address the socket actually reaches: loopback, private (RFC 1918), link-local (including `169.254.169.254`),
   unique-local IPv6, unspecified and multicast addresses are refused. Redirects and proxies are off.
2. Hosts on the allowlist are let through anyway. The allowlist is the union of the configuration key
   `Mcp:Client:HostAllowlist` and the Site setting `McpClientAllowedHosts` (comma, semicolon or newline separated).
3. The stored API key, if any, is sent as `Authorization: Bearer <key>` on that guarded client only.
4. It connects, calls `tools/list`, and stops after 20 seconds.
5. What the server offers is bounded before it is kept: at most 256 tools; a name of 1 to 128 characters, unique ignoring
   case (an unusable entry is dropped, the others kept); a description cut to 1,000 characters; a schema over 16 KB
   dropped.

The outcome is one of: done (the tools replace the stored set and the row becomes `Verified`, even when the server offers
none), `Refused` (the guard refused the destination - the message names the host and why), or `Failed` (the server did
not answer as an MCP server, or not within 20 seconds). A refused or failed refresh leaves the row as it was.

### Per site, always

Every by-id operation - refresh, enable, disable, remove, set key - looks the registration up for the request's site.
Another site's id answers exactly like an id that does not exist (`NotFound`), even when both sites share a tenant.

### Reading what is offered

`IMcpClientToolSource.GetAvailableToolsAsync()` (namespace `Stomme.Mcp`, in `Stomme.Shared`) returns an
`McpClientToolDescriptor` (`RegistrationId`, `ServerName`, `Name`, `Description`, `ParametersJsonSchema`) for each tool
of each ENABLED registration of the request's site, as the last refresh stored it. It never dials, and with no resolved
site it returns an empty list. It is list-only: there is no way through it to call a tool.

## Using it from an app

### The admin page

While `Mcp:Enabled` (or `Mcp:Server:Enabled`) is on, the admin page `/admin/mcp` exists (Administrators; added to
existing sites at the next start). Its main control, for a holder of `mcp:manage`, lists the site's external servers -
name, endpoint, state, tool count, whether a key is set, last refreshed - with **Register a server**, **Refresh**,
**Enable**/**Disable**, **Set key** and **Remove**. With only `Mcp:Server:Enabled` on, the page says "The MCP client
half is not enabled on this install (Mcp:Enabled)." and the API behind it answers 404, although the **Register a
server** button is still shown.

### The API

Every route answers 404 while `Mcp:Enabled` is off; each needs `mcp:manage`.

| Method and route | Body | Answer |
|---|---|---|
| `GET api/Mcp/servers` | - | the site's registrations (`McpServerRegistrationView`) |
| `POST api/Mcp/servers` | `{ "name", "endpoint", "maxRequestsPerMinute" }` | the new registration; 400 for a duplicate name or an invalid endpoint, with the reason |
| `POST api/Mcp/servers/{id}/refresh` | - | 200, 404, or 400 with the reason (refused or failed) |
| `POST api/Mcp/servers/{id}/enable` | - | 200 or 404 |
| `POST api/Mcp/servers/{id}/disable` | - | 200 or 404 |
| `DELETE api/Mcp/servers/{id}` | - | 200 or 404 |
| `PUT api/Mcp/servers/{id}/apikey` | `{ "apiKey": "..." }` (empty removes it) | 200 or 404 |

The views and request types (`McpServerRegistrationView`, `McpServerRegistrationRequest`, `McpApiKeyRequest`,
`McpAuditEntryView`, `McpPublishedItemView`) are in `Stomme.Shared`, namespace `Stomme.Models.Mcp`.

### From server code

Inject `IMcpClientToolSource` to list what the site's external servers offer, for example to show it in your module.
It is registered only while `Mcp:Enabled` is on, so take it as an optional dependency:

```csharp
#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp;

namespace Stomme.Application.Modules.Notes.Mcp
{
    public sealed class ExternalToolOverview(IMcpClientToolSource? tools = null)
    {
        public async Task<IReadOnlyList<McpClientToolDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
            tools is null ? new List<McpClientToolDescriptor>() : await tools.GetAvailableToolsAsync(cancellationToken);
    }
}
```

Registered as `services.AddScoped<ExternalToolOverview>();` - the container fills an optional constructor parameter with
null when the service is not registered.

## Configuration

| Key | Where | Default | Effect |
|---|---|---|---|
| `Mcp:Enabled` | configuration | `false` | Composes the client half and its API. Restart after changing it. |
| `Mcp:Client:HostAllowlist` | configuration | empty array | Hosts every site may dial although they resolve to a private or loopback address. An array of host names, or one delimited string. |
| `McpClientAllowedHosts` | Site setting, Host only | empty | The same, for one site; unioned with the configuration list. |

`McpClientAllowedHosts` has no page of its own today; a Host user writes it through the site settings API. The settings
API refuses a write of it by anyone but a Host user.

## Rules and pitfalls

- **External tools are not used by anything yet.** Registering a server and refreshing it makes its tools visible on the
  admin page and through `IMcpClientToolSource`. No agent, pipeline step or API calls them. Do not build on the
  assumption that the assistant can.
- **Refresh, not Register, talks to the server.** A new registration showing `Unverified` and 0 tools is correct until
  someone presses Refresh.
- **A local or LAN MCP server is refused** until its host is on `Mcp:Client:HostAllowlist` or the site's
  `McpClientAllowedHosts`. The refusal is reported as `Refused` with the guard's reason, not as `Failed`.
- **`Mcp:Client:HostAllowlist` works from any source** since 10.73.2, the installation's `appsettings.override.json`
  included. Before, the endpoint guard read the container's `IConfiguration`, which leaves the override file out.
- **The key is write-only.** If a key is lost, set a new one; nothing can read the stored one back.
- **The endpoint cannot carry credentials** (`https://user:pass@host/...` is refused). Use Set key.
- **A disabled registration keeps its discovered tools** but offers none through `IMcpClientToolSource`.

## Related

- [The MCP server](mcp-server.md) - what this installation publishes
- [The AI assistant](ai-assistant.md)
- [Connectors overview](../connectors/overview.md) - the outbound guard the client half shares
- [Configuration](../architecture/configuration.md)
