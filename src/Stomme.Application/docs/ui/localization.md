# Localization

Nova Globen Stomme localizes UI text with the standard ASP.NET Core localization: `IStringLocalizer<T>` over `.resx` resource files
in a `Resources` folder, with the culture taken from a cookie, the user's profile and the site's languages. This page
explains how a culture is chosen, where an app's resource files go so they are found, and how Stomme's own controls
look up text.

## How it works

### Culture

1. Each site has languages (Admin > Language Management > Add Language): any .NET culture can be added, one is the site's
   default.
2. On the server, the host runs ASP.NET Core request localization, which reads the culture from the
   `.AspNetCore.Culture` cookie (`CookieRequestCultureProvider.DefaultCookieName`). When it renders a page, the host works
   out the UI language itself: the language in the cookie when the site offers it (the visitor's choice); otherwise the
   signed-in user's own language (User Profile > Language) when the site offers it; with no profile language the site's
   default language, with one the site does not offer the site's content language (Admin > Site Settings > Language). It
   rewrites the cookie when it names a language the site does not offer.
3. So **the visitor's choice decides**, then the profile. The language switcher (`Stomme.Themes.Controls.LanguageSwitcher`,
   the globe button the stock themes show when a site has more than one language) writes the cookie and reloads the page,
   and the page comes back in the chosen language (since 10.73.2; before, the host put the cookie back to the profile's
   or the site's default language on every render, so the switcher changed nothing).
4. In a WebAssembly client the host reads the same cookie at start and sets `CultureInfo.DefaultThreadCurrentCulture` and
   `DefaultThreadCurrentUICulture`; an unknown culture falls back to the site's default language.
5. The page's `<html lang="...">` is the UI language the page is shown in, or the content language on a site that
   localizes its content by URL (since 10.73.2; before, always the content language). The host does not set `dir`; see the
   pitfalls.
6. A site can also localize its content by giving a language its own URL (alias); the language switcher then links to that
   URL instead of setting the cookie. (Not driven.)

### Text

- Both the server and the WebAssembly client register localization with `ResourcesPath = "Resources"`.
- Stomme ships its own strings in English only, as neutral resources inside `Stomme.Client`. A culture without
  translated resources shows the English text. Admin > Language Management lists the site's languages (and English), showing
  the version of each one's translation package where its satellite assembly (`<culture>/*.resources.dll`) is in the
  application folder ("---" where none is), and can upload translation packages.
- `IStringLocalizer<Stomme.SharedResources>` holds the framework's common words: `Save`, `Cancel`, `Delete`, `Edit`,
  `Add`, `Update`, `Yes`, `No`, `Search`, `Reset`, `Loading`, `Not Specified`, `Message.InfoRequired` and more.
- A missing key returns the key itself, so `Localizer["Save the note"]` shows "Save the note" when no resource defines it.

### Where a component's resource file must be

The resource name is built from three parts: the assembly's root namespace, the `Resources` folder, and the component's
full type name with the root namespace removed. The template's `Client/AssemblyInfo.cs` declares the root namespace:

```csharp
using Microsoft.Extensions.Localization;

[assembly: RootNamespace("Stomme.Application.Client")]
```

A component whose namespace starts with the root namespace is looked up with the root namespace removed; any other
component is looked up under its **whole** namespace. A module's components are in the second group: they live in
`Stomme.Application.Modules.<Module>` (the namespace the app's modules use), which does not start with
`Stomme.Application.Client`. So for the Notes module's `Stomme.Application.Modules.Notes.Index`:

| | Path |
|---|---|
| Component | `Client/Modules/Notes/Index.razor` |
| Neutral (English) resources | `Client/Resources/Stomme.Application.Modules.Notes.Index.resx` |
| Swedish resources | `Client/Resources/Stomme.Application.Modules.Notes.Index.sv.resx` |

The file name carries the namespace with its dots. Folders `Client/Resources/Stomme/Application/Modules/Notes/Index.resx`
name the same resource, but keep the dotted file name: it follows the app's name, and having both forms at once fails the
build with `MSB3577` ("Two output file names resolved to the same output path"). Measured: a file at
`Client/Resources/Modules/Notes/Index.resx` is **not** found for the Notes module - the folder under `Resources` follows
the namespace, not the file location.

A component in a namespace under the root namespace, for example a theme in `Stomme.Application.Client.Themes.Harbor`,
drops the root: `Client/Resources/Themes/Harbor/Default.resx` (by the same rule; not driven). The same rules apply to the `Server` project, whose
`AssemblyInfo.cs` declares `Stomme.Application.Server`.

Keep the `RootNamespace` attribute. The assembly name ends in `.Stomme` (the naming convention; discovery
needs only `Stomme` in it), so without the
attribute the localizer looks for resources under the assembly name and finds none.

### Stomme's controls and `ResourceKey`

The controls that inherit `Stomme.Modules.Controls.LocalizableComponent` (`Label`, `ActionLink`, `ActionDialog`,
`Section`, `TabPanel`, `AutoComplete`, `InputList`) take a `ResourceKey` and an optional `ResourceType`
(`AutoComplete` accepts them but looks nothing up). With a `ResourceKey`, the control looks up
`<ResourceKey>.<Property>` in the resources of `ResourceType`, which defaults to the
module component it is placed in:

- `Label`: `<ResourceKey>.Text` replaces the label text, `<ResourceKey>.HelpText` the help text.
- `ActionLink`: `<ResourceKey>.Text` (or `.Action`).
- `ActionDialog`, `Section`, `TabPanel`: `<ResourceKey>.Header`, `.Message`, `.Text`, `.Heading` for the parameter of that
  name; `Section` and `TabPanel` with no `Heading` look up `<ResourceKey>.Name`.
- `InputList`: `<ResourceKey>.<option text>` for each `DataList` entry (the entry's value, or its key when the value is empty).

A key that is missing or empty leaves the text you wrote in the markup. So a page can ship in English with no resource file
and gain translations later without a markup change. Measured on the Notes module's `Edit` page: with
`<Label For="note-title" ResourceKey="Title" HelpText="A short name, shown in the list">Title:</Label>` and only a Swedish
resource file holding `Title.Text` and `Title.HelpText`, an English user sees the markup text and a Swedish user the
translation, help text included.

## Using it from an app

Localizing the Notes module's empty-state title. In `Client/Modules/Notes/Index.razor`, add the localizer below the
existing `@inject` line and use it where the text was:

```razor
@inject IStringLocalizer<Index> Localizer
```

```razor
<div class="db-empty-title">@Localizer["Notes.EmptyTitle"]</div>
```

(`IStringLocalizer<T>` is in `Microsoft.Extensions.Localization`, which the app's `Client/_Imports.razor` already
imports. `IStringLocalizer<Stomme.SharedResources>` gives the framework's shared words, for example
`SharedLocalizer["Save"]`.)

`Client/Resources/Stomme.Application.Modules.Notes.Index.resx`, the neutral (English) file:

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <data name="Notes.EmptyTitle" xml:space="preserve">
    <value>No notes yet</value>
  </data>
</root>
```

`Client/Resources/Stomme.Application.Modules.Notes.Index.sv.resx`, the Swedish file: the same content with
`<value>Inga anteckningar ännu</value>`.

Then:

1. Build. Check that `Server/bin/Debug/net10.0/sv/` holds the Client project's `.resources.dll` (measured: it does).
2. Admin > Language Management > Add Language: add Swedish. The list shows it with "---" under Translation (no Stomme
   translation package is installed; Stomme's own text stays English).
3. Set a user's Language to Swedish (User Profile > Language), sign in as that user and open the Notes page: the empty
   list says "Inga anteckningar ännu". Another user still sees "No notes yet".

Dates, numbers and currency format with the current culture; format them with the culture (`ToString("d")`,
`ToString("N2")`), not with fixed patterns, and convert stored UTC times with `ModuleBase.UtcToLocal`.

## Configuration

- Languages per site: Admin > Language Management (add, set the default, upload translations).
- A user's language: their profile (and the Language field on registration), used until they choose another in the
  language switcher.
- `Localization:DefaultCulture` appears in the shipped `appsettings.json` with the value `en`, but the host does not bind
  that section: the fallback culture is always `en` (`Stomme.Shared.Constants.DefaultCulture`). Set the site's default
  language in Language Management instead.

## Rules and pitfalls

- **Wrong folder, no error.** A resource file in the wrong folder is simply not found and the key (or the markup text)
  shows. Check the name against the component's namespace, not its file path. For a module component the name starts
  with the whole module namespace (`Stomme.Application.Modules.<Module>.<Component>`), not with `Modules/`.
- **The language switcher does not change the language (before 10.73.2).** The host reset the culture cookie to the
  profile's language or the site's default on every render. On an older Stomme, test a translation by setting a user's
  profile language.
- **Name keys, do not use sentences as keys in resource files.** `Notes.EmptyTitle` survives a wording change; a sentence key
  does not. Literal English in `Localizer["..."]` without a resource file works, but it cannot be translated without a
  resource file that repeats the sentence as its name.
- **Satellite assemblies must reach the output.** A culture appears as installed only when its satellite assembly is in the
  application folder. After adding a `.sv.resx`, build and check that `Server/bin/<Configuration>/net10.0/sv/` contains
  your assembly's `.resources.dll`. (The `Server` project sets `SatelliteResourceLanguages` to `none`, which limits the
  satellites copied from NuGet packages; the app's own Client satellites still arrive - measured.)
- **Right-to-left languages are not laid out right-to-left.** The host sets `lang` but not `dir`; Stomme's stylesheet is
  written left-to-right. An app that must support Arabic, Hebrew or Persian has to set `dir` and check its layouts itself.
- **Do not hard-code English in shared components** that other modules reuse; give them a `ResourceKey`-able parameter or
  their own resource file.
- **An Interactive Server circuit keeps the culture it started with.** After a user changes their profile language,
  the new language shows from the next full page load.

## Related

- [Components and controls](components-and-controls.md) - the controls that take `ResourceKey`.
- [UX standards](ux-standards.md) - wording of messages and empty states.
- [Module UI](../modules/module-ui.md) - `ModuleBase`.
- [Configuration](../architecture/configuration.md).
