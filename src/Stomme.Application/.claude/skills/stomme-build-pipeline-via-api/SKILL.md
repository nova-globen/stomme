---
name: stomme-build-pipeline-via-api
description: Use when an agent or a script must create, change, publish, run or check a pipeline (workflow, automation) without the editor - over the site's HTTP API at api/Pipelines - including writing the graph JSON, polling a run and reading its steps and log.
---

# Build and run a pipeline over the API

You will have a pipeline created, its graph saved, published, run and checked - every step over `api/Pipelines`, with
the result read back from the run's steps and log. The contract (routes, the graph's members, keys, errors) is in
[the pipelines HTTP API](../../../docs/pipelines/api.md); triggers, expressions and variables in
[the pipelines overview](../../../docs/pipelines/overview.md).

## Steps

1. **Check the switch and your access.** `GET /api/Pipelines` must answer 200. 404 means `Pipelines:Enabled` is off (or
   the wrong site address); 403 means the caller lacks `pipelines:view`. Authenticate with `Authorization: Bearer
   <token>` for a script (a POST without that header needs the antiforgery cookie and `X-XSRF-TOKEN-HEADER`; with the
   cookies of a signed-in session, the header's value is the `__RequestVerificationToken` hidden field of any page the
   session loads - without it every POST, PUT and DELETE answers 400); the caller needs `pipelines:manage` to author
   and `pipelines:run` to run.

2. **Read the catalogue.** `GET /api/Pipelines/steps`. For every step you plan to use, take its `type`, the exact
   input `name`s, each input's `typeName`, `options` (enum members) and `isRequired`, its `outputs`, and its `ports`.
   Do not guess names.

3. **Create the pipeline.** `POST /api/Pipelines` with `{ "name": "...", "description": "..." }`. Keep the answer's
   `definitionId`.

4. **Write the graph.**
   - One entry in `steps` per step: a unique `id`, the catalogue `type`, a `name`, a `position` (x grows to the right;
     about 280 apart reads well).
   - Fixed values in `inputs`, as text. Templates in `inputExpressions` (`{{ variable.path }}`, `{{ $input.name }}`,
     `{{ $vars.name }}`, `{{ $now }}`). A true-or-false input as rules in `inputConditions`. Never `{{ }}` in `inputs`.
   - To pass data on: declare `variables` (`id`, `name`), save an output with `outputVariables` (`"Result": "v-id"`),
     read it by name in a later template. Data steps also take the previous step's result when their text input is
     empty.
   - `connections`: `sourceId`, `targetId`, and `sourcePort` - the outcome (`Done`, `Failed`, `True`, `False`, a
     case name) for a step whose catalogue entry lists `ports`, null for one that lists none.
   - A trigger (a step of `kind` `Trigger`) with nothing leading to it starts the pipeline; without a trigger the
     pipeline runs by hand from its first step.
   - A credential never goes in a graph: make a connection (Admin > Connections, or its API) and put the connection's
     id in the step's connection input.

   Example - a webhook whose body is logged and, when its `amount` is over 1000, logged again as a warning:

   ```json
   {
     "variables": [ { "id": "v-order", "name": "order" } ],
     "steps": [
       { "id": "hook", "type": "Stomme.Webhook", "name": "Order arrives", "position": { "x": 80, "y": 160 },
         "inputs": { "WebhookName": "orders" }, "outputVariables": { "Result": "v-order" } },
       { "id": "log", "type": "Stomme.LogMessage", "name": "Log it", "position": { "x": 360, "y": 160 },
         "inputExpressions": { "Message": "Order {{ order.id }} for {{ order.amount }}" } },
       { "id": "big", "type": "Stomme.FlowDecision", "name": "Large?", "position": { "x": 640, "y": 160 },
         "inputConditions": { "Condition": { "match": "all", "ignoreCase": false,
           "rules": [ { "left": "{{ order.amount }}", "operator": "greater", "right": "1000" } ] } } },
       { "id": "warn", "type": "Stomme.LogMessage", "name": "Warn", "position": { "x": 920, "y": 80 },
         "inputs": { "Level": "Warning" }, "inputExpressions": { "Message": "Large order {{ order.id }}" } }
     ],
     "connections": [
       { "sourceId": "hook", "targetId": "log" },
       { "sourceId": "log", "targetId": "big" },
       { "sourceId": "big", "sourcePort": "True", "targetId": "warn" }
     ]
   }
   ```

   Started by hand (step 8) instead of by a call, this pipeline runs with an empty `order`: the Log step writes
   `Order  for ` and nothing faults. A placeholder whose variable exists but holds nothing renders as empty text; test
   it with values (step 6) or call the webhook.

5. **Save it.** `PUT /api/Pipelines/{definitionId}/graph` with the graph. 400 carries the reason in `detail` (an unknown
   type, a value its input cannot hold, a webhook name or cron that is not one): fix and save again.

6. **Test before publishing.** `POST /api/Pipelines/{definitionId}/test-step` with
   `{ "graph": ..., "stepId": "log", "values": { "order": "{\"id\": 7, \"amount\": 1500}" } }` runs that one step with
   the variable `order` holding the value given (by variable name), storing nothing. `"upToHere": true` runs the steps
   leading to it first; `"whole": true` (no `stepId`) runs the whole graph as it stands. Read `status` (usually `Finished`, `Faulted`, `Suspended`
   or `Refused`; `Stopped` when the test ran past its 60-second limit), `error`, `steps[]` and `log[]`. Steps run for real: an HTTP step sends its request. A test runs inside
   your request, as you; a published run has no signed-in user, so a step that needs one can pass a test and fault in
   a run - always finish with step 8.

7. **Publish.** `POST /api/Pipelines/{definitionId}/publish` with `{ "note": "what changed" }`. The answer is 200 either
   way: `succeeded` false lists `errors` (a graph problem as "step: message"; an archived pipeline or an
   engine validation error as a plain sentence). Fix the graph, save, publish again.

8. **Run and poll.** `POST /api/Pipelines/{definitionId}/runs` with `{ "input": { "name": "value" } }` (values are text;
   JSON text is read as JSON by `{{ $input... }}`). Keep `runId`. Poll `GET /api/Pipelines/runs/{runId}` about twice a
   second: 204 means not stored yet, keep polling; stop when `status` is `Finished`. A run waiting on a Wait, an
   approval or an event stays `Running` with `subStatus` `Suspended`.
   For a webhook-started pipeline, call `POST /api/Pipelines/webhooks/{name}` instead with the body and
   `X-Signature-256: sha256=<hex HMAC-SHA256 of the exact body bytes under the secret>` (needs
   `Pipelines:Webhooks:Enabled` and a configured secret); it answers 202 (also when no published pipeline listens under
   that name, so a 202 alone proves nothing) and the run is found in
   `GET /api/Pipelines/runs?definitionId={definitionId}`; the Webhook step's `Result` is the body (for a form-encoded
   call, a JSON object of the form's fields).

9. **Read what happened.** `GET /api/Pipelines/runs/{runId}/steps`: one entry per step execution with `stepId`,
   `status`, `error`, and for an author `inputsJson` and `outputsJson`. `GET /api/Pipelines/runs/{runId}/log`: the log
   lines in order.

10. **Change it later** with `GET .../graph`, edit, `PUT .../graph`, publish. The published version runs until the new
    one is published. `GET .../versions` lists them.

## Verify

- `subStatus` of the run is `Finished` (not `Faulted`, not `Cancelled`).
- Every step you expected appears in `runs/{runId}/steps` with `status` `Completed`; a step on a branch not taken does
  not appear.
- The outputs in `outputsJson` and the log lines hold the values you expected - not an empty text where a template
  should have filled in, not the literal `{{ ... }}`.
- `GET /api/Pipelines/{definitionId}/graph` returns the graph you saved, and contains no secret.
- Open Admin > Pipelines in a browser and the pipeline on the canvas: the steps are laid out where you put them, no
  problem is marked, and the run shows on the canvas when chosen.

## Common mistakes

- **404 everywhere** - the switch is off or the base address is not the site's. **404 on run** - nothing is published.
- **400 "is not a step type this installation offers"** - a wrong `type`, or a custom step not registered. Copy types
  from the catalogue.
- **The log says `Hello, {{ who }}`** - the template went into `inputs`. Move it to `inputExpressions`.
- **The step faults "There is no variable named ..."** - a placeholder names a variable not declared in `variables`
  (by `name`), or a built-in misspelled (`$input`, `$vars`, `$run`, `$pipeline`, `$now`, `$today`).
- **A step after a Decision never runs** - the connection has no `sourcePort`; a Decision needs `True` or `False`.
- **Publish "succeeded": false** treated as success - always read `succeeded`. Save accepts a graph with problems (a
  required input left empty saves with 200); only publish refuses it, with `errors` such as `"x: Text needs a value."`.
- **A value came out empty, no fault** - the variable the placeholder reads was never set, or holds a list where the
  template reads a field (`{{ rows.city }}` on a list is empty; `{{ rows[0].city }}` reads the first record).
- **Polling stops at the first 204** - 204 means started, not stored; keep polling.
- **`outputsJson` is null** - the caller lacks `pipelines:manage`, or the pipeline keeps no step data.
- **401 on the webhook** - the signature was computed over different bytes (re-serialized JSON) or another secret.
