---
name: stomme-create-pipeline-step
description: Use when the app needs its own pipeline step, workflow action, node or activity - a step type that appears in the pipeline editor's picker and runs in pipelines - including its inputs, outputs, outcome ports and registration.
---

# Create a pipeline step

You will have a step type in the app's Server project, registered with the pipeline engine, shown in the editor's
picker under its own category, and proven by a published pipeline that runs it. How steps work - the base classes, the
attributes, inputs, outputs, outcomes, failing, entering the site - is in
[custom steps](../../../docs/pipelines/custom-steps.md); the feature as a whole in
[the pipelines overview](../../../docs/pipelines/overview.md).

## Steps

1. **Check that no built-in step does it.** With the app running and Pipelines on, read `GET /api/Pipelines/steps` (or
   search the editor's picker with Ctrl+K). HTTP calls to an authenticated API belong in a connection and the Send
   through connection or Extract from connection step, not in a new step.

2. **Switch Pipelines on** for development, in the configuration file the host reads
   ([configuration](../../../docs/architecture/configuration.md)), and restart:

   ```json
   { "Pipelines": { "Enabled": true } }
   ```

3. **Choose the names once.** The step's type name is `{[Step] namespace}.{ClassName}` and is stored in every pipeline
   that uses it. Use a short namespace of your own (for example your product's name), never `Stomme`. The picker
   groups steps by the exact category text: to join a built-in group, copy its name from the catalogue (`Validations`,
   `Transformations`, `Data`, `Notifications`, ...); `Validation` would make a second group beside `Validations`.

4. **Choose the base class:**
   - one outcome, no result: `CodeStep`;
   - one outcome and a result: `CodeStep<T>` (the result is the `Result` output);
   - several outcomes: `Step<T>` or `Step` with `[FlowNode("A", "B")]`, completing with
     `await context.CompleteStepWithOutcomesAsync("A")`.

5. **Write the class** in `Server/Steps/<Name>.cs`. It must be `public`, concrete and non-generic. Copy the shape of
   one of these and change the logic:

   ```csharp
   #nullable enable
   using System;
   using System.Threading.Tasks;
   using Stomme.Engine.Expressions.Models;
   using Stomme.Engine.Extensions;
   using Stomme.Engine.Pipelines;
   using Stomme.Engine.Pipelines.Attributes;
   using Stomme.Engine.Pipelines.Exceptions;
   using Stomme.Engine.Pipelines.Models;
   using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;

   namespace Stomme.Application.Server.Steps
   {
       public enum RoundingMode
       {
           Nearest,
           Up,
           Down,
       }

       [FlowNode(InRangeOutcome, OutOfRangeOutcome)]
       [Step("MyApp", "Numbers", "Round a number to a step size and check it against a range; the run goes on through In range or Out of range.", DisplayName = "Round and check")]
       public class RoundAndCheck : Step<double>
       {
           public const string InRangeOutcome = "In range";
           public const string OutOfRangeOutcome = "Out of range";

           [Input(Description = "What the number is, for the run's log.", Order = 0)]
           public Input<string> Label { get; set; } = null!;

           [Input(Description = "The number to round.", Order = 1)]
           public Input<double> Value { get; set; } = null!;

           [Input(DisplayName = "Step size", Description = "Round to a multiple of this; more than zero.", DefaultValue = 1, Order = 2)]
           public Input<double> StepSize { get; set; } = new(new Literal<double>(1));

           [Input(Description = "How to round.", DefaultValue = RoundingMode.Nearest, Order = 3)]
           public Input<RoundingMode> Mode { get; set; } = new(new Literal<RoundingMode>(RoundingMode.Nearest));

           [Input(Description = "The smallest value in range.", DefaultValue = 0, Order = 4)]
           public Input<double> Minimum { get; set; } = new(new Literal<double>(0));

           [Input(Description = "The largest value in range.", DefaultValue = 100, Order = 5)]
           public Input<double> Maximum { get; set; } = new(new Literal<double>(100));

           [Output(DisplayName = "Steps", Description = "How many step sizes the rounded value is.")]
           public Output<long>? Steps { get; set; }

           protected override async ValueTask ExecuteAsync(StepExecutionContext context)
           {
               var value = Value.GetOrDefault(context);
               var size = StepSize.GetOrDefault(context);
               if (size <= 0)
               {
                   throw new FaultException("STEP_SIZE_INVALID", "MyApp", "Configuration", "The step size must be more than zero.");
               }

               var steps = Mode.GetOrDefault(context) switch
               {
                   RoundingMode.Up => Math.Ceiling(value / size),
                   RoundingMode.Down => Math.Floor(value / size),
                   _ => Math.Round(value / size, MidpointRounding.AwayFromZero),
               };
               var rounded = steps * size;
               context.Set(Result, rounded);
               context.Set(Steps, (long)steps);
               // A run uses the server's culture: format numbers for the log invariantly, or 42.4 may read 42,4.
               context.AddExecutionLogEntry("Information", FormattableString.Invariant($"{Label.GetOrDefault(context)}: {value} rounded to {rounded}"));

               var inRange = rounded >= Minimum.GetOrDefault(context) && rounded <= Maximum.GetOrDefault(context);
               await context.CompleteStepWithOutcomesAsync(inRange ? InRangeOutcome : OutOfRangeOutcome).ConfigureAwait(false);
           }
       }
   }
   ```

   Rules for inputs: use `string`, `bool`, `int`, `long`, `double`, `decimal` or an enum so the editor can type a fixed
   value; a `string` (or other reference-type) input left `= null!` in a `#nullable enable` file is required - a
   value-type input never is, it runs as its default; a starting value or a nullable type makes an input optional; `UIHint =
   InputUIHints.MultiLine` (`Stomme.Engine.Pipelines.UIHints`) for long text; `CanContainSecrets = true` for a value
   that must be hidden in run data (it is still stored in the pipeline - prefer a connection). Read services with
   `context.GetService<T>()`; before calling a per-site Nova Globen Stomme service or your module's repository, enter the site
   with the `StepSite` helper in [custom steps](../../../docs/pipelines/custom-steps.md#entering-the-site) (copy it to
   `Server/Steps/StepSite.cs`); a step that writes a module's data is shown under
   [a step that writes your module's data](../../../docs/pipelines/custom-steps.md#a-step-that-writes-your-modules-data).
   Pass `context.CancellationToken` to any I/O.

6. **Register the step** in `Server/Steps/StepsStartup.cs` (one startup can register every step in the assembly):

   ```csharp
   using Stomme.Engine.Pipelines.Management.Extensions;
   using Stomme.Infrastructure;
   using Microsoft.AspNetCore.Builder;
   using Microsoft.AspNetCore.Hosting;
   using Microsoft.Extensions.DependencyInjection;

   namespace Stomme.Application.Server.Steps
   {
       public class StepsStartup : IServerStartup
       {
           public void ConfigureServices(IServiceCollection services)
           {
               services.AddStepsFrom<StepsStartup>();
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

   If the app already has a startup in the Server project, add the `services.AddStepsFrom<...>()` line there instead.

7. **Build and restart** the app (`dotnet build`, then run `Server`). Steps are read into the registry at start.

## Verify

1. `GET /api/Pipelines/steps` (signed in as an administrator) lists `MyApp.RoundAndCheck` with category `Numbers`,
   `ports` `["In range", "Out of range"]`, its inputs with `isRequired` true for `Label` only (a value-type input
   left empty runs as its default and is never required), and outputs `Result` and `Steps`.
2. In Admin > Pipelines, create a pipeline, open it (Edit), press Ctrl+K and type "round": the step search under
   **Steps** offers "Round and check" under Numbers; Enter adds it. The inspector shows Label marked `*`, Mode as a
   drop-down "(default: Nearest)", and the step shows two outcome ports.
3. Leave Label empty: the problems box above the canvas reads "1 problem to fix before publishing" with
   "Label needs a value.", and publishing is refused. Set Label to `price` and Value to `42.4`, connect a Log step from
   "In range" and another from "Out of range".
4. Select the step and press **Test step** (under "Test this step" in the inspector; it needs `pipelines:manage` and
   `pipelines:run`): it finishes, its Result is `42`, and the test's log holds `price: 42.4 rounded to 42`.
5. **Save and publish**, then **Run**: the run finishes, the step shows done, and the "In range" Log ran while the other
   did not. Set Step size to `0`, publish and run: the run's status is Finished with sub-status Faulted, and the step's
   error is `The step size must be more than zero.`
6. Open the run page: the step's inputs and outputs are shown. The browser console has no errors.
7. Restart the app and open the pipeline again: it loads unchanged (the type name is stable).

The same checks over HTTP: `PUT api/Pipelines/{definitionId}/graph` with `"type": "MyApp.RoundAndCheck"`,
`POST .../test-step` with `"stepId"`, publish, run and read `runs/{runId}/steps` (see
[build a pipeline over the API](../stomme-build-pipeline-via-api/SKILL.md)).

## Common mistakes

- **The step is not in the picker.** The class is not public, is abstract or generic, the `AddStepsFrom` marker is in
  another assembly, the startup is not in the Server project, or the app was not restarted. Check
  `GET /api/Pipelines/steps` first.
- **Every route answers 404.** `Pipelines:Enabled` is not on in the configuration the host reads.
- **The run stays Running / Suspended at the step.** A `Step` returned without `CompleteStepAsync` or
  `CompleteStepWithOutcomesAsync`.
- **A connection cannot leave the step from an outcome.** The name passed to `CompleteStepWithOutcomesAsync` is not in
  `[FlowNode]`. Use constants for both.
- **A number input refuses a value on save.** The editor typed a value its type cannot hold; for free text use a
  `string` input and parse it in the step.
- **Old pipelines break after a rename.** The type name changed. Restore the old class name or set the `[Step]`
  attribute's `Type` to the old name.
- **A Stomme service refuses inside the step.** The run has no HTTP request and no site entered. Enter the site
  first; never use `IHttpContextAccessor` in a step.
- **A module command passes in Test step and faults in the run** ("Access denied for '...Command'."). A test runs in
  the tester's request; a run has no signed-in user, so a request marked `[Requires]` and a handler that needs
  `ICurrentUser` refuse. Use the module's repository after entering the site, as
  [custom steps](../../../docs/pipelines/custom-steps.md#a-step-that-writes-your-modules-data) shows.
- **The step sits in a group of its own next to a built-in one.** Its category differs from the built-in name by a
  letter (`Validation` against `Validations`). Copy the category from the catalogue.
- **The run's log shows `42,4` where a test showed `42.4`.** The run formats with the server's culture. Format numbers
  for text with `FormattableString.Invariant` or `CultureInfo.InvariantCulture`.
- **No input is marked required.** The catalogue reads required-ness from nullable annotations: the file needs
  `#nullable enable` (or `<Nullable>enable</Nullable>` in the project), and only reference-type inputs count.
- **The build fails on `GetOrDefault`.** `using Stomme.Engine.Extensions;` is missing.
