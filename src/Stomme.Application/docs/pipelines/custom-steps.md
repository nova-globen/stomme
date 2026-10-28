# Custom pipeline steps

A step type is a C# class the pipeline engine runs. The app adds its own step types next to the built-in ones: once
registered, a step is in the editor's picker with its inputs, outputs and outcome ports, in the catalogue
(`GET api/Pipelines/steps`), and in every run. This document is the contract; the
[create a pipeline step skill](../../.claude/skills/stomme-create-pipeline-step/SKILL.md) is the procedure.

## How it works

### The contract

A step is a public, concrete, non-generic class that implements `Stomme.Engine.Pipelines.IStep`, in practice by
deriving from one of these base classes (all in the `Stomme.Engine.Pipelines` namespace, package
`Stomme.Engine.Pipelines`, which `Stomme.Server` brings):

| Base class | Use it when | Completes |
|---|---|---|
| `CodeStep` | the step does work and has one outcome | by itself when `Execute`/`ExecuteAsync` returns |
| `CodeStep<T>` | the same, with a `Result` output of type `T` | by itself |
| `Step` / `Step<T>` | the step chooses among several outcomes, or completes later | only when the step calls `context.CompleteStepAsync()` or `context.CompleteStepWithOutcomesAsync(...)` |

Override `protected virtual void Execute(StepExecutionContext context)` or
`protected virtual ValueTask ExecuteAsync(StepExecutionContext context)`. A `Step` that returns without completing
leaves the run suspended.

### Metadata the picker reads

| Attribute | Namespace | What it sets |
|---|---|---|
| `[Step(namespace, category, description, DisplayName = ...)]` | `Stomme.Engine.Pipelines.Attributes` | The type name (`{namespace}.{ClassName}`, or `{namespace}.{Type}` when `Type` is set), the category the picker groups by, the description, the display name (default: the class name in words). `Version` defaults to 1. |
| `[Input(...)]` on an `Input<T>` property | `Stomme.Engine.Pipelines.Attributes` | `DisplayName`, `Description`, `Category`, `Order`, `DefaultValue` (shown in the editor), `UIHint`, `CanContainSecrets`, `IsBrowsable` |
| `[Output(...)]` on an `Output<T>` property | `Stomme.Engine.Pipelines.Attributes` | `DisplayName`, `Description` |
| `[FlowNode("A", "B")]` | `Stomme.Engine.Pipelines.Steps.Flowchart.Attributes` | The outcome ports, in order. Without it the step has one implicit outcome. |
| `[PipelineConnectorTypes("slack", ...)]` | `Stomme.Modules.Pipelines.Steps` | On a connection-picker input: the connector types the picker offers. |

`Input<T>` and `Output<T>` are in `Stomme.Engine.Pipelines.Models`; `Literal<T>` in `Stomme.Engine.Expressions.Models`.

### Inputs

- **Types the editor can type a fixed value into:** `string`, `bool`, `int`, `long`, `short`, `double`, `float`,
  `decimal` and enums. Anything else is passed as the text typed. An enum is a drop-down of its member names.
- **Required or optional.** The catalogue reads it from the class: an input is required when the step gives it no
  starting value and it holds a reference type annotated not-null. `public Input<string> Text { get; set; } = null!;`
  in a file with `#nullable enable` is required; the editor marks it `*` and publishing is refused while it is empty.
  A value-type input (`Input<int>`, `Input<bool>`, an enum) is never required: left empty it runs as the type's
  default. Without nullable annotations (no `#nullable enable` and no `<Nullable>enable</Nullable>`) nothing is
  required. Give a starting value (`= new(new Literal<int>(60));`) or make the type nullable (`Input<string?>?`) for an
  optional one.
- **Reading:** `Text.GetOrDefault(context)` (an extension member in `Stomme.Engine.Extensions`). Expressions,
  variables and conditions are evaluated before your code sees the value: the step never parses `{{ }}`.
- **UI hints:** `Stomme.Engine.Pipelines.UIHints.InputUIHints.MultiLine` (`multiline`), `Checkbox`, `CodeEditor`
  (`code-editor`, a JavaScript box), and the module's own in `Stomme.Models.Pipelines.PipelineInputHints`
  (`ConnectionPicker`, `SiteTablePicker`, `PipelineFile`, `SqlQuery` and others). An enum input gets a drop-down without
  a hint.
- **Secrets.** `CanContainSecrets = true` shows the input as a password box and hides its value in run data. The value
  is still stored in the pipeline. For a credential, take a connection instead (`PipelineInputHints.ConnectionPicker`)
  and use it as [using connections in code](../connectors/using-connections-in-code.md) describes.

### Outputs and outcomes

- Set an output with `context.Set(Output, value)`. `CodeStep<T>` and `Step<T>` have an output named `Result`; setting it
  also makes it the run's **last result**, which the built-in data steps read when their text input is left empty.
  Records passed between data steps are a JSON array of objects as text; follow that convention to chain with them.
- In the graph, an author saves an output to a variable by its output name (`OutputVariables["Result"]`).
- A `Step` with `[FlowNode("Valid", "Invalid")]` completes with
  `await context.CompleteStepWithOutcomesAsync("Valid")`; connections leave from the port of that name.

### Failing

- Throw `Stomme.Engine.Pipelines.Exceptions.FaultException(code, category, type, message)` to fault the step with a
  code and a message the run page and log show. Any other exception faults it too.
- The author's per-step settings (retries, go on past a failure, a Failed path, switched off) apply to every step,
  yours included; the module wraps each step with them. You do not implement them.
- Write to the run's log with `context.AddExecutionLogEntry(eventName, message)`.
- Honour `context.CancellationToken` in any wait or I/O: Stop and the pipeline's time limit cancel through it.

### Services

`context.GetService<T>()` and `context.GetRequiredService<T>()` resolve from the run's scope, so a step reaches anything
the host or your module registered. The engine creates step instances from stored definitions; the built-in steps take
no constructor parameters and reach every service through the context - do the same, and keep no state in fields.
(The catalogue also makes a prototype of the step to read its starting values, using a constructor whose parameters are
all optional; a step with none gets no required inputs.)

### Entering the site

A run executes on the engine's background worker. Its scope knows the engine tenant
(`Stomme.Engine.Common.Multitenancy.ITenantAccessor.TenantId`, in the form `"{TenantId}/{SiteId}"`) but has no HTTP
request, and `Stomme.Shared.SiteState.Alias` may be null. Nova Globen Stomme services that work per site (repositories, the
cache keyed by alias, notifications, connections) need the site entered first. Since 10.73.2 call the framework's own
helper, as the built-in steps do: `await Stomme.Modules.Pipelines.Tenancy.StommeSiteScope.EnsureSiteAsync(context)` (or
`EnsureSiteAsync(serviceProvider)` in a recurring task), which returns false when the run has no engine tenant. On an older
Stomme it was internal; an app writes the same few lines with public types:

```csharp
#nullable enable
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines;
using Stomme.Infrastructure;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Shared;

namespace Stomme.Application.Server.Steps
{
    /// <summary>Enters the Stomme site of the engine tenant a step runs under.</summary>
    public static class StepSite
    {
        /// <summary>True when the scope has a site (already, or now); false when the run has no engine tenant.</summary>
        public static async Task<bool> EnterAsync(StepExecutionContext context)
        {
            if (context.GetService<SiteState>()?.Alias is not null)
            {
                return true;
            }

            var tenants = context.GetService<ITenantAccessor>();
            var tenantManager = context.GetService<ITenantManager>();
            if (tenantManager is null || !EngineTenantIds.TryParse(tenants?.TenantId, out var tenantId, out var siteId))
            {
                return false;
            }

            await tenantManager.SetAliasAsync(tenantId, siteId).ConfigureAwait(false);
            return true;
        }
    }
}
```

After `EnterAsync` returns true, `SiteState.Alias` is the run's site and
`Stomme.Tenancy.Abstractions.ITenantContext.CurrentTenantId` its tenant, so a module's repository reads and writes
that site's rows.

### A step that writes your module's data

A run has no signed-in user: `ICurrentUser.UserId` is null, and a mediator request marked `[Requires]` is refused
("Access denied for 'CreateNoteCommand'."). Beware: **Test step** in the editor runs inside the request of the person
testing, so the same command passes in a test and faults in the published run. A step therefore enters the site and
uses the module's repository itself, the way a handler would, and takes from its inputs what a request would have taken
from the user. For the Notes module (`Server/Steps/CreateNote.cs`, with
`StepSite` above in `Server/Steps/StepSite.cs`):

```csharp
#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Application.Modules.Notes.Persistence;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Application.Server.Steps
{
    [Step("MyApp", "Notes", "Write a note on the pipeline's site; the result is the new note's id.", DisplayName = "Create note")]
    public class CreateNote : CodeStep<string>
    {
        [Input(Description = "The note's title; unique on the site.", Order = 1)]
        public Input<string> Title { get; set; } = null!;

        [Input(Description = "The note's text.", UIHint = InputUIHints.MultiLine, Order = 2)]
        public Input<string?>? Body { get; set; }

        [Input(DisplayName = "Author user id", Description = "The id of the user the note is written by.", Order = 3)]
        public Input<int> AuthorUserId { get; set; } = null!;

        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            if (!await StepSite.EnterAsync(context).ConfigureAwait(false))
            {
                throw new FaultException("NO_SITE", "MyApp", "Configuration", "The run has no site to write the note on.");
            }

            var siteId = context.GetRequiredService<SiteState>().Alias?.SiteId ?? 0;
            var tenantId = context.GetRequiredService<ITenantContext>().CurrentTenantId
                ?? throw new FaultException("NO_TENANT", "MyApp", "Configuration", "The run has no tenant.");
            var author = AuthorUserId.GetOrDefault(context);
            if (author <= 0)
            {
                throw new FaultException("NO_AUTHOR", "MyApp", "Configuration", "Give the id of the user the note is written by.");
            }

            var notes = context.GetRequiredService<INoteRepository>();
            var title = Title.GetOrDefault(context) ?? "";
            if (await notes.FindByTitleAsync(siteId, title, context.CancellationToken).ConfigureAwait(false) is not null)
            {
                throw new FaultException("NOTE_EXISTS", "MyApp", "Business", $"A note titled '{title.Trim()}' already exists on this site.");
            }

            var now = context.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            var note = Note.Create(Guid.NewGuid(), tenantId, siteId, author, title, Body.GetOrDefault(context) ?? "", now);
            await notes.AddAsync(note, context.CancellationToken).ConfigureAwait(false);
            await context.GetRequiredService<IUnitOfWork>().SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
            context.AddExecutionLogEntry("Information", $"Note {note.Id} created on site {siteId}.");
            context.Set(Result, note.Id.ToString());
        }
    }
}
```

The step checks what the module's command handler checks (here the unique title); `Note.Create` refuses an empty
title. The note appears in `GET api/Notes` and on the Notes page of the run's site.

### Registration

`Stomme.Engine.Pipelines.Management.Extensions.ManagementServiceCollectionExtensions` adds step types to the engine's
`ManagementOptions.StepTypes`, which the step registry reads:

- `services.AddStepsFrom<TMarker>()` - every public, concrete, non-generic `IStep` in `TMarker`'s assembly.
- `services.AddStep<TStep>()` - one type.
- `services.AddSteps(types)` - a list.

Call one of them from an `IServerStartup.ConfigureServices` in the app's Server project (the host discovers startups in
the app's `.Stomme` assemblies; see [startup and discovery](../architecture/startup-and-discovery.md)). The calls only
add to options, so they are safe whether or not `Pipelines:Enabled` is on, and the order of startups does not matter.
The registry is filled when the host starts: a new step appears after a restart.

## Using it from an app

A step with one outcome and a result (`Server/Steps/MakeSlug.cs`):

```csharp
#nullable enable
using System;
using System.Globalization;
using System.Text;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Application.Server.Steps
{
    [Step("MyApp", "Text", "Turn a text into a URL slug: lower-case ASCII letters and digits joined by hyphens.", DisplayName = "Make slug")]
    public class MakeSlug : CodeStep<string>
    {
        [Input(Description = "The text to turn into a slug.", Order = 1)]
        public Input<string> Text { get; set; } = null!;

        [Input(DisplayName = "Max length", Description = "The longest slug to make.", DefaultValue = 60, Order = 2)]
        public Input<int> MaxLength { get; set; } = new(new Literal<int>(60));

        [Output(DisplayName = "Length", Description = "How many characters the slug has.")]
        public Output<int>? Length { get; set; }

        protected override void Execute(StepExecutionContext context)
        {
            var text = Text.GetOrDefault(context) ?? "";
            var maxLength = Math.Max(1, MaxLength.GetOrDefault(context));
            var slug = Slugify(text, maxLength);
            if (slug.Length == 0)
            {
                throw new FaultException("SLUG_EMPTY", "MyApp", "Business", "The text has no letters or digits to make a slug of.");
            }

            context.AddExecutionLogEntry("Information", "Slug: " + slug);
            context.Set(Result, slug);
            context.Set(Length, slug.Length);
        }

        private static string Slugify(string text, int maxLength)
        {
            var slug = new StringBuilder();
            var gap = false;
            foreach (var c in text.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsAsciiLetterOrDigit(c))
                {
                    if (gap && slug.Length > 0)
                    {
                        slug.Append('-');
                    }

                    gap = false;
                    slug.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    gap = true;
                }
            }

            var result = slug.ToString();
            return result.Length <= maxLength ? result : result[..maxLength].TrimEnd('-');
        }
    }
}
```

A step that chooses an outcome (`Server/Steps/CheckEmailAddress.cs`):

```csharp
#nullable enable
using System.Net.Mail;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;

namespace Stomme.Application.Server.Steps
{
    [FlowNode(ValidOutcome, InvalidOutcome)]
    [Step("MyApp", "Validations", "Check that a text is one e-mail address; the run goes on through Valid or Invalid.", DisplayName = "Check e-mail address")]
    public class CheckEmailAddress : Step<string>
    {
        public const string ValidOutcome = "Valid";
        public const string InvalidOutcome = "Invalid";

        [Input(Description = "The address to check.")]
        public Input<string> Address { get; set; } = null!;

        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var address = (Address.GetOrDefault(context) ?? "").Trim();
            var valid = MailAddress.TryCreate(address, out var parsed) && parsed.Address == address;
            context.Set(Result, address);
            await context.CompleteStepWithOutcomesAsync(valid ? ValidOutcome : InvalidOutcome).ConfigureAwait(false);
        }
    }
}
```

The registration (`Server/Steps/StepsStartup.cs`):

```csharp
using Stomme.Engine.Pipelines.Management.Extensions;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Application.Server.Steps
{
    /// <summary>Adds this app's pipeline steps to the engine's step registry.</summary>
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

In a graph sent over the API the steps are `"type": "MyApp.MakeSlug"` and `"type": "MyApp.CheckEmailAddress"`, the
second connected through `"sourcePort": "Valid"` or `"Invalid"`.

## Configuration

A step reads its own settings like any service: bind an options class in your startup and resolve
`IOptions<TOptions>` with `context.GetService<T>()`. Default to the safe behaviour when the setting is absent (the
built-in steps refuse scripts, processes and private-network HTTP unless switched on).

## Rules and pitfalls

- **The type name is stored in every pipeline that uses the step.** Renaming the class, changing the `[Step]` namespace
  or `Type`, or deleting the class breaks those pipelines: the engine puts its not-found placeholder step in its place,
  and a graph naming a type the installation does not offer is refused on save. Choose the name once. Use your own `[Step]` namespace (not `Stomme`) so no name collides with a built-in step.
- **Not in the picker:** the class is not public, is abstract or generic, is not in the scanned assembly, the startup
  is not in an assembly the host discovers, or the host was not restarted.
- **The run sits Suspended:** a `Step` (not `CodeStep`) returned without completing. Call `CompleteStepAsync` or
  `CompleteStepWithOutcomesAsync`.
- **To choose an outcome, derive from `Step` or `Step<T>`.** A `CodeStep` completes itself with its one outcome when
  `Execute` returns.
- **An outcome name in `CompleteStepWithOutcomesAsync` that is not in `[FlowNode]`** has no port on the canvas, so no
  connection can leave from it. Keep the names in constants used by both.
- **An input reads null in a run but has a value in the editor:** the input is bound to a variable that nothing set, or
  the author typed `{{ }}` into a fixed value. Check the step's inputs in the run data.
- **A Stomme service refuses or finds nothing for want of a site:** the step did not enter the site. Use the helper
  above before calling it.
- **No `HttpContext`, no current user.** A run started by a webhook or a schedule has no person behind it. Do not rely
  on `IHttpContextAccessor`; services authorized per user may refuse.
- **Async all the way down.** Never `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` in a step; override
  `ExecuteAsync`.
- **Tests are real, and run as the tester.** The editor's Test step runs your code for real, inside the request of the
  person testing: a site is already entered and a user is signed in. A published run has neither, so prove a step
  that uses site or user services with a published run, not only a test.
- **Format numbers in text invariantly.** A run formats with the server's culture (`42,4` where a test showed `42.4`);
  use `FormattableString.Invariant` or `CultureInfo.InvariantCulture` for log lines and text outputs.
- **Use the catalogue's category names.** The picker groups by the exact category text; `Validation` makes a group
  beside the built-in `Validations`.
- **Custom triggers are not covered here.** A trigger is a `Trigger<T>` with a stimulus the runtime must be told about;
  the public path for starting pipelines from your code is the Event trigger and `RaisePipelineEventCommand` (see the
  [overview](overview.md#triggers)).

## Related

- [Pipelines overview](overview.md)
- [The editor](editor.md)
- [The HTTP API](api.md)
- [Using connections in code](../connectors/using-connections-in-code.md)
- [Startup and discovery](../architecture/startup-and-discovery.md)
- Skill: [create a pipeline step](../../.claude/skills/stomme-create-pipeline-step/SKILL.md)
