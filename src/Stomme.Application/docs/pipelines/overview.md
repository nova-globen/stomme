# Pipelines

A pipeline is a workflow a site runs: steps connected on a canvas, started by hand, on a schedule, by a webhook call, by
a named event or by another pipeline. Nova Globen Stomme ships a pipeline engine, a visual editor, a step catalogue and an HTTP API
for them. The whole feature is off until the installation switches it on.

## How it works

### The pieces

- **The engine.** A workflow engine derived from Elsa 3, renamed into the `Stomme.Engine.*` packages
  (`Stomme.Engine.Pipelines`, `Stomme.Engine.Pipelines.Management`, `Stomme.Engine.Pipelines.Runtime`,
  `Stomme.Engine.Expressions` and others). It owns the step model (`Stomme.Engine.Pipelines.Step`,
  `CodeStep`), definitions and versions, runs (the engine calls them pipeline instances), triggers and bookmarks.
- **The Pipelines module.** Part of `Stomme.Server`. It composes the engine into the host, stores the engine's data
  with the installation's own database provider, maps each engine tenant to one Stomme **site**, adds the built-in
  steps, the triggers, the HTTP API (`api/Pipelines`) and the MCP contributor.
- **The editor.** A Blazor page (Admin > Pipelines, path `admin/pipelines`) that lists the site's pipelines and opens
  each one on a canvas. See [the editor](editor.md).
- **The graph.** The contract between the editor (or any API client) and the server:
  `Stomme.Models.Pipelines.PipelineGraph` in the `Stomme.Shared` package. The server turns a graph into the engine's
  own flowchart document and back; the engine judges every save. See [the HTTP API](api.md).

### Composition and the switch

The module reads `Pipelines:Enabled` from the host's configuration once, while the host composes its services. With
the switch absent or `false`, no engine type is registered, the `api/Pipelines` routes do not exist (404) and the
Admin > Pipelines page is not created. With it `true`:

1. The engine is added with stores on the installation's database provider (the one `Database:DefaultDBType`
   names). Its lock files go to a folder `engine-locks` under the host's data directory.
2. Every public step in the module's assembly is registered with the engine's step registry.
3. Recurring tasks are added: due schedules, due waits, run retention, queued runs, watched folders, watched
   mailboxes and Telegram bots, evaluations.
4. The admin site template adds the Admin > Pipelines page. Admin pages are reconciled on every start, so a site that
   existed before the switch was turned on gets the page at the next start.

Changing the switch takes a restart.

### Sites, tenants and permissions

- **A pipeline belongs to one site.** The engine's tenant id is `"{TenantId}/{SiteId}"`
  (`Stomme.Modules.Pipelines.Tenancy.EngineTenantIds.For`). A request enters its site's engine tenant after
  Stomme resolves the alias, so every API call reads and writes the pipelines of the site it was sent to.
- **A run executes on a background worker**, not in the request that started it. It has the engine tenant but no
  HTTP request and no Stomme alias of its own; a step that needs Stomme's site context enters the site first (see
  [custom steps](custom-steps.md#entering-the-site)).
- **Permission keys** (`Stomme.Pipelines.PipelinesPermissions`, in `Stomme.Shared`), each checked on the server
  for every action, each granted to Administrators by default:

  | Key | Allows |
  |---|---|
  | `pipelines:view` | See the pipelines, their runs, a run's log and steps (states only, no data) |
  | `pipelines:manage` | Create, edit, publish, retract, archive and delete; see what each step received and produced |
  | `pipelines:run` | Start a run, cancel, retry, test a step, raise an event |
  | `pipelines:approve` | Decide an "Ask for approval" step |

  An operator widens a key in the Pipelines module definition's permissions. No key is given to Registered Users by
  default. See [identity and permissions](../security/identity-and-permissions.md).

### Definitions, versions and publishing

- A **pipeline** has a `DefinitionId` that stays the same across versions; each **version** has its own `Id`
  (`PipelineView.Id` is the version id, `PipelineView.DefinitionId` the pipeline's).
- **Saving** writes the latest version. When the latest version is published, a save makes a new draft version (the
  engine copies the published one); the published version stays live until the next publish.
- **Publishing** first runs the problem check (`Stomme.Models.Pipelines.PipelineGraphProblems`); a problem of
  severity `Error` refuses the publish with the reasons. Then the engine publishes and indexes the version's triggers.
  An archived pipeline is not published until it is restored.
- **Retracting** unpublishes; **archiving** unpublishes and sets the pipeline aside, read-only.
- Only the **published** version runs. Starting a pipeline with no published version answers 404.

### Runs

A run is dispatched, never awaited: starting one returns its id (`PipelineRunStarted.RunId`) and the caller polls
`GET api/Pipelines/runs/{id}`. The engine stores a run when its first burst of work ends, so a run read right after it
started answers 204 until then.

- `PipelineRunView.Status` is the engine's status: `Running` or `Finished`.
- `PipelineRunView.SubStatus` is the detail: `Executing`, `Suspended` (waiting for a time, an event, a call, a form or a
  decision), `Pending`, `Finished`, `Faulted` or `Cancelled`.
- Each step execution is recorded (`PipelineStepRunView`): its state, its times, its fault, and for a caller with
  `pipelines:manage` the inputs and outputs as JSON. An input marked as able to hold a secret is shown hidden.
- A pipeline can say how many runs go at once (`PipelineGraph.RunsAtOnce`), whether finished runs that went well are
  kept (`KeepSuccessfulRuns`), what step data runs keep (`KeepStepData`) and a time limit (`TimeoutMinutes`).

### Triggers

A trigger is a step of kind `Trigger`. A trigger at the top level of the pipeline with no connection leading to it
starts the pipeline; a trigger placed after other steps waits instead. A pipeline with no trigger still runs by hand or
through the API. When a run is started by hand, it starts at the first trigger, else a Start step, else the first step
nothing leads to.

| Trigger (display name) | Type name | Starts a run when |
|---|---|---|
| Schedule | `Stomme.Schedule` | a five-field cron line in its `Cron` input is due (several lines allowed), in UTC or its `TimeZone` |
| Webhook | `Stomme.Webhook` | `POST api/Pipelines/webhooks/{name}` arrives with a valid signature (needs `Pipelines:Webhooks:Enabled`) |
| Event | `Stomme.Event` | a named event is raised for the site (`POST api/Pipelines/events/{name}`, or from code) |
| When called | `Stomme.WhenCalled` | another pipeline runs this one (Execute or Dispatch pipeline) |
| Pipeline failed | `Stomme.PipelineFailed` | a run of another pipeline on the site fails |
| Form submitted, Chat message received | | a person uses the page the site serves for it (needs `Pipelines:Forms:Enabled` / `Pipelines:Chats:Enabled`) |
| File added, Email received, Telegram message received | | a file lands in the site's pipeline folder, a message in a watched mailbox or bot |

Read the full list from the catalogue: `GET api/Pipelines/steps` returns every step with `Kind`.

**Webhook calls** must carry `X-Signature-256: sha256=<lower-case hex HMAC-SHA256 of the raw body>` under the
webhook's own secret (made in the editor's trigger panel) or the installation's (`Pipelines:Webhooks:Secret` or
`Pipelines:Webhooks:SecretFile`). With no secret at all every call is refused. The receiver answers 202 whether or not
a pipeline listens under the name, 401 for a bad signature, 403 for a caller outside the allowlist, 404 while
webhooks are off, 413 for a body over the limit. The Webhook step's result is the body (JSON text is read as JSON by
the expressions that use it; a form-encoded body becomes a JSON object of its fields). An `X-Correlation-Id` header
becomes the run's correlation id. A webhook-started pipeline run by hand has an empty body.

**Events from your own code.** Send `Stomme.Modules.Pipelines.Triggers.RaisePipelineEventCommand(name, payload,
correlationId)` through `Stomme.Mediator.IMediator`. It is authorized like the API: the current user needs
`pipelines:run`. The Event step's result is the payload.

### Data between steps

- **Variables.** A pipeline declares variables (`PipelineGraph.Variables`: an `Id`, a `Name`, a `TypeName` of
  `Object`, `String`, `Int32`, `Double` or `Boolean`, an optional starting `Value`). A step saves an output to a variable
  (`PipelineGraphStep.OutputVariables`: output name to variable id); a later step reads it into an input
  (`InputVariables`: input name to variable id).
- **The last result.** The data steps (records as a JSON array of objects, text, documents) take the previous step's
  result when their text input is left empty, so a chain such as Parse CSV, Map fields, Validate records needs no
  variable.
- **Expressions.** An input can be an expression instead of a fixed value (`InputExpressions`): text with
  `{{ name.path }}` placeholders. No code runs. A template that is exactly one placeholder is the value itself (a list
  stays a list); otherwise each placeholder becomes text. A name that is neither a variable nor a built-in faults the
  step. Filters follow a bar: `{{ name | upper }}`, `{{ amount | round: 2 }}`, `{{ "a b" | urlencode }}`. The filters are
  `upper`, `lower`, `capitalize`, `trim`, `default`, `length`, `truncate`, `replace`, `split`, `join`, `first`, `last`,
  `number`, `round`, `fixed`, `plus`, `minus`, `times`, `divide`, `date`, `json`, `urlencode`, `html` and `raw`
  (`Stomme.Models.Pipelines.PipelineTemplateFilters`).
- **Built-in values** every expression and condition can read (`Stomme.Models.Pipelines.PipelineBuiltIns`):
  `{{ $now }}`, `{{ $today }}` (in the pipeline's time zone, UTC unless `PipelineGraph.TimeZone` is set),
  `{{ $run.id }}`, `{{ $run.correlationId }}`, `{{ $run.startedAt }}`, `{{ $pipeline.id }}`, `{{ $pipeline.name }}`,
  `{{ $pipeline.version }}`, `{{ $vars.name }}` (the site's variables, `GET`/`PUT api/Pipelines/variables`) and
  `{{ $input.name }}` (what the run was started with; a value that is JSON text is read as JSON).
- **Conditions.** A true-or-false input (a Decision's, an If's, a While's) can be a condition built from rules
  (`InputConditions`, `Stomme.Models.Pipelines.PipelineCondition`): `Match` `all` or `any`, `IgnoreCase`, and rules of
  `Left`, `Operator`, `Right`. Operators: `equals`, `not-equals`, `contains`, `not-contains`, `starts-with`, `ends-with`,
  `greater`, `at-least`, `less`, `at-most`, `empty`, `not-empty`, `true`, `false`.
- **JavaScript.** An input can be computed by JavaScript (`InputScripts`) and the Run script step runs a script, both
  in a bounded sandbox with no host access, only where `Pipelines:Scripts:Enabled` is `true`.

### What the built-in steps cover

The catalogue groups steps by category: AI, Branching, Composition, Console, Control Flow, Data, Files, Flow, HTTP,
Looping, Notifications, Primitives, Scripting, Testing, Transformations, Triggers and Validations (an app's own steps
add their categories). Highlights:

- **Reaching other systems:** HTTP request (`Stomme.SendHttpRequest`), Send through connection, Extract from connection
  (`Stomme.ExtractFromConnection`, every connector's operations), Send email, Send chat message. Credentials live in
  the site's connections, never in the pipeline. See [connectors](../connectors/overview.md).
- **Files** in the site's pipeline folder (`Pipelines:Files:Root`, one folder per site): read and write text,
  spreadsheets, documents, zip archives. The Files page (Admin > Pipelines > Files) manages the folder.
- **Data tables** the site keeps between runs (Admin > Pipelines > Data tables, and the Data table step).
- **Waiting durably:** Wait, Wait for a call, Wait for a form, Ask for approval. The run is stored and holds no worker.
  Delay holds the worker and is bounded by `Pipelines:MaxDelaySeconds`.
- **AI:** Ask AI, AI agent, Classify text, Extract information, guardrails - on the site's AI assistant model and quota.
  See [the AI assistant](../ai/ai-assistant.md).
- **Calling pipelines:** Execute pipeline and Dispatch pipeline, with When called as the callee's start.

A step type that cannot be found when a stored pipeline is loaded becomes the engine's not-found placeholder.

### Other ways in

- **MCP.** When the MCP server is on, the pipelines contributor lists pipelines and runs and offers tools to start
  them. See [the MCP server](../ai/mcp-server.md).
- **Your module's UI.** Inject `Stomme.Modules.Pipelines.Services.IPipelinesService` (in the `Stomme.Client`
  package) into a component; the client implementation calls `api/Pipelines` over HTTP.

## Using it from an app

1. Switch it on in the host's configuration (see [configuration](../architecture/configuration.md) for which file):

   ```json
   {
     "Pipelines": {
       "Enabled": true
     }
   }
   ```

2. Restart. Sign in as an administrator and open Admin > Pipelines.
3. Build pipelines in [the editor](editor.md) or over [the HTTP API](api.md); add your own step types with
   [custom steps](custom-steps.md).

Starting a run of a published pipeline from a component:

```razor
@using Stomme.Models.Pipelines
@using Stomme.Modules.Pipelines.Services
@inject IPipelinesService PipelinesService

<button type="button" class="db-btn" @onclick="StartAsync">Start</button>
<p>@_status</p>

@code {
    [Parameter]
    public string DefinitionId { get; set; } = "";

    private string _status = "";

    private async Task StartAsync()
    {
        var started = await PipelinesService.StartPipelineAsync(DefinitionId, new StartPipelineRequest
        {
            Input = new Dictionary<string, string> { ["orderId"] = "1001" },
        });
        _status = started is null ? "Not started" : "Started run " + started.RunId;
    }
}
```

The pipeline reads the input as `{{ $input.orderId }}`. The current user needs `pipelines:run`.

## Configuration

All keys are read from the host's configuration under `Pipelines`. Defaults are the values in the reader classes
(`Stomme.Modules.Pipelines.Steps.PipelineStepOptions`, `Stomme.Modules.Pipelines.Triggers.PipelineTriggerOptions`);
the one exception is `Pipelines:Files:Root`, which the host fills in from the data directory at startup when it is not set.

| Key | Default | Effect |
|---|---|---|
| `Pipelines:Enabled` | `false` | Composes the engine, the API, the page. Read at startup. |
| `Pipelines:Webhooks:Enabled` | `false` | The public webhook receiver `api/Pipelines/webhooks/{name}`. |
| `Pipelines:Webhooks:Secret` | none | The installation's webhook signing secret. Prefer `SecretFile`. |
| `Pipelines:Webhooks:SecretFile` | none | A file holding the secret; wins over `Secret`. |
| `Pipelines:Webhooks:AllowedCallers` | empty (anyone) | Addresses or CIDR ranges a webhook call may come from. |
| `Pipelines:Webhooks:DeduplicationSeconds` | `0` (off) | A repeat of an authentic delivery inside the window is answered 202 and not run. |
| `Pipelines:Webhooks:DeduplicationHeader` | none | The header holding a delivery id; empty uses the body's hash. |
| `Pipelines:Webhooks:Endpoints:{name}:...` | | Per-webhook `AllowedCallers`, `DeduplicationSeconds`, `DeduplicationHeader`. |
| `Pipelines:Webhooks:RequestsPerMinute` | `60` | Rate limit per site and caller. |
| `Pipelines:Webhooks:AnswerWithinSeconds` | `30` | How long a caller waits for a Respond to webhook step. |
| `Pipelines:Webhooks:MaxBodyBytes` | `1048576` | Largest webhook body. |
| `Pipelines:Schedules:Enabled` | `true` | Due schedules start runs. |
| `Pipelines:Schedules:IntervalSeconds` | `60` | How often schedules are checked (at least 5). |
| `Pipelines:Forms:Enabled` | `false` | The Form trigger's pages and the Wait for a form pages. |
| `Pipelines:Chats:Enabled` | `false` | The Chat trigger's pages. |
| `Pipelines:Scripts:Enabled` | `false` | The Run script step and JavaScript expressions. |
| `Pipelines:Scripts:StatementLimit` / `TimeoutSeconds` / `MemoryLimitBytes` | `100000` / `5` / `67108864` | Sandbox bounds. |
| `Pipelines:Processes:Enabled` | `false` | The Run process step. |
| `Pipelines:Processes:AllowedRoots` | empty | Folders an approved program may be launched from; empty refuses every launch. |
| `Pipelines:Processes:TimeoutSeconds` / `MaxOutputChars` | `300` / `1000000` | Process bounds. |
| `Pipelines:Http:AllowedHosts` | empty | Host names the HTTP request step may reach although they resolve to a private, loopback or link-local address. |
| `Pipelines:Http:MaxTimeoutSeconds` / `MaxResponseBytes` / `MaxRedirects` / `MaxPages` | `120` / `10485760` / `3` / `100` | HTTP step bounds. |
| `Pipelines:Files:Root` | `Pipelines/Files` under the data directory | The pipeline folder root; each site has its own folder below it. |
| `Pipelines:Files:MaxFileBytes` | `67108864` | Largest file a file step writes. |
| `Pipelines:MaxDelaySeconds` | `300` | Longest Delay step. |
| `Pipelines:Runs:KeepDays` | `0` (keep all) | Finished runs older than this are deleted. |
| `Pipelines:Waits:IntervalSeconds` / `MaxDays` | `30` / `365` | Due waits are picked up this often; the longest wait. |

## Rules and pitfalls

- **Switch on, nothing appears.** `Pipelines:Enabled` is read at startup from the host's own configuration; edit the
  file the host reads and restart. The page appears under Admin, not in the public navigation.
- **404 on every `api/Pipelines` route** means the switch is off, not that the user lacks rights. With the switch on,
  a missing key answers 403.
- **Run started, nothing happens.** Starting needs a published version; a save after publishing makes a draft that
  does not run until it is published.
- **Polling answers 204.** The run exists but is not stored yet. Keep polling.
- **A value with `{{ }}` went out as typed.** A fixed value is never rendered. Put a template in `InputExpressions` (in
  the editor: "Make it an expression").
- **A placeholder names a variable that does not exist:** the step faults with "There is no variable named ...".
- **A placeholder renders empty, with no fault:** its variable exists but was never set, or it reads a field of a
  list. A records step's result (Map fields, Filter records, Parse CSV) is a list: `{{ rows[0].city }}` reads the
  first record's field, `{{ rows.city }}` reads nothing.
- **A test passes and the run faults.** A test from the editor or `test-step` runs inside the tester's request, as
  that user and on the entered site; a published run has no user. Finish a change with a real run.
- **A secret in a step input is stored in the pipeline.** Marking an input as sensitive hides it in run data only. Keep
  credentials in a connection (Admin > Connections) and pick the connection in the step.
- **Webhook calls all answer 401:** no secret is configured, the signature is not over the exact raw body, or the hex
  is computed under another secret.
- **Code that runs inside a step has no HTTP request.** Do not read `IHttpContextAccessor` or assume `SiteState.Alias`
  is set; enter the site from the engine tenant (see [custom steps](custom-steps.md#entering-the-site)).

## Related

- [The editor](editor.md)
- [Custom steps](custom-steps.md)
- [The HTTP API](api.md)
- [Connectors](../connectors/overview.md)
- [The MCP server](../ai/mcp-server.md)
- [Configuration](../architecture/configuration.md)
- Skills: [create a pipeline step](../../.claude/skills/stomme-create-pipeline-step/SKILL.md),
  [build a pipeline over the API](../../.claude/skills/stomme-build-pipeline-via-api/SKILL.md),
  [import an n8n workflow](../../.claude/skills/stomme-import-n8n-workflow/SKILL.md)
