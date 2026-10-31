# The pipelines HTTP API

Everything the editor does goes through one controller at `api/Pipelines` on the site's address: list, create, save a
graph, publish, run, poll, read a run's steps and log, test a step, import. An agent or a script can build and run a
pipeline with no browser. The request and response types are in `Stomme.Models.Pipelines` (package
`Stomme.Shared`).

## How it works

### Routes exist only while Pipelines is on

With `Pipelines:Enabled` off every route answers 404. With it on, every action checks a permission key of the site the
request was sent to (granted to Administrators by default):

| Key | Needed for |
|---|---|
| `pipelines:view` | reading pipelines, graphs, the catalogue, versions, runs, a run's steps and log |
| `pipelines:manage` | creating, saving, importing, publishing, retracting, archiving, deleting; deleting a run; seeing step data |
| `pipelines:run` | starting a run, cancel, retry, testing a step, raising an event |
| `pipelines:approve` | deciding an approval |

A refused request is 403. A request the module refuses on its merits (a graph the engine will not take, a run that may
not start now) is 400 with a ProblemDetails body: `title` "The pipelines request was refused.", `detail` the reason.

### Authentication and antiforgery

The API uses the site's normal authentication. Every POST, PUT and DELETE is checked for an antiforgery token unless
the request carries an `Authorization` header (or comes from the MAUI app); the public receivers (`POST api/Pipelines/webhooks/{name}`,
`forms/{name}`, `chats/{name}`, `calls/{token}` and `calls/{token}/form`) are never checked:

- **From the app's own UI**, inject `Stomme.Modules.Pipelines.Services.IPipelinesService`; it sends the cookie and
  the antiforgery header (`X-XSRF-TOKEN-HEADER`) for you.
- **From a script or another service**, send a bearer token in `Authorization: Bearer ...`. A request with that header
  is not checked for antiforgery. The token's user needs the keys above. How a site issues tokens is in
  [authentication options](../security/authentication-options.md).
- A cookie-authenticated script must send the antiforgery cookie and the matching request token in
  `X-XSRF-TOKEN-HEADER`, which is how the browser does it. After signing in, load any page of the site with the same
  cookies: the cookie `X-XSRF-TOKEN-COOKIE` is set, and the page's hidden `__RequestVerificationToken` field holds the
  token for the header. Without the header a POST, PUT or DELETE answers 400.

JSON bodies use camelCase property names; dictionary keys (input names, output names) are sent as written
(`"Message"`, `"Result"`).

### The graph

`PipelineGraph` is the body of a pipeline as the editor and the API see it. The server turns it into the engine's
document and back, typing each fixed input by the catalogue and refusing a value its input cannot hold.

| Member | Meaning |
|---|---|
| `steps[]` (`PipelineGraphStep`) | `id` (yours, unique in the graph), `type` (a catalogue `type`, e.g. `Stomme.LogMessage`), `version` (1), `name`, `position {x, y}` |
| `steps[].inputs` | fixed values by input name, as text (`"true"`, `"60"`, an enum member name) |
| `steps[].inputExpressions` | templates by input name: `"Hello, {{ $input.who }}"` |
| `steps[].inputVariables` | input name to a variable `id` |
| `steps[].inputConditions` | input name to a `PipelineCondition` (`match`, `ignoreCase`, `rules[{left, operator, right}]`) |
| `steps[].inputScripts` | input name to a JavaScript expression (needs `Pipelines:Scripts:Enabled`) |
| `steps[].outputVariables` | output name (`Result`, or another output of the step) to a variable `id` |
| `steps[].parent`, `parentPort` | for a step inside a loop's or an If's body: the holder's `id` and the body name (`Body`, `Then`, `Else`) |
| `steps[].retries`, `retryDelaySeconds`, `continueOnError`, `failedPath`, `failureVariable`, `disabled`, `comment` | per-step settings |
| `connections[]` | `sourceId`, `sourcePort` (an outcome such as `Done`, `Failed`, `True`, `False`; null for a step with one outcome), `targetId` |
| `variables[]` | `id`, `name` (what expressions use), `typeName` (`Object` default, `String`, `Int32`, `Double`, `Boolean`), `value` (starting value, text) |
| `notes[]` | sticky notes, never run |
| `runsAtOnce`, `keepSuccessfulRuns`, `saveProgress`, `keepStepData`, `timeoutMinutes`, `calledBy`, `timeZone`, `minutesSavedPerRun` | pipeline settings, null for the default |

A step's inputs, their types, its outputs and its outcome ports are in the catalogue: `GET api/Pipelines/steps`
returns `StepCatalogEntry[]` (`type`, `displayName`, `category`, `kind`, `ports`, `bodies`, `inputs[]` with `name`,
`typeName`, `uiHint`, `defaultValue`, `options`, `isRequired`, `isSensitive`, and `outputs[]`). Read it before writing a
graph; do not guess input names.

An input is either fixed, an expression, a variable, a condition or a script: name it in one of those dictionaries
only. A `{{ }}` in `inputs` is sent as plain text, never rendered.

### Routes

| Method and path | Key | Body | Answer |
|---|---|---|---|
| `GET api/Pipelines?lastRuns=true` | view | | `PipelineView[]` (with each newest run when asked) |
| `GET api/Pipelines/steps` | view | | `StepCatalogEntry[]` |
| `POST api/Pipelines` | manage | `CreatePipelineRequest {name, description}` | `PipelineView`; use its `definitionId` |
| `POST api/Pipelines/import` | manage | `ImportPipelineRequest {name, description, graph}` | `PipelineView`; 400 when the graph is refused |
| `GET api/Pipelines/{definitionId}` | view | | `PipelineDetail` |
| `PUT api/Pipelines/{definitionId}` | manage | `UpdatePipelineRequest {name, description, body}` (`body` null keeps it) | `PipelineView` |
| `GET api/Pipelines/{definitionId}/graph` | view | | `PipelineGraph` |
| `PUT api/Pipelines/{definitionId}/graph` | manage | `PipelineGraph` | `PipelineView`; 400 with the reason when refused |
| `POST api/Pipelines/graph/validate` | manage | `PipelineGraph` | `string[]`: the reasons the save would refuse it, empty when none (since 10.79.0) |
| `POST api/Pipelines/{definitionId}/publish` | manage | `PublishPipelineRequest {note}` or empty | `PublishResult {succeeded, errors[]}` |
| `POST api/Pipelines/{definitionId}/retract` / `archive` / `restore` | manage | | 204 |
| `DELETE api/Pipelines/{definitionId}` | manage | | 204 |
| `GET api/Pipelines/{definitionId}/versions` | view | | `PipelineVersionView[]` |
| `GET api/Pipelines/{definitionId}/versions/{version}/graph` | view | | `PipelineGraph` |
| `POST api/Pipelines/{definitionId}/test-step` | run | `StepTestRequest {graph, stepId, values, upToHere, whole}` | `StepTestResult {status, error, steps[], log[]}`; nothing stored |
| `POST api/Pipelines/{definitionId}/runs` | run | `StartPipelineRequest {correlationId, input}` | `PipelineRunStarted {runId, queued}`; 404 when nothing is published |
| `GET api/Pipelines/runs?definitionId=&take=&state=&search=&from=&to=` | view | | `PipelineRunView[]` (`take` defaults to 100) |
| `GET api/Pipelines/runs/{runId}` | view | | `PipelineRunView`; 204 while started but not stored yet; 404 for none |
| `GET api/Pipelines/runs/{runId}/steps` | view | | `PipelineStepRunView[]` (`inputsJson`/`outputsJson` for manage only) |
| `GET api/Pipelines/runs/{runId}/log` | view | | `PipelineRunLogLine[]` |
| `POST api/Pipelines/runs/{runId}/cancel` | run | | |
| `POST api/Pipelines/runs/{runId}/retry?onItsVersion=false` | run | | `PipelineRunStarted` |
| `POST api/Pipelines/runs/{runId}/retry-from-failure` | run | | `PipelineRunStarted` |
| `DELETE api/Pipelines/runs/{runId}` | manage | | |
| `GET` / `PUT api/Pipelines/variables` | view / manage | `{ "name": "value" }` | the site's variables (`{{ $vars.name }}`) |
| `GET api/Pipelines/capabilities` | view | | `PipelineCapabilitiesView {scripts, webhooks, ai}` |
| `POST api/Pipelines/events/{name}?correlationId=` | run | the payload as raw text | 202; 404 when it cannot be raised |
| `POST api/Pipelines/webhooks/{name}` | none (signed) | the payload | see the [overview](overview.md#triggers) |

### Run states

Poll `GET api/Pipelines/runs/{runId}` until `status` is `Finished` (`isFinished` true). Then `subStatus` is `Finished`,
`Faulted` or `Cancelled`. A run that waits (a Wait step, an approval, an event) stays `Running` with `subStatus`
`Suspended`. Each step's `status` in `runs/{runId}/steps` is `Running`, `Completed`, `Canceled` or `Faulted`, with
`error` for a fault.

## Using it from an app

The smallest pipeline: one Log step that greets what the run was started with.

```http
POST /api/Pipelines
Authorization: Bearer <token>
Content-Type: application/json

{ "name": "Greeter" }
```

The answer's `definitionId` names the pipeline in every later call.

```http
PUT /api/Pipelines/{definitionId}/graph
Authorization: Bearer <token>
Content-Type: application/json

{
  "steps": [
    {
      "id": "greet",
      "type": "Stomme.LogMessage",
      "name": "Greet",
      "position": { "x": 80, "y": 160 },
      "inputExpressions": { "Message": "Hello, {{ $input.who }}" }
    }
  ],
  "connections": []
}
```

```http
POST /api/Pipelines/{definitionId}/publish
Authorization: Bearer <token>
Content-Type: application/json

{ "note": "First version" }
```

Check `succeeded`; when it is false, `errors` says which step and why.

```http
POST /api/Pipelines/{definitionId}/runs
Authorization: Bearer <token>
Content-Type: application/json

{ "input": { "who": "Ada" } }
```

Poll `GET /api/Pipelines/runs/{runId}`, then read `GET /api/Pipelines/runs/{runId}/log`: the Log step's line says
`Hello, Ada`.

A branch: a variable saved from one step and tested by a Decision.

```json
{
  "variables": [ { "id": "v-amount", "name": "amount", "typeName": "Double" } ],
  "steps": [
    { "id": "set", "type": "Stomme.LogMessage", "name": "Start", "position": { "x": 80, "y": 160 },
      "inputExpressions": { "Message": "Amount {{ $input.amount }}" } },
    { "id": "big", "type": "Stomme.FlowDecision", "name": "Large?", "position": { "x": 360, "y": 160 },
      "inputConditions": { "Condition": { "match": "all", "ignoreCase": false,
        "rules": [ { "left": "{{ $input.amount }}", "operator": "greater", "right": "1000" } ] } } },
    { "id": "warn", "type": "Stomme.LogMessage", "name": "Warn", "position": { "x": 640, "y": 80 },
      "inputs": { "Message": "A large amount", "Level": "Warning" } }
  ],
  "connections": [
    { "sourceId": "set", "targetId": "big" },
    { "sourceId": "big", "sourcePort": "True", "targetId": "warn" }
  ]
}
```

(`v-amount` is declared to show the shape; a step would set it through `outputVariables`.)

A C# client for a test project or a tool that references the `Stomme.Shared` package:

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;

namespace Stomme.Application.Tools
{
    /// <summary>Creates, saves, publishes and runs a pipeline over the site's API with a bearer token.</summary>
    public sealed class PipelineApiClient
    {
        private readonly HttpClient _http;

        /// <param name="http">A client whose BaseAddress is the site's address, ending in '/'.</param>
        public PipelineApiClient(HttpClient http, string token)
        {
            _http = http;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<string> CreateAsync(string name, PipelineGraph graph, CancellationToken ct)
        {
            var created = await ReadAsync<PipelineView>(await _http.PostAsJsonAsync("api/Pipelines", new CreatePipelineRequest { Name = name }, ct), ct);
            await ReadAsync<PipelineView>(await _http.PutAsJsonAsync($"api/Pipelines/{created.DefinitionId}/graph", graph, ct), ct);
            var published = await ReadAsync<PublishResult>(await _http.PostAsJsonAsync($"api/Pipelines/{created.DefinitionId}/publish", new PublishPipelineRequest(), ct), ct);
            if (!published.Succeeded)
            {
                throw new InvalidOperationException("Not published: " + string.Join("; ", published.Errors));
            }

            return created.DefinitionId;
        }

        public async Task<PipelineRunView> RunAsync(string definitionId, Dictionary<string, string> input, TimeSpan limit, CancellationToken ct)
        {
            var started = await ReadAsync<PipelineRunStarted>(
                await _http.PostAsJsonAsync($"api/Pipelines/{definitionId}/runs", new StartPipelineRequest { Input = input }, ct), ct);
            var until = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < until)
            {
                using var response = await _http.GetAsync($"api/Pipelines/runs/{started.RunId}", ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var run = await ReadAsync<PipelineRunView>(response, ct);
                    if (run.IsFinished)
                    {
                        return run;
                    }
                }
                else if (response.StatusCode != HttpStatusCode.NoContent)
                {
                    throw new InvalidOperationException($"Reading the run answered {(int)response.StatusCode}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }

            throw new TimeoutException($"Run {started.RunId} did not finish in {limit}.");
        }

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"{(int)response.StatusCode}: {body}");
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                ?? throw new InvalidOperationException("The answer was empty.");
        }
    }
}
```

Testing one step without publishing:

```json
POST /api/Pipelines/{definitionId}/test-step
{ "graph": { ...the graph... }, "stepId": "greet", "values": { "amount": "1200" }, "upToHere": false, "whole": false }
```

`values` are by variable **name**. `whole: true` runs the whole graph as a test; `upToHere: true` runs the steps
leading to `stepId` first. The step runs for real; nothing is stored.

Signing a webhook call (the receiver needs `Pipelines:Webhooks:Enabled` and a secret):

```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace Stomme.Application.Tools
{
    public static class WebhookSignature
    {
        /// <summary>The X-Signature-256 header value for a body sent as these exact bytes.</summary>
        public static string For(string secret, byte[] body) =>
            "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
    }
}
```

Send the same bytes you signed: `POST api/Pipelines/webhooks/{name}` with `X-Signature-256` set; 202 means accepted.

## Configuration

The API has no settings of its own; `Pipelines:Enabled` must be on. The webhook, form and chat receivers have their
switches in the [configuration table](overview.md#configuration).

## Rules and pitfalls

- **404 on everything:** `Pipelines:Enabled` is off, or the request went to another site's address.
- **400 on save, "is not a step type this installation offers":** the `type` is wrong or the custom step is not
  registered. Read `GET api/Pipelines/steps`.
- **400 on save for a value:** a fixed value its input's type cannot hold (`"abc"` for a number, an enum member that
  does not exist).
- **Publish answers 200 with `succeeded: false`.** It is not an HTTP error; read `errors` (`"x: Text needs a value."`).
  Save does not run the problem check: a graph with a required input left empty saves with 200 and only publish
  refuses it.
- **A refused save is logged by the server** at Information ("SaveGraphCommand was refused: ...") since 10.73.2; before,
  it was a `fail` line ("Unhandled exception while processing SaveGraphCommand") although the caller got a clean 400.
- **A test passes, the run faults.** `test-step` runs inside your request, as you, with the site entered; a published
  run has no signed-in user. A step that needs a user or calls a permission-checked command can pass the test and fault
  in the run. Finish every change with a real run.
- **A value comes out empty, with no fault:** a placeholder reads a variable that holds nothing (a webhook pipeline
  started by hand has an empty body), or a field of a list (`{{ rows.city }}`; write `{{ rows[0].city }}`).
- **Run answers 404:** the pipeline has no published version. A save after publishing is an unpublished draft.
- **Polling answers 204** right after the start. Keep polling; it is not an error.
- **400 on start:** the pipeline's `runsAtOnce` setting refuses another run now; the detail says why. A pipeline set to
  take its runs in turn answers 200 with `queued: true` instead and starts the run when the one before has ended.
- **Antiforgery failure (400) on a POST from a script:** send a bearer token, or the antiforgery cookie and header.
- **`inputsJson` and `outputsJson` are null:** the caller lacks `pipelines:manage`, or the pipeline does not keep step
  data (`keepStepData`).
- **Validate before you save:** `Stomme.Models.Pipelines.PipelineGraphProblems.Find(graph, catalog)` runs the
  editor's problem check in your own code with the catalogue from `GET api/Pipelines/steps`.

## Related

- [Pipelines overview](overview.md)
- [The editor](editor.md)
- [Custom steps](custom-steps.md)
- [Authentication options](../security/authentication-options.md)
- [Identity and permissions](../security/identity-and-permissions.md)
- Skills: [build a pipeline over the API](../../.claude/skills/stomme-build-pipeline-via-api/SKILL.md),
  [import an n8n workflow](../../.claude/skills/stomme-import-n8n-workflow/SKILL.md)
