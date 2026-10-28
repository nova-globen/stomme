# Brief for a WRITER of the app template's guidance (give it whole, with the writer's own area and files)

EVERY CLAIM BELOW IS A HYPOTHESIS, NOT A FINDING. Evidence wins. LEAD your report with anything in this brief you found
wrong. A truthful PARTIAL beats a polished overclaim; "I could not establish X" is a SUCCESS, not a failure.

## What the guidance is, and why

Stomme is the foundation an AI agent or a developer builds any application on (`AGENTS.md`, "What Stomme is for").
A new app is made with `dotnet new stomme-app` from the template at `src/Stomme.Application`, and it references
Stomme ONLY AS NUGET PACKAGES (`Stomme.Server`, which brings `Stomme.Client`, `Stomme.Shared` and the rest of its
project closure - `scripts/pack-framework.ps1`). The agent working in that app has NO Stomme source: the template's
`AGENTS.md`, `docs/` (how each piece WORKS) and `.claude/skills/` (how to BUILD each kind of piece) are all it knows.

The bar is ACCURACY. A wrong type name, namespace, config key, route or step sends an agent down a dead end and is worse
than saying nothing. Comprehensive, but every statement verified.

## Where your output goes (paths relative to `src/Stomme.Application/`)

- Reference docs: `docs/<area>/<topic>.md`, listed in `docs/README.md` (add a new one there).
- Task skills: `.claude/skills/stomme-<name>/SKILL.md`.
- An evidence ledger, NOT shipped, in your scratch folder: one line per non-obvious claim - the claim, then the
  `src/...path:line` where you verified it. The reviewer checks claims against it; a claim without a ledger line counts
  as unverified.

Write only your assigned files.

## Hard rules

1. No git command that changes state. Reading (`git grep`, `git log`, `git show`) is fine.
2. No `dotnet` command and no long-running process: read the source; the validators compile and drive afterwards.
3. Search with `git grep`, or scope to `src/`, `test/`, `examples/`.
4. A type, member, namespace or config key may appear only if you found it in the source AND an app can use it: `public`
   (or `protected` on a public base) in a project of `Stomme.Server`'s package closure. Give the namespace. If something
   useful is `internal`, say the app cannot use it.
5. Config keys: verify the exact path and its default where it is READ (the reader, not a comment). The defaults an app
   gets are `git show :src/Stomme.Server/appsettings.json` - never a local, git-ignored copy in the template folder.
6. No numbers unless measured (count with a command or leave it out).
7. Nothing repository-internal in the shipped text: no `src/...` paths, no BL or D ids, no test names, nothing of this
   repository's CI or release process. Name TYPES (with namespaces), config keys, routes, UI locations as the UI shows them
   (a `Section`'s heading is its localized resource, not the `Heading` attribute - read the `.resx`).
8. The template engine replaces the text `Stomme.Application` with the new app's name in every file, and `net10.0`,
   `44358` and `44359` too. Use `Stomme.Application` only for the app's own projects and namespaces.
9. Plain, precise English, present tense, short sentences. No marketing words. No emoji.
10. One topic per file; link to the file that owns anything else (relative links).
11. Every code sample complete enough to compile in the app as written (usings, namespaces, `#nullable enable` where it
    uses `?` - the template's projects enable neither nullable nor implicit usings), in the app's real layout (Server /
    Client / Shared), in the framework's style (`.editorconfig`). A sample that touches the Notes module uses the
    canonical names (`scripts/app-guidance/canonical/NAMES.md`).
12. Async all the way: no `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`.
13. Report: the files you wrote, what in this brief was wrong, what you could not verify, and the gaps you noticed in
    Stomme itself (a missing public extension point, a confusing behaviour) - those are findings for the owner.

## Shape of a reference doc

`# <Topic>`, two or three sentences on what it is for, then `## How it works` (the mechanism, the types and their
namespaces, discovery, the flow, what is per tenant / per site), `## Using it from an app` (the public surface, compiling
samples), `## Configuration` (every key, its default, its effect), `## Rules and pitfalls` (what breaks and how it
looks when it does) and `## Related`.

## Shape of a task skill

```
---
name: stomme-<name>          (exactly the folder's name)
description: Use when ...      (the tasks and words that should trigger it)
---
# <Title>
<One paragraph: what you will have built; the docs to read for how it works.>
## Steps          (numbered: which project, folder and file, the complete code, what to register)
## Verify         (how to prove it works - run and driven, not compiled - and what must NOT appear)
## Common mistakes   (each: the symptom, the cause, the fix)
```
