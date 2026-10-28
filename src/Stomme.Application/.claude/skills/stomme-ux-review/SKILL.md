---
name: stomme-ux-review
description: Use when reviewing or checking the UX of a page, module, form or theme built on Nova Globen Stomme - before calling UI work done, after changing a page, or when asked whether something is usable, accessible, consistent with the admin UI, works in dark mode, on mobile or with the keyboard.
---

# Review a page against Stomme's UX standards

You will have a review report for one page (or one module's pages): every rule of
[docs/ui/ux-standards.md](../../../docs/ui/ux-standards.md) marked pass or fail with what you saw, the failures fixed or
listed with their cause. The review is done in a running app, in a browser - at two widths, in light and dark, with the
mouse and with the keyboard only. Reading the code alone is not a review. Read the standards document first; the
controls it names are in [docs/ui/components-and-controls.md](../../../docs/ui/components-and-controls.md).

## Steps

1. **Read the page's source** (the `.razor` files of the module or theme) and note, before running anything:
   - every state the page can be in: loading, empty, a list, a form, each message, a confirmation, no access;
   - any `style="..."` that sets a colour, any `var(--db-...)` with a fallback value, any class of the module's own that
     starts with `db-` (rules S7, S8, L2);
   - any `@onclick` on a `div`, `span`, `td` or an `a` without `href` (K3);
   - every input and its `Label For` / `id` pair, and duplicate ids (A1);
   - every service call and whether it is in a `try`/`catch` that shows a message (R1);
   - every `AddModuleMessage` that includes user text without `WebUtility.HtmlEncode` (R4);
   - every delete or irreversible action and whether an `ActionDialog` guards it (C1).

2. **Run the app.** `dotnet build` at the app root, then `cd Server` and `dotnet run`; open the URL it prints. Sign in as
   the host (and later as a user with less permission). Place the module on a page if it is not on one. Open the browser's
   developer console and keep it open for the whole review.

3. **Pass 1 - wide, light, mouse.** Window about 1280 pixels wide. Set light mode: click the theme toggle, or run
   `localStorage.setItem('stomme-theme', JSON.stringify({ mode: 'light' }))` in the console and reload. Then walk every
   state from step 1:
   - empty state (remove or filter all items), loading (throttle the network in the developer tools), list, search,
     paging (add items until there are more than ten, the `Pager`'s default page size), add, edit, save with a missing
     required field, save, delete then Cancel, delete then confirm;
   - force an error: enter a value the server refuses (a duplicate, a too-long text), and make a call fail. On a
     WebAssembly site, block the module's API in the developer tools (Network > block request URL, for example
     `*/api/Notes*`) and act; on an Interactive Server site stopping the app only drops the circuit (the browser shows
     the reconnect banner and no module code runs), so test failures there with a refused value. Each message must say
     what failed and what to do (R2), with no exception text such as "TypeError: Failed to fetch" (W3).
   Check S1-S9, E1-E5, R1-R4, C1-C3, T1-T3, W1-W3, and H1 after each action.

4. **Pass 2 - wide, dark.** Toggle to dark (or set `{ mode: 'dark' }` and reload). Walk the same states. Check L1-L4:
   read every text on every surface, every message type, the dialog, the status pills, icons and images. Check that
   primary buttons show the theme's colour (the same colour as the Search button of a `Pager`) and that nothing on the page
   sets its own button colour.

5. **Pass 3 - narrow.** Set the window, or the developer tools' device mode, to 375 pixels wide. Walk the states in both
   modes, signed in (the header holds more controls then). Check N1-N4: no horizontal page scroll
   (`document.documentElement.scrollWidth <= window.innerWidth` in the console), form rows stacked, tables scrolling
   inside their box, the menu as a toggle, nothing reachable only by hover. Note whether a table's row actions are visible
   without scrolling the table sideways.

6. **Pass 4 - keyboard only.** Put the mouse aside. From the address bar, Tab through the page and do every task: open
   add, fill the form, save, edit, open delete, cancel, confirm, search, page. Check K1-K5: the order follows the reading
   order, a focus ring is always visible, Enter and Space activate, Enter in the search box searches, nothing needs the
   mouse. After each dialog opens and closes, check where focus is (`document.activeElement` in the console): inside the
   dialog, then back where the user continues - not on the page body. Note every control you could not reach.

7. **Pass 5 - accessibility.** In the developer tools' accessibility view (or an automated checker such as axe, if the
   environment has one), check A1-A7: every input and icon-only button has a name, no information by colour alone, text
   contrast at least 4.5:1 in both modes (the developer tools' colour picker shows the ratio of a text element; check the
   danger button and status colours too, not only body text), images have `alt`, messages have `role="alert"`. Turn on
   the system's reduced motion setting and check nothing animates (A7).

8. **Pass 6 - permission and reload.** Sign in as a user who may view but not edit: edit controls are hidden or the page
   says why (E3). Reload the browser on every URL the page has (list, an edit URL with `?id=`): each works directly (H2).

9. **Pass 7 - other render modes, when the page is interactive.** As the Host, open Admin > Site Settings and click the
   **UI Component Settings** heading (the section is collapsed, and only the Host sees it). Switch Interactivity to
   "Client (WebAssembly)" and save; repeat pass 1 briefly with the console open. Then, separately, switch Render Mode
   between Static and Interactive. Changing Render Mode also changes Prerender in the form (Interactive sets Yes, Static
   sets No), so note all three values first and restore all three afterwards.

10. **Fix or report.** Fix every failure that is in the app's own code, then repeat the pass that found it. A failure
    that comes from a Stomme control (see "known gaps" in the standards document) is reported, not hidden, with a
    workaround if the page can avoid it.

11. **Write the report** in this shape:

    ```
    UX review: <module / page>, <date>, theme <name>, site render mode <mode>/<runtime>
    Passes run: 1 wide-light, 2 wide-dark, 3 narrow (light, dark), 4 keyboard, 5 a11y, 6 permissions/reload, 7 runtimes
    Fail  K3  Pager page links not reachable by Tab (Stomme control) - reported
    Fail  E2  Empty list showed headers only - fixed: db-empty block with Add note
    Pass  L1-L4, N1-N4, ...
    Console: clean / <errors with the action that caused them>
    Not checked: <rule> - <why>
    ```

## Verify

The review is complete when every rule in the standards document has a line - pass, fail, or "not checked" with a
reason - and each pass in steps 3 to 9 was actually run in a browser. A report that marks L, N, K or H rules as passed
without a browser run is not a review. Re-run the failing pass after each fix and record the new result.

## Common mistakes

- **Reviewing only the happy path.** Most failures are in the empty, error and no-access states. Force each one.
- **Checking dark mode on one screen.** Messages, dialogs and status pills each have their own colours; open every one in
  both modes.
- **Checking "mobile" by shrinking the window a little.** Use 375 pixels exactly, and measure horizontal scroll with the
  console expression above.
- **Tabbing with the mouse nearby.** Hover states hide missing focus rings. Use only the keyboard for pass 4.
- **Ignoring console warnings.** A warning on every render is a defect; record it with the action that caused it.
- **Leaving the site in another render mode** after pass 7. Restore Render Mode, Interactivity and Prerender.
- **Sign-in fails with HTTP 400, or you are signed out at random, while another Stomme app runs on this machine.**
  Browsers do not separate cookies by port, so two apps on `localhost:5000` and `localhost:5001` overwrite each other's
  sign-in and antiforgery cookies (the server log says "The antiforgery token could not be decrypted"). Give each app its
  own host name: install it with the alias `myapp.localhost:<port>` and browse there.
- **Checking a dialog only with the mouse.** Keyboard users land behind the overlay when focus is not moved into the
  dialog; check `document.activeElement` after it opens.
- **Fixing a Stomme control's colour or behaviour with page CSS.** That breaks the theme rule (S7). Report it instead.
- **Reviewing a stale build.** Rebuild and restart after each fix; a browser can also hold old scripts - reload with the
  cache disabled in the developer tools.
