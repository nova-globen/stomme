# Render modes

Nova Globen Stomme renders the same Razor components in several ways: as static HTML from the server, or interactively, with the
component code running on the server over a SignalR circuit, in the browser on WebAssembly, or in a .NET MAUI shell. Each
site chooses its mode; each module component can ask for its own. Code that assumes one of these environments breaks in
the others, so this page says what differs and what a component must not assume.

## How it works

### The values

Two string constant classes in `Stomme.Shared` (the `Stomme.Shared` package):

| `RenderModes` | Meaning |
|---|---|
| `Static` (`"Static"`) | The page is rendered once on the server as HTML. No Blazor events run; links and form posts drive it. |
| `Interactive` (`"Interactive"`) | The page is a live Blazor component tree; events run. Where the code runs is the runtime. |
| `Headless` (`"Headless"`) | The site is used without Stomme's page rendering: the host does not compose its pages (use it for a site served only through the API). |

`"Server"` and `"WebAssembly"` are **not** render modes; they are runtimes.

| `Runtimes` | Meaning |
|---|---|
| `Server` (`"Server"`) | Interactive components run on the server; the browser holds a SignalR connection (a circuit). |
| `WebAssembly` (`"WebAssembly"`) | Interactive components run in the browser. The host streams each module's and theme's client assembly to the browser, which loads them at start. |
| `Auto` (`"Auto"`) | Blazor's auto mode: Server first, WebAssembly once the client has downloaded. |
| `Hybrid` (`"Hybrid"`) | The component runs inside a .NET MAUI Blazor Hybrid app that talks to a remote Stomme server. |

### Where the mode is decided

1. **Per site.** Each site stores `RenderMode`, `Runtime`, `Prerender` and `EnhancedNavigation`. The first site takes the
   first three at install from the configuration keys below (Enhanced Navigation starts on); afterwards the
   Host changes them in Admin > Site Settings > UI Component
   Settings, a section only the Host sees and collapsed until its heading is clicked: Render Mode (Interactive, Static,
   Headless (API Only)), Enhanced Navigation (shown for Static only), Interactivity (Server (SignalR), Client
   (WebAssembly), Auto), Prerender, Hybrid?. Choosing Interactive sets Prerender to Yes in the form, choosing Static sets
   it to No; check it before saving. A site added in Admin > Site Management starts as Static on the Server runtime.
2. **Per module component.** Every module component (anything inheriting `Stomme.Modules.ModuleBase`, which implements
   `Stomme.Modules.IModuleControl`) declares:
   - `public virtual string RenderMode` - `RenderModes.Interactive` by default;
   - `public virtual bool? Prerender` - `null` by default, meaning "use the site's Prerender".
   Each action component (Index, Edit, Settings, ...) declares its own.
3. **The effective mode** of a module on a page:
   - site Interactive: the module is interactive with the site's runtime, whatever it declares;
   - site Static and module declares `Static`: the module is static HTML;
   - site Static and module declares `Interactive` (the default): the module becomes an interactive island, using the
     site's runtime and its own `Prerender ?? site Prerender`.
   The host writes a comment before each module, for example `<!-- rendermode: Interactive:Server - prerender: True -->`;
   read it in the page source to see what a module actually got. Measured with the Notes module (whose components keep
   the default Interactive): on a Static site `<!-- rendermode: Interactive:Server - prerender: False -->`, on an
   Interactive site `Interactive:Server - prerender: True`, on an Interactive WebAssembly site
   `Interactive:WebAssembly - prerender: True`; the stock HtmlText module showed `<!-- rendermode: Static -->` on the
   Static site and `Interactive:Server - prerender: True` on the Interactive one.
4. **Per module definition.** `ModuleDefinition.Runtimes` (comma-separated, empty = all) limits the runtimes a module
   supports; on a site with another runtime the module's components are not used and Stomme's error module renders in
   its place.

### A static page without Blazor's script

A Static site's page still loads `_framework/blazor.web.js` (about 200 KB with the FluentUI and Stomme JS modules its
initializers load) for enhanced navigation, interactive islands, FluentUI web components and `[StreamRendering]`. A site
whose public pages need none of that can leave it out (since 10.78.0): the site setting **`OmitBlazorScriptOnStaticPages`
= `true`** (a Site setting, `EntityName` `Site`; set it from a module or a site template) drops `blazor.web.js` and
`js/reload.js` from a page when the site is Static, the visitor is not signed in and no module on the page is
interactive (the module's `RenderMode` is `Static`). A signed-in user, and any page with an interactive module, still gets
the script. Turn it on only when the anonymous pages use no FluentUI component and no `[StreamRendering]`: without the
script those render inert. Since 10.79.0 Stomme's own `js/app.js`, `js/interop.js` and `js/loadjs.min.js` go too: they are
called only through Blazor's JS interop, so such a page loads no framework script at all. Your own scripts (a theme's or
module's resources) are unaffected.

### Prerender

With prerender on, the server first renders the interactive component as HTML, then the browser makes it interactive and
the component initializes **again**. `OnInitializedAsync` runs twice: once on the server, once in the interactive runtime.
A WebAssembly site that does not prerender sends an empty page with a loading status until the client has downloaded.

### What a component must not assume

The same component runs on the server (Static, Interactive Server, prerender) and in the browser (WebAssembly, Hybrid). So:

- **Data goes through the module's `IService`, which calls the server's API over HTTP.** Never inject a repository, a
  `DbContext` or any server-only service into a Client component; it does not exist in the browser. On the server the
  module may register a second implementation of the same interface that skips HTTP. See
  [API and client services](../modules/api-and-client-services.md).
- **No `HttpContext`, file system, environment variables or server configuration** in a component.
- **No JavaScript before the first render.** `IJSRuntime` calls belong in `OnAfterRenderAsync`; during prerender and static
  rendering there is no browser.
- **No static (global) mutable state.** On the Server runtime a static field is shared by every user of the process.
- **State is per circuit on Server, per tab on WebAssembly.** A Server circuit is one long-lived dependency-injection scope:
  whatever a scoped service caches lives as long as the tab.

## Using it from an app

A module component that is pure display and needs no events can declare Static, so it costs no circuit and no download.
`Client/Modules/Notes/Summary.razor` (in the module's namespace, so it is one of the Notes module's components):

```razor
@namespace Stomme.Application.Modules.Notes
@inherits ModuleBase

<p>@_text</p>

@code {
    private string _text = "Rendered on the server, once.";

    public override string RenderMode => RenderModes.Static;
}
```

A component that must not prerender (for example one that reads browser storage first) says so,
`Client/Modules/Notes/BrowserStatus.razor`:

```razor
@namespace Stomme.Application.Modules.Notes
@inherits ModuleBase

<p>@_status</p>

@code {
    private string _status = "Starting";

    public override bool? Prerender => false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (firstRender)
        {
            _status = "Interactive";
            StateHasChanged();
        }
    }
}
```

Both build in the app, and each is reachable as an action of the module by its class name
(`/notes/*/{moduleId}/Summary`). Measured as the host on a Static site: `Summary` rendered with
`<!-- rendermode: Static -->`; `BrowserStatus` with `<!-- rendermode: Interactive:Server - prerender: False -->` and
showed "Interactive".

A component that needs to know where it is running reads `PageState.RenderMode` (`"Static"` or `"Interactive"`) and
`PageState.Runtime` (the `Stomme.Shared.Runtime` enum); the stock controls use exactly this to choose between buttons
and form posts.

## Configuration

Read once, when the installer creates the first site. Changing them later changes nothing; use Site Settings.

| Key | Default in the app's `appsettings.json` | Effect |
|---|---|---|
| `RenderMode` | `"Static"` | The first site's render mode (`Static`, `Interactive` or `Headless`). |
| `Runtime` | `"Server"` | The first site's runtime (`Server`, `WebAssembly`, `Auto`). |
| `Prerender` | not present | `true`/`false`. Absent: prerender is on when `RenderMode` is `Interactive`, off otherwise. |

## Rules and pitfalls

- **`@using Microsoft.AspNetCore.Components.Web` must be in scope** of every Razor file with `@onclick`, `@onchange`,
  `@bind:event` and the other event attributes. Without it the page builds, renders and shows no error, and every event
  handler is compiled as a plain HTML attribute: buttons do nothing. The app's `Client/_Imports.razor` has it; a new
  project or a component library you add needs it in its own `_Imports.razor`. Only a click in an interactive page finds
  this; a build or an HTTP check never does.
- **On a Static page, `@onclick` does nothing.** A module that must react to clicks keeps the default
  `RenderMode => RenderModes.Interactive`. Declare Static only for display-only components.
- **A static-site module that declares Interactive does not see the site's page list.** `PageState.Site.Pages` is emptied
  for the island to keep the page small. Ask a service for pages instead.
- **Verify interactive changes on a WebAssembly site, in a browser, with the console open.** Two failures show only there:
  a client service not registered in the browser (the page fails to render; register it in the module's
  `IClientStartup`), and a server refusal read as `null` by a client service (see
  [API and client services](../modules/api-and-client-services.md)). Neither shows on a Static or Server site.
- **Prerender behind a reverse proxy.** The prerender pass of an interactive site makes requests to the site's own public
  URL; a container that cannot resolve its public host name serves empty pages. Turn Prerender off for that site or fix
  name resolution. See [deploy](../operations/deploy.md).
- **Interactive Server and stale data.** A circuit keeps its scoped services for its whole life, so a service that caches or
  tracks what it read can show stale data across clicks. Re-read after every write and do not cache entities in a scoped
  service. See [module persistence](../persistence/module-persistence.md).
- **Do not use the words `Server` or `WebAssembly` as a render mode** in configuration or site settings; the value is
  ignored or wrong. Use `RenderModes` and `Runtimes`.

## Related

- [Components and controls](components-and-controls.md) - which controls switch between static and interactive.
- [Module UI](../modules/module-ui.md) - `ModuleBase`, `PageState`, `ModuleState`.
- [API and client services](../modules/api-and-client-services.md) - the `IService`-over-HTTP rule.
- [Configuration](../architecture/configuration.md) - every key.
- [Deploy](../operations/deploy.md) - prerender behind a proxy.
- Skill: [stomme-build-module-ui](../../.claude/skills/stomme-build-module-ui/SKILL.md).
