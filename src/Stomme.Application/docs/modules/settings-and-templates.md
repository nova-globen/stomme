# Module settings and templates

A module has two kinds of configuration and one way to put itself on pages. **Instance settings** belong to one module
instance on a site and are edited by the site's administrator on the placement's Settings page. **Module-wide
configuration** belongs to the module type across the whole installation and is edited by a Host user, or collected by the
installer. **Page templates** declare pages the host creates on every site with the module already placed. A fourth switch,
`module.<id>.enabled`, decides whether those templates are applied at all.

## How it works

### Instance settings (`SettingsType`)

1. The module declares a settings component in `ModuleDefinition.SettingsType` as an assembly-qualified name, for example
   `"Stomme.Application.Modules.Notes.Settings, Stomme.Application.Client.Stomme"`. Without it, the host looks for a
   component named `Settings` in the module's namespace.
2. An administrator opens the placement's actions menu in edit mode and chooses the settings item (the framework's
   `Settings` action, at `{page path}/*/{moduleId}/Settings`). The framework's settings page shows the placement's title,
   pane, container and permissions, and the module's component on the **Module Settings** tab. (The framework reads the
   component's `Title` for that heading, but the tab's own localized text "Module Settings" replaces it.)
3. On Save, the framework calls the component's `UpdateSettings()` - through `Stomme.Interfaces.ISettingsControl`, which the
   component implements - BEFORE it saves the placement.
4. The values are stored as settings of the module instance (`Stomme.Shared.EntityNames.Module`, keyed by `ModuleId`) in
   the tenant's database. Every placement of that instance shares them.
5. When the module renders, `ModuleState.Settings` (a `Dictionary<string, string>`) holds them.

The framework's settings page needs `Edit` permission on the page. Reading instance settings over the API needs `View` on the
instance; a setting saved as private (`isPrivate: true`) is returned only to users who hold `Edit` on it. Writing needs `Edit`
on the instance.

### Module-wide configuration (`ConfigSettingsType`)

1. The module declares a configuration component in `ModuleDefinition.ConfigSettingsType`.
2. It is shown in two places:
   - **Admin > Module Management**, editing the module, on the **Configure** tab. The host passes the component a
     `ModuleDefinitionId` parameter (an `int`) and calls its `UpdateSettings()` when the tab's own **Save** button is
     chosen ("The Module Configuration Has Been Saved"). The Save on the other tabs does not call it.
   - **The installer**, as an onboarding step, for every module that declares `ConfigSettingsType` and is not switched off by
     `module.<id>.enabled`. There is no database yet, so the host passes no `ModuleDefinitionId` (declare it `= -1`, so the
     component can tell) and an `InstallSettings` parameter (a `Dictionary<string, string>`) instead; the component writes its values into that
     dictionary, and the
     installer stores them as the module definition's settings once the installation has created it.
3. The values are stored as settings of the module definition (`EntityNames.ModuleDefinition`) in the installation's master
   database - one set for the whole installation, every tenant and site.
4. Anyone can read them over the API; private ones only a Host user. Only a Host user can write them.

The component is **not** a `ModuleBase`: in the installer there is no page, site or placement to cascade.

### Page templates (`PageTemplates`)

On every startup, for every site, the host collects the `PageTemplates` of every module definition and creates what is
missing:

1. A template is applied only when its `AliasName` is `"*"` or the name of the site's alias. **The default `AliasName` is an
   empty string, which matches nothing here** - a template without `AliasName = "*"` is silently skipped.
2. A template whose `Version` is `"*"` (the default) is considered on every startup. A template with a version number is
   applied only while that number is higher than the site's configuration version, and the site's version then moves up to
   it - so it is applied once.
3. The page is found by `Path` (case-insensitive; `"home"` or `"/"` means the home page). A missing page is created with the
   template's name, title, order, icon, navigation flags and `PermissionList`. An existing page is changed only when the
   template has `Update = true`.
4. The parent is the page whose path is `Parent`, or, with `Parent` empty, the page at the path's prefix
   (`notes/archive` -> `notes`). Declare a parent template before its children in the same list: pages are created in list
   order.
5. Each `PageTemplateModule` places a module on the page. An empty `ModuleDefinitionName` means this module; a non-empty one
   places another definition (for example a second module in the same assembly). An empty `Title` means the definition's
   `Name`. A placement is matched by page, definition and title; a missing one is created with its `Pane`, `Order`,
   `ContainerType` and `PermissionList`, and its `Settings` are stored as the instance's settings.
6. A `PageTemplateModule.Content` is imported into a newly created instance through the module's `IPortable` import (see
   [Portable, searchable, migratable](portable-searchable-migratable.md)).
7. With `Update = true`, the page, the placement, its permissions, its settings and its `Content` are re-applied every time
   the template is processed - with `Version = "*"`, on every startup - which reverts what an administrator changed. Leave
   `Update` false unless you mean that.

A module component can declare a page more briefly with `@page "/notes"` on its `Index`: the host turns the first route into
a template for all sites (`"/path"`) or one alias (`"alias/path"`), with `View` for everyone, or for registered users when
the component carries `[Authorize]`. Nested paths are not allowed this way, and `@page` is ignored when the module declares
`PageTemplates`.

Without any template, the module appears in the control panel (edit mode, **Add Module To Page**) under its `Categories`, and
an administrator places it by hand.

### `module.<id>.enabled`

A module the app ships - inside the app or bundled as a package - is always discovered. Whether its page templates are
applied is decided by a feature key read from configuration:

1. The key is `module.<id>.enabled`, where `<id>` is the module definition's `Name` lower-cased with spaces removed:
   `"Notes"` -> `module.notes.enabled`, `"Knowledge Base"` -> `module.knowledgebase.enabled`.
2. It is read from the `FeatureManagement` section of the host's configuration: `Server/features.json` (loaded by the host,
   optional, reloaded on change), `appsettings.json`, environment variables or the override file.
3. `false` (or `{ "enabled": false }`, or `{ "EnabledFor": [] }`) means: the module's page templates are skipped, the module is
   placed on no page by any template, and the installer does not show its configuration step. `true`, absent, or any other
   value means the normal behaviour.
4. It does not unload the module. The assembly, its services, its API and its definition remain, and an administrator can
   still place it by hand. Placements that already exist stay.

## Using it from an app

### An instance settings component

`Client/Modules/Notes/Settings.razor`:

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

and in `ModuleInfo`:

```csharp
SettingsType = "Stomme.Application.Modules.Notes.Settings, Stomme.Application.Client.Stomme",
```

`ISettingService` is `Stomme.Services.ISettingService` (package `Stomme.Client`). The module's `Index` reads the value
with `SettingService.GetSetting(ModuleState.Settings, Settings.PageSizeSetting, "20")` and hands it to its `Pager` as
`PageSize="@PageSize.ToString()"`. Code in `Server` reads an instance
setting with `Stomme.Repository.ISettingRepository`:
`await settings.GetSettingValueAsync(EntityNames.Module, moduleId, "PageSize", "20")`. A module id that arrives from a
client must be checked against the current site before it is used.

### A module-wide configuration component

`Client/Modules/Notes/Config.razor`:

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

and in `ModuleInfo`:

```csharp
ConfigSettingsType = "Stomme.Application.Modules.Notes.Config, Stomme.Application.Client.Stomme",
```

A module component reads the value with
`await SettingService.GetModuleDefinitionSettingsAsync(ModuleState.ModuleDefinition.ModuleDefinitionId)`. Server code reads it
with `await settings.GetSettingValueAsync(EntityNames.ModuleDefinition, moduleDefinitionId, "MaxLength", "4000")`. A handler
does not know `moduleDefinitionId`: find the definition with `Stomme.Repository.IModuleDefinitionRepository`
(`GetModuleDefinitionsAsync(siteId)`) by its `ModuleDefinitionName`, which is the `Client` namespace of `ModuleInfo`, a comma
and the `Client` assembly - `"Stomme.Application.Modules.Notes, Stomme.Application.Client.Stomme"`. The
[stomme-module-settings](../../.claude/skills/stomme-module-settings/SKILL.md) skill has the complete class.

### A page template

```csharp
PageTemplates = new List<PageTemplate>
{
    new PageTemplate
    {
        Name = "Notes",
        Path = "notes",
        AliasName = "*",
        Version = "*",
        Order = 10,
        Icon = "oi oi-document",
        IsNavigation = true,
        PermissionList = new List<Permission>
        {
            new Permission(PermissionNames.View, RoleNames.Registered, true),
            new Permission(PermissionNames.View, RoleNames.Admin, true),
            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
        },
        PageTemplateModules = new List<PageTemplateModule>
        {
            new PageTemplateModule
            {
                Title = "Notes",
                Pane = PaneNames.Default,
                Order = 1,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Registered, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                }
            }
        }
    }
},
```

Types: `Stomme.Models.PageTemplate`, `Stomme.Models.PageTemplateModule`, `Stomme.Models.Permission`,
`Stomme.Shared.PermissionNames`, `Stomme.Shared.RoleNames` (`Everyone` is "All Users", `Registered` "Registered Users",
`Admin` "Administrators", `Host` "Host Users"), `Stomme.Shared.PaneNames`.

### Switching a shipped module off

`Server/features.json`:

```json
{
  "FeatureManagement": {
    "module.notes.enabled": false
  }
}
```

## Configuration

| Key | Default | Effect |
| --- | --- | --- |
| `FeatureManagement:module.<id>.enabled` | absent (treated as on) | `false` skips the module's page templates and its installer configuration step. |

## Rules and pitfalls

- **`AliasName = "*"` on every module page template.** The default empty alias name never matches, and a skipped template
  logs nothing.
- **Prefer `Version = "*"` with `Update = false`.** A numbered version is compared with the SITE's configuration version,
  which every module's templates and site migrations move. A template numbered lower than what another part already moved
  the site to is never applied.
- **Every module definition that places itself must declare the permission keys its pages check.** A placement receives the
  site's key rows only from its own definition. A second module (a second namespace) that checks `notes:view` but does not
  declare it renders "no access" for everyone, while the API behind it answers.
- **The feature key follows the module's `Name` as stored.** An administrator can rename a module in Admin > Module
  Management, and the stored name wins over the one in code - which also changes the `module.<id>.enabled` key the host
  looks for. Name the key after the name in effect.
- **`module.<id>.enabled` is not a security switch.** A module switched off this way still answers its API and can be placed
  by hand. To remove a capability, gate its API (`[RequiresPermissionKey]`, a feature check) as well.
- **`ConfigSettingsType` components must work with no page.** Do not inherit `ModuleBase` and do not read `PageState` or
  `ModuleState` there.
- **Settings are strings.** Parse and validate in `UpdateSettings` and again where they are read; keep a default for every
  read.
- **Secrets do not belong in settings shown to the browser.** Instance settings are returned to anyone with `View` unless
  saved private; module-wide ones to anyone unless private. Use `SetSetting(settings, name, value, true)` for a value only
  editors should read, and keep real secrets out of settings; see [Security posture](../security/security-posture.md).

## Related

- [Module anatomy](anatomy.md)
- [Module UI](module-ui.md)
- [Configuration](../architecture/configuration.md) - `features.json`, the override file, every switch
- [Identity and permissions](../security/identity-and-permissions.md)
- Skills: [stomme-module-settings](../../.claude/skills/stomme-module-settings/SKILL.md),
  [stomme-bundle-external-module](../../.claude/skills/stomme-bundle-external-module/SKILL.md),
  [stomme-configure-features](../../.claude/skills/stomme-configure-features/SKILL.md)
