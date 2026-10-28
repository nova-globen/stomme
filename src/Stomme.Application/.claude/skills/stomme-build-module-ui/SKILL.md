---
name: stomme-build-module-ui
description: Use when building or reworking the pages of a Nova Globen Stomme module - a list page, an add/edit form, a delete confirmation, empty/loading/error states, messages, or making a module's UI match the admin pages (Pager, ActionLink, ActionDialog, Label, db-field forms).
---

# Build a module's UI

You will have a module's list page and its add/edit page built from Stomme's controls in the admin pages' style: a
`Pager` list with an empty state, `ActionLink` to add and edit, `ActionDialog` to confirm a delete, a `db-field` form with
Save and Cancel, and every outcome reported with a module message. It works in every theme, in light and dark, on a
narrow screen and from the keyboard. Read [docs/ui/components-and-controls.md](../../../docs/ui/components-and-controls.md)
and [docs/ui/ux-standards.md](../../../docs/ui/ux-standards.md) first; module structure and `ModuleBase` are in
[docs/modules/anatomy.md](../../../docs/modules/anatomy.md) and [docs/modules/module-ui.md](../../../docs/modules/module-ui.md).

This skill builds the **pages**. The module itself (its `ModuleInfo`, server side and storage) comes from the
`stomme-create-module` skill, and the service the pages call - its HTTP client in the browser, its controller on the
server - from `stomme-add-api-endpoint`. The example is the reference **Notes** module; the pages below are its own,
unchanged, driven on an Interactive Server site and on a WebAssembly site. `Stomme.Application` is your app's name.

## Steps

1. **The service contract the pages use** - `Shared/Modules/Notes/Services/INotesService.cs`, with the wire models in
   `Shared/Modules/Notes/Models/NoteModels.cs` (both from `stomme-create-module`). Pages only ever talk to this
   interface, never to a repository or the mediator:

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Threading.Tasks;
   using Stomme.Application.Modules.Notes.Models;

   namespace Stomme.Application.Modules.Notes.Services
   {
       /// <summary>The contract the pages use: over HTTP in a browser, through the mediator on the server.</summary>
       public interface INotesService
       {
           Task<List<NoteModel>> ListAsync();

           Task<NoteModel?> GetAsync(Guid noteId);

           Task<NoteModel> CreateAsync(SaveNoteRequest request);

           Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request);

           Task DeleteAsync(Guid noteId);
       }
   }
   ```

   `NoteModel(Guid Id, string Title, string Body, int AuthorUserId, DateTime CreatedUtc, DateTime UpdatedUtc)` and
   `SaveNoteRequest { Title, Body }` are what it sends and returns. Implement and register it with
   `stomme-add-api-endpoint` (a client service over HTTP registered in the module's `IClientStartup`, and the
   controller it calls).

2. **The module definition** - `Client/Modules/Notes/ModuleInfo.cs`, in the **same namespace** as the pages
   (`Stomme.Application.Modules.Notes`). `stomme-create-module` makes it; keep its `PermissionNames`, which the
   `IsAuthorizedPermission` checks below depend on.

   `Client/Modules/Notes/_Imports.razor` brings the module's own namespaces to its pages:

   ```razor
   @using System.Net
   @using Stomme.Application.Modules.Notes.Models
   @using Stomme.Application.Modules.Notes.Services
   ```

3. **The list page** - `Client/Modules/Notes/Index.razor`. `Index` is the module's default view.

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

   The links say `Security="SecurityAccessLevel.View"` on purpose: `SecurityAccessLevel.Edit` means "may edit this page"
   (Administrators), while who may write notes is the module's `notes:edit` key, checked with `IsAuthorizedPermission`
   here and by the server on every call.

4. **The add/edit page** - `Client/Modules/Notes/Edit.razor`. `Actions => "Add,Edit"` routes both actions to this one
   component; it opens in the admin dialog with `Title` as its heading.

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

5. **Keep the render mode interactive.** `ModuleBase` declares `RenderModes.Interactive`; do not override it on pages with
   buttons. On a Static site these pages become interactive islands. See
   [docs/ui/render-modes.md](../../../docs/ui/render-modes.md).

6. **Localize when the module needs more than one language.** The literal texts above work as they are; to translate them,
   inject `IStringLocalizer<Index>` and add `Client/Resources/Stomme.Application.Modules.Notes.Index.resx` (the file
   name follows the component's namespace, not its folder) - the rules are in
   [docs/ui/localization.md](../../../docs/ui/localization.md).

7. **Build** with `dotnet build` from the app root.

## Verify

Run the app (`cd Server`, `dotnet run`), sign in as the host, and drive it:

1. Open `/notes` (the page template of `stomme-create-module` placed the module there). To place it on another page:
   turn on edit mode from the Control Panel, add the Notes module to a pane, leave edit mode.
2. The list shows "Loading notes...", then the empty state "No notes yet" with its sentence and the Add note button.
3. Add note: the dialog opens with the heading Note. Save with an empty title: the warning "Give the note a title."
   appears and nothing is saved. Enter a title and save: the dialog closes, the note is in the list with a local time.
4. Edit: the form shows the saved values; change and save; the list shows the change. Save a title another note already
   has (in any letter case): "The note was not saved: A note titled '...' already exists on this site."
5. Delete: the confirmation names the note; Cancel changes nothing; Delete removes it and shows "The note '...' was deleted."
6. Add enough notes to page past ten: the pager appears; search by title; press Enter in the search box.
7. Reload the browser on the list and on the edit URL (with `?id=`): both work directly.
8. Repeat 2-5 in light and in dark mode, at 375 pixels wide, and with the keyboard only (Tab, Enter, Space).
9. Sign in as a Registered User: Add note shows; Edit and Delete show only on that user's own notes. A user without
   `notes:edit` sees no Add note at all.
10. Repeat on a site whose runtime is WebAssembly (Admin > Site Settings > UI Component Settings, a section only the Host
    sees), with the console open.
11. The browser console shows no errors at any step, except one "Failed to load resource: ... 409" line on a WebAssembly
    site for the duplicate title you provoked in step 4.
12. Run the `stomme-ux-review` skill on both pages.

What must NOT appear: an empty table with only headers; a message reading "Error" or a raw exception type; a button that
does nothing when clicked.

## Common mistakes

- **The module shows "Missing service(s): ...".** `INotesService` is not registered in the runtime the page runs in (add
  the client registration in the module's `IClientStartup`). Stomme checks this only when the site is browsed on
  `localhost`; elsewhere the page fails to render instead.
- **"Module Type Is Invalid For ..." where the module should be.** The page's namespace differs from `ModuleInfo`'s, or the
  Client assembly was not rebuilt.
- **Add (only Add) shows "Module Type Is Invalid For ...".** No component handles the `Add` action: keep
  `Actions => "Add,Edit"` on `Edit.razor`, or create `Add.razor`.
- **A Registered User who holds the key sees no Add note, or the form refuses them.** An `ActionLink` or `Edit.razor` says
  `SecurityAccessLevel.Edit`. Use `SecurityAccessLevel.View` and `IsAuthorizedPermission(NotesPermissions.Edit)`.
- **An `ActionLink` shows in the warning colour.** `Security` is not set and no component exists for its `Action`; set
  `Security` explicitly.
- **Buttons do nothing, no error.** `Microsoft.AspNetCore.Components.Web` is missing from the `_Imports.razor` that covers
  the page (a new folder or project with its own imports), or the page declared `RenderMode => RenderModes.Static`.
- **The list does not change after Delete.** `StateHasChanged()` missing at the end of the `ActionDialog` handler.
- **`InvalidOperationException` about `RenderModeBoundary`.** `AddModuleMessage` called from a child component; pass
  `RenderModeBoundary="RenderModeBoundary"` to it or show the message from the module component.
- **A message shows raw HTML or breaks the layout.** User text went into `AddModuleMessage` without
  `WebUtility.HtmlEncode`.
- **Times are off by hours.** Printed `UpdatedUtc` directly; use `UtcToLocal(...)`.
- **The Delete confirmation opens for the wrong row on a Static page.** Several `ActionDialog`s without distinct `Id`s.
- **Works on a Server site, fails on WebAssembly.** The page reached a server-only service, or the client service returns
  `null` on a refusal and the page did not check (`ThrowOnRefusal` off); test on WebAssembly before calling it done.
