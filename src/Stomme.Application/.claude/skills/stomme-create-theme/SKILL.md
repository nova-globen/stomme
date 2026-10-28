---
name: stomme-create-theme
description: Use when creating, restyling or rebranding a Nova Globen Stomme theme or container - a new site look, a page layout with panes, the site's colours (--db-accent), light/dark support, an admin theme, or packaging a theme for other apps.
---

# Create a Stomme theme

You will have a theme in the app's `Client` project - a layout component with panes, a container, a `ThemeInfo` and a
stylesheet that sets the site's colours through the `--db-*` tokens - selectable in Admin > Site Settings, working in
light and dark, on narrow screens and in every render mode. How themes are discovered, rendered and coloured is in
[docs/ui/themes.md](../../../docs/ui/themes.md); read it first. The bar the result must meet is
[docs/ui/ux-standards.md](../../../docs/ui/ux-standards.md).

The example theme is called Harbor. Replace `Harbor` and the `hb-` prefix with your theme's name and a short prefix of
your own. `Stomme.Application` below is your app's name (the template replaced it when the app was created).

## Steps

1. **Choose the namespace.** Everything in one theme shares one namespace:
   `Stomme.Application.Client.Themes.Harbor`. The theme's identity is that namespace plus the Client assembly name; the
   stylesheet folder is named after the namespace.

2. **Create `Client/Themes/Harbor/ThemeInfo.cs`:**

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
               // Follows the visitor's light/dark choice. A theme whose surfaces only work in one mode
               // sets ThemeColorModes.Dark or ThemeColorModes.Light instead.
               ColorMode = ThemeColorModes.FollowVisitor,
               Resources = new List<Resource>
               {
                   // "~" becomes /Themes/<this namespace>/
                   new Stylesheet("~/Theme.css")
               }
           };
       }
   }
   ```

3. **Create the layout, `Client/Themes/Harbor/Default.razor`.** It must declare a pane named `Default`
   (`PaneNames.Default`): modules without a matching pane and every module action page render there.

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

       // Every pane the markup renders, comma-separated. Page editors offer exactly these.
       public override string Panes => PaneNames.Default + ",Left,Right,Footer";
   }
   ```

   The toggle uses a plain `onclick` (JavaScript, not `@onclick`) so it works on Static pages too. Keep the
   `db-theme-toggle` class: the host hides the button when a theme fixes its colour mode.

4. **Create the container, `Client/Themes/Harbor/Container.razor`** (same namespace, or it is not part of the theme):

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

   `ModuleActions` is the module's edit-mode menu; leave it in. A module title of `-` means "no title".

5. **Create the stylesheet, `Server/wwwroot/Themes/Stomme.Application.Client.Themes.Harbor/Theme.css`.** Set the accent
   at `:root` (dark values, the default) and again under `html[data-theme="light"]`. Use your prefix for your classes and
   `var(--db-*)` for every colour, with no fallback values:

   ```css
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

   The `.app-logo img`, `.db-icon-btn`, `.db-btn` and `.app-search` rules size Stomme's own header controls (`Logo`,
   `Search`, `UserProfile`, `Login`, `ControlPanel`); they set sizes, never colours. Without them the site logo renders at
   its full size (the stock one is several hundred pixels wide) and, signed in, the header is wider than a 390-pixel
   screen.

   To change surfaces too, set `--db-bg`, `--db-bg-elev`, `--db-bg-elev-2`, `--db-bg-elev-3`, `--db-border`,
   `--db-border-strong`, `--db-text`, `--db-text-2` and `--db-muted` in both blocks - in both, because what you set on
   `:root` alone applies in light mode as well.

6. **Check the stylesheet is in source control.** The app's `.gitignore` ignores `Server/wwwroot/Themes/*` (installed
   theme packages unpack there) but already keeps the app's own theme folders with the line
   `!Server/wwwroot/Themes/Stomme.Application.*`. A theme whose namespace starts with the app's name, as Harbor's does,
   needs nothing more. Confirm that
   `git check-ignore -v Server/wwwroot/Themes/Stomme.Application.Client.Themes.Harbor/Theme.css` prints nothing. A theme
   whose namespace does not start with the app's name needs its own `!Server/wwwroot/Themes/<namespace>` line.

7. **Optional - an admin theme.** Any theme can be the site's Admin Theme (Admin > Site Settings > Theme > Admin Theme);
   it then renders every page under `admin/` that has no page theme of its own. Admin pages put modules in the
   `Default` pane, so the same rules apply.

8. **Optional - theme settings.** Only if the theme needs options a page editor sets: add a component that inherits
   `ModuleBase`, implements `Stomme.Interfaces.ISettingsControl` and carries `@attribute [Stomme.Shared.StommeIgnore]`
   (without it the host registers the component as a module named after the theme's namespace), and set
   `ThemeSettingsType = "Stomme.Application.Client.Themes.Harbor.ThemeSettings, Stomme.Application.Client.Stomme"`
   in `ThemeInfo`. Read the values in the layout with `ISettingService.GetSetting(settings, GetType().Namespace + ":Key", "default")`.

9. **Build** with `dotnet build` from the app root.

## Verify

Run the app (`cd Server`, `dotnet run`), sign in as the host, and:

1. Admin > Site Settings, the Theme section: choose **Harbor - Harbor** as Default Theme (the list shows
   "<theme name> - <layout name>"). The Default Container list then offers only Harbor's containers and selects
   **Harbor Container** (the container's `Name`). Save; the page says "Site Settings Saved".
2. Open the home page. The header, menu and footer are Harbor's; the modules show inside Harbor's container.
3. Page source: a `<link ... href="/Themes/Stomme.Application.Client.Themes.Harbor/Theme.css?...">` is present and
   that URL answers 200 with your CSS (open it).
4. Primary buttons (for example on Admin > Role Management, which renders in the default theme unless an admin theme is
   set) show your accent with your `--db-on-accent` text, in both modes. FluentUI primary buttons (the Pager's Search)
   show the same colour.
5. Toggle light/dark with your toggle: surfaces, text and buttons change, everything stays readable. Reload: the choice
   persists.
6. Narrow the window to 375 pixels, signed out and signed in (signed in, the header holds more controls): no horizontal
   page scroll (`document.documentElement.scrollWidth <= window.innerWidth` in the console), the logo is small, the menu
   becomes a toggle, columns stack.
7. Tab through the header: every control is reachable and shows a focus ring.
8. Turn on edit mode (the pencil button): each pane shows its name ("Default Pane", "Left Pane", "Right Pane",
   "Footer Pane"), and the Control Panel's pane list offers exactly the panes in `Panes`. Adding a module to `Left` shows
   it there.
9. The browser console shows no errors.
10. Run the `stomme-ux-review` skill on a page that uses the theme.

What must NOT appear: amber buttons (the framework default - your accent is missing), an unstyled page (the stylesheet
link is dead), modules missing from pages that showed them before (no `Default` pane).

## Common mistakes

- **Theme not in the Default Theme list.** The component does not inherit `ThemeBase`, is abstract, or the app was not
  rebuilt and restarted. The Client assembly's name must still contain `Stomme` (the template's
  `Stomme.Application.Client.Stomme`); do not rename it.
- **Theme listed under the component's class name, page unstyled.** `ThemeInfo` is in a different namespace from the
  layout. Move it into the same namespace.
- **Container not offered.** It is in another namespace, or inherits `ThemeBase` instead of `ContainerBase`.
- **Stylesheet 404.** The folder under `Server/wwwroot/Themes/` is not exactly the namespace, or the URL does not start
  with `~`.
- **Theme unstyled after a fresh clone or a deploy from git.** The stylesheet was never committed: its folder is not
  named after a namespace that starts with the app's name, so `.gitignore` drops it. See step 6.
- **A huge logo, or a header wider than a phone screen.** The narrow-screen rules of step 5 are missing. The stock
  controls do not size themselves for a theme's header; the theme does.
- **Buttons amber, or two different accent colours.** No `--db-accent` at `:root`, or the theme sets a colour on
  `.db-btn` or a FluentUI token directly. Remove the direct rule; set the token.
- **Unreadable text in one mode.** Surfaces hard-coded (`#fff`, `white`) or set only on `:root`. Use tokens and set both
  blocks, or declare `ColorMode` for a single-mode design.
- **Theme toggle does nothing on Static pages.** It was written as `@onclick`. Use `onclick="stommeTheme.toggle()"`.
- **Duplicate toasts or dialogs.** The theme renders FluentUI providers; remove them - the host renders them.
- **Your classes pick up odd padding or layout.** You named them `db-...`; Stomme's stylesheet styles those names. Use
  your own prefix.
- **Used Admin > Theme Management > Create Theme and the result does not build.** It writes a separate,
  Bootstrap-based solution to a folder beside the app's folder, not into the app. With the default Framework Reference
  ("Installed Version") its project references `Stomme.Client.dll` and `Stomme.Shared.dll` by a path under
  `<app folder>\Stomme.Server\bin\Debug`, which an app does not have, so the build fails with `CS0246` on every
  `Stomme` namespace; its packaging scripts call tools that exist only in a Stomme source checkout. Follow this skill
  instead; see also [docs/ui/themes.md](../../../docs/ui/themes.md#a-theme-in-its-own-package).
