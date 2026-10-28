# UX standards

This is the bar for every page built on Nova Globen Stomme. It is taken from what the admin pages do consistently, and written as
rules an agent can check one by one on a running page. A page that fails a rule is not finished. The skill
[stomme-ux-review](../../.claude/skills/stomme-ux-review/SKILL.md) runs these checks.

## How it works

Stomme's admin pages share one vocabulary: a module component inside a theme container, lists through `Pager`, actions
through `ActionLink`, destructive actions behind `ActionDialog`, forms as `db-field` rows of `Label` + control, Save as
the theme's primary button next to a neutral Cancel, outcomes through `AddModuleMessage`. The `db-` classes and `--db-*`
tokens make all of it follow the active theme and the light/dark mode. An app page that uses the same pieces inherits the
same behaviour; a page that invents its own does not, and looks foreign the moment the site changes theme. The pieces are
described in [components and controls](components-and-controls.md).

## The rules

Each rule has an identifier for review reports.

### Structure and consistency

- **S1** The page is a module component (`ModuleBase`) placed on a page; its title is the module title the container
  shows. Do not repeat that title as a heading inside the module.
- **S2** Headings inside a module start below the container's title (the stock containers render it as `h2`; use `h3` and
  below) and do not skip levels. Use `Section` for a collapsible group of fields; it renders its own heading.
- **S3** Lists use `Pager` (with `SearchProperties` when the list can grow beyond one page). Row actions sit in the last
  column, Edit before Delete. The stylesheet's `db-col-action` and `db-actions` classes style that column (the stock
  admin lists use plain cells; new pages use the classes).
- **S4** Add, Edit and other secondary screens are module actions opened with `ActionLink` (they open in the admin dialog),
  not hand-made modals.
- **S5** Forms use `db-fields` / `db-field` rows: a `Label` with `For` on the left, the control in `db-field-control` on
  the right. One control style per form (native `db-input`/`db-select`, or FluentUI inputs - not mixed).
- **S6** Button order and style: the primary action (Save, Create, Add) is a `db-btn` (or a FluentUI
  `ButtonAppearance.Primary`); Cancel or Back is `db-btn db-btn-neutral` and returns to the list (`NavigateUrl()`);
  destructive actions are `db-btn db-btn-danger`. One primary button per form.
- **S7** No colour is set on any button, link or control by the module. No inline `style` that sets a colour; colours come
  from `var(--db-*)` tokens only, with no fallback value inside `var()`.
- **S8** Own CSS classes use the module's own prefix, never `db-`.
- **S9** Words use the framework's shared terms (`SharedLocalizer["Save"]`, `["Cancel"]`, `["Delete"]`, `["Edit"]`,
  `["Add"]`) so the same action has the same name everywhere.

### States

- **E1 Loading.** While data loads, the page shows a short loading text (`SharedLocalizer["Loading"]`), not an empty table
  and not a blank area.
- **E2 Empty.** An empty list shows a `db-empty` block that says what the list holds and what to do next ("No notes yet.
  Add the first note with Add note."), and the action to do it is visible. Never an empty table with only headers.
- **E3 No access.** A user without permission sees a sentence that says so, or does not see the control at all (pass
  `Security`/`PermissionList` to `ActionLink` and `ActionDialog`). The server refuses the same request regardless.
- **E4 Busy.** While a save or a long action runs, its button is disabled (`disabled="@_busy"`) so it cannot be sent twice;
  a long action shows progress (`ShowProgressIndicator()`).
- **E5 Success.** A completed action is visible: the list reloads with the change, or a `MessageType.Success` message
  says what happened ("The note was saved.").

### Errors

- **R1** Every service call is in a `try`/`catch`; the `catch` logs (`await logger.LogError(ex, "...")`) and shows a
  module message. A failure never leaves the page silently unchanged.
- **R2** An error message says what failed and what the user can do: "The note was not saved: the title is required."
  Not "Error", not an exception type, not a stack trace. Show the server's reason when it has one (a client service can
  turn a refusal into an exception carrying it; see [API and client services](../modules/api-and-client-services.md)).
  Only a refusal's reason is fit to show: a pattern that appends `ex.Message` to every failure also shows a network
  failure's text, which on WebAssembly reads "TypeError: Failed to fetch". Catch the refusal separately and give other
  failures a sentence of your own.
- **R3** Validation happens before the call: required fields are marked `required`, and a missing value produces
  `MessageType.Warning` with `SharedLocalizer["Message.InfoRequired"]` or a sentence naming the field.
- **R4** User-entered text in a message is HTML-encoded (`System.Net.WebUtility.HtmlEncode`); messages render as markup.

### Confirmation

- **C1** Every delete and every irreversible action asks first, through `ActionDialog`, with a `Header` naming the action
  and a `Message` naming the item and the consequence ("Delete the note 'Q3 plan'? This cannot be undone.").
- **C2** The confirm button repeats the action's name (`Action="Delete"`); the dialog's Cancel changes nothing.
- **C3** After the confirmed action the page re-renders (`StateHasChanged()` at the end of the `OnClick` handler).

### Light and dark

- **L1** Every text, border and surface is readable in both modes: toggle the mode and look at every state (list, empty,
  form, dialog, each message type).
- **L2** Nothing is hard-coded white, black or grey; surfaces use `--db-bg*`, text `--db-text`, `--db-text-2`,
  `--db-muted`, borders `--db-border*`.
- **L3** Images and icons stay visible on both backgrounds (no dark icon on a dark surface).
- **L4** Mode-specific CSS keys on `html[data-theme="light"]` / `html[data-theme="dark"]`, never on
  `prefers-color-scheme`.

### Narrow screens

- **N1** At 375 pixels wide the page has no horizontal page scroll. A wide table scrolls inside `db-table-wrap` (`Pager`
  does this), not the page.
- **N2** Form rows stack (the `db-field` rows do below 640px); no fixed widths in pixels on containers or inputs wider
  than the screen.
- **N3** Touch targets are usable: buttons are not shrunk below the stock `db-btn` size, and adjacent actions do not touch.
- **N4** Nothing important is only visible on hover.

### Keyboard

- **K1** Every action can be reached with Tab and used with Enter or Space, in a sensible order (reading order).
- **K2** The focused element is always visible (the stock `:focus-visible` ring); never `outline: none` without a
  replacement.
- **K3** Controls are real `button`, `a href`, `input`, `select` elements. A clickable `div`, `span` or an `a` without
  `href` is not reachable from the keyboard.
- **K4** Enter in a search box searches; Enter in a single-field form submits it.
- **K5** After a dialog closes or an editor opens, focus or scroll lands where the user continues (`BringIntoView` for an
  editor below a list).

### Accessibility

- **A1** Every input has a label that names it (`Label For` matching a unique `id`, or `aria-label`).
- **A2** Every icon-only button has an accessible name (`aria-label`, or `AltText` on `ActionLink`/`ActionDialog` with
  `IconOnly`).
- **A3** Information is not carried by colour alone: a status pill has text, an error has words.
- **A4** Text contrast meets WCAG AA (4.5:1 for body text) in both modes.
- **A5** Images have `alt` text (empty `alt=""` for decoration).
- **A6** Dynamic outcomes are announced: module messages carry `role="alert"`; a custom status line uses `role="status"`.
- **A7** Motion respects `prefers-reduced-motion` (the stock stylesheet already does; custom animation must too).

### Time, numbers and language

- **T1** Stored times (UTC) are shown with `UtcToLocal(...)`; a time intentionally shown in UTC says so.
- **T2** Numbers and dates use the current culture's formatting.
- **T3** Text a user reads is localizable: either through `IStringLocalizer` or `ResourceKey`; see
  [localization](localization.md).

### Wording

- **W1** One capitalisation style across the module's headings, labels and buttons. The older admin pages use Title Case
  ("Add Role"), the newer ones sentence case ("New format"); prefer sentence case ("Add note") and do not mix.
- **W2** Labels name the thing ("Title"), help text says why or how ("A short name shown in the list").
- **W3** Messages are full sentences in plain language, from the user's point of view, with no internal names (no type
  names, ids, exception text, "null").

### Health

- **H1** The browser console shows no errors or warnings caused by the page, on first load and after each action.
- **H2** The page works after a full reload on every URL it has (list, `?id=` edit), not only when reached by clicking.

## Configuration

None. The theme and the site's render mode decide appearance; the rules above hold under every theme and mode.

## Rules and pitfalls

- **The admin pages are not all at this bar yet.** Some newer framework pages use inline styles and hand-made tables; do
  not copy a page because it ships with Stomme - copy the patterns above.
- **Stomme's own controls have known gaps you must not repeat and may need to work around** (each seen in a review of
  a module built the recommended way):
  - the interactive `Pager`'s page links are `a` elements without `href`: Tab skips them, so a list longer than one page
    cannot be paged from the keyboard (K3);
  - `Label`'s help tooltip appears only on hover and its help icon has no `alt` (N4, A5);
  - `ActionDialog` carries `role="dialog"` but no `aria-modal` or label, does not move focus into the dialog, does not trap it
    and does not close on Escape; focus stays on the row's button behind the overlay (K5, A1);
  - after a module action dialog (Add, Edit) opens or closes, focus is on the page body, not in the dialog or back on the
    control that opened it (K5);
  - in light mode the `db-btn db-btn-danger` button (red text on a pale red fill) is below 3:1 contrast on the stock
    surfaces, far below the 4.5:1 of A4; in dark mode it is about 4.4 to 4.7:1 depending on the surface, at the edge of A4;
  - the Bootstrap-era `was-validated` form class is not styled, so invalid fields are not visually marked;
  - on a 390-pixel screen a `Pager` table's last column (the row actions) is cut off until the table is scrolled sideways
    inside its box; the page itself does not scroll (N1 passes).
  Report such findings in a review; do not hide them.
- **A review that did not run the page did not happen.** Rules L, N, K and H can only be checked in a browser.

## Related

- [Components and controls](components-and-controls.md)
- [Themes](themes.md)
- [Render modes](render-modes.md)
- [Localization](localization.md)
- Skills: [stomme-ux-review](../../.claude/skills/stomme-ux-review/SKILL.md),
  [stomme-build-module-ui](../../.claude/skills/stomme-build-module-ui/SKILL.md).
