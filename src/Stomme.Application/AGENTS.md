# AGENTS.md

Guidance for an AI agent, or a developer, working in this app. Read it before changing anything.

## This app is built on Nova Globen Stomme

Stomme is the foundation this app is built on. The things every application needs are already here, finished and
proven: tenants and sites, sign-in and identity, pages and content, modules and themes, storage on the database the
installation chose, background jobs, notifications, files, search, backups, AI, an MCP server, a visual pipeline editor
and a catalogue of connectors. You build the product; Stomme is the plumbing.

Stomme arrives as NuGet packages (`Stomme.Server`, `Stomme.Client`, `Stomme.Shared` and what they bring). There
is no Stomme source in this repository and you must not need any: this file, [docs/](docs/README.md) and the skills in
[.claude/skills/](.claude/skills/) describe how each piece works and how to build on it.

## The first rule: look before you build

Before you write a service, a table, a page, a job, a login, an upload, an export, an integration or an AI call, check
whether Stomme already provides it - it usually does. The [docs index](docs/README.md) lists every capability with the
document that explains it. Rebuilding a piece Stomme has makes the app larger, less consistent and less secure, and
leaves the user with two ways of doing one thing.

## What comes first: stability, usability and UX

People use what you build every day. **A feature that is hard to find, confusing or fragile is not done**, however much
code it has. So:

- Match the admin UI's look and behaviour; use Stomme's components and the theme's colours, never your own styling of
  buttons or controls. Read [docs/ui/ux-standards.md](docs/ui/ux-standards.md) and run the `stomme-ux-review` skill on
  every page you build.
- Every page works in light and dark, on a narrow screen and from the keyboard; every empty state says what to do;
  every error says what went wrong and how to fix it.
- Finish and polish before adding more.
- Every capability serves both audiences: an agent or a developer through code, the API or MCP, and a person through
  a clear UI.

## Where things go

| Project | Holds |
|---|---|
| `Server/` | The host (it runs Stomme's own host, see `Server/Program.cs`), your modules' server side: domain, handlers, controllers, startup. |
| `Client/` | Your modules' and themes' Razor components and client services; it is also the WebAssembly client a browser downloads. No server-only code here. |
| `Shared/` | Models and contracts both sides use. No infrastructure. |
| `docs/` | How Stomme works, piece by piece. |
| `.claude/skills/` | Step-by-step skills for building each kind of piece. |

Your own code lives in modules (and themes). Read [docs/modules/anatomy.md](docs/modules/anatomy.md) first, then follow
the `stomme-create-module` skill. A module plugs in by convention; you never edit the framework or register your
module anywhere central.

## Build, run, test

```
dotnet build
cd Server
dotnet run
```

The first run opens the installer (database and host account). Unattended installs, upgrades and deployment are in
[docs/operations/install-run-upgrade.md](docs/operations/install-run-upgrade.md) and
[docs/operations/deploy.md](docs/operations/deploy.md); every switch is in
[docs/architecture/configuration.md](docs/architecture/configuration.md).

## Rules that are not negotiable

- **Verified means run and driven.** A green build is not a working feature. Run the app, use what you built in a
  browser as a user would - wide and narrow, light and dark, keyboard only - and check the browser console is clean.
- **Async all the way.** Never `.Result`, `.Wait()` or `.GetAwaiter().GetResult()`; Stomme deadlocks on them under
  real databases.
- **Storage-blind, tenant-owned data.** A module's data is an aggregate behind Stomme's repository - no `DbContext`, no
  provider-specific code - and it belongs to a tenant. See [docs/persistence/module-persistence.md](docs/persistence/module-persistence.md).
- **Fail closed.** Authorize every endpoint, check permissions per row, trust nothing from the client. See
  [docs/security/security-posture.md](docs/security/security-posture.md).
- **No secrets in committed files.** The installation writes its secrets to `appsettings.override.json`, which git
  ignores; keep it that way.
- **Components talk to the server through services over HTTP**, never to repositories directly: the same component runs
  on the server and in the browser.
- **Style:** `.editorconfig` governs - 4-space indent, braces on a new line, `var`, `System` usings first.
- **Upgrade Stomme as one:** the three `Stomme.*` versions in `Directory.Packages.props` move together - and the
  framework's guidance with them: `docs/` and the `stomme-*` skills describe the Stomme version this app was made
  from, so an upgrade takes them from the new version's template too (the `stomme-upgrade-stomme` skill says how).
- **Keep the guidance current.** When you add, change or remove a feature of this app, update the documents and skills
  that describe it in the same change - this app's own, and an app-specific skill when it is something others will
  build again. Stale guidance sends the next agent confidently down a dead end.

## Skills

Each skill in [.claude/skills/](.claude/skills/) is a folder with a `SKILL.md`: when to use it, the steps, how to verify
the result and the common mistakes. Claude Code loads them by itself; any other agent should open the one that fits
the task before starting it. The [docs index](docs/README.md) lists them by area.
