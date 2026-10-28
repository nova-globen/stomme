---
name: stomme-import-n8n-workflow
description: Use when moving workflows from n8n to this app - importing an n8n workflow export (one or many), pasting n8n nodes onto the pipeline canvas, or converting n8n JSON to a Nova Globen Stomme pipeline in code - and finishing what the import could not map.
---

# Import an n8n workflow

You will have an n8n workflow as a pipeline on this site: its nodes as the steps here that do the same, its
connections, positions and sticky notes kept, what could not be mapped marked, and the pipeline finished, published
and run. How the editor imports and what it keeps is in [the editor](../../../docs/pipelines/editor.md#import-and-export);
the graph and the API in [the pipelines HTTP API](../../../docs/pipelines/api.md).

## Steps

1. **Export from n8n.** In n8n, download the workflow as JSON, or run `n8n export:workflow --all` for many at once (a
   JSON array). A file of one workflow may be up to 2 MB; a file of many up to 16 MB and 200 workflows. Nodes copied in
   n8n (Ctrl+C) can also be pasted straight onto a canvas here.

2. **Switch on what the workflow needs** in the host's configuration (see the
   [configuration table](../../../docs/pipelines/overview.md#configuration)), and restart:
   `Pipelines:Enabled` always; `Pipelines:Webhooks:Enabled` plus `Pipelines:Webhooks:SecretFile` (or a webhook's own
   secret, made later in the editor) for Webhook nodes; `Pipelines:Forms:Enabled` for forms; `Pipelines:Scripts:Enabled`
   for Code nodes.

3. **Import.** One of:
   - Admin > Pipelines > **New pipeline** > **Import a pipeline file**: each workflow in the file becomes a draft
     pipeline named as in n8n (or as typed in Name for a single one). The list says how many came over and which the
     server refused, and why. A workflow the file reader cannot read at all (not a workflow, no nodes) refuses the
     whole file, naming it ("Its workflow 2: ..."); take it out of the file and import again.
   - In an open pipeline, **Import** puts the workflow on the canvas in place of what is there (Undo takes it back), or
     Ctrl+V pastes nodes copied in n8n.
   - In code (a tool or test project referencing the `Stomme.Shared` package): convert with
     `Stomme.Models.Pipelines.PipelineExchange.ReadMany` and post each graph to `POST api/Pipelines/import`:

     ```csharp
     using System;
     using System.Collections.Generic;
     using System.IO;
     using System.Net.Http;
     using System.Net.Http.Json;
     using System.Threading;
     using System.Threading.Tasks;
     using Stomme.Models.Pipelines;

     namespace Stomme.Application.Tools
     {
         public static class N8nImport
         {
             /// <summary>Imports every workflow in an n8n export as a draft pipeline; returns the new definition ids.</summary>
             /// <param name="http">Authorized for the site (a bearer token), BaseAddress the site's address ending in '/'.</param>
             public static async Task<List<string>> ImportFileAsync(HttpClient http, string path, CancellationToken ct)
             {
                 var documents = PipelineExchange.ReadMany(await File.ReadAllTextAsync(path, ct));
                 var made = new List<string>();
                 foreach (var document in documents)
                 {
                     var request = new ImportPipelineRequest
                     {
                         Name = document.Name ?? Path.GetFileNameWithoutExtension(path),
                         Description = document.Description,
                         Graph = document.Graph,
                     };
                     using var response = await http.PostAsJsonAsync("api/Pipelines/import", request, ct);
                     if (!response.IsSuccessStatusCode)
                     {
                         throw new InvalidOperationException($"'{request.Name}' was refused: {await response.Content.ReadAsStringAsync(ct)}");
                     }

                     var view = await response.Content.ReadFromJsonAsync<PipelineView>(cancellationToken: ct);
                     made.Add(view?.DefinitionId ?? "");
                 }

                 return made;
             }
         }
     }
     ```

     `ReadMany` throws `PipelineExchangeException` with the reason when the file, or any one workflow in it, is not a
     workflow export. Authenticate the `HttpClient` as the API skill says (a bearer token, or a signed-in session's
     cookies plus `X-XSRF-TOKEN-HEADER`); the caller needs `pipelines:manage`.

4. **Read the amber note.** Open the pipeline (Edit). The import puts a note at the left of the canvas, headed
   "Brought over from n8n", counting what came over ("6 nodes: 3 steps, 1 placeholder, 1 note.") and, under
   "To finish:", each thing left, by node name: placeholders, conditions to set, expressions written for n8n's items
   (`$json`), credentials to make, webhook callers to change. Over the API the note is in the graph's `notes` (the one
   with `color` `amber`). Until the `$json` expressions are rewritten, publish is refused
   (`"HTTP Request: Url reads {{ $json }}, but no variable is named '$json'."`) and a test faults on them.

5. **Finish each item:**
   - **Placeholders.** A node no step here does became a switched-off Log step in its place, still connected. Replace
     it with a step that does the job (Ctrl+K to search; a connector operation through Extract from connection or Send
     through connection), or remove it.
   - **Credentials.** n8n credentials do not travel. Make a connection in Admin > Connections for each service, test
     it, and choose it in the step's connection picker.
   - **Expressions.** n8n's `{{ $json.field }}` reads the current item; here an expression reads variables by name.
     Save the earlier step's `Result` to a variable (its **Variables** panel, or `outputVariables` over the API) and
     read it by name. Mind its shape: an HTTP request's `Result` is one object (`{{ weather.temperature }}`), but a
     records step's - Map fields, Filter records, Parse CSV - is a list (`{{ fields[0].city }}` for the first record;
     `{{ fields.city }}` on a list renders empty, with no fault). In a Filter records condition the record's own fields
     are read directly (`{{ status }}`). The Problems list flags placeholders naming no variable.
   - **A Set (Edit Fields) node right after the Manual Trigger** becomes a Map fields step with nothing to map: n8n
     starts a workflow with one empty item, a pipeline here with no records, so the step's `Result` is `[]`. Give it the
     records `[{}]` (its Records input) and it produces one record with the fields it sets.
   - **Code nodes** became Run script steps whose "Written for" is `N8nAllItems` or `N8nEachItem`, so `$input`, `$json`
     and `items` work. A Python Code node is listed to rewrite in JavaScript.
   - **Webhooks.** Callers must now `POST` to `api/Pipelines/webhooks/{name}` on this site, signed with
     `X-Signature-256` (an HMAC-SHA256 of the body under the webhook's secret). Make the webhook's own secret in its
     trigger panel (**Give it its own secret**) and give it to the caller.
   - **The manual trigger** is not brought over: a pipeline here runs by hand without one. **AI sub-nodes** (model,
     memory, tools hanging off an agent) are not either: the site's AI model serves every AI step.

6. **Clear Problems,** then test: **Test this step** on the steps that matter, or **Test pipeline**. Pin the result of a
   step that calls a paid or side-effecting service so tests stop repeating it.

7. **Save and publish,** then run it the way it will run in production (a schedule, a signed webhook call, Run).

## Verify

- The pipeline has the n8n workflow's steps in the same places, connected as they were (an IF's true and false as
  the Decision's True and False ports; an error output as the step's Failed path).
- The Problems list is empty of errors and Save and publish succeeds.
- No step is still a switched-off placeholder unless deliberately kept, and the amber note's items are all done (then
  delete the note).
- A real run finishes with the expected data in each step's output; a webhook call with a correct signature answers
  202 and starts a run; one with a wrong signature answers 401.
- No credential appears in the graph (`GET api/Pipelines/{definitionId}/graph`) or the run's step data.

## Common mistakes

- **"The file is not a pipeline's JSON" or "not a Stomme pipeline"** - the file is not an n8n workflow export (an
  export has top-level `nodes` and `connections`) or not valid JSON.
- **A step faults "There is no variable named ..."** - an n8n expression left as it was. Rewrite it on variables.
- **The first data step's result is `[]` and everything after it reads empty** - a Set node that followed the Manual
  Trigger had n8n's empty starting item; here it has no records. Set its Records to `[{}]`.
- **A URL or message reads empty where a field should be** - the template reads a field of a list. Use
  `{{ name[0].field }}`, or save a step whose result is one object.
- **Run script steps fault** - `Pipelines:Scripts:Enabled` is off.
- **Webhook callers get 404** - `Pipelines:Webhooks:Enabled` is off; **401** - unsigned or signed with another secret.
- **An Execute Workflow node calls nothing** - n8n's workflow ids do not travel; the note says so. Choose the imported
  pipeline in the Execute pipeline step.
- **Something in the note was skipped** - the import cannot judge it; publishing a pipeline with a switched-off
  placeholder runs past it silently.
