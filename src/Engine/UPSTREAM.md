# UPSTREAM — the vendored engine

**This tree is DERIVED, not authored.** It is produced by `scripts/vendor-engine.ps1` (BL-93 phase A, D98)
and re-derived on every run of that script; hand fixes applied after derivation are recorded in
`src/Engine/README.md` (phase B) so that *script + recorded fixes* reproduces the tree.

| | |
|---|---|
| Upstream repository | https://github.com/elsa-workflows/elsa-core |
| Tag | `3.7.1` |
| Commit | `c7a97c64129ee5dd502f88240e51b4ffcbf9efc3` |
| Licence | MIT (copyright 2021 Elsa Workflows) — notice retained in `THIRD-PARTY-NOTICES.md` |
| Vendored on | 2026-09-13 |
| Script invocation | `pwsh -NoProfile -File scripts/vendor-engine.ps1 -Source <path-to-elsa-core-3.7.1-clone>` |

## What was taken

The 12-package transitive closure of the `Elsa` meta-package minus `Elsa.Api.Common`, plus the shared
test helper and the seven upstream unit-test projects:

| Upstream | Vendored |
|---|---|
| `src/common/Elsa.Features` | `Stomme.Engine.Features` |
| `src/common/Elsa.Mediator` | `Stomme.Engine.Mediator` |
| `src/modules/Elsa.Common` | `Stomme.Engine.Common` |
| `src/modules/Elsa.Expressions` | `Stomme.Engine.Expressions` |
| `src/modules/Elsa.Caching` | `Stomme.Engine.Caching` |
| `src/modules/Elsa.KeyValues` | `Stomme.Engine.KeyValues` |
| `src/modules/Elsa.Tenants` | `Stomme.Engine.Tenants` |
| `src/modules/Elsa.Workflows.Core` | `Stomme.Engine.Pipelines` |
| `src/modules/Elsa.Workflows.Management` | `Stomme.Engine.Pipelines.Management` |
| `src/modules/Elsa.Workflows.Runtime` | `Stomme.Engine.Pipelines.Runtime` |
| `src/modules/Elsa` | `Stomme.Engine` |
| `src/common/Elsa.Testing.Shared` | `Stomme.Engine.Testing.Shared` |
| `test/unit/Elsa.Mediator.UnitTests` | `Stomme.Engine.Mediator.Tests` |
| `test/unit/Elsa.Common.UnitTests` | `Stomme.Engine.Common.Tests` |
| `test/unit/Elsa.Expressions.UnitTests` | `Stomme.Engine.Expressions.Tests` |
| `test/unit/Elsa.Tenants.UnitTests` | `Stomme.Engine.Tenants.Tests` |
| `test/unit/Elsa.Workflows.Core.UnitTests` | `Stomme.Engine.Pipelines.Tests` |
| `test/unit/Elsa.Workflows.Management.UnitTests` | `Stomme.Engine.Pipelines.Management.Tests` |
| `test/unit/Elsa.Workflows.Runtime.UnitTests` | `Stomme.Engine.Pipelines.Runtime.Tests` |

## The drop list (applied by the script)

- `src/common/Elsa.Api.Common` — the whole project (FastEndpoints / Swagger / CShells: the HTTP API Stomme does not expose).
- Every file under a `ShellFeatures/`, `ShellHandlers/` or `Endpoints/` folder, the CShells
  `ShellInstalledFeatureProvider`, `Elsa.Tenants/Features/TenantManagementEndpointsFeature.cs` — and a
  content guard that drops any remaining `.cs` file mentioning CShells, FastEndpoints or `Elsa.Abstractions`.
  Files dropped this run: **49** (listed below).
- `FodyWeavers.xml` and `*.DotSettings` (weaver config and IDE settings).
- Package references to `FastEndpoints*`, `CShells*`, `Fody`, `ConfigureAwait.Fody`, `PolySharp`.
- Project references to `Elsa.Api.Common` and to the out-of-closure expression providers
  (`Elsa.Expressions.CSharp` / `JavaScript` / `Liquid`, referenced only by `Elsa.Testing.Shared`'s csproj).
- Upstream's coverlet `<Include>`/`<Threshold>` knobs, `<RootNamespace>` overrides (root namespace = project name).
- The `System.Linq.Async` item group (upstream: only for TargetFrameworks other than net10.0).
- Source surgery (members whose only purpose is to install a dropped file — see `System.Collections.Hashtable` in the script):
  `Elsa.Tenants/Extensions/ModuleExtensions.cs` loses `UseTenantManagementEndpoints`.
- Rename-collision pre-rule: upstream's pre-existing identifier `pipeline` (the middleware chain) becomes
  `executionPipeline` in code so the renamed `workflow` → `pipeline` locals cannot shadow it.

### Files dropped this run

- `src/common/Elsa.Features/FodyWeavers.xml`
- `src/common/Elsa.Features/Services/ShellInstalledFeatureProvider.cs`
- `src/common/Elsa.Mediator/FodyWeavers.xml`
- `src/modules/Elsa.Common/Elsa.Common.csproj.DotSettings`
- `src/modules/Elsa.Common/FodyWeavers.xml`
- `src/modules/Elsa.Common/ShellFeatures/DefaultFormattersFeature.cs`
- `src/modules/Elsa.Common/ShellFeatures/MediatorFeature.cs`
- `src/modules/Elsa.Common/ShellFeatures/MultitenancyFeature.cs`
- `src/modules/Elsa.Common/ShellFeatures/StringCompressionFeature.cs`
- `src/modules/Elsa.Common/ShellFeatures/SystemClockFeature.cs`
- `src/modules/Elsa.Common/ShellHandlers/ActivateShellTenants.cs`
- `src/modules/Elsa.Expressions/FodyWeavers.xml`
- `src/modules/Elsa.Expressions/ShellFeatures/ExpressionsFeature.cs`
- `src/modules/Elsa.Caching/Elsa.Caching.csproj.DotSettings`
- `src/modules/Elsa.Caching/FodyWeavers.xml`
- `src/modules/Elsa.Caching/ShellFeatures/MemoryCacheFeature.cs`
- `src/modules/Elsa.KeyValues/FodyWeavers.xml`
- `src/modules/Elsa.KeyValues/ShellFeatures/KeyValueFeature.cs`
- `src/modules/Elsa.Tenants/Elsa.Tenants.csproj.DotSettings`
- `src/modules/Elsa.Tenants/Endpoints/Tenants/Add/Endpoint.cs`
- `src/modules/Elsa.Tenants/Endpoints/Tenants/Delete/Endpoint.cs`
- `src/modules/Elsa.Tenants/Endpoints/Tenants/Get/Endpoint.cs`
- `src/modules/Elsa.Tenants/Endpoints/Tenants/List/Endpoint.cs`
- `src/modules/Elsa.Tenants/Endpoints/Tenants/Refresh/Endpoint.cs`
- `src/modules/Elsa.Tenants/Endpoints/Tenants/Update/Endpoint.cs`
- `src/modules/Elsa.Tenants/Features/TenantManagementEndpointsFeature.cs`
- `src/modules/Elsa.Tenants/FodyWeavers.xml`
- `src/modules/Elsa.Tenants/ShellFeatures/TenantManagementEndpointsFeature.cs`
- `src/modules/Elsa.Tenants/ShellFeatures/TenantManagementFeature.cs`
- `src/modules/Elsa.Tenants/ShellFeatures/TenantsFeature.cs`
- `src/modules/Elsa.Workflows.Core/Elsa.Workflows.Core.csproj.DotSettings`
- `src/modules/Elsa.Workflows.Core/FodyWeavers.xml`
- `src/modules/Elsa.Workflows.Core/Services/ShortGuidIdentityGenerator.cs`
- `src/modules/Elsa.Workflows.Core/ShellFeatures/CommitStrategiesFeature.cs`
- `src/modules/Elsa.Workflows.Core/ShellFeatures/FlowchartFeature.cs`
- `src/modules/Elsa.Workflows.Core/ShellFeatures/WorkflowsFeature.cs`
- `src/modules/Elsa.Workflows.Management/Elsa.Workflows.Management.csproj.DotSettings`
- `src/modules/Elsa.Workflows.Management/FodyWeavers.xml`
- `src/modules/Elsa.Workflows.Management/ShellFeatures/CachingWorkflowDefinitionsFeature.cs`
- `src/modules/Elsa.Workflows.Management/ShellFeatures/WorkflowDefinitionsFeature.cs`
- `src/modules/Elsa.Workflows.Management/ShellFeatures/WorkflowInstancesFeature.cs`
- `src/modules/Elsa.Workflows.Management/ShellFeatures/WorkflowManagementFeature.cs`
- `src/modules/Elsa.Workflows.Runtime/Elsa.Workflows.Runtime.csproj.DotSettings`
- `src/modules/Elsa.Workflows.Runtime/FodyWeavers.xml`
- `src/modules/Elsa.Workflows.Runtime/ShellFeatures/CachingWorkflowRuntimeFeature.cs`
- `src/modules/Elsa.Workflows.Runtime/ShellFeatures/WorkflowRuntimeFeature.cs`
- `src/modules/Elsa/FodyWeavers.xml`
- `src/modules/Elsa/ShellFeatures/ElsaFeature.cs`
- `src/common/Elsa.Testing.Shared/FodyWeavers.xml`

## Not derived — authored here

`scripts/vendor-engine.ps1` WIPES `src/Engine` and `test/Engine` before re-deriving, so anything authored in
those trees must be written BY the script or a re-run deletes it. Five groups are:

- **In-house replacements** (step 3b) — `Stomme.Engine.Pipelines/Services/ShortGuid.cs`,
  `ShortGuidIdentityGenerator.cs` and their test. Each names the upstream file it replaces
  (`DependsOnDropOf`); the run FAILS if that file was not dropped.
- **Authored files** (step 3c) — `test/Engine/Stomme.Engine.Tests/` (the BL-93 contact-surface smoke test).
  Net-new, replacing nothing upstream, so no `DependsOnDropOf` guard.
- **Recorded hand fixes** (step 3d) — `scripts/vendor-engine.d33-handfixes.patch`, the D33 fix to the Mediator's
  two handler invokers (an awaited unwrap instead of reading `Task<T>.Result` by reflection) and the
  `CommandContext.Result` → `CommandResult` rename; and (BL-95, 2026-10-05) `ObjectConverter` turning a JSON
  object or array `JsonElement` read into a text input into its JSON text, as it already did a `JsonNode` - a
  ForEach over records faulted its body's first text input. The run FAILS when it no longer applies.
- **The pipeline editor's hand fixes** (step 6b, BL-229) — `scripts/vendor-engine.bl229-handfixes.patch`, applied
  after the `ConfigureAwait(false)` pass whose lines they carry: the step pipeline's hook
  (`EngineFeature.ConfigureStepExecutionPipeline`), a cancel deleting what the run waited on, a step that begins a
  run only when the run names it (`IStartsOnlyWhenNamed`) and the flowchart's start rule that honours it, an input
  that fails to evaluate saying why, five optional inputs made nullable, and a call waiting for another pipeline given
  back that pipeline's result when it had paused (`ChildPipelineCompletion`) and failing when it fails. The run FAILS
  when it no longer applies.
- **`src/Engine/README.md`** — the engine's design notes; the wipe preserves it.

## Deliberate departure from upstream — `ConfigureAwait(false)` everywhere (BL-95 Step 0)

Upstream awaits without `ConfigureAwait(false)` (its `ConfigureAwait.Fody` weaver was configured to weave
nothing - measured), so a run started on a single-threaded `SynchronizationContext` resumes on it. Here every
await in `src/Engine` carries `.ConfigureAwait(false)`: step 6 runs the SDK's CA2007 code fix over the library
projects and splits `await using` declarations so their variables keep their type, and the generated
`src/Engine/.editorconfig` makes CA2007 an ERROR there (not in `test/Engine`). Added this run:
**1042** `ConfigureAwait(false)` calls and **13** `await using` splits.
`test/Engine/Stomme.Engine.Tests/CapturedContextTests.cs` is the behavioural gate (0 posts to the caller's
context). Decision and measurements: `docs/backlog.md` §J.BL-93 "DECIDED" and `src/Engine/README.md`.

## The rename dictionary

See `scripts/vendor-engine.ps1` (the single source of truth) and `docs/backlog.md` §J.BL-93.