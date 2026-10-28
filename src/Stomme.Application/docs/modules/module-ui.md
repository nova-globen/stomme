# Module UI

A module's user interface is a set of Razor components in the app's `Client` project that inherit
`Stomme.Modules.ModuleBase`. The host renders them inside a page's pane and a theme's container, hands them the page, site
and placement they are on, and routes between them by action name. This page explains what `ModuleBase` supplies, how a
module moves between its own views, and the one rule for reaching data: through a service, over HTTP when in a browser.

## How it works

### Controls and actions

Every component of a module that inherits `ModuleBase` is a **module control** (`Stomme.Modules.IModuleControl`). The
host resolves which control to render from the URL:

1. A page URL with no action renders the control named `Index` in the module's namespace (`Index.razor`) - or, when the
   module definition sets `DefaultAction`, the control of that action.
2. An action URL has the form `{page path}/*/{moduleId}/{action}`, for example `/notes/*/12/Edit`. The host renders the
   control whose class name is the action (`Edit.razor` for `Edit`), looked up as `"{namespace}.{action}, {assembly}"`,
   case-insensitively.
3. A control can answer more action names through its `Actions` property (comma-separated). `public override string
   Actions => "Add,Edit";` on `Edit.razor` makes both `.../Add` and `.../Edit` render it.
4. `Settings`, `Import` and `Export` are the framework's own actions: they render the framework's admin controls for the
   placement (see [Settings and templates](settings-and-templates.md) and
   [Portable, searchable, migratable](portable-searchable-migratable.md)). Do not name a control after them expecting it to be
   routed directly.

On an action URL only the addressed placement renders its action control; it is shown in the page's `Default` pane (or the
`Admin` pane when the page has no `Default` pane). With `UseAdminContainer` `true` (the default) it is shown in the admin
container; `false` renders it in the page instead.

### Who may open a control

- The `Index` view of a placement renders for a user who holds `View` on the module instance (`Module.PermissionList`).
- An action control renders according to its `SecurityAccessLevel` (`Stomme.Shared.SecurityAccessLevel`):
  `Anonymous` - everyone; `View` (the default) - `View` on the instance; `Edit` - `Edit` on the instance; `Admin` - the
  Administrators role; `Host` - the Host Users role.
- The framework actions `Settings`, `Import` and `Export` need `Edit` on the PAGE.

These checks decide what the page renders. They are not the security of your data: the API and the mediator check again on
every request (see [API and client services](api-and-client-services.md)).

### What `ModuleBase` supplies

`Stomme.Modules.ModuleBase` (package `Stomme.Client`) is a `ComponentBase` with:

| Member | What it is |
| --- | --- |
| `PageState` (cascading, `Stomme.UI.PageState`) | The current request's view: `Alias`, `Site`, `Page`, `Modules`, `User`, `Uri`, `QueryString` (a `Dictionary<string, string>`), `UrlParameters` (the raw text after `/!/`), `ModuleId` and `Action` of an action URL, `EditMode`, `RenderMode`, `Runtime`. |
| `ModuleState` (cascading, `Stomme.Models.Module`) | The placement being rendered: `ModuleId`, `PageModuleId`, `SiteId`, `Title`, `Pane`, `Settings` (the instance's settings), `PermissionList`, `ModuleDefinition`. |
| `SiteState` (injected, `Stomme.Shared.SiteState`) | Per-circuit state shared with services: `Alias`, `AntiForgeryToken`, `IsPrerendering`, `RemoteIPAddress`. |
| `JSRuntime` (injected) | `Microsoft.JSInterop.IJSRuntime`. |
| `logger` | Writes to the site's event log: `await logger.LogError(ex, "message")`, also `LogInformation`, `LogWarning` and the rest. |
| `IsAuthorizedPermission(string)` | True when the current user holds a plain permission name (`"View"`, `"Edit"`) or a permission key (`"notes:edit"`) on this instance. Decided from `ModuleState.PermissionList` with no server call. |
| `IsAuthorizedRole(string)` | True when the current user holds the role (`Stomme.Shared.RoleNames`). |
| `NavigateUrl(...)` | URL of the current page, of another page path, with a query string, or of an action of a module. |
| `EditUrl(action)`, `EditUrl(action, querystring)`, `EditUrl(moduleId, action)` | URL of an action of this (or another) placement: `{page}/*/{moduleId}/{action}`. |
| `AddUrlParameters(params object[])` | Builds the `/a/b` segment that `NavigateUrl(path, parameters)` puts after `/!/`. |
| `UrlParameters`, `UrlParametersTemplate` | The segments after `/!/` as a dictionary; set `UrlParametersTemplate` to `"/{kind}/{value}"` to name them, otherwise they are `parameter0`, `parameter1`, ... |
| `FileUrl(...)`, `ImageUrl(...)` | URLs of files in the site's file system. |
| `AddModuleMessage(message, MessageType)`, `ClearModuleMessage()`, `ShowProgressIndicator()`, `HideProgressIndicator()` | Messages and progress in the placement's container. These need the `RenderModeBoundary` parameter the host passes to the control; pass it on to child components that call them. |
| `SetModuleTitle`, `SetModuleVisibility`, `SetPageTitle`, `AddHeadContent` | Change the container title, hide the placement, set the page title, add head content. |
| `UtcToLocal`, `LocalToUtc` | Time zone conversion for the current user. |
| `BringIntoView(elementId)` | Scrolls an element into view after the next render. |
| Overridable: `Title`, `Actions`, `SecurityAccessLevel`, `UseAdminContainer`, `Resources`, `RenderMode`, `Prerender` | The control's metadata. `RenderMode` defaults to Interactive; see [Render modes](../ui/render-modes.md). |

### Parameterised addresses inside one page

A module can give each item its own address without a page per item: put the parameters after `/!/` in the page URL.

```razor
<a href="@NavigateUrl(PageState.Page.Path, AddUrlParameters("note", note.Id))">@note.Text</a>
```

This produces `/notes/!/note/{id}`. With `public override string UrlParametersTemplate { get; set; } = "/{kind}/{value}";`
the control reads `UrlParameters["kind"]` and `UrlParameters["value"]`. Because following such a link keeps the same page,
load in `OnParametersSetAsync` and compare the parameters with what you loaded last, rather than only in
`OnInitializedAsync`.

### The service-over-HTTP rule

A module component never reads a repository, a `DbContext` or a handler. It injects a **service contract** declared in
`Shared` and calls it. The same component runs in three places:

| Where it runs | What implements the contract |
| --- | --- |
| Static or Interactive Server site, and prerendering | A server class in `Server` that sends the module's commands and queries to the mediator directly |
| WebAssembly site, in the browser | A client class in `Client` that calls the module's API over HTTP |

Both are registered under the same interface:

1. The client implementation inherits `Stomme.Services.ServiceBase`, whose `GetJsonAsync`, `PostJsonAsync`,
   `PutJsonAsync` and `DeleteAsync` send the site's alias path and the antiforgery token. `CreateApiUrl("Notes")` builds the
   URL of `api/Notes` for the current alias.
2. An `IClientStartup` in `Client` registers it only when nothing is registered yet for the interface.
3. An `IServerStartup` in `Server` registers the server implementation with `AddTransient`. On the server both startups run;
   whichever order they run in, the server implementation is the one resolved.

The `IService` convention does not do this for you: it registers the one class it finds, with no notion of a server
twin, and on the server it pairs that class only with an `I{Name}` interface in the same assembly and namespace, while
the contract is in `Shared`. The full code is in
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md); the HTTP side in depth is in
[API and client services](api-and-client-services.md).

By default `ServiceBase` returns the type's default value (for example `null`) when the server refuses a request; a GET
answered 404 always returns the default. A client service that should surface the server's reason overrides
`protected override bool ThrowOnRefusal => true;` and the page then catches `Stomme.Services.ServiceRefusalException`
(its `Message` is the server's reason, `StatusCode` the HTTP status).

## Using it from an app

A list with add, edit and delete actions, in `Client/Modules/Notes/` - the pages of the reference Notes module, as built
and driven by [stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md). `INotesService`,
`NoteModel`, `SaveNoteRequest` and `NotesPermissions` come from the module's `Shared` code; the module's
`_Imports.razor` brings `System.Net` and the module's `.Models` and `.Services` namespaces.

`Index.razor`:

```razor
@namespace Stomme.Application.Modules.Notes
@inherits ModuleBase
@inject INotesService NotesService

@if (!IsAuthorizedPermission(NotesPermissions.View))
{
    <p id="notes-denied">You do not have access to the notes on this site.</p>
}
else if (_notes == null)
{
    <p class="db-muted">Loading notes...</p>
}
else
{
    @if (IsAuthorizedPermission(NotesPermissions.Edit))
    {
        <div class="db-btn-row">
            <ActionLink Action="Add" Text="Add note" Security="SecurityAccessLevel.View" />
        </div>
    }

    @if (_notes.Count == 0)
    {
        <div class="db-empty" id="notes-empty">
            <div class="db-empty-title">No notes yet</div>
            <div>@(IsAuthorizedPermission(NotesPermissions.Edit) ? "Notes you add appear here. Use Add note to write the first one." : "Notes appear here when someone adds one.")</div>
        </div>
    }
    else
    {
        <Pager Items="@_notes" SearchProperties="Title">
            <Header>
                <th>Title</th>
                <th>Last changed</th>
                <th class="db-col-action"><span class="db-visually-hidden">Actions</span></th>
            </Header>
            <Row>
                <td>@context.Title</td>
                <td>@UtcToLocal(context.UpdatedUtc)?.ToString("g")</td>
                <td class="db-col-action">
                    @if (MayChange(context))
                    {
                        <div class="db-actions">
                            <ActionLink Action="Edit" Text="Edit" Parameters="@($"id={context.Id}")" Security="SecurityAccessLevel.View" />
                            <ActionDialog Id="@($"delete-{context.Id}")" Header="Delete note" Message="@($"Delete the note '{context.Title}'? This cannot be undone.")" Action="Delete" Security="SecurityAccessLevel.View" Class="db-btn db-btn-danger" OnClick="@(async () => await DeleteAsync(context))" />
                        </div>
                    }
                </td>
            </Row>
        </Pager>
    }
}

@code {
    private List<NoteModel> _notes;

    protected override async Task OnParametersSetAsync()
    {
        if (IsAuthorizedPermission(NotesPermissions.View))
        {
            await LoadAsync();
        }
    }

    // A convenience for the UI only: the server decides again on every change.
    private bool MayChange(NoteModel note) =>
        IsAuthorizedPermission(NotesPermissions.Edit)
        && (note.AuthorUserId == PageState.User?.UserId || IsAuthorizedPermission(NotesPermissions.Manage));

    private async Task LoadAsync()
    {
        try
        {
            _notes = await NotesService.ListAsync();
        }
        catch (Exception ex)
        {
            await logger.LogError(ex, "Could not load the notes {Error}", ex.Message);
            _notes = new List<NoteModel>();
            AddModuleMessage("The notes could not be loaded. Reload the page; if it keeps failing, ask an administrator to check the event log.", MessageType.Error);
        }
    }

    private async Task DeleteAsync(NoteModel note)
    {
        try
        {
            await NotesService.DeleteAsync(note.Id);
            await logger.LogInformation("Note deleted {NoteId}", note.Id);
            await LoadAsync();
            AddModuleMessage($"The note '{WebUtility.HtmlEncode(note.Title)}' was deleted.", MessageType.Success);
        }
        catch (Exception ex)
        {
            await logger.LogError(ex, "Could not delete note {NoteId} {Error}", note.Id, ex.Message);
            AddModuleMessage("The note was not deleted: " + WebUtility.HtmlEncode(ex.Message), MessageType.Error);
        }

        // ActionDialog calls a plain Action, so nothing re-renders the page unless it asks to.
        StateHasChanged();
    }
}
```

`Edit.razor`, reached by the `Add` and `Edit` actions (`ActionLink Action="Add"`, `ActionLink Action="Edit"`):

```razor
@namespace Stomme.Application.Modules.Notes
@inherits ModuleBase
@inject INotesService NotesService
@inject NavigationManager NavigationManager

@if (_loading)
{
    <p class="db-muted">Loading...</p>
}
else
{
    <form @onsubmit="SaveAsync" @onsubmit:preventDefault novalidate>
        <div class="db-fields">
            <div class="db-field">
                <Label For="note-title" HelpText="A short name, shown in the list">Title:</Label>
                <div class="db-field-control">
                    <input id="note-title" class="db-input" @bind="_title" maxlength="120" required />
                </div>
            </div>
            <div class="db-field">
                <Label For="note-body" HelpText="The text of the note">Note:</Label>
                <div class="db-field-control">
                    <textarea id="note-body" class="db-input" @bind="_body" rows="6" maxlength="4000"></textarea>
                </div>
            </div>
        </div>
        <div class="db-form-actions">
            <button type="submit" class="db-btn" disabled="@_busy">Save</button>
            <NavLink class="db-btn db-btn-neutral" href="@NavigateUrl()">Cancel</NavLink>
        </div>
    </form>
}

@code {
    private Guid _id = Guid.Empty;
    private string _title = string.Empty;
    private string _body = string.Empty;
    private bool _loading = true;
    private bool _busy;

    // View, not Edit: who may write is the notes:edit key, decided on the server; Edit would mean "edits the page".
    public override SecurityAccessLevel SecurityAccessLevel => SecurityAccessLevel.View;

    public override string Actions => "Add,Edit";

    public override string Title => "Note";

    protected override async Task OnInitializedAsync()
    {
        try
        {
            if (PageState.QueryString.ContainsKey("id") && Guid.TryParse(PageState.QueryString["id"], out var id))
            {
                var note = await NotesService.GetAsync(id);
                if (note == null)
                {
                    AddModuleMessage("This note no longer exists. Go back to the list to see the current notes.", MessageType.Warning);
                }
                else
                {
                    _id = note.Id;
                    _title = note.Title;
                    _body = note.Body;
                }
            }
        }
        catch (Exception ex)
        {
            await logger.LogError(ex, "Could not load the note {Error}", ex.Message);
            AddModuleMessage("The note could not be loaded. Close this dialog and try again.", MessageType.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_title))
        {
            AddModuleMessage("Give the note a title.", MessageType.Warning);
            return;
        }

        _busy = true;
        try
        {
            var request = new SaveNoteRequest { Title = _title.Trim(), Body = _body ?? string.Empty };
            if (_id == Guid.Empty)
            {
                await NotesService.CreateAsync(request);
            }
            else
            {
                await NotesService.EditAsync(_id, request);
            }

            await logger.LogInformation("Note saved {NoteId}", _id);
            NavigationManager.NavigateTo(NavigateUrl());
        }
        catch (Exception ex)
        {
            await logger.LogError(ex, "Could not save the note {Error}", ex.Message);
            AddModuleMessage("The note was not saved: " + WebUtility.HtmlEncode(ex.Message), MessageType.Error);
        }
        finally
        {
            _busy = false;
        }
    }
}
```

`SecurityAccessLevel.View` lets every user who can see the placement open the control; the control and the server then
decide by the module's own key (`notes:edit`). `SecurityAccessLevel.Edit` means the instance's `Edit` permission (set per
placement, Administrators by default) - use it only when that is the right gate, never to stand for a module key: it
hides the form from a Registered User who holds `notes:edit`. An `ActionLink` or `ActionDialog` without `Security`
takes the level of the component its `Action` names; set it explicitly as above.

## Rules and pitfalls

- **`@using Microsoft.AspNetCore.Components.Web` must reach every module component.** Without it a component builds, renders,
  and every `@onclick`, `@onsubmit` and `@bind:event` compiles to a plain HTML attribute - silently dead in an interactive
  render mode, with nothing in the console. The app's `Client/_Imports.razor` already has it and applies to every folder
  under `Client`; a packaged module needs it in its own `_Imports.razor`. Click every button of a module in an interactive
  mode before calling it done.
- **Set `@namespace` in every component** to the module's namespace (the namespace of its `ModuleInfo`). The folder-derived
  default namespace (`Stomme.Application.Client.Modules.Notes`) differs from it and makes the component a separate module.
- **A page's HTML does not contain an interactive module's content.** On a default install modules render interactively,
  so an HTTP request for the page returns the container and title, not the list. Prove content through the API and in a
  browser.
- **A circuit is one long-lived scope.** On an Interactive Server site every service resolved by a component lives as long as
  the browser tab. Do not cache data in a server-side service and expect it to refresh; reload through the service.
- **`AddModuleMessage` throws** when the control was not given `RenderModeBoundary` - for example a child component you
  render yourself. Pass `RenderModeBoundary="RenderModeBoundary"` to it.
- **Do not assume the server.** The component also runs in the browser on a WebAssembly site: no file system, no server
  configuration, no direct database. Everything goes through the service.
- **Hide, then enforce.** `IsAuthorizedPermission` only decides what to show. Every API action carries
  `[RequiresPermissionKey]` and every command or query `[Requires]` with the same key.
- **Use the theme's colours and Nova Globen Stomme's controls** (`class="db-btn"` and the components in
  [Components and controls](../ui/components-and-controls.md)); never style buttons yourself. See
  [UX standards](../ui/ux-standards.md).

## Related

- [Module anatomy](anatomy.md)
- [API and client services](api-and-client-services.md)
- [Render modes](../ui/render-modes.md)
- [Components and controls](../ui/components-and-controls.md)
- [UX standards](../ui/ux-standards.md)
- [Localization](../ui/localization.md)
- Skills: [stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md),
  [stomme-build-module-ui](../../.claude/skills/stomme-build-module-ui/SKILL.md),
  [stomme-ux-review](../../.claude/skills/stomme-ux-review/SKILL.md)
