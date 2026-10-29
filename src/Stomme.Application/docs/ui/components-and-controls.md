# Components and controls

Nova Globen Stomme gives a module page three layers to build with: its own Razor controls (lists, dialogs, tabs, labels, file
picking, rich text, messages), a CSS component layer of `db-` classes that follows the active theme, and the FluentUI
Blazor components it is built on. Using them is what makes a page look and behave like the admin pages, in every theme,
in light and dark.

## How it works

- Stomme's controls live in the `Stomme.Modules.Controls` namespace (the `Stomme.Client` package). Most inherit
  `Stomme.Modules.ModuleControlBase` (a `ModuleBase`), so they read the cascading `PageState` and `ModuleState` of the
  module they are placed in. Use them **inside a module component**; outside one they have no `ModuleState`.
- Several controls work in both render modes: interactive (Blazor events) and static (plain links and forms posting back).
  `Pager`, `ActionDialog`, `TabStrip` and `Section` switch on their own. See [render modes](render-modes.md).
- The `db-` classes are defined in the framework stylesheet `css/app.css`, which every page loads before the theme's
  stylesheet. They read the `--db-*` tokens, so they take the active theme's colours. See [themes](themes.md).
- FluentUI Blazor (`Microsoft.FluentUI.AspNetCore.Components`, version 5, brought in by `Stomme.Client`) is registered by
  the host. Its brand colours are bound to the theme's `--db-accent` tokens. The host renders its toast, dialog and
  tooltip providers once per page.
- The app's `Client/_Imports.razor` already imports `Stomme.Modules.Controls`, `Stomme.Modules`, `Stomme.Shared`,
  `Stomme.Models`, `Stomme.Services` and `Microsoft.AspNetCore.Components.Web`. It does not import FluentUI.

## Using it from an app

### Stomme's controls

| Control | Use it for | Key parameters |
|---|---|---|
| `Pager<TableItem>` | Every list. Renders a `db-table` (or a grid) with paging and an optional search box. | `Items`, `Header`, `Row` (`RenderFragment<TableItem>`), `Detail`, `Footer`, `Format` (`"Table"` default, or `"Grid"`), `PageSize` (string, default 10), `SearchProperties` (comma-separated property names; adds the search box), `Toolbar` (`"Top"` default, `"Bottom"`, `"Both"`), `PaginationAlignment` (`"start"`, `"center"` default, `"end"`), `OnPageChange`, `Parameters` (query string kept on static paging links). |
| `ActionLink` | A button that opens one of the module's action pages (Add, Edit, ...) and hides itself from users not allowed to use it. | `Action` (required), `Text`, `Parameters` (`"id=5"`), `Security` (`SecurityAccessLevel`), `PermissionList`, `Disabled`, `Class` (default `db-btn`), `IconName`, `IconOnly`, `AltText`, `ReturnUrl`, `EditMode`, `ResourceKey`. |
| `ActionDialog` | A confirmation before a destructive or irreversible action. | `Header`, `Message` (both required), `Action` (the action's name: without it there is no confirm button; without `Security` it names the component whose level applies), `Text` (the buttons' text, default `Action`), `OnClick` (an `Action` delegate), `Security`, `Class` (default `db-btn`; use `"db-btn db-btn-danger"` for delete), `ConfirmClass`, `CancelClass` (default `db-btn db-btn-neutral`), `Disabled`, `Id` (required when a static page holds several), `IconName`, `IconOnly`, `ResourceKey`. |
| `Label` | The label of a form field, with optional help text. | `For` (the field's `id`), `HelpText`, `HelpTopic`, `Class`, `ResourceKey`, `ResourceType`. |
| `Section` | A collapsible group of fields with a heading. | `Name` (required, also the element id), `Heading`, `Expanded` (`"true"`/`"false"` string), `IsVisible`, `ResourceKey`. |
| `TabStrip` + `TabPanel` | Tabs. Static pages switch with a `tab=` query string. | TabStrip: `ActiveTab`, `Refresh`, `Id`. TabPanel: `Name` (required), `Heading`, `Security`, `RoleName`, `PermissionName`, `ResourceKey`. |
| `RichTextEditor` | HTML content editing with the site's text editor (QuillJS by default, `Constants.DefaultTextEditor`). | `Content`, `Placeholder`, `ReadOnly`, `Provider`, `Style`. Read the result with `await editor.GetHtml()` through an `@ref`. |
| `FileManager` | Picking or uploading a file in the site's folders. | `Id`, `Folder` or `FolderId`, `FileId`, `Filter` (`"jpg,png"`), `ShowFiles`, `ShowFolders`, `ShowUpload`, `ShowImage`, `UploadMultiple`, `MaxUploadFileSize` (MB), `OnSelectFile`, `OnUpload`, `OnDelete` (`EventCallback<int>`). Methods: `GetFileId()`, `GetFolderId()`, `GetFile()`, `Refresh()`. |
| `AutoComplete` | A text box that searches as the user types. | `OnSearch` (`Func<string, Task<Dictionary<string, string>>>`, required), `Characters` (default 3), `Rows`, `Placeholder`, `Value`, `Key`, `Required`, `Id`. |
| `InputList` | A text input with suggestions from a fixed list. | `Value`/`ValueChanged`, `DataList` (`Dictionary<string, string>`), `Id`. |
| `TimeZonePicker` | A time zone, searched by typing a city, region or offset (a text input over a datalist of the zones `ITimeZoneService` lists; arrow keys and Enter pick). What is typed becomes the list's IANA id when it names one (any case, an older tzdb name such as `Asia/Calcutta`, a city alone such as `Stockholm`, or a part of exactly one city such as `toky`), otherwise it stays as typed: check it with `TimeZoneMatcher.Match(value, picker.Zones)` (null = not a zone) before saving. | `Value`/`ValueChanged`, `Id`, `Placeholder`, `Class`, `UseBrowserTimeZone` (once interactive, replace the value with the browser's zone when it is in the list and the person has not typed yet; the Register page uses it, so a new account starts in the visitor's zone, else the site's). |
| `TriStateCheckBox` | Yes / No / not set. | `Value` (`bool?`), `Disabled`, `OnChange` (`Func<bool?, bool?>`). |
| `PermissionGrid` | Editing role and user permissions on an entity. | `EntityName`, `PermissionNames`, `PermissionList`. |
| `AuditInfo` | Created / modified / deleted by and on. | `CreatedBy`, `CreatedOn`, `ModifiedBy`, `ModifiedOn`, `DeletedBy`, `DeletedOn`, `IsDeleted`, `DateTimeFormat`. |
| `ModuleMessage` | Rendered for you by `AddModuleMessage`; you rarely place it directly. | `Message`, `Type`, `Style`. |

Messages, progress and titles come from `ModuleBase` (namespace `Stomme.Modules`), not from a control:

- `AddModuleMessage(string message, MessageType type)` and overloads with a position (`"top"` default, `"bottom"`) and a
  `MessageStyle` (`Alert` default, `Toast`). `MessageType` is `Success`, `Info`, `Warning`, `Error`; an error is always
  shown as an alert. `ClearModuleMessage()` removes it.
- `ShowProgressIndicator()` / `HideProgressIndicator()`.
- `SetModuleTitle(string)`, `SetPageTitle(string)`; Blazor's `<PageTitle>` also works (the host mounts a `HeadOutlet`).
- `UtcToLocal(DateTime?)` / `LocalToUtc(DateTime?)` convert with the user's time zone, else the site's. The browser's
  own zone is `await new Interop(JSRuntime).GetTimeZone()` (an IANA name, empty when none; interactive only).
- `BringIntoView(string elementId)` scrolls an element into view after the next render (for an editor that opens below a
  long list). Call it after the awaits in your handler.

A list page in the house style - the markup of the reference Notes module's `Index.razor` (its `@code` block, the
service and the model are in [the module UI skill](../../.claude/skills/stomme-build-module-ui/SKILL.md)):

```razor
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
```

`Security` on `ActionLink` and `ActionDialog` is a `SecurityAccessLevel` on the page or placement: `Edit` means "may edit
this page" (Administrators by default). When who may act is a module's permission key, set `Security` to `View` and
decide with `IsAuthorizedPermission(...)` as above; the server checks the key again. Without `Security`, an `ActionLink`
takes the level of the component its `Action` names; when no such component exists it shows to everyone, and in the
warning colour only from its next parameter update (never on a static page), so do not rely on the colour to find it.

### The `db-` component layer

Use these classes for Stomme-styled markup. They exist in every theme because they are global.

| Purpose | Classes |
|---|---|
| Buttons | `db-btn` (primary, the theme's accent), `db-btn db-btn-neutral` (secondary, Cancel), `db-btn db-btn-danger` (destructive), `db-icon-btn` (icon only), `db-text-btn`; groups: `db-btn-row`, `db-form-actions` |
| Forms | `db-fields` (wrapper), `db-field` (label left, control right; stacks below 640px; a button as the row's third item, such as a password's Show, sits beside the control at every width), `db-field-control`, `db-input` (input and textarea), `db-select`, `db-input-group`, `db-radio-group`, `db-radio`; filter bars: `db-filters`, `db-filter` |
| Tables | `db-table-wrap` (horizontal scroll on narrow screens), `db-table`, `db-col-action`, `db-actions` |
| Surfaces | `db-card`, `db-grid` (responsive card grid), `db-note`, `db-note-warn`, `db-banner` |
| States | `db-empty` + `db-empty-title` (empty list), `db-status-pill` with `online`, `standby`, `warning`, `offline` or `muted` |
| Text | `db-muted`, `db-mono`, `db-eyebrow`, `db-visually-hidden` (screen-reader-only text) |
| Layout | `db-row`, `db-col`, `db-col-auto`, `db-center` |

A form in the house style:

```razor
<div class="db-fields">
    <div class="db-field">
        <Label For="title" HelpText="A short name for the note">Title:</Label>
        <div class="db-field-control">
            <input id="title" class="db-input" @bind="_title" maxlength="200" required />
        </div>
    </div>
</div>
<div class="db-form-actions">
    <button type="button" class="db-btn" @onclick="SaveAsync">@SharedLocalizer["Save"]</button>
    <NavLink class="db-btn db-btn-neutral" href="@NavigateUrl()">@SharedLocalizer["Cancel"]</NavLink>
</div>
```

`SharedLocalizer` is `@inject IStringLocalizer<SharedResources> SharedLocalizer` (`Stomme.SharedResources`); it holds
the framework's common words (`Save`, `Cancel`, `Delete`, `Edit`, `Add`, `Loading`, `Yes`, `No`, `Search`,
`Message.InfoRequired`, ...). See [localization](localization.md).

### FluentUI

The admin pages use FluentUI for a small set of inputs (`FluentTextInput`, `FluentSelect`/`FluentOption`, `FluentSwitch`,
`FluentCheckbox`, `FluentTextArea`, `FluentNumberInput`, `FluentButton`) and plain `db-input`/`db-select` elements for the
rest. Either is acceptable; keep one style per form. To use FluentUI in a component, import it and keep Stomme's
`MessageType` unambiguous:

```razor
@using Microsoft.FluentUI.AspNetCore.Components
@using MessageType = Stomme.Modules.MessageType

<FluentTextInput id="name" @bind-Value="_name" class="db-field-control" />
<FluentSwitch id="enabled" @bind-Value="_enabled" />
<FluentButton Appearance="ButtonAppearance.Primary" OnClick="SaveAsync">Save</FluentButton>

@code {
    private string _name = string.Empty;
    private bool _enabled;

    private async Task SaveAsync()
    {
        await Task.CompletedTask;
    }
}
```

FluentUI 5 names: `FluentTextInput` (not `FluentTextField`), `FluentNumberInput<TValue>`, `FluentSelect<TOption, TValue>`
with `FluentOption<TValue>`, `ButtonAppearance.Primary`/`Default`. FluentUI's icon package is not referenced by Stomme;
reference `Microsoft.FluentUI.AspNetCore.Components.Icons` yourself if you need its icons, or use Stomme's icon names in
`Stomme.Shared.Icons` (Open Iconic CSS classes, for example `Icons.Pencil`, `Icons.Trash`).

## Configuration

- The rich-text editor is the site's choice: Admin > Site Settings > Functionality > Text Editor (the site setting
  `TextEditor`; default `Constants.DefaultTextEditor`, the QuillJS editor). `RichTextEditor.Provider` picks an editor by
  name instead.
- No control reads an `appsettings.json` key.

## Rules and pitfalls

- **A FluentUI tag that does not exist compiles.** Razor treats an unknown `<FluentSomething>` as plain HTML: the page
  builds, renders and shows nothing where the control should be. Check every FluentUI tag name against version 5.
- **`ActionDialog.OnClick` is a plain `Action`, not an `EventCallback`.** The component does not re-render after it runs:
  end the handler with `StateHasChanged()`, and catch exceptions inside it (an async lambda passed as an `Action` runs
  unobserved).
- **Messages are rendered as HTML.** `AddModuleMessage` text goes out as markup. Encode anything a user typed
  (`System.Net.WebUtility.HtmlEncode`) before putting it in a message.
- **`AddModuleMessage` throws in a child component** that was not given the module's `RenderModeBoundary`. Pass it down:
  `<MyChild RenderModeBoundary="RenderModeBoundary" />`, or raise an event and let the module show the message.
- **`Pager` with an empty list still renders an empty table** with its header row (with `Items` null it renders nothing).
  Render a `db-empty` state instead when the
  list is empty.
- **`Label For` must name an element `id` on the page**, unique on the page. A label with no target names nothing for a
  screen reader; two elements with one id make the label name the wrong field.
- **`Label HelpText` is a hover tooltip.** It is not reachable from the keyboard; put anything a user must know in visible
  text, not only in `HelpText`.
- **Times in lists go through `UtcToLocal`.** Values are stored in UTC; printing them raw shows the wrong hour to every
  user outside UTC.
- **Do not restyle Stomme's controls.** No colours on `.db-btn`, `.db-page-link` or FluentUI buttons from module CSS;
  the theme decides. See [themes](themes.md).
- **A page that overrides `OnAfterRenderAsync` must call `await base.OnAfterRenderAsync(firstRender)`**, or
  `BringIntoView` and the module's scripts stop working.

## Related

- [Themes](themes.md) - the tokens the component layer reads.
- [Render modes](render-modes.md) - how controls behave statically and interactively.
- [Localization](localization.md) - `ResourceKey` and resource files.
- [UX standards](ux-standards.md) - how a page built from these must behave.
- [Module UI](../modules/module-ui.md) - `ModuleBase`, actions and state.
- [Files and folders](../services/files-and-folders.md) - what `FileManager` stores.
- Skill: [stomme-build-module-ui](../../.claude/skills/stomme-build-module-ui/SKILL.md).
