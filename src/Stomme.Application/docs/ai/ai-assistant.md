# The AI assistant

Nova Globen Stomme carries an AI assistant that reaches a language model configured per site: a chat panel with agents that
propose pipelines and dashboards, the Landing Page Designer, and the AI steps of pipelines. The same model seam is open
to your modules, so a module can ask the site's model a question with the site's quota, metering, egress rules and
guardrail applied. Everything is off until the installation and the site switch it on.

## How it works

### Two switches, then the site's configuration

1. The installation switch `AiAssistant:Enabled` (configuration, default `false`). It is bound once, when the host first
   needs it, so changing it needs a restart.
2. The site switch: the Site setting `AiAssistantEnabled`. A site administrator turns it on in the chat panel's settings.
3. The site's model configuration, all Site settings, edited in the same panel:
   - `AiAssistantProvider` - the provider name. One provider ships: `openai`, the OpenAI-compatible chat API (OpenAI,
     Azure OpenAI's OpenAI-compatible endpoint, and local servers that speak the same API, such as Ollama or LM Studio).
   - `AiAssistantEndpoint` - default `https://api.openai.com/v1`. An absolute http or https address with no user name or
     password in it.
   - `AiAssistantModel` - no default. The assistant cannot be switched on for a site until a model is named.
   - `AiAssistantTemperature` - optional, 0 to 2.
   - The API key, kept in the per-site secret store under the name `ai-assistant.provider.api-key`. It is written when
     the administrator saves it and never read back to any page or API: the settings view says only `Stored`, `None` or
     `Unreadable`. With no key stored, a placeholder key is sent (a local server needs none).

With either switch off, nothing reaches a model and every caller gets a refusal that names the switch.

### One call, step by step

Every model call in Stomme - the chat panel's agents, the Landing Page Designer, the pipeline steps, and your module -
goes through one service, `IAssistantModelAccess` (namespace `Stomme.Modules.AiAssistant.Application`):

1. `OpenAsync(agent)` resolves the current site from the request (the signed-in user's site, else the resolved alias).
2. It checks, in order: the installation switch, that there is a site, the site switch, that a model is named, that the
   site's provider is one the installation offers, and that a stored key can be read. Each failure throws
   `AssistantUnavailableException` with a `Reason` (`AssistantUnavailableReason`) and a message that names the setting,
   never a value.
3. It builds the HTTP client from the site's egress (the framework's single outbound guard, see
   [the connectors overview](../connectors/overview.md)). A model server on a loopback, private or link-local address is
   refused unless a Host user has allowed its host under **Private hosts** on the Connections page.
4. It wraps the provider's client in `MeteredChatClient`. Before each call that client asks the site's quotas `ai.calls`
   and `ai.tokens`; a spent quota throws `AssistantUnavailableException` (`QuotaExceeded`), and a quota store that cannot
   be read refuses the call (`MeteringUnavailable`) rather than letting it through. After each call it records the call in
   the site's AI usage with the provider, the model, the agent name, the token counts and the outcome - a call refused
   by the egress guard is recorded too, as an error (a streamed call too, since 10.73.2: one that fails or is refused is recorded as an error, and one its reader stops early with what it used). The per-call records are readable at once through
   `GET api/Metering/ai/calls` (key `metering:view`); the Usage page (`/admin/usage`) shows daily totals made by the
   usage rollup job, which runs only while `Metering:Enabled` is on. Quotas are enforced whatever that switch says. See
   [logging, health and metering](../services/logging-health-metering.md).
5. It returns an `AssistantModel`: `Client` (a `Microsoft.Extensions.AI.IChatClient`), `Provider`, `Model` and
   `Temperature`. The caller disposes it; it owns the egress HTTP client.

`IAssistantModelAccess.IsAvailableAsync()` answers whether both switches are on for the current site without making a
model call. It does not check the model name, the key, the quota or whether the endpoint's address is allowed.

### The guardrail

`IAssistantGuardrail` (same namespace) runs before each turn of the assistant's chat and of the pipeline designer and
script-writer agents; `OpenAsync` does not run it, so a call you make yourself is not rate-limited by it. `EnsureAllowedAsync(request, siteId, userId)` checks:

- A per-user rate window: `AiAssistant:RateLimitPerMinute` requests per minute (default 30, at least 1), keyed by site and
  user. The window is held in memory by one host instance and is shared by every agent that runs the guardrail.
- A fixed list of prompt-injection phrases ("ignore previous instructions", "reveal your system prompt", ...), matched in
  the USER messages only, ignoring case and runs of white space. It is a tripwire for the obvious, not a defence.
- An authorization check of every resource the request names explicitly (`AssistantResourceRef`). The default
  authorizer refuses every such reference.

A refusal throws `AssistantGuardrailException` with a `Reason` (`PromptInjection`, `RateLimitExceeded`, `Unauthorized`)
and a message safe to show.

### The chat panel and its agents

- The page is `/admin/assistant` (not in the navigation; Administrators by default). It lists the user's conversations,
  one transcript, and each proposal with Apply and Discard. A holder of `assistant:configure` also sees the site's
  settings, with a write-only key field.
- A turn goes to the orchestrator: the guardrail first, then one routing call in which the model names the specialists
  to use, then each named specialist. With none named, general chat answers.
- The specialists shipped: `assistant` (general chat over read-only workspace tools), `dashboard-designer`,
  `pipeline-architect` (only while `Pipelines:Enabled` is on) and `contextual-help` (only while `Help:Enabled` is on;
  answers only from the site's published help topics).
- Agents read the site through the owning modules' own permission-gated queries, as the signed-in user, so an agent sees
  exactly what its user may see. Agents only PROPOSE: a pipeline or a dashboard is created only when the user presses
  Apply, through the owning module's own gated commands, and a failed apply removes what it had created.
- Every tool loop is bounded: at most 12 tool rounds, then one closing call without tools.
- A conversation is stored per site and per user, tenant-owned. Erasing a user deletes their conversations on the site,
  and a user's data package carries them.

### The Landing Page Designer

The Landing module's Edit action offers "Describe it to the AI assistant". The designer generates a static HTML page
from a brief, or revises one named section from an instruction, and submits through a tool that accepts only HTML the
landing sanitizer leaves unchanged (at most three submissions). It proposes only; the administrator applies the page.
Generating or revising needs `landing:manage` and `assistant:use`. It is not routed from the chat panel.

### Pipelines

The pipeline steps **Ask AI** and **AI agent** ask the site's model under the agent name `pipelines`, with the same
switches, quota and metering. The editor's "Build with AI" and the Run script step's "Ask AI to write it" use the agent
names `pipeline-designer` and `pipeline-script-writer`. See [the pipelines overview](../pipelines/overview.md).

### Permissions

The keys are in `Stomme.AiAssistant.AssistantPermissions` (in `Stomme.Shared`), both granted to Administrators on a
new site:

| Key | Constant | Allows |
|---|---|---|
| `assistant:use` | `AssistantPermissions.Use` | Start, list, read and send conversations; apply and discard proposals. It grants no authority of its own: what an agent reads and what an apply writes are still decided by the owning module's keys. |
| `assistant:configure` | `AssistantPermissions.Configure` | Read and change the site's assistant configuration. |

## Using it from an app

### Ask the site's model from server code

Inject `IAssistantModelAccess` into a server-side class (a handler, a service, a controller). It is registered on every
host, whether the assistant is on or not; it refuses at call time when it is off.

```csharp
#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.AiAssistant.Application;
using Microsoft.Extensions.AI;

namespace Stomme.Application.Modules.Notes.Ai
{
    public sealed class NoteTitleSuggester(IAssistantModelAccess models)
    {
        // The name the site's AI usage records these calls under. Lower-case, stable, unique to this use.
        public const string AgentName = "notes-title";

        public async Task<string> SuggestAsync(string body, CancellationToken cancellationToken = default)
        {
            using var model = await models.OpenAsync(AgentName, cancellationToken);
            var options = new ChatOptions { ModelId = model.Model, Temperature = model.Temperature };
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "Suggest one short title for the note. Answer with the title only."),
                new(ChatRole.User, body),
            };

            var response = await model.Client.GetResponseAsync(messages, options, cancellationToken);
            return response.Text ?? string.Empty;
        }
    }
}
```

The full task - permission check, guardrail, time limit, an API endpoint and the refusal mapping - is the skill
[stomme-use-ai-in-module](../../.claude/skills/stomme-use-ai-in-module/SKILL.md).

### Map a refusal to an HTTP answer

`AssistantRefusals.Describe(exception)` (namespace `Stomme.Modules.AiAssistant.Application`) returns a
`Refusal(int Status, AssistantRefusalView View)` for the assistant's own refusals and null for anything else:

| Exception | Reason | Status |
|---|---|---|
| `AssistantGuardrailException` | `RateLimitExceeded` | 429 |
| `AssistantGuardrailException` | `Unauthorized` | 403 |
| `AssistantGuardrailException` | `PromptInjection` | 400 |
| `AssistantUnavailableException` | `QuotaExceeded` | 429 |
| `AssistantUnavailableException` | `MeteringUnavailable` | 503 |
| `AssistantUnavailableException` | `Disabled`, `NotConfigured`, `KeyUnreadable`, `UnknownProvider`, `EndpointNotPermitted` | 409 |
| `AssistantConflictException` | - | 409 |
| `FluentValidation.ValidationException` | - | 400 |

`AssistantRefusalView` (namespace `Stomme.AiAssistant`) is `(string Code, string Message)`; the message is written to be
shown to a user.

### Add a specialist to the chat panel

A class implementing `IAssistantSpecialist` (`Name`, `Description`, `InvokeAsync`) and registered with
`services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssistantSpecialist, YourSpecialist>())` joins the orchestrator's
routing prompt by its `Description`. Its reply is an `AssistantReply`; use `AssistantReply.Text(...)` for a text answer.
The proposal kinds the panel can apply are the framework's own (pipelines and dashboards), so an app specialist answers
in text. This path is public but has not been driven from an app; prefer a module endpoint (above) for anything that
writes.

### The HTTP API

`api/AiAssistant` is the chat panel's API. Every route answers 404 while `AiAssistant:Enabled` is off.

| Method and route | Key |
|---|---|
| `GET api/AiAssistant/conversations` | `assistant:use` |
| `POST api/AiAssistant/conversations` | `assistant:use` |
| `GET api/AiAssistant/conversations/{id}` | `assistant:use` |
| `POST api/AiAssistant/conversations/{id}/messages` (body `{ "message": "..." }`, 1 to 32,000 characters) | `assistant:use` |
| `POST api/AiAssistant/conversations/{id}/artifacts/{artifactId}/apply` | `assistant:use` |
| `POST api/AiAssistant/conversations/{id}/artifacts/{artifactId}/discard` | `assistant:use` |
| `GET api/AiAssistant/settings` | `assistant:configure` |
| `PUT api/AiAssistant/settings` (`SaveAssistantSettingsRequest`) | `assistant:configure` |

Another user's conversation answers 404, the same as one that does not exist.

## Configuration

| Key | Where | Default | Effect |
|---|---|---|---|
| `AiAssistant:Enabled` | configuration | `false` | The installation switch. Read once; restart after changing it. |
| `AiAssistant:RateLimitPerMinute` | configuration | `30` | Guardrail requests per user per site per minute; values below 1 read as 1. |
| `AiAssistant:RequestTimeoutSeconds` | configuration | `120` | Timeout of one model HTTP call; values below 5 read as 5. |
| `AiAssistantEnabled` | Site setting | off | The site switch. |
| `AiAssistantProvider` | Site setting | `openai` | Must name a provider the installation offers. |
| `AiAssistantEndpoint` | Site setting | `https://api.openai.com/v1` | The model server's base address. |
| `AiAssistantModel` | Site setting | none | Required before the site switch can be turned on. |
| `AiAssistantTemperature` | Site setting | none | 0 to 2. |
| `ai-assistant.provider.api-key` | per-site secret store | none | The provider's API key. |
| `ConnectivityAllowedHosts` | Site setting, Host only (the **Private hosts** list on the Connections page) | empty | Lets the site reach a model server on a private or loopback address. |

The configuration keys are read from the host's merged configuration, so `appsettings.json`, environment variables and
the installation's `appsettings.override.json` all work. See [configuration](../architecture/configuration.md).

The site's AI quotas are the metering metrics `ai.calls` and `ai.tokens`. With no quota defined for a metric, calls are
allowed and still recorded.

## Rules and pitfalls

- **Two switches.** `AiAssistant:Enabled` on and the site's `AiAssistantEnabled` off answers `Disabled` with the message
  "switched off for this site". Check both before debugging the provider.
- **No model named, no assistant.** `AiAssistantModel` has no default. `NotConfigured` means the site has not named one.
- **A local model server is refused until a Host allows it.** Ollama on `localhost` or a LAN address fails with
  `EndpointNotPermitted` ("on a private, loopback or internal address this site may not reach") until its host is listed
  under Private hosts on the Connections page. That check runs at connect time against the address actually reached.
- **Streaming bypasses one mapping.** `MeteredChatClient` turns an egress refusal into `EndpointNotPermitted` only for
  `GetResponseAsync`. A streamed call (`GetStreamingResponseAsync`) is quota-gated and metered when its stream completes; a
  streamed call that fails, an egress refusal included, is not recorded and surfaces as the raw transport exception. No framework agent streams.
- **Every call costs quota, including each tool round.** If you give the model tools with
  `ChatClientBuilder.UseFunctionInvocation`, bound the loop yourself
  (`invocation.MaximumIterationsPerRequest = n`). The library default is 40 rounds per request, each one a metered
  call. The framework's own bound (12) is internal and does not apply to your client.
- **A refusal is not a 500.** Catch `AssistantGuardrailException` and `AssistantUnavailableException` and answer with
  `AssistantRefusals.Describe`. A provider error (a bad key at the provider, a model name the provider does not know, a
  network failure) is none of these: it arrives as the provider SDK's exception, after the call was metered as an error.
- **Dispose the model.** `AssistantModel` owns an HTTP client from the site's egress. Use `using var model = ...`.
- **A request-bound call needs a time limit.** A model can take longer than a browser or a reverse proxy waits. The
  framework's pipeline script writer and graph designer cancel at 60 and 90 seconds with a linked
  `CancellationTokenSource`; set your own.
- **The site comes from the request.** Outside a request (a job), the site is the resolved alias; set it per site before
  calling (see [jobs](../services/jobs.md)). With no site, `OpenAsync` refuses (`Disabled`, "needs a site").
- **Agent names are metering labels and routing keys.** Two `IAssistantSpecialist` registrations with the same `Name`
  break the orchestrator for every chat turn (its dictionary of specialists cannot be built). Pick a name nobody else
  uses; the framework's are `orchestrator`, `assistant`, `pipeline-architect`, `dashboard-designer`, `contextual-help`,
  `widget-generator`, `landing-page-designer`, `pipelines`, `pipeline-designer` and `pipeline-script-writer`.
- **Model output is data, not instructions.** The framework's rule is that an agent proposes and a user applies. Never
  store or execute model output without validating it the way your module validates user input; HTML must be sanitized
  (the Landing module sanitizes and frames generated pages).
- **Do not call the model from the client project.** `IAssistantModelAccess` is server-side and the key never leaves the
  server. A WebAssembly component calls your module's API, which calls the model.
- **The external-MCP half does not feed the assistant.** Registering an external MCP server does not give any agent its
  tools. See [the MCP client](mcp-client.md).

## Related

- [The MCP server](mcp-server.md) - publishing your module to AI clients
- [The MCP client](mcp-client.md) - external MCP servers a site registers
- [stomme-use-ai-in-module](../../.claude/skills/stomme-use-ai-in-module/SKILL.md) - the task: call the site's AI
- [Logging, health and metering](../services/logging-health-metering.md) - quotas and AI usage
- [Connectors overview](../connectors/overview.md) - the egress guard and Private hosts
- [Identity and permissions](../security/identity-and-permissions.md) - permission keys and `IAccessGuard`
- [Configuration](../architecture/configuration.md) - where switches are set
