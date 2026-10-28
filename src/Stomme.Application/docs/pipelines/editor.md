# The pipeline editor

The editor is where a person builds, tests, publishes and watches a pipeline: a canvas of steps and connections, a step
picker, an inspector for the selected step, a problems list, run data on the canvas, versions, and import from files
and from n8n. It ships with the Pipelines module and appears when `Pipelines:Enabled` is on.

## How it works

### Where it is

Admin > Pipelines (path `admin/pipelines`) lists the site's pipelines. From there:

- **New pipeline** opens a form: a name, a description, and **Start from** - empty, or one of the shipped starters.
  **Import a pipeline file** makes a pipeline from a file (this site's export format, or an n8n export).
- **Edit** (or **Open** for someone without `pipelines:manage`) opens a pipeline in the editor.
- **Runs**, **All runs**, **Data tables** and **Files** open the runs list, the site's data tables and the site's pipeline
  folder.

The editor is a Blazor component that works on a Server and a WebAssembly site alike. It reads and writes everything
through `Stomme.Modules.Pipelines.Services.IPipelinesService`, which calls the [HTTP API](api.md); it has no access
the API does not give. Every button is authorized on the server by the permission keys in the
[overview](overview.md#sites-tenants-and-permissions).

### The step picker and the canvas

- The picker is the server's step catalogue (`GET api/Pipelines/steps`), grouped by category. **Ctrl+K** or **/** opens
  the search: every word is matched against a step's name, category, description and type; arrow keys and Enter add the
  highlighted step. A step added while another is selected is placed to its right and connected to it. A palette entry
  can also be dragged onto the canvas.
- The same search finds editor actions (save, tidy up, versions, export) and the site's connections: picking a
  connection adds the step that suits it with the connection chosen.
- Connections run from an outcome port of one step to another step. A step with several outcomes (`Done`/`Failed`,
  `True`/`False`, a Switch's cases, a Fork's branches) has one port per outcome. A loop or an If holds other steps in a
  **body**, opened on the canvas; a body is not an outcome.
- **+** after a step, and **+** and **x** on a connection, add a step there or remove the connection.
- **Add note** puts a sticky note on the canvas; **Tidy up** lays the steps out left to right; **Fit** shows all steps.
- **Shortcuts** (or **?**) lists every key. The main ones: Ctrl+S save, Ctrl+Enter run the published version,
  Ctrl+Z / Ctrl+Y undo and redo, Ctrl+C / Ctrl+X / Ctrl+V copy, cut and paste (the copy also goes to the system
  clipboard, so steps paste into another pipeline with the variables they use), Ctrl+D duplicate, Ctrl+F find on the
  canvas, D switch the selected steps off or on, Enter open the selected step in the wide view, F2 rename, Delete remove.

### The inspector

Selecting a step shows its name, a comment, and one control per input of the catalogue entry:

| The input is | The control |
|---|---|
| `Boolean`, or the hint `checkbox` | a checkbox |
| an enum | a drop-down of its members |
| the hint `multiline` | a multi-line box |
| the hint `code-editor` | a code box with completion (Ctrl+Space) |
| marked as able to hold a secret | a password box |
| a number | a number box |
| the module's own hints (`Stomme.Models.Pipelines.PipelineInputHints`) | a connection picker, a data table picker, a pipeline file picker, a variable picker, a SQL box, a form-field builder, a condition builder, an operation picker (the chosen connection's operations), an AI agent's tools box |
| anything else | a text box |

Each input can be **a value**, **an expression** (a `{{ }}` template, with suggestions of the variables and the fields
of the records they hold, and a preview), **a variable**, a **condition** built from rules (for a true-or-false input) or
**JavaScript** (offered only where `Pipelines:Scripts:Enabled`). A value that contains `{{ name }}` of a variable is a
warning, and **Make it an expression** converts it. Variables are declared under **Variables**; outputs are saved to
them from the inspector. A variable or a record field can be dragged into an input.

An input marked required (the step gives it no starting value) shows `*`. A step's **When it fails** settings:
retries (count and delay), go on past a failure, or a **Failed path** - an outcome port of its own whose error can be
saved to a variable. A step can be switched off: it stays, greyed, and a run goes past it.

### Problems before publish

The editor runs the same check the server runs before a publish (`PipelineGraphProblems`) and marks each problem on its
step, in the inspector beside the input, and in the problems box above the canvas ("1 problem to fix before
publishing, 1 warning", each line naming the step). Errors block **Save and publish**: a required input left empty, a value its input
cannot hold, a placeholder naming no variable, an incomplete condition. Warnings do not block: a step nothing leads to,
a variable read but never set, a value that looks like an expression, a value written into SQL by an expression.
**Fix with AI** (when the site's AI assistant is on and the person may use it) asks the site's model to fix them.

### Saving, publishing, running

- **Save** stores the graph as the latest version (a draft). Two people editing one pipeline see each other, and
  their saves are merged field by field; a conflicting change is refused with the conflicts shown. Leaving with
  unsaved changes asks first.
- **Save and publish** saves and publishes. The "What changed (optional)" box beside it is kept with the version it
  publishes and shown in Versions.
- **Run** starts the published version and follows the run on the canvas: each step shows its state and time; a
  chosen earlier run can be shown the same way. Selecting a step shows what it received and produced in that run, as
  a table or JSON, with search; the **wide view** lays them side by side. Stop cancels a run.
- Data a run's steps received and produced is shown to authors (`pipelines:manage`) only; a viewer sees states.

### Testing without publishing

Nothing a test does is stored as a run, but each step runs for real (an HTTP request is sent, a file is written).

- **Test step** (under "Test this step" in the inspector, for a person with `pipelines:manage` and `pipelines:run`)
  runs the selected step on its own, with the values the variables hold for the test; each step on the canvas also has
  a test button of its own. The result, its log and **Pin this result** appear below it.
- **Test up to here** (shown when steps lead to the selected one) runs every step that leads to it first.
- A test runs inside the tester's request, as that user, on the entered site. A published run has no user, so a step
  that needs one can pass a test and fault in the run: finish with a real run.
- **Test pipeline** runs the whole pipeline as it stands on the canvas, unpublished.
- **Test values**: the values typed for a test are kept for the session; **Keep with the pipeline** saves them with the
  pipeline (authors only; a run never reads them; an export never carries them); **Forget kept values** drops them;
  **Reset values** clears what was typed.
- **Pin this result** keeps a step's result with the pipeline. A test then hands the pinned result on instead of
  running that step (an API not called again, a model not asked again). A published run never reads a pin. **Unpin**
  removes it.
- A trigger has a panel: a webhook's address and how to sign a call, **Listen for a call** (waits for one authentic call
  and uses it as test data), **Give it its own secret**; a schedule's next runs; for Email received and File added a
  button that fetches the newest message or file as test data.

### Versions

**Versions** lists every version the engine keeps, newest first, with who saved it and the publish note. A version can
be opened read-only, compared with the canvas (steps added, removed and changed are marked on the canvas), and
restored onto the canvas as an ordinary change that is stored only when saved.

### Import and export

- **Export** writes the pipeline as a JSON file (`Stomme.Models.Pipelines.PipelineExchangeDocument`, format
  `stomme-pipeline`). Credentials never travel: steps name connections by id, and test values and pins are left out.
- **Import** puts a file on the canvas in place of what is there; Undo takes it back. A file holds at most 1,000 steps
  and 2 MB.
- **n8n.** An n8n workflow export (or nodes copied in n8n and pasted onto the canvas with Ctrl+V) is converted by
  `Stomme.Models.Pipelines.N8nWorkflowImport`: each node becomes the step here that does the same, with what of its
  parameters maps plainly, its connections by output, positions, sticky notes, switched-off state and retry settings. A
  node with no counterpart stays in place as a switched-off placeholder, connected as it was. An amber note on the
  canvas lists what is left to finish. See the [n8n import skill](../../.claude/skills/stomme-import-n8n-workflow/SKILL.md).
- **Import cURL** on the HTTP request step fills it from a curl command; a curl command pasted onto the canvas becomes
  an HTTP request step.

### Building with AI

**Build with AI** (when the site's AI assistant is on and the person may use it) lets the author say what to build or
change; the site's model changes the canvas, the change is judged by the save's own rules, marked as the AI's, and one
Undo away. It never sees a secret. Nothing is stored until the author saves.

## Using it from an app

The editor needs nothing from your code. Your contributions appear in it:

- **A custom step type** is in the picker as soon as it is registered: its category, display name, description,
  inputs (with the controls above) and outcome ports come from its attributes. See [custom steps](custom-steps.md).
- **A connection** made in Admin > Connections is offered by connection pickers. See
  [connectors](../connectors/overview.md).
- **Giving people access:** grant the `pipelines:*` keys to a role in the Pipelines module definition's permissions.

## Configuration

The editor has no settings of its own. What it offers follows the module's switches
(`GET api/Pipelines/capabilities` reports `Scripts`, `Webhooks` and `Ai`): JavaScript only with
`Pipelines:Scripts:Enabled`; the webhook panel always shows the address, but with `Pipelines:Webhooks:Enabled` off it
warns that the site does not receive webhooks and offers neither **Listen for a call** nor a secret of its own. See the
[configuration table](overview.md#configuration).

## Rules and pitfalls

- **Testing is real.** Test this step, Test up to here and Test pipeline call external systems and write files. Pin
  the result of a step whose side effect must not repeat.
- **Publish is refused** while an error remains in Problems; the list says which step and why.
- **Ctrl+Enter runs the published version**, not the canvas. Use Test pipeline for the canvas.
- **A save after publishing is a draft.** The published version keeps running until the next publish.
- **Restoring a version changes only the canvas.** Save to keep it.
- **Imported n8n workflows need finishing.** Read the amber note; placeholders are switched off and run nothing;
  credentials must be made as connections; n8n's webhook callers must now POST and sign their calls.
- **A step type the server no longer has** (an app removed or renamed a custom step) cannot be saved: the save refuses
  the graph naming the type.

## Related

- [Pipelines overview](overview.md)
- [Custom steps](custom-steps.md)
- [The HTTP API](api.md)
- [Connectors](../connectors/overview.md)
- [The AI assistant](../ai/ai-assistant.md)
- Skills: [import an n8n workflow](../../.claude/skills/stomme-import-n8n-workflow/SKILL.md),
  [create a pipeline step](../../.claude/skills/stomme-create-pipeline-step/SKILL.md)
