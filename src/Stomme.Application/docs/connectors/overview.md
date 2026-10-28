# Connectors and connections

A **connector** is code that knows how to reach one kind of external system: an HTTP API, a database, a mail server, a
file server. A **connection** is a configured instance of a connector on one site: an endpoint, settings, a timeout and
credentials. Pipelines, the cloud cost sync and your own module code reach external systems through connections, and
never hold an address and a password of their own. (The AI assistant keeps its model endpoint and key in its own site
settings; it shares only the site's egress allowlist, below.)

## How it works

### The pieces

| Piece | Type (namespace) | What it is |
| --- | --- | --- |
| Connector contract | `IConnector`, `IConnectorExtractor` (`Stomme.Connectivity`) | A connector type. Registered as a singleton `IConnector` by an `IServerStartup`. |
| Catalogue | `IConnectorCatalog` (`Stomme.Connectivity`) | Every registered connector, looked up by its type key, compared case-insensitively. |
| Connection | stored by the Connectivity module | One site's configured instance. The page and the API show it as `ConnectionView` (`Stomme.Connectivity`). |
| Credentials | the per-site secret store | The values of the credential fields the connector's authentication scheme declares. A connection row stores only the field **names**. |
| Egress | `IConnectorEgress` (`Stomme.Connectivity`) | The only network a connector gets. The host builds one per site from the site's allowlist. |
| Extraction port | `IConnectorExtractionService` (`Stomme.Connectivity`) | How a consumer (a pipeline step, your module) runs one operation of a connection and gets records back. |
| Client contract | `IConnectionsService` (`Stomme.Modules.Connectivity.Services`) | List, read, create, update, test and delete connections, and read or save the site's allowlist. Over HTTP in the browser; through the mediator on the server. |

### The catalogue

Every connector registered in the server's container is in the catalogue. Nova Globen Stomme ships a large set (generic REST,
PostgreSQL, SQL Server, MySQL, FTP, SFTP, SSH, IMAP, Slack, Microsoft Teams, Gmail, GitHub, Jira, Stripe, cloud cost
sources and many more). The list for the version you run is the answer of `GET api/Connections/connectors`, or the
connector picker on the Connections page. Each entry (`ConnectorView`) carries:

- `ConnectorType`: the stable key stored on every connection row, for example `rest`, `postgresql`, `slack`.
- `DisplayName`, `Categories` (the picker groups by them), `WebsiteUrl`, `ApiDocumentationUrl`.
- `AuthType` and `AuthFields`: the credential fields the connection form asks for.
- `EndpointKind`: `Url` (an absolute `http` or `https` URL) or `ConnectionString` (anything else: a database
  connection string, `sftp://host:22`, `ldap://host`).
- `Fields`: the connector's settings, with type, default, help text and visibility rules.
- `Operations`: what the connector can do once connected, each with its parameters. A connector with no operations can
  only be tested.
- `HostOnly`: the connector reaches the host itself; only the Host role may create or edit such a connection.
- `Icon`: an inline SVG. Unless a connector declares its own, it is a neutral monogram tile (two letters on the
  category's colour). The page shows no vendor logos and prints a third-party trademark notice
  (`ConnectorTrademarkNotice.Text`) beside the catalogue.

Two connectors that publish the same type key cannot both be registered: the catalogue throws when it is first built and
names both classes. The host still starts; the Connections page and every `api/Connections` read then fail with 500
(the write and test routes answer 400 or 404), a step that uses a connection faults its run, and the event log
(Admin > Event Log) carries the message.

### A connection

Created on the Connections page (`/admin/connections`) or with `POST api/Connections`. A connection holds:

- `Name`: unique on the site, compared case-insensitively.
- `ConnectorType`: fixed at creation.
- `Endpoint`: checked against the connector's `EndpointKind`. A URL must be absolute `http`/`https` and must not
  carry a user name or password. A connection string must not carry a credential as a key (`password`, `pwd`,
  `secret`, `token`, `apikey`, `api_key`, `api-key`). Either refusal is a 400 with the reason.
- `Settings`: validated against the connector's `ConfigSchema` (required fields, integer, number, boolean, URL,
  duration and select types). A field hidden by its `VisibleWhen` rule is neither required nor validated.
- `Credentials`: written to the per-site secret store, one row per field given a value; a field the scheme declares
  but no one filled in has no row. A field the scheme
  does not declare is refused before anything is written. On an update a blank or absent credential keeps the stored
  value; a value given replaces that one field.
- `TimeoutSeconds`: 1 to 300, default 30.
- `Status`: `Untested`, `Healthy` or `Failing`, and `LastTestedUtc`. An edit resets the status to `Untested`.

No response ever carries a credential value. `ConnectionView.CredentialFields` lists only the names stored.

### Credentials and authentication schemes

A connector declares one `AuthenticationType`. The scheme decides the credential field names
(`AuthenticationScheme.FieldsFor`):

| `AuthenticationType` | Field names (secret ones marked *) |
| --- | --- |
| `None` | none |
| `ApiKey` | `api-key`* |
| `ApiKeyPair` | `access-key`, `secret-key`* |
| `Basic` | `username`, `password`* |
| `Bearer` | `token`* |
| `OAuth2ClientCredentials` | `client-id`, `client-secret`*, `token-url` |
| `OAuth2RefreshToken` | `client-id`, `client-secret`*, `refresh-token`* |
| `Certificate` | `certificate`*, `passphrase`* |
| `Windows` | `username`, `password`* |

A connector can relabel a field in the service's own words (`IConnector.CredentialLabels`), but the field name, and so
where the secret is stored, stays the scheme's. The values are protected with the installation's Data Protection key
ring. If that key ring changes, stored credentials no longer decrypt: a test answers `CredentialsUnreadable` and an
operator enters them again.

### The egress: where a connection may go

A connector never opens its own network connection. The host hands it an `IConnectorEgress` for the connection's site:

- `CreateHttpClient(timeout)` returns an `HttpClient` whose every connection is checked where the bytes go. Loopback,
  private (RFC 1918), link-local (including the cloud metadata address `169.254.169.254`), unique-local IPv6 and the
  unspecified addresses are refused. Redirects are not followed.
- `ResolveAndPinAsync(host, port)` resolves a host for a database or other driver, checks every address, and returns
  the one address the driver must dial, or `null` when none is permitted.

**The site's allowlist** opens named private hosts to that site's connections. It lives on the Connections page in the
**Private hosts** panel, or `GET`/`PUT api/Connections/egress`:

- Any holder of `connections:view` reads it. Only the **Host** role saves it; anyone else gets 403.
- Each entry is an exact host name or an IP address written in full. Ranges, wildcards, schemes, ports and short IP
  forms are refused with the reason (400). At most 50 entries and 2,000 characters. An entry matches the host as the
  endpoint writes it: `127.0.0.1` opens `http://127.0.0.1:...` but not `http://localhost:...`.
- An allowlisted host name may resolve to private addresses; any other private address stays refused.
- The same allowlist covers the AI assistant's model endpoint for that site.

A refusal by the egress is reported as the outcome `DeniedByPolicy`, and the remote system never sees a request.

### Testing a connection and outcomes

`POST api/Connections/{id}/test` (the **Test** button) runs the connector's `TestConnectionAsync` with the endpoint, the
settings, the credentials from the secret store, the site's egress and the connection's timeout. The answer is a
`ConnectionTestView` (`Success`, `Outcome`, `Message`, `Status`): a `ConnectorOutcome` and a fixed message from
`ConnectorOutcomes.Message`. The connector's own error text is never shown, logged or put in an event, because it may
echo an endpoint, a user name or a password. A `Faulted` outcome is logged as a warning with the exception type and
the connection id. Over the HTTP API the outcome travels as its number (the first column).

| # | `ConnectorOutcome` | Meaning |
| --- | --- | --- |
| 0 | `Succeeded` | The connector reported success. The page says "Connection succeeded." |
| 1 | `Failed` | The connector reported failure (wrong key, an HTTP error, a server that answered wrongly). |
| 2 | `ConnectorUnavailable` | No connector of this type is installed (for example, its assembly was removed). |
| 3 | `CredentialsMissing` | A credential the connection names has no stored row. |
| 4 | `CredentialsUnreadable` | A stored credential no longer decrypts. |
| 5 | `DeniedByPolicy` | The egress refused the destination. |
| 6 | `TimedOut` | The connection's timeout elapsed. |
| 7 | `Faulted` | The connector threw. |
| 8 | `ConnectionNotFound` | No connection with that id on this site. |
| 9 | `ExtractionUnsupported` | The connector does not extract (it implements no `IConnectorExtractor`). |
| 10 | `OperationUndefined` | The operation named is not one the connector declares. |
| 11 | `HostKeyUntrusted` | An SSH or SFTP server offered a host key the connection does not name; nothing was sent. |

### Health probes

With `Connectivity:HealthProbes:Enabled` set to `true`, the health module's per-site pass tests every connection of the
site, writes each verdict on its row, and reports a `connections` probe whose status is the worst of them. Off (the
default), nothing dials out on its own. See [logging, health and metering](../services/logging-health-metering.md).

### Using a connection from a pipeline

Pipelines are off until `Pipelines:Enabled` is `true`. These built-in steps take a connection, chosen by name in the
editor's connection picker and stored by id:

| Step (display name) | What it does |
| --- | --- |
| Extract from connection (`Stomme.ExtractFromConnection`) | Runs one operation of any connector that has operations. Inputs (by their names in the graph): `Connection` (the id), `Operation` (empty: the connector's first), `Query` (a SQL statement, an SSH command, a message body; becomes the `query` parameter), `Parameters` (one `name = value` per line; `headers =` and `filters =` lines may repeat), `MaxRecords` (shown as Maximum records; 1 to 100,000, default 10,000), `OnFailure`. The outputs are `Result` (the records as a JSON array), `RecordCount`, `Truncated` and `Error`. |
| Send through connection | An HTTP request through a REST connection (`rest`, its `send` operation): method, path, body, content type, extra headers. The response body is the result and `Status code` an output. |
| Send chat message | Slack, Telegram, Discord, Mattermost, Twilio, Microsoft Teams or WhatsApp connections. |
| Send email | Gmail or Microsoft Outlook connections. |
| Email received (trigger) | IMAP or Gmail connections. |

A refused extraction fails the step with the code `EXTRACTION_<OUTCOME>` in upper case (for example
`EXTRACTION_DENIEDBYPOLICY`) and the fixed message, and honours the step's `On failure` setting. Other codes:
`CONNECTION_INVALID` (not a connection id), `PARAMETER_INVALID` (a line that is not `name = value`),
`SITE_UNAVAILABLE`, `CONNECTIVITY_UNAVAILABLE`. A connector's HTTP request that outlasts the connection's timeout fails
the step with `EXTRACTION_TIMEDOUT` and "The connection timed out." since Stomme 10.73.0 (before it, the step
faulted with the exception's own message unless the connector turned the timeout into a `TimeoutException`). The credentials
never appear in the pipeline definition, the step data or the run log. See [the pipelines overview](../pipelines/overview.md).

## Using it from an app

- **Use an existing connection from code:** [using connections in code](using-connections-in-code.md) and the
  [stomme-use-connection](../../.claude/skills/stomme-use-connection/SKILL.md) skill.
- **Add a connector type of your own:** [writing a connector](writing-a-connector.md) and the
  [stomme-write-connector](../../.claude/skills/stomme-write-connector/SKILL.md) skill. An app registers its
  connector from its own `IServerStartup`; it then appears in the catalogue beside the built-in ones.
- **Turn records into a dataset and a dashboard:** [datasets, dashboards and formats](../data/datasets-dashboards-formats.md).

### The HTTP API

All routes are relative to the site (`api/Connections`), take and return JSON, and are decided by permission keys
(`ConnectionPermissions` in `Stomme.Connectivity`):

| Route | Key | Body / answer |
| --- | --- | --- |
| `GET api/Connections` | `connections:view` | `ConnectionView[]` |
| `GET api/Connections/connectors` | `connections:view` | `ConnectorView[]` |
| `GET api/Connections/{id}` | `connections:view` | `ConnectionView` or 404 |
| `POST api/Connections` | `connections:manage` | `ConnectionRequest` in, `ConnectionView` out |
| `PUT api/Connections/{id}` | `connections:manage` | `ConnectionRequest` in (the type is ignored), `ConnectionView` out |
| `POST api/Connections/{id}/test` | `connections:manage` | `ConnectionTestView` |
| `DELETE api/Connections/{id}` | `connections:manage` | 204; 400 while something uses the connection |
| `GET api/Connections/egress` | `connections:view` | `ConnectionEgressView` (`AllowedHosts`, `CanEdit`) |
| `PUT api/Connections/egress` | `connections:manage` and the Host role | `ConnectionEgressRequest` (`AllowedHosts` as text) |

Both keys default to the Administrators role. A refusal of the caller's input is 400 with `{ "error": "..." }`; a
settings validation failure is a problem document; a `HostOnly` refusal is 403. Writes from a browser need the
antiforgery token; see [API and client services](../modules/api-and-client-services.md).

## Configuration

| Key | Default | Effect |
| --- | --- | --- |
| `Connectivity:HealthProbes:Enabled` | `false` | Tests every connection of each site during the health pass. |
| `DemoContent:Enabled` | `false` | Adds two demo connections (a REST endpoint and a PostgreSQL database whose password an operator fills in; until then its Test answers `CredentialsMissing`) to every site, once per site. A rename or delete survives later starts. |

The site allowlist is a private site setting named `ConnectivityAllowedHosts`. Change it through the Private hosts panel
or `PUT api/Connections/egress`, never through the generic settings API: that API refuses a non-Host write of it.

## Rules and pitfalls

- **A private address is refused until the Host allowlists it.** A connection to `localhost`, `10.x`, `192.168.x` or a
  LAN database tests as `DeniedByPolicy`. Add the exact host name or address under Private hosts, as the Host.
- **A test message is a category, never the remote's text.** To see why a connector failed, read the server log: it
  names the exception type and the connection id. The connector's message is discarded on purpose.
- **`Url` endpoints accept only `http` and `https`.** A connector whose endpoint is `ldap://`, `sftp://` or a
  database connection string uses `EndpointKind.ConnectionString`; a connection to it with the wrong kind is refused.
- **Never put a credential in the endpoint or a setting.** The endpoint is stored and listed in plain text. Credentials
  belong in the scheme's fields.
- **A deleted connection is refused while in use.** Any registered `IConnectionUsageProvider` (`Stomme.Connectivity`)
  that says the connection is in use blocks the delete with its reason.
- **The type key is permanent.** Every connection row stores it. Renaming a connector's key orphans its connections:
  they show `ConnectorInstalled = false` and test as `ConnectorUnavailable`.
- **Settings defaults are the form's, not the host's.** A `ConnectorConfigField.Default` pre-fills the connection
  form. A connection created through the API without that setting does not get it; the connector must fall back
  itself.

## Related

- [Writing a connector](writing-a-connector.md)
- [Using connections in code](using-connections-in-code.md)
- [Datasets, dashboards and formats](../data/datasets-dashboards-formats.md)
- [Security posture](../security/security-posture.md) and [identity and permissions](../security/identity-and-permissions.md)
- [Pipelines overview](../pipelines/overview.md)
- Skills: [stomme-write-connector](../../.claude/skills/stomme-write-connector/SKILL.md),
  [stomme-use-connection](../../.claude/skills/stomme-use-connection/SKILL.md)
