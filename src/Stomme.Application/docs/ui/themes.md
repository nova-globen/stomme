# Themes

A theme is the page shell Nova Globen Stomme renders around modules: the header, the menu, the panes modules are placed in, the
footer, and one or more containers that frame each module. A theme also owns the site's colours, through the `--db-*`
CSS tokens that every Stomme control and every FluentUI control reads. This page explains how a theme is discovered,
rendered and coloured, and what an app must do to ship one.

## How it works

### The types

All in the `Stomme.Client` package unless noted.

| Type | Namespace | Role |
|---|---|---|
| `ITheme` | `Stomme.Themes` | Optional. One class per theme returns a `Stomme.Models.Theme` describing it (name, version, stylesheets, settings controls, colour mode). |
| `ThemeBase` | `Stomme.Themes` | Base class of a theme's page layout component. Supplies `PageState` (cascading), `SiteState`, `JSRuntime`, URL helpers (`NavigateUrl`, `EditUrl`, `FileUrl`, `ImageUrl`), `SetPageTitle`, `AddHeadContent`, `AddScript`, `ThemePath()`, `StaticAssetPath`. Implements `IThemeControl` (`Name`, `Thumbnail`, `Panes`, `Resources`). |
| `ContainerBase` | `Stomme.Themes` | Base class of a container. Adds the cascading `ModuleState` (`Stomme.Models.Module`). |
| `LayoutBase` | `Stomme.Themes` | Marked `[Obsolete]`; the theme scan skips layout controls. Do not use it. |
| `Theme` | `Stomme.Models` (`Stomme.Shared` package) | The theme description: `Name`, `Version`, `PackageName`, `Resources`, `ThemeSettingsType`, `ContainerSettingsType`, `ColorMode`. |
| `ThemeColorModes` | `Stomme.Shared` | `FollowVisitor` (`""`), `Dark` (`"dark"`), `Light` (`"light"`). |
| `Stylesheet`, `Script`, `Resource` | `Stomme.Models` (`Stomme.Shared` package) | The files a theme asks the host to link. |
| `PaneNames` | `Stomme.Shared` | `Default` (`"Default"`) and `Admin` (`"Admin"`). |
| `Pane` | `Stomme.UI` | Renders the modules placed in the named pane. |
| `ModuleInstance` | `Stomme.UI` | Inside a container: renders the module itself. |
| Theme controls | `Stomme.Themes.Controls` | `Logo`, `Menu`, `Search`, `UserProfile`, `Login`, `ControlPanel`, `LanguageSwitcher`, `Breadcrumbs`, `CookieConsent`, `HelpLauncher` for the shell; `ModuleActions` and `ModuleTitle` for containers. |

### Discovery

1. At startup the host loads every assembly whose name contains `Stomme` from the application folder. The app's own
   projects qualify: the template names them `Stomme.Application.Client.Stomme`, `Stomme.Application.Server.Stomme`
   and `Stomme.Application.Shared.Stomme`.
2. In each assembly it finds every type that implements `IThemeControl` (so every `ThemeBase` subclass) and is not
   abstract, generic, marked `[StommeIgnore]`, a container or a layout.
3. A theme's identity is its component's **namespace plus assembly name** (for example
   `Stomme.Application.Client.Themes.Harbor, Stomme.Application.Client.Stomme`). Every theme component in that
   namespace belongs to the same theme; each one is a selectable layout of it.
4. The `ITheme` class in the **same namespace** supplies the theme's description. Without one the theme is named after the
   component type and has no stylesheet.
5. Every `ContainerBase` subclass in the **same namespace** becomes one of the theme's containers.
6. A stylesheet URL that starts with `~` is rewritten to `/Themes/<theme namespace>/`. So `new Stylesheet("~/Theme.css")`
   in the theme `Stomme.Application.Client.Themes.Harbor` is linked as
   `/Themes/Stomme.Application.Client.Themes.Harbor/Theme.css`.

A new theme is enabled on every site by default (`Theme.IsAutoEnabled` is `true`).

### Which theme a page renders

- A page's own theme (Admin > Page Management > edit a page > Theme) wins.
- Otherwise an admin page (the path `admin` or anything under `admin/`) renders the site's **Admin Theme** when one is
  set (Admin > Site Settings > Theme > Admin Theme). This is `Site.AdminThemeType`.
- Everything else renders the site's **Default Theme** (`Site.DefaultThemeType`, Admin > Site Settings > Theme).
- The rule is `Stomme.Shared.Utilities.ResolveThemeType(pageThemeType, pagePath, defaultThemeType, adminThemeType)`; the
  host calls it on the server render and in the interactive router, so both agree.
- Five themes ship with Stomme and can be chosen per site: **Graphite** (the default), **Stomme Theme** and **Blazor
  Theme** in `Stomme.Client`, and **Aurora (Public)** and **Slate (Admin)**, the `Stomme.Theme.Aurora.Stomme` and
  `Stomme.Theme.Slate.Stomme` packages that `Stomme.Server` brings at its own version.
- The stock default is the Graphite theme (`Stomme.Shared.Constants.DefaultTheme`); containers default to Graphite's
  container (`Constants.DefaultContainer`), and admin action pages use `Stomme.Themes.AdminContainer`
  (`Constants.DefaultAdminContainer`).

### How a page is put together

1. The host renders the page's theme component, then FluentUI's toast, dialog and tooltip providers once, after the theme.
   A theme never renders FluentUI providers itself.
2. Each `<Pane Name="..." />` in the theme renders, in order, the modules placed in that pane, each wrapped in its
   container (`ContainerBuilder` picks the module's container, else the page's, else the site's default).
3. A module placed in a pane the theme does not declare moves to the `Default` pane; if the theme has no `Default` pane
   either, it moves to `Admin`. A theme with neither pane cannot show those modules.
4. A module action page (Edit, Settings, any action other than `Index`) renders into the `Default` pane (or `Admin`), inside
   the admin container, which draws it as a dialog over the page.
5. A theme's stylesheets (its description's and its layout component's) are linked in the page `<head>` after the
   framework's `css/app.css`, and only while that theme renders the page. A container's own `Resources` are used for
   scripts only - its stylesheets are never linked - so put a container's styles in the theme's stylesheet.

### Colours: the `--db-*` tokens

The framework's stylesheet (`css/app.css`, served by the `Stomme.Server` package) defines a design system of CSS custom
properties and a component layer of `db-` classes (`db-btn`, `db-input`, `db-table`, `db-dialog`, `db-tabs` and more)
that read them. FluentUI's brand colours are bound to the same tokens on `<body>`, so a FluentUI primary button and a
`.db-btn` always show one colour.

| Token | Meaning |
|---|---|
| `--db-accent` | The theme's colour: primary buttons, focus rings, the active tab, the active pager item. |
| `--db-accent-2`, `--db-accent-3` | Hover and pressed states. Derived from `--db-accent` with `color-mix` unless the theme sets them. |
| `--db-on-accent` | Text on the accent colour. |
| `--db-ok`, `--db-warn`, `--db-bad`, `--db-info` | Status colours (messages, status pills, danger buttons). |
| `--db-bg`, `--db-bg-elev`, `--db-bg-elev-2`, `--db-bg-elev-3` | Page background and raised surfaces. |
| `--db-border`, `--db-border-strong` | Borders. |
| `--db-text`, `--db-text-2`, `--db-muted` | Body text, secondary text, muted text. |
| `--db-radius`, `--db-radius-sm`, `--db-radius-xs` | Corner radii. |
| `--db-shadow`, `--db-shadow-2`, `--db-shadow-3` | Shadows. |
| `--db-sans`, `--db-mono` | Font stacks. |
| `--db-z-dropdown`, `--db-z-sticky`, `--db-z-overlay`, `--db-z-modal`, `--db-z-toast`, `--db-appbar-h` | Stacking order and the header height. |

The rule, set by the product owner: **a button takes the colours its theme defines, and nothing overrides them; a theme
that defines none gets the framework's defaults.** The framework's own values are declared with zero specificity
(`:where(:root)` for the dark defaults, `:where(html[data-theme="light"], body[mode="light"])` for the light surfaces), so whatever a
theme declares on `:root` wins in both modes.

### Light and dark

- The host's head script sets `data-theme="dark"` or `data-theme="light"` and `color-scheme` on `<html>` before the first
  paint. The visitor's choice is stored in the browser under the `localStorage` key `stomme-theme`; with no choice the
  page follows the operating system's preference, live.
- `stommeTheme.toggle()` switches the mode; the stock themes put it on a button with the class `db-theme-toggle`.
- A theme designed for one mode declares it: `ColorMode = ThemeColorModes.Dark` (or `Light`) in its `Theme`. The host then
  renders `data-theme-fixed`, `data-theme` and `color-scheme` on `<html>` from the server, ignores the visitor's stored
  choice, makes `toggle()` do nothing and hides every element with the `db-theme-toggle` class.
- Style light mode by keying on `html[data-theme="light"]`, never on a `prefers-color-scheme` media query: the query answers
  for the operating system, not for the mode the site is in.

## Using it from an app

A theme can live in the app's `Client` project (simplest; it ships with the app) or in a separate package (to share it
between apps). The step-by-step build is the skill
[stomme-create-theme](../../.claude/skills/stomme-create-theme/SKILL.md); the essentials:

`Client/Themes/Harbor/ThemeInfo.cs`:

```csharp
using System.Collections.Generic;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Themes;

namespace Stomme.Application.Client.Themes.Harbor
{
    public class ThemeInfo : ITheme
    {
        public Theme Theme => new Theme
        {
            Name = "Harbor",
            Version = "1.0.0",
            ColorMode = ThemeColorModes.FollowVisitor,
            Resources = new List<Resource>
            {
                new Stylesheet("~/Theme.css")
            }
        };
    }
}
```

`Client/Themes/Harbor/Default.razor` (the layout; it must declare a `Default` pane):

```razor
@namespace Stomme.Application.Client.Themes.Harbor
@inherits ThemeBase

<div class="hb-shell">
    <header class="hb-header">
        <Logo UseSiteNameAsFallback="true" />
        <nav class="hb-nav"><Menu Orientation="Horizontal" /></nav>
        <div class="hb-actions">
            <Search />
            <UserProfile ShowRegister="true" />
            <Login ShowLogin="true" />
            <ControlPanel LanguageDropdownAlignment="right" />
            <button type="button" class="db-icon-btn db-theme-toggle" onclick="stommeTheme.toggle()" title="Toggle light / dark theme" aria-label="Toggle light / dark theme">&#9680;</button>
        </div>
    </header>
    <main role="main" class="hb-main">
        <Pane Name="@PaneNames.Default" />
        <div class="hb-columns">
            <div class="hb-column"><Pane Name="Left" /></div>
            <div class="hb-column"><Pane Name="Right" /></div>
        </div>
    </main>
    <footer class="hb-footer"><Pane Name="Footer" /></footer>
    <CookieConsent />
</div>

@code {
    public override string Name => "Harbor";

    public override string Panes => PaneNames.Default + ",Left,Right,Footer";
}
```

`Client/Themes/Harbor/Container.razor`:

```razor
@namespace Stomme.Application.Client.Themes.Harbor
@inherits ContainerBase

<div class="hb-module">
    @if (ModuleState.Title != "-")
    {
        <div class="hb-module-head">
            <ModuleActions /><h2><ModuleTitle /></h2>
        </div>
    }
    else
    {
        <ModuleActions />
    }
    <ModuleInstance />
</div>

@code {
    public override string Name => "Harbor Container";
}
```

`Server/wwwroot/Themes/Stomme.Application.Client.Themes.Harbor/Theme.css` (the folder name is the theme's namespace):

```css
/* Dark values (the default) on :root; light values on html[data-theme="light"]. */
:root {
    --db-accent:    #4FB3BF;
    --db-on-accent: #0B1A1C;
}

html[data-theme="light"] {
    --db-accent:    #146C78;
    --db-on-accent: #FFFFFF;
}

.hb-shell { min-height: 100vh; display: flex; flex-direction: column; background: var(--db-bg); color: var(--db-text); }
.hb-header { display: flex; align-items: center; gap: 0.5rem; min-height: var(--db-appbar-h); padding: 0 1.5rem; border-bottom: 1px solid var(--db-border); background: var(--db-bg-elev); }
.hb-header .app-logo img { max-height: 38px; }
.hb-nav { flex: 1 1 auto; min-width: 0; }
.hb-actions { display: flex; align-items: center; gap: 0.35rem; margin-left: auto; }
.hb-main { flex: 1 1 auto; width: 100%; max-width: 1200px; margin: 0 auto; padding: 1.5rem 1rem 3rem; }
.hb-columns { display: grid; grid-template-columns: 1fr 1fr; gap: 0 1.5rem; }
.hb-column { min-width: 0; }
.hb-module { margin-bottom: 1.5rem; }
.hb-module-head { display: flex; align-items: center; gap: 0.5rem; margin-bottom: 0.75rem; }
.hb-footer { padding: 1rem; border-top: 1px solid var(--db-border); color: var(--db-text-2); text-align: center; }

/* Below the breakpoint the menu is only its toggle, and the header's controls must fit the width. */
@media (max-width: 767.98px) {
    .hb-header { flex-wrap: wrap; padding: 0.25rem 0.5rem; gap: 0.2rem; }
    .hb-header .app-logo img { max-height: 32px; }
    .hb-nav { flex: 0 0 auto; }
    .hb-actions { min-width: 0; flex-wrap: wrap; justify-content: flex-end; gap: 0.1rem; }
    .hb-header .db-icon-btn { width: 32px; height: 32px; font-size: 15px; }
    .hb-header .db-btn { padding: 0.4rem 0.5rem; font-size: 12px; }
    .hb-header .app-search .db-search-input { display: none; }
    .hb-header .app-search .db-search-btn { position: static; transform: none; }
    .hb-columns { grid-template-columns: 1fr; }
}
```

The stock header controls do not size themselves for a theme's header: without the `.app-logo img` rule the site logo
renders at its full size, and without the narrow rules a signed-in header (user name, Logout, edit mode, Control Panel,
the mode toggle) is wider than a 390-pixel screen. These rules set sizes only, never colours.

Select the theme in Admin > Site Settings, Theme section: the Default Theme list names it "Harbor - Harbor" (the theme's
`Name`, then the layout's `Name`). Choosing it limits the Default Container list to the theme's containers, here
"Harbor Container".

### A theme in its own package

To share a theme between apps, build it as a Razor class library and ship it as a NuGet package:

- Reference `Stomme.Client` only (a theme is client-side; it needs nothing from `Stomme.Server`).
- Give the assembly a name that contains `Stomme` (for example `<PackageId>.Stomme`), or the host never loads it.
- Put the stylesheet in the library's `wwwroot/Theme.css`, set
  `<StaticWebAssetBasePath>_content/<PackageId></StaticWebAssetBasePath>` in the project, set `PackageName = "<PackageId>"`
  in the `Theme`, and link the stylesheet as `new Stylesheet("_content/<PackageId>/Theme.css")`. With the base path set,
  the file is served at the same URL whether the app references the package at build time or the host installs the
  `.nupkg` at run time; without it, a build-time reference serves the file under the assembly name and the link is dead.
- Install it in an app either by a `<PackageReference>` in `Server/Stomme.Application.Server.csproj` (bundled at build
  time, discovered like any assembly) or by uploading the `.nupkg` in Admin > Theme Management (installed at the next
  start).

Admin > Theme Management > Create Theme (Host only) generates a separately packaged theme from the template in
`Server/wwwroot/Themes/Templates/External`. It does not produce a working theme in an app as it stands; read what it
generates before using it:

- It writes the new solution to a folder named `<Owner>.Theme.<Name>` beside the app's own folder (two levels above the
  `Server` project), not inside the app.
- With the default Framework Reference ("Installed Version") its `Client` project references `Stomme.Client.dll` and
  `Stomme.Shared.dll` by a hint path under `<app folder>\Stomme.Server\bin\Debug\net10.0`, a folder an app does not
  have. Built as generated, it fails with `CS0246` for every `Stomme` namespace. It also pins
  `Microsoft.AspNetCore.Components.WebAssembly`, `...WebAssembly.Authentication` and `Microsoft.Extensions.Localization`
  to versions older than `Stomme.Client` needs, which fails restore with `NU1605` once `Stomme.Client` is referenced.
  Replace the two `Reference` items with `<PackageReference Include="Stomme.Client" Version="<the app's Stomme
  version>" />` and delete the three pinned `Microsoft.*` references; the project then builds (it has no
  `NuGet.config`, so copy the app's).
- The generated layout uses Bootstrap class names and links Bootstrap from a CDN (`Constants.BootstrapStylesheetUrl`).
- Its `Theme.css` sets no `--db-accent`, so Stomme's controls show the framework's default colour until you add the
  token block above.
- Its project sets no `StaticWebAssetBasePath`, while its `ThemeInfo` links `_content/<Owner>.Theme.<Name>/Theme.css`
  (see the previous list).
- Its `Package` folder's scripts call `FixProps.exe` and `nuget.exe` under `<app folder>\Stomme.Package` (`release.sh` and one line of
  `debug.cmd` name a sibling folder literally called `Stomme` instead) and copy the package to
  `<app folder>\Stomme.Server\Packages`, paths that exist only in a Stomme source checkout; pack with
  `dotnet pack` over your own `.nuspec` instead.

## Configuration

A theme reads no configuration key. The site's theme choices are stored per site (Admin > Site Settings > Theme) and per
page (Admin > Page Management). A theme can offer settings of its own: set `ThemeSettingsType` (shown on a page's Theme
Settings tab) or `ContainerSettingsType` (shown on a module's Container Settings tab) to the assembly-qualified name of a
component that inherits `Stomme.Modules.ModuleBase` and implements `Stomme.Interfaces.ISettingsControl`
(`Task UpdateSettings()`) and carries `[Stomme.Shared.StommeIgnore]` (else the host registers it as a
module), and read the values with `Stomme.Services.ISettingService`
(`GetSetting(settings, GetType().Namespace + ":Key", "default")`). Leave both empty when the theme has no settings.

## Rules and pitfalls

- **Declare a `Default` pane.** Modules whose pane the theme lacks, and every module action page, go to `Default` (else
  `Admin`). Symptom of a theme with neither: pages render the shell and no modules.
- **Keep the theme, its `ThemeInfo` and its containers in one namespace.** A container in another namespace is not part of
  the theme (it does not appear in the container lists); an `ITheme` in another namespace is not used (the theme shows
  under the component's type name and links no stylesheet).
- **Set the accent at `:root`.** A theme that sets no `--db-accent` shows Stomme's buttons in the framework default
  (amber), next to whatever colour its own markup uses.
- **Never override a button's colour another way.** No `background` on `.db-btn` or FluentUI buttons, no FluentUI
  `--colorBrand*` tokens, no `var(--db-accent, #fallback)` fallbacks. Read only tokens that exist in the table above; an
  undefined token falls back to the browser default silently.
- **Do not name your own classes `db-...`.** `css/app.css` styles generic `db-` names (`db-card`, `db-grid`, `db-row`,
  `db-filters`, ...); a class of yours with such a name inherits those rules. Use a prefix of your own (`hb-` above).
  Using Stomme's `db-` classes on purpose, for Stomme's components, is the intended use.
- **A theme whose surfaces are fixed light or dark must declare `ColorMode`.** A theme that hard-codes white backgrounds but
  follows the visitor shows light text (`--db-text` in dark mode) on white when the visitor picks dark.
- **The app's `.gitignore` ignores `Server/wwwroot/Themes/*`** (installed theme packages unpack there) and keeps only
  `Server/wwwroot/Themes/Stomme.Themes.*` and the app's own `Server/wwwroot/Themes/Stomme.Application.*`. Start the
  theme's namespace with the app's name (as `Stomme.Application.Client.Themes.Harbor` does) and its stylesheet folder is
  committed; otherwise add a `!Server/wwwroot/Themes/<namespace>` line, or a fresh clone renders the theme unstyled.
- **Size the stock header controls for narrow screens.** `Logo`, `Search`, `UserProfile`, `Login` and `ControlPanel` do
  not shrink by themselves; see the narrow rules in the sample stylesheet.
- **Do not render FluentUI providers** (`FluentToastProvider`, `FluentDialogProvider`, `FluentTooltipProvider`) in a theme;
  the host renders them after every theme. Rendering them again duplicates toasts and dialogs.
- **The narrow breakpoint is 767.98px.** The stock menu turns into a toggle below it, and form rows stack below 640px. A
  theme's own grid should collapse at the same width.
- **Static pages have no event handlers.** On a site in Static render mode the theme renders as HTML; `@onclick` in a theme
  does nothing there. The stock controls handle both modes; use plain `onclick="..."` JavaScript (as the theme toggle
  does) or links for anything the shell itself must do. See [render modes](render-modes.md).
- **Do not use `LayoutBase`.** It is obsolete and layout controls are skipped by the scan.

## Related

- [Components and controls](components-and-controls.md) - the `db-` component layer and Stomme's controls.
- [Render modes](render-modes.md) - Static, Interactive and the runtimes.
- [UX standards](ux-standards.md) - what a theme must pass.
- [Module anatomy](../modules/anatomy.md) - modules, panes and placement.
- [Startup and discovery](../architecture/startup-and-discovery.md) - the `Stomme` assembly-name convention.
- Skills: [stomme-create-theme](../../.claude/skills/stomme-create-theme/SKILL.md),
  [stomme-ux-review](../../.claude/skills/stomme-ux-review/SKILL.md).
