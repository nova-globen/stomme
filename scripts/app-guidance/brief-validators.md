# Brief for a VALIDATOR of the app template's guidance (give it whole, with the validator's files, an id and a port)

EVERY CLAIM BELOW IS A HYPOTHESIS. Evidence wins. A truthful PARTIAL beats a polished overclaim; "could not drive X,
because Y" is a SUCCESS.

Your job: make your assigned skills and docs TRUE by following them literally in a real app, as an agent new to Stomme
would, and fixing the text wherever reality differs. Read `scripts/app-guidance/brief-writers.md` first - its rules for
shipped text bind you too - and `scripts/app-guidance/canonical/NAMES.md`, the canonical Notes module every sample uses.

## Your app

Pack the framework and the template into a local feed of your own (`pwsh scripts/pack-framework.ps1 -Output <feed>`, then
`pwsh scripts/pack-template.ps1 -Output <feed>`), then scaffold, build, install and sign in the way
`scripts/drive-app-template.ps1` does: a private template hive, NuGet pointed at that feed, SQLite, unattended through an
`appsettings.override.json` in a folder `STOMME_APPSETTINGS_PATH` names. Add the canonical Notes module from
`scripts/app-guidance/canonical/` (it is in the app's own Server/Client/Shared projects, at the paths it has there), build
and check it works before you start on your skills. Work only in your own scratch folder, on your own port.

**Browser cookies ignore the port**: several apps on `localhost` sign each other out and break antiforgery ("the
antiforgery token could not be decrypted"). Give your app its own host name - install with `Installation:DefaultAlias`
set to `<id>.localhost:<port>` and browse there.

## Hard rules

1. Edit only your assigned files in the repository; never build, test or run anything inside the repository - only in
   your scratch app. No git command that changes state.
2. Stop any process you start BY ITS PID, never by name - other sessions run dotnet and node on this machine. Free your
   port at the end.
3. Process or measurement loops go in PowerShell, not Bash.
4. Never call a real third-party service. Stand in for an external system (an identity provider, an AI model, an API)
   with something you start on loopback, or verify up to the point where the call would leave the machine and say so.
5. A defect you find in Stomme itself is a FINDING to report - with the exact request and answer - not something to fix.
   Verify security findings on your loopback app only.

## The work, per assigned skill or doc

1. Align its names with the canonical module; apply what is already known to be wrong (the findings you were given).
2. FOLLOW IT LITERALLY: every file where it says, every registration it says. BUILD (0 errors). RUN. DRIVE what its
   Verify says - in a browser if you have one, otherwise over HTTP with the cookie and the antiforgery token (a fresh
   session per check: a PowerShell `-WebSession` keeps headers from earlier calls). Read the server log for `fail`.
3. Where reality differs, FIX THE TEXT - steps, code, Verify, Common mistakes - so the next agent succeeds first time.
   Keep the file's structure and voice.
4. For a doc: check every claim you can against the source (`git grep`, read-only) and make its samples match what you
   built. Extract the samples and compile them in your app.

## Report

Per file: what changed and why; what was driven and what was observed; what could not be driven and why. Every Stomme
defect you hit (symptom, cause, where). What in this brief was wrong.
