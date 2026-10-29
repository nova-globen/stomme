# Logging, health and metering

Three operational services an app uses rather than rebuilds. **Logging**: each site's Event Log (Admin > Event Log), a file
log for what has no site, and any `ILogger` provider you add. **Health**: probes run by a job every minute, a health page,
optional alerts, and two anonymous endpoints for orchestrators (`/healthz`, `/readyz`). **Metering**: a usage ledger and
per-site quotas that a module records against and checks before it consumes.

## How it works

### Logging

- **`Stomme.Infrastructure.ILogManager`** is the framework's log surface on the server. Its `Log(...)` overloads take a
  `Stomme.Shared.LogLevel` (`Trace` ... `Critical`, `None`), the logging object (its type becomes the entry's category),
  a `Stomme.Enums.LogFunction` (`Create`, `Read`, `Update`, `Delete`, `Security`, `Other`), a message template with
  `{Named}` holes, and the arguments; one pair of overloads also takes an explicit `siteId`.
- `Log` is synchronous and touches no database: it enriches the entry on the calling thread (site from the current alias,
  user id from the request's principal, URL with sensitive query values redacted, remote IP) and queues it. A background
  service (the log drain) writes it to the **site's Event Log** in its own scope and tenant. The queue holds 10,000 entries;
  when full, entries go to the file log instead of blocking.
- **No site, no Event Log.** An entry whose site cannot be determined (no alias in the scope: before tenant resolution, or
  in a job that has not set an alias) goes to the **file log**, `Content/Log/error.log` under the content root, and only
  at or above `Logging:FileLogger:LogLevel:Default` (default `Error`). The file is shown on Admin > System Info.
- **Notifications.** An Event Log entry at or above `Logging:LogLevel:Notify` (default `Error`) is sent as a notification to
  the site's Host users, with repeats of the same message held back and counted.
- **Broadcast.** An entry at or above `Logging:LogLevel:Broadcast` is also written to `Microsoft.Extensions.Logging`
  providers (Application Insights, Serilog, the console). The template ships `Broadcast: None`, so nothing is broadcast.
- **`ILogger<T>`** in server code goes to the standard logging providers, including the file logger. It does **not** reach
  the Event Log.
- **Retention**: the Purge Job deletes Event Log entries older than the site's `LogRetention` setting (days, default 30),
  set on the Event Log page.
- **Client code** logs through the `logger` that `ModuleBase` provides (see [module UI](../modules/module-ui.md)); it posts to
  the server, which logs through `ILogManager`.

### Health

- **Probes.** A contributor implements `Stomme.Health.IHealthContributor` (`ContributorId`, `Probes`, `Metrics`,
  `SourceFor(metricName)`); each probe implements `Stomme.Health.IHealthProbe` (`Name`, `Dependency`, `Scope`,
  `CheckAsync(HealthProbeContext, CancellationToken)`) and returns a `Stomme.Health.HealthProbeResult` with a
  `Stomme.Health.HealthState` (`Healthy`, `Degraded`, `Unhealthy`). Scope (`Stomme.Health.HealthProbeScope`) decides how
  often it runs: `Installation` and `Tenant` once per tenant per tick with `SiteId = 0`, `Site` once per site with the
  site's id and the site's alias set. Contributors are registered explicitly (scoped, as an enumerable) and resolved inside
  each scope's own DI scope, so they may inject scoped services. The framework's own probes (its stores, the cache, the
  site's mail server, files missing from disk) use the same interface.
- **The tick.** The **Health Probe Job** runs every minute: per tenant, the Installation and Tenant probes, then each site's
  Site probes, the whole tick bounded at 45 seconds and each probe bounded by a timeout (it is abandoned if it overruns).
  The mail-server probe runs on its own interval, `Health:MailServerProbeIntervalMinutes` (every tick by default).
  Results are stored as per-subject alert state and rollups; the **Health Rollup Job** rolls them up hourly. A latency above
  the degraded threshold makes one of the framework's own healthy probes `Degraded`; a contributed probe's state is
  recorded as the probe returns it.
- **The page**: **Admin > Health** (`admin/health`), behind the permission keys `health:view` (read) and `health:manage`
  (acknowledge, run now). Both seed to Administrators. Thresholds (degraded latency, probe timeout, minutes before an
  alert, cooldown, retention) are installation-wide Host settings, edited there by Host users only.
- **Endpoints** (always mapped, anonymous, one-word bodies, `Cache-Control: no-store`):
  - `/healthz` - `alive`, from the process only. Answers even before installation, and for any `Host` header - also
    one that names no site, on an installation with several sites. Use it as the container liveness / restart check.
  - Both paths (also when moved with `Health:LivenessPath` / `Health:ReadinessPath`) skip site resolution and the HTTPS
    redirect: a probe from an unknown host answers (any other path from that host gets a 404) and is not redirected.
  - `/readyz` - the **last completed tick's** verdict from memory: `ready` (200) when it was Healthy or Degraded,
    `not-ready` (503) when it was Unhealthy **or when no tick has completed yet** (not installed, just started, or health
    probing switched off). Neither endpoint runs a probe.
- **Alerts** (`Health:Alerting`): a subject that stays Unhealthy or Degraded for the configured minutes raises a notification,
  and a recovery is sent too. A Site-scope alert goes to the site's administrators and Host users; Installation- and
  Tenant-scope alerts go to Host users only. Alerts carry the exception type, never its message (the message can contain
  hosts and user names and stays on the health page). Off, everything is still recorded.
- **Live update**: with `Transport:SignalR:Enabled`, the health page is pushed each site's state after a tick.

### Metering and quotas

- **Recording.** A module takes `Stomme.Metering.IUsageRecorder` and calls
  `RecordAsync(new UsageEvent(metric, quantity, operationId, source))`. The caller names only **what** and **how much**: the
  site comes from the request (or the current alias outside a request), the user from the request principal (none outside
  a request), the time from the clock. The ledger row id is derived from `(site, metric, operationId)`, so recording the
  same `OperationId` twice appends once (`UsageOutcome.Idempotent`).
- **Keys.** Metric and source keys must be lowercase letters, digits, `.`, `_`, `:` or `-`, at most 100 characters;
  anything else is rejected (`Stomme.Metering.MetricKeys.IsMetric` / `RequireMetric`). A source names the producer; never
  put user data in it.
- **Checking before consuming.** `Stomme.Metering.IQuotaInspector.EvaluateAsync(metric, quantity)` answers for the current
  site's quota with a `QuotaDecision` (`Allowed`, `Reason`, `Limit`, `Consumed`, `Remaining`). Reasons
  (`QuotaDecisionReason`): `Within`, `Exceeded`, `Unlimited`, `Undefined` (no quota for that metric on the site: allowed and
  still recorded), `Unavailable` (the store could not be read: **not allowed** - enforcement fails closed), `NoSite`.
- **Nothing is blocked for you.** Recording over quota is still recorded (`UsageOutcome.WithinQuota` is false). Whether the
  operation is refused is the module's decision, made with `IQuotaInspector` before it acts.
- **Failure.** `IUsageRecorder` throws `Stomme.Metering.MeteringUnavailableException` when the metering store cannot be
  reached and never swallows it; decide whether your operation fails with it. `IQuotaInspector` does not throw: it
  answers `Unavailable` (not allowed).
- **AI calls** go through `Stomme.Metering.IAiUsageRecorder` (`AiUsageEvent`: provider, model, agent, tokens, latency,
  status, operation id); it records `ai.calls` and `ai.tokens` usage and prices the call from `Metering:AiPricing`
  server-side.
- **Quotas** are per site, per metric, over a calendar window (`Total`, `Daily`, `Monthly`, UTC). An operator defines them on
  **Admin > Usage** (`admin/usage`); each user sees their own on the `usage` page. Keys: `metering:view-own`,
  `metering:view`, `metering:manage`. A stock install defines none. A module can declare quotas every site should carry by
  registering a `Stomme.Modules.Metering.Infrastructure.IDeclaredQuotaSource` (a `Name` for the log, and `Declare(siteId)`
  returning `DeclaredQuota(metric, QuotaPeriod, limit, warnAtPercent)` per site; `QuotaAggregate.UnlimitedLimit` is -1); they
  are created on boot when absent and never overwritten. A licence or plan can supply limits through
  `Stomme.Metering.IMeteredLimitProvider` (none is shipped).
- **Jobs.** The **Usage Rollup Job** (daily) rolls up and repairs counters from the ledger; the **Metering Fold Recovery
  Job** (every five minutes) finishes recordings interrupted between the ledger write and the counter update. Both do nothing
  unless `Metering:Enabled` is true.

## Using it from an app

Log to the site's Event Log from a server class:

```csharp
#nullable enable
using Stomme.Enums;
using Stomme.Infrastructure;
using LogLevel = Stomme.Shared.LogLevel;

namespace Stomme.Application.Modules.Notes.Application
{
    public sealed class NotesAudit
    {
        private readonly ILogManager _log;

        public NotesAudit(ILogManager log)
        {
            _log = log;
        }

        public void Created(string title)
        {
            _log.Log(LogLevel.Information, this, LogFunction.Create, "Note Created {Title}", title);
        }
    }
}
```

Meter an operation and refuse it when the quota says no:

```csharp
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Metering;

namespace Stomme.Application.Modules.Notes.Application
{
    public sealed class NotesExportMeter
    {
        public const string Metric = "notes.exported";
        public const string Source = "notes";

        private readonly IUsageRecorder _usage;
        private readonly IQuotaInspector _quotas;

        public NotesExportMeter(IUsageRecorder usage, IQuotaInspector quotas)
        {
            _usage = usage;
            _quotas = quotas;
        }

        /// <summary>Asks before the export; false when the quota is exceeded or metering cannot be read.</summary>
        public async Task<bool> MayExportAsync(CancellationToken ct)
        {
            var decision = await _quotas.EvaluateAsync(Metric, 1, ct);
            return decision.Allowed;
        }

        /// <summary>Records one export; pass the same operation id on a retry so it is counted once.</summary>
        public Task<UsageOutcome> RecordAsync(Guid operationId, CancellationToken ct) =>
            _usage.RecordAsync(new UsageEvent(Metric, 1, operationId, Source), ct);
    }
}
```

Contribute a health probe:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Microsoft.AspNetCore.Hosting;

namespace Stomme.Application.Modules.Notes.Health
{
    public sealed class ImportFolderContributor : IHealthContributor
    {
        public ImportFolderContributor(IWebHostEnvironment environment)
        {
            Probes = new IHealthProbe[] { new ImportFolderProbe(Path.Combine(environment.ContentRootPath, "Content", "Imports")) };
        }

        public string ContributorId => "notes";

        public IReadOnlyList<IHealthProbe> Probes { get; }

        public IReadOnlyList<HealthMetricDescriptor> Metrics { get; } = Array.Empty<HealthMetricDescriptor>();

        public IHealthMetricSource? SourceFor(string metricName) => null;

        private sealed class ImportFolderProbe : IHealthProbe
        {
            private readonly string _folder;

            public ImportFolderProbe(string folder)
            {
                _folder = folder;
            }

            public string Name => "import-folder";

            public string Dependency => "Import folder";

            public HealthProbeScope Scope => HealthProbeScope.Installation;

            public Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var exists = Directory.Exists(_folder);
                    return Task.FromResult(new HealthProbeResult(Name, Dependency,
                        exists ? HealthState.Healthy : HealthState.Unhealthy,
                        exists ? null : "The import folder does not exist.", watch.Elapsed));
                }
                catch (Exception ex)
                {
                    // A probe must not throw.
                    return Task.FromResult(new HealthProbeResult(Name, Dependency, HealthState.Unhealthy, ex.Message, watch.Elapsed));
                }
            }
        }
    }
}
```

Register it in your `IServerStartup.ConfigureServices`:

```csharp
services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Health.IHealthContributor, ImportFolderContributor>());
```

(`TryAddEnumerable` and `ServiceDescriptor` are in `Microsoft.Extensions.DependencyInjection.Extensions` and
`Microsoft.Extensions.DependencyInjection`.)

## Configuration

| Key | Default | Effect |
|---|---|---|
| `Logging:LogLevel:Default` | `Information` | Lowest level written to the Event Log (and the ASP.NET Core default). |
| `Logging:LogLevel:Notify` | `Error` | Lowest Event Log level that notifies the site's Host users. |
| `Logging:LogLevel:Broadcast` | `None` | Lowest level also sent to `ILogger` providers. |
| `Logging:FileLogger:LogLevel:Default` | `Error` | Lowest level written to `Content/Log/error.log`. |
| `Health:Enabled` | `true` when absent | Runs the Health Probe and Health Rollup jobs. Off: nothing is probed and `/readyz` answers 503 until it is on again. Does not remove the page or the endpoints. |
| `Health:Alerting` | `false` | Sends alert and recovery notifications. |
| `Health:MailServerProbeIntervalMinutes` | `1` when absent | How often the probe job connects to each site's mail server (`SMTPHost:SMTPPort`): every tick at 1, at most once per N minutes above it, never at 0. Each probe is a connection to the relay, so raise it when the relay counts connections. Run now always probes it. The store and cache probes still run every minute. |
| `Health:LivenessPath` | `/healthz` | Path of the liveness endpoint (read at startup). |
| `Health:ReadinessPath` | `/readyz` | Path of the readiness endpoint (read at startup). |
| `Metering:Enabled` | `false` | Runs the two metering jobs. Recording and quota checks work either way. |
| `Metering:AiPricing` | empty | AI prices: `"provider": { "model": { "InputPerMillion": n, "OutputPerMillion": n, "Currency": "USD" } }`, or a bare model. An unpriced model costs 0. |
| `module.health.enabled`, `module.metering.enabled` | seeded | Feature flags that stop those modules' pages from being seeded; see [configuration](../architecture/configuration.md). |

The `Health:*` and `Metering:*` switches are read from the host's configuration (including the override file) on every run.

## Rules and pitfalls

- **A log entry with no site vanishes into the file log**, and only at Error and above. In a job, set the site's alias
  before logging, or pass the `siteId` overload.
- **Do not log secrets or personal data in messages or arguments.** Administrators read the Event Log; Error entries are
  mailed to Host users.
- **`Health:Enabled = false` makes `/readyz` permanently 503.** Do not point a load balancer's readiness check at `/readyz`
  on an install with probing off; use `/healthz` for liveness in any case.
- **A probe that only resolves a service is not a probe.** It proves the container, not the dependency, and can only say
  Healthy. Make a real, cheap call.
- **A probe must not throw**, should honour the token, and must not put secrets in `Description` beyond what an
  administrator may see.
- **`ContributorId` must be unique** in the installation; a duplicate is rejected at run time with an error naming both.
- **`Metering:Enabled` does not turn metering on or off for producers.** `IUsageRecorder` and `IQuotaInspector` are always
  registered and always record and answer; the switch only runs the repair jobs.
- **Outside a request there is no user**: background usage is recorded without one. Quotas are per site, not per user. Without an alias in the scope the recorder returns `NoSite` and records nothing.
- **Pass a stable `OperationId`** for an operation that can be retried; a new `Guid` per attempt counts each attempt.
- `IDeclaredQuotaSource` is a server type; its quota rows are created only when absent, so changing a declared limit does
  not change sites that already have the quota.

## Related

- [Jobs](jobs.md) - the jobs these services run, and setting the alias in your own
- [Notifications](notifications.md) - where log and health notifications go
- [Configuration](../architecture/configuration.md) - the switches and the override file
- [Deploy](../operations/deploy.md) - liveness and readiness in a container
- [Identity and permissions](../security/identity-and-permissions.md) - the permission keys
