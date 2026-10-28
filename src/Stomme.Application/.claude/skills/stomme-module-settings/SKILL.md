---
name: stomme-module-settings
description: Use when a module needs configuration - "add a setting to the module", "per-placement options", "module-wide configuration", "ask for a value in the installer", "put the module on a page automatically", "page template", "switch a bundled module off with module.<id>.enabled".
---

# Add settings and page templates to a module

You will have added, to an existing module (the Notes module of `stomme-create-module` in the examples): an **instance
setting** an administrator edits on the placement's Settings page, a **module-wide setting** a Host user edits in Admin >
Module Management (and the installer collects), the code that reads both, and - if the module lacks one - a **page
template**. Read [Settings and templates](../../../docs/modules/settings-and-templates.md) first; it explains where each value
is stored and who may read or write it.

Pick the right kind first:

| The value differs ... | Use |
| --- | --- |
| per placement (this list shows 10, that one 50) | Instance setting (`SettingsType`) |
| per installation (one limit for every site) | Module-wide setting (`ConfigSettingsType`) |
| per site | A site setting through `ISettingService.GetSiteSettingsAsync(siteId)` / `UpdateSiteSettingsAsync` - not covered here |

## Steps

### 1. An instance setting

1. Create `Client/Modules/Notes/Settings.razor`:

   ```razor
   @namespace Stomme.Application.Modules.Notes
   @inherits ModuleBase
   @implements Stomme.Interfaces.ISettingsControl
   @inject ISettingService SettingService

   <div class="db-fields">
       <div class="db-field">
           <label for="notes-pagesize">Notes per page</label>
           <div class="db-field-control">
               <input id="notes-pagesize" type="number" min="1" max="100" class="db-input" @bind="_pageSize" />
           </div>
       </div>
   </div>

   @code {
       public const string PageSizeSetting = "PageSize";

       private string _pageSize = "20";

       public override string Title => "Notes";

       protected override void OnInitialized()
       {
           _pageSize = SettingService.GetSetting(ModuleState.Settings, PageSizeSetting, "20");
       }

       public async Task UpdateSettings()
       {
           var settings = await SettingService.GetModuleSettingsAsync(ModuleState.ModuleId);
           if (int.TryParse(_pageSize, out var pageSize) && pageSize >= 1 && pageSize <= 100)
           {
               settings = SettingService.SetSetting(settings, PageSizeSetting, pageSize.ToString());
           }

           await SettingService.UpdateModuleSettingsAsync(settings, ModuleState.ModuleId);
       }
   }
   ```

2. Name it in `ModuleInfo` (the `ModuleDefinition` initializer):

   ```csharp
   SettingsType = "Stomme.Application.Modules.Notes.Settings, Stomme.Application.Client.Stomme",
   ```

3. Read it in `Index.razor`, where `ModuleState.Settings` already holds the instance's settings. Add
   `@inject ISettingService SettingService` and, in `@code`:

   ```csharp
   private int PageSize => int.TryParse(SettingService.GetSetting(ModuleState.Settings, Settings.PageSizeSetting, "20"), out var size) ? size : 20;
   ```

   Use it when rendering. The Notes list is a `Pager`, which pages by its `PageSize` parameter (a string):

   ```razor
   <Pager Items="@_notes" PageSize="@PageSize.ToString()" SearchProperties="Title">
   ```

   Do not cut the list with `_notes.Take(PageSize)`: the notes after the first page then cannot be reached at all.

4. To read it on the server (in a handler), inject `Stomme.Repository.ISettingRepository` and call
   `await settings.GetSettingValueAsync(EntityNames.Module, moduleId, "PageSize", "20")`. A `moduleId` that came from the client
   must belong to the current site before you use it.

### 2. A module-wide setting

1. Create `Client/Modules/Notes/Config.razor`. It must NOT inherit `ModuleBase` - the installer renders it with no page:

   ```razor
   @namespace Stomme.Application.Modules.Notes
   @implements Stomme.Interfaces.ISettingsControl
   @inject ISettingService SettingService

   <div class="db-fields">
       <div class="db-field">
           <label for="notes-maxlength">Longest note (characters)</label>
           <div class="db-field-control">
               <input id="notes-maxlength" type="number" min="1" max="4000" class="db-input" @bind="_maxLength" />
           </div>
       </div>
   </div>

   @code {
       public const string MaxLengthSetting = "MaxLength";

       [Parameter]
       public int ModuleDefinitionId { get; set; } = -1;

       [Parameter]
       public Dictionary<string, string> InstallSettings { get; set; }

       private string _maxLength = "4000";

       protected override async Task OnInitializedAsync()
       {
           if (ModuleDefinitionId > 0)
           {
               var settings = await SettingService.GetModuleDefinitionSettingsAsync(ModuleDefinitionId);
               _maxLength = SettingService.GetSetting(settings, MaxLengthSetting, "4000");
           }
           else if (InstallSettings != null && InstallSettings.TryGetValue(MaxLengthSetting, out var value))
           {
               _maxLength = value;
           }
       }

       public async Task UpdateSettings()
       {
           if (ModuleDefinitionId > 0)
           {
               var settings = await SettingService.GetModuleDefinitionSettingsAsync(ModuleDefinitionId);
               settings = SettingService.SetSetting(settings, MaxLengthSetting, _maxLength);
               await SettingService.UpdateModuleDefinitionSettingsAsync(settings, ModuleDefinitionId);
           }
           else if (InstallSettings != null)
           {
               InstallSettings[MaxLengthSetting] = _maxLength ?? string.Empty;
           }
       }
   }
   ```

2. Name it in `ModuleInfo`:

   ```csharp
   ConfigSettingsType = "Stomme.Application.Modules.Notes.Config, Stomme.Application.Client.Stomme",
   ```

3. Read it in a component with
   `await SettingService.GetModuleDefinitionSettingsAsync(ModuleState.ModuleDefinition.ModuleDefinitionId)`.

4. Enforce it on the server, where it matters: the API must refuse a note longer than the limit; the page only helps the
   user. A handler does not know the module definition's id, so look the definition up by its name - the `Client`
   namespace of `ModuleInfo`, a comma, and the `Client` assembly. Create
   `Server/Modules/Notes/Application/NotesConfiguration.cs`:

   ```csharp
   #nullable enable
   using System.Linq;
   using System.Threading.Tasks;
   using Stomme.Application.Modules.Notes.Domain;
   using Stomme.Repository;
   using Stomme.Shared;

   namespace Stomme.Application.Modules.Notes.Application
   {
       /// <summary>The Notes module-wide settings (the Configure tab), read on the server.</summary>
       public sealed class NotesConfiguration(IModuleDefinitionRepository definitions, ISettingRepository settings, SiteState siteState)
       {
           public const string DefinitionName = "Stomme.Application.Modules.Notes, Stomme.Application.Client.Stomme";

           public const string MaxLengthSetting = "MaxLength";

           public async Task<int> MaxBodyLengthAsync()
           {
               var siteId = siteState.Alias?.SiteId ?? -1;
               var definition = (await definitions.GetModuleDefinitionsAsync(siteId))
                   .FirstOrDefault(item => item.ModuleDefinitionName == DefinitionName);
               if (definition == null)
               {
                   return Note.MaxBodyLength;
               }

               var value = await settings.GetSettingValueAsync(EntityNames.ModuleDefinition, definition.ModuleDefinitionId, MaxLengthSetting, Note.MaxBodyLength.ToString());
               return int.TryParse(value, out var length) && length > 0 && length <= Note.MaxBodyLength ? length : Note.MaxBodyLength;
           }
       }
   }
   ```

   Register it in `NotesServerStartup.ConfigureServices`, after the repository: `services.AddScoped<NotesConfiguration>();`.
   Then take `NotesConfiguration configuration` as one more constructor parameter of `CreateNoteCommandHandler` (and
   `EditNoteCommandHandler`) and, before the note is created or changed:

   ```csharp
   var maxLength = await configuration.MaxBodyLengthAsync();
   if ((request.Body ?? string.Empty).Trim().Length > maxLength)
   {
       throw new ArgumentException($"A note is at most {maxLength} characters on this installation.");
   }
   ```

   `NotesController` already answers an `ArgumentException` with 400 and its message.

### 3. A page template (if the module has none)

Add `PageTemplates` to the `ModuleDefinition` exactly as in
[Settings and templates](../../../docs/modules/settings-and-templates.md#a-page-template). Keep `AliasName = "*"`,
`Version = "*"` and `Update = false` (the default). Add `using System.Collections.Generic;` and `using Stomme.Shared;` to
`ModuleInfo.cs`.

### 4. Switching the module's templates off for this app

In `Server/features.json`, under `FeatureManagement`, set `"module.notes.enabled": false` (the key is the module's `Name`,
lower-cased, spaces removed). The module stays installed and placeable by hand; its templates and installer step are skipped.

## Verify

1. `dotnet build`, then `cd Server` and `dotnet run`.
2. Sign in as the host and add at least six notes on `/notes`. Switch the page to edit mode, open the Notes placement's
   actions menu and choose the settings item (the address is `/notes/*/{moduleId}/Settings`). The settings page has the tabs
   **Settings**, **Permissions**, **Module Settings** and **Container Settings**; your component is on **Module Settings**
   (the framework names that tab itself: its localized "Module Settings" replaces the component's `Title`) with
   "Notes per page" at 20. Change it to 5 and choose Save at the bottom of the page. On `/notes` the list now shows 5 notes and the pager "Page 1 of 2". Reopen
   the settings: the value is 5.
3. Open **Admin > Module Management** and edit Notes: a **Configure** tab shows "Longest note (characters)" at 4000. The tab
   has its own **Save** button; change the value to 50, save it there, and "The Module Configuration Has Been Saved" appears.
   Reload: the value is 50. (The API accepts this write from Host users only.)
4. Add a note whose text is longer than 50 characters: it is refused with "A note is at most 50 characters on this
   installation." (`POST api/Notes` answers 400). A shorter one is saved. The server log shows the refusal as a `fail`
   line from the mediator ("Unhandled exception while processing CreateNoteCommand"): the mediator logs every exception
   but a FluentValidation `ValidationException` and a cancellation the caller asked for. To keep user mistakes out of
   the error log, make the check an async rule of the command's validator (inject `NotesConfiguration` into it) instead; the API then answers 400 with a validation
   problem.
5. `GET api/Installation/modules` (open, no sign-in) lists Notes with its `configSettingsType` - that list is what the
   installer's onboarding step renders. On a fresh installation through the browser installer (a new override file, see
   `stomme-run-and-install`), the step for Notes appears and the value entered there is in the Configure tab afterwards.
6. With `"module.notes.enabled": false` on a fresh installation, `api/Installation/modules` no longer lists Notes and
   `/notes` answers 404 - the page was not created.
7. The browser console shows no errors throughout.

## Common mistakes

- **No Module Settings tab appears.** `SettingsType` names a type that does not resolve (wrong namespace or assembly name),
  or the component is not in the `Client` project. With no `SettingsType`, only a component named exactly `Settings` in the
  module's namespace is found.
- **The Configure value does not save.** The Configure tab has its own Save button; the Save on the Module and Permissions
  tabs saves the definition (name, description, categories, permissions), not your component's values.
- **You are signed out between two pages, and the log says "Unprotect ticket failed" or "The antiforgery token could not be
  decrypted".** Another Nova Globen Stomme app runs on `localhost` at another port in the same browser: cookies do not carry the port,
  so the two apps overwrite each other's sign-in cookie. Use one app per host name at a time (for example an alias like
  `myapp.localhost:5000`), or a separate browser profile.
- **Saving the placement does not save the setting.** The component does not implement `Stomme.Interfaces.ISettingsControl`
  or `UpdateSettings` swallows a failure; the framework calls `UpdateSettings()` before saving the placement.
- **The Configure tab crashes or the installer step is blank.** `Config.razor` inherits `ModuleBase` or reads `PageState` /
  `ModuleState`, which do not exist there.
- **The installer value is lost.** In installer mode (`ModuleDefinitionId` is -1) the component must write into
  `InstallSettings`, not call the setting service - there is no database yet.
- **The page template never appears.** `AliasName` was left empty, or the app was not restarted, or the feature key is off.
- **An administrator's change of the page or placement keeps being reverted at startup.** The template has `Update = true`.
- **A private value is visible in the browser.** Settings are readable by viewers unless saved private
  (`SetSetting(settings, name, value, true)`); never store secrets in settings.
