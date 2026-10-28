# Datasets, dashboards, formats and cloud cost

Four built-in modules turn data into something people read. **Datasets** register a tabular sample with an inferred
schema and a classification. **Dashboards** lay widgets (charts, grids, lists, KPIs, pivots) out on a grid, each bound
to a dataset. **Formats** are named display formats for dates, numbers, currencies and locales. **Cloud Usage** and
**Cost Optimization** pull a site's cloud and SaaS bills through connections and turn them into rollups, budgets and
recommendations. All of them are per site, storage-blind, and admin pages of their own.

## How it works

### Datasets

A dataset is a **reference** to where data comes from plus a bounded sample, not a copy of the data.

| Concept | Type (namespace `Stomme.Datasets`, in `Stomme.Shared`) | Rule |
| --- | --- | --- |
| Origin | `OriginKind`: `PipelineOutput`, `Upload`, `ExternalQuery`, `DemoSeed`, plus an `OriginReference` string | The dataset's identity is the tenant, the site, the origin kind and the normalised reference (or the name when there is no reference). Registering the same origin again **refreshes** that dataset. |
| Name | | Unique on the site. A new origin under a name another dataset holds is refused, never overwritten. At most 200 characters (`DatasetLimits.MaxNameLength`). |
| Sample | `SampleJson`: a JSON array of objects | At most 1 MiB (`DatasetLimits.MaxSampleBytes`), refused before parsing. The first 50 rows (`DatasetLimits.MaxSampleRows`) are kept for preview; the row count is the sample's, or the `RowCountEstimate` the producer gives. |
| Schema | `DatasetSchemaDescriptor` / `DatasetColumnDescriptor` | Inferred per column from the values: `ColumnDataType` `Boolean`, `Number`, `Date`, `Json`, else `String`, with a semantic hint (email, ip, url, currency). |
| Summary | `DatasetSummaryDescriptor` | Per column: samples, min, max, unique and null counts. |
| Classification | `DataClassification`: `Public < Internal < Confidential < Restricted` | Only ever escalates. The classifier raises it: a column **named** like a secret (password, secret, api key, token, credential, private key) makes it `Confidential`; personal data **in the sample** (an e-mail address, a US social security number, a card number passing the Luhn check) makes it `Restricted`. A refresh never lowers it. |

`DatasetClassificationPolicy` holds the one rule: **`Confidential` and `Restricted` datasets have their summary masked
(no samples, no min, no max) and their raw rows refused** on preview, as a dashboard widget's rows, as an export source
and over MCP.

Ports for code (server side, scoped):

- `IDatasetCatalog`: `ListDescriptorsAsync`, `GetDescriptorAsync(id)`, `GetPreviewRowsAsync(id)` (a
  `DatasetPreviewRows` with `Allowed`, `RowsJson`, `Reason`). Only the scope's site's datasets; another site's id is
  not found.
- `IDatasetRegistrar`: `RegisterOrRefreshAsync(DatasetRegistration)`, returns the dataset id. Needs a resolved site
  and tenant; throws `InvalidOperationException` without them.
- `IDatasetUsageProvider`: implement it when your module references datasets by id; a dataset in use cannot be deleted.

Neither port checks permissions. The keys (`DatasetPermissions`): `datasets:view` (list, schema, masked summary),
`datasets:manage` (register, refresh, delete), `datasets:preview` (raw rows, the higher key). All default to
Administrators.

The page is `/admin/datasets`: the list (origin, classification, rows, columns, refreshed), a schema and summary panel,
**Preview** for `datasets:preview` holders, and for managers a register form (name, origin, reference, classification,
sample rows as JSON), refresh, delete, and an upload section that makes a dataset from a CSV, JSON or XLSX file held by
the file manager. An uploaded file's origin is `file:<id>`, so registering the same file again refreshes it, and the
row count is the whole file's.

### Dashboards

A dashboard (`DashboardView`, `Stomme.Dashboards`) is a name, a status (`Draft`, `Published`), a layout grid
(`Columns` 1 to 48, `RowHeightPx`) and widgets in render order. Each widget (`WidgetView`) has:

- `Kind` (`WidgetKind`): `Chart`, `Grid`, `List` (column summaries), `Kpi` (the row estimate), `Pivot` (groups by the
  first bound column, counts, and sums the second when it is numeric).
- `DatasetId` and `Columns`: the bound dataset and the columns it uses. Each column must be in the dataset's schema;
  this is checked when the widget's data is read (a 400 naming the widget, the column and the dataset), not when the
  widget is added.
- `Column`, `Row`, `ColumnSpan`, `RowSpan`: the position. Widgets may not overlap or reach past the last column; a
  layout change that would orphan a widget is refused.
- `RefreshKind`: `Manual`, `Interval` (`IntervalSeconds`, at least 30) or `EventDriven` (when the bound dataset is
  refreshed).
- `Interaction` (`None`, `Navigate`, `Popup`, `Form`) and `Target`.
- `ChartSpecJson` (charts only, required for them): an ECharts option document, at most 64,000 characters. It must be
  **theme-free**: a `color`, `colors`, `backgroundColor`, `borderColor`, `textColor`, `fontFamily`, `fontSize`,
  `fontWeight` or `fontStyle` property anywhere is refused, because the renderer injects the theme's colours and fonts.
  A string `"$column"` is replaced by that column's values from the widget's rows. Put the tokens in a `dataset.source`
  and name the dimensions in `encode`; a token inside `encode` draws nothing:

  ```json
  { "dataset": { "source": { "region": "$region", "amount": "$amount" } },
    "xAxis": { "type": "category" }, "yAxis": { "type": "value" },
    "series": [ { "type": "bar", "encode": { "x": "region", "y": "amount" } } ] }
  ```
- `GridState` (grids only): column widths, sort, filter.

A widget's data (`WidgetDataView`) is read through the Datasets catalogue: the dataset's descriptor with its masked
summary, and the rows only when the classification allows them (`RowsAllowed`, `RowsReason`). A widget bound to a
`Restricted` dataset therefore shows its count but no rows.

Keys (`DashboardPermissions`): `dashboards:view`, `dashboards:manage`, `dashboards:publish`, all Administrators by
default. A dataset bound by a widget cannot be deleted.

The page is `/admin/dashboards`: the list with clone, publish and delete; a board opened as a viewer with its widgets
drawn; and for managers the builder (add a widget with the site's datasets as the picker, reorder, remove, rename, the
layout). A new board takes only a name; it starts with 12 columns and 40-pixel rows. When a bound dataset is refreshed
or its schema changes inside a site's request, an open board re-reads every widget bound to it (the page shows a
**live** badge): on an Interactive Server page through an in-process signal, with no switch; on a page running in the
browser through the hub at `/hubs/v1/dashboards`, which is mapped only while `Transport:SignalR:Enabled` is on. Either
way the signal carries the dataset id and the kind of change, never rows. A refresh made outside a site's scope
signals nothing (read from the source, not driven). Otherwise widgets refresh on their interval or by hand (**Refresh widgets**).

The client side (`Stomme.Client` package) offers `IDashboardsService` (`Stomme.Modules.Dashboards.Services`) with
every API call, and a read-only board component, `DashboardBoard` (`Stomme.Modules.Dashboards.Components`), that
renders a board statically from a `DashboardView` and the widget data its static `LoadAsync` reads. A page that shows
charts with it must load the chart scripts the admin page loads:
`_content/Vizor.ECharts/js/vizor-echarts-bundle-min.js` and `Modules/Dashboards/dashboards-charts.js` (embedding the
board in an app module is not verified).

### Formats

A format (`FormatDescriptor`, `Stomme.Formats`) has a `Kind` (`Date`, `Number`, `Currency`, `Locale`), a .NET format
`Pattern`, a `Culture`, an optional `TimeZone`, and a `Scope`: `System` (presets compiled into the framework, with fixed
ids) or `Tenant` (definitions a site saves). `Format(value)` and `TryParse(text, out value)` never throw: an unknown
culture, a bad time zone or a value that does not fit falls back to an invariant rendering. `Example()` renders a sample
value.

`IFormatService` (`GetAsync(id)`, `ListAsync(kind, culture, scope)`) is the port for server code: the presets and the
scope's site's definitions. Keys (`FormatPermissions`): `formats:view`, `formats:manage`. The page is `/admin/formats`.
Data exports use formats: an export column whose format hint names a format (by id or name) is rendered through it.

### Cloud Usage and Cost Optimization

**Cloud Usage** (`/admin/cloud-usage`, keys `cloud-usage:view`, `cloud-usage:manage`) syncs a site's costs from a
connection into a ledger: `POST api/CloudUsage/sync` with a `SyncCloudCostsRequest` (`ConnectionId`, `Provider`,
`Window` `daily` (yesterday, the default) or `monthly` (last month), and optionally `TargetDataset` to register or
refresh a dataset with the rows and `DatasetScope` `run` or `ledger`). The `Provider` is a `CloudProvider` name
(`azure`, `aws`, `gcp`, `datadog`, `snowflake`, `github`, `microsoft365`, `salesforce`, `genericrest`), and the
connection must be that provider's cost connector type (`azurecost`, `awscost`, `gcpcost`, `datadogcost`,
`snowflakecost`, `githubcost`, `m365cost`, `salesforcecost`, or `rest` for `genericrest`), holding its `token`
credential. A refused sync answers 200 with `Success = false` and a code such as `CONNECTION_TYPE_MISMATCH` or
`CREDENTIAL_MISSING`; a fetch failure's detail is a category, never the remote's text. The rollups are
`GET api/CloudUsage/summary`, `by-provider`, `by-service`, `month-over-month`, `budget-status` and `budgets`; budgets are
set with `PUT api/CloudUsage/budgets`. Totals stay per currency; nothing is converted. AWS and GCP request signing is not
built in this version: a sync from those sources says it has no token.

With `CloudUsage:Alerts:Enabled`, a daily job compares each budget's month-to-date spend with 80 % (Warning) and 100 %
(Critical), and the latest day with the rolling average, and notifies the site's administrators and Host users once per
alert.

**Cost Optimization** (`/admin/cost-optimization`, keys `cost-optimization:view`, `cost-optimization:manage`) runs
advisors over the site's cloud spend and AI usage and raises recommendations, each with evidence and an estimated
saving, that a manager applies or dismisses (`GET api/CostOptimization`, `POST api/CostOptimization/run`,
`POST api/CostOptimization/{id}/apply`, `POST api/CostOptimization/{id}/dismiss`). A weekly job runs the pass per site.

## Using it from an app

- **Register a dataset from your code**: inject `IDatasetRegistrar`, build a `DatasetRegistration(Name, OriginKind,
  OriginReference, SampleJson, Classification, RowCountEstimate)`, and call `RegisterOrRefreshAsync`. Records from a
  connection become a sample with `ConnectorRecordJson.Serialize(records)` (`Stomme.Connectivity`). The
  [stomme-build-dataset-and-dashboard](../../.claude/skills/stomme-build-dataset-and-dashboard/SKILL.md) skill
  has the complete code.
- **Read datasets**: inject `IDatasetCatalog`; never read rows past `DatasetPreviewRows.Allowed`.
- **Build a board**: the builder at `/admin/dashboards`, or `IDashboardsService` / `api/Dashboards`.
- **Render a value**: inject `IFormatService`, get the descriptor, call `Format(value)`.

### The HTTP APIs

All relative to the site, decided by the keys above.

| Route | Key |
| --- | --- |
| `GET api/Datasets`, `GET api/Datasets/{id}` | `datasets:view` |
| `GET api/Datasets/{id}/preview` | `datasets:preview` |
| `POST api/Datasets` (`RegisterDatasetRequest`), `POST api/Datasets/upload` (`UploadDatasetRequest`: `FileId`, `Name`, `Classification`), `POST api/Datasets/{id}/refresh` (`RefreshDatasetRequest`), `DELETE api/Datasets/{id}` | `datasets:manage` |
| `GET api/Dashboards`, `GET api/Dashboards/{id}`, `GET api/Dashboards/{id}/widgets/{widgetId}/data` | `dashboards:view` |
| `POST api/Dashboards` (`CreateDashboardRequest`), `PUT api/Dashboards/{id}` (rename), `POST .../{id}/clone`, `POST .../{id}/layout`, `DELETE .../{id}`, `POST .../{id}/widgets` (`AddWidgetRequest`), `PUT .../{id}/widgets/{widgetId}`, `POST .../widgets/{widgetId}/move`, `POST .../widgets/{widgetId}/reorder`, `PUT .../widgets/{widgetId}/grid-state`, `DELETE .../widgets/{widgetId}` | `dashboards:manage` |
| `POST api/Dashboards/{id}/publish` | `dashboards:publish` |
| `GET api/Formats?kind=&scope=&culture=`, `GET api/Formats/{id}` | `formats:view` |
| `POST api/Formats` (`FormatDefinitionRequest`; no id creates, an id updates), `DELETE api/Formats/{id}` | `formats:manage` |

Enumerations travel as **numbers** in these APIs (no string-enum converter is configured): for example
`OriginKind.ExternalQuery` is `2`, `DataClassification.Internal` is `1`, `WidgetKind.Chart` is `0`.

### Export and MCP

Datasets and dashboards are export sources: `datasets.rows` (under `datasets:preview`; a sensitive dataset exports its
schema only), `dashboards.grid` and `dashboards.report` (under `dashboards:view`). See
[data rights](../services/data-rights.md). Each module also has an MCP surface behind a feature flag that is off by
default (`datasets.mcp`, `dashboards.mcp`), served only while `Mcp:Server:Enabled` is on; see
[the MCP server](../ai/mcp-server.md).

## Configuration

| Key | Default | Effect |
| --- | --- | --- |
| `DemoContent:Enabled` | `false` | Seeds demo datasets (sales, customers, inventory), a "Demo operations" board and a cloud cost board on every site, once per site. |
| `Transport:SignalR:Enabled` | `false` | Maps the dashboards hub, which a board running in the browser (WebAssembly) needs for live refresh. An Interactive Server page refreshes live without it. |
| `CloudUsage:Alerts:Enabled` | `false` | The daily budget and spike alert job. |
| `CloudUsage:Alerts:AnomalyLookbackDays` | `7` | The rolling average's window. |
| `CloudUsage:Alerts:AnomalyThresholdPercent` | `20` | A day this far over the average is a spike. |
| `CostOptimization:Advisor` | section | The advisors' thresholds and the dismissal cooldown. |
| `CostOptimization:ModelTiers` | empty | Which model each request tier uses; empty leaves the caller's model. |
| `CostOptimization:ResponseCache:Enabled` / `TtlHours` | `true` / `24` | Caching of deterministic AI answers per site. |
| `CostOptimization:Cleanup:TempFiles:Enabled` | `false` | A daily sweep of the directories in `CostOptimization:Cleanup:TempFiles:Directories`. |
| `CostOptimization:Cleanup:DryRun` / `SafetyWindowDays` | `true` / `7` | The sweep only reports, and never touches files younger than the window. |

## Rules and pitfalls

- **The ports do not check permissions.** `IDatasetCatalog`, `IDatasetRegistrar` and `IFormatService` serve the scope's
  site to whoever calls them. Gate the endpoint that calls them with the module's keys.
- **A classification you assert is a floor.** Register as `Public` and a sample with an e-mail address comes back
  `Restricted`, masked, its rows refused everywhere. Remove or hash personal columns before registering if the rows
  must be shown.
- **The sample is capped at 1 MiB before parsing.** Serialize only the rows the sample needs (50 are kept); pass the
  full count as `RowCountEstimate`.
- **Same origin, same dataset.** Choose a stable `OriginReference` (for example `connection:<id>:orders`). A new
  reference under an existing name is refused.
- **A registration needs a site and a tenant.** In a background job, enter the site first (see [jobs](../services/jobs.md)).
- **A bound column must exist in the schema.** Adding a widget does not check it; reading its data does, so a typo or a
  refresh that drops a column shows on the board as "The widget's data was not loaded: Widget '...' binds column
  '...', which does not exist in dataset '...'".
- **Charts are theme-free.** A colour in the spec is refused ("A chart spec must be theme-free; ..."); buttons and
  charts take the theme's colours.
- **Live refresh in the browser needs the transport.** A board on an Interactive Server page redraws when a bound
  dataset is refreshed with no switch. A board running in the browser needs `Transport:SignalR:Enabled`; without it,
  its widgets update on their interval or when the page refreshes them.
- **Demo content is additive.** Turning `DemoContent:Enabled` on seeds each site once; deleting a demo item keeps it
  deleted.

## Related

- [Connectors and connections](../connectors/overview.md)
- [Using connections in code](../connectors/using-connections-in-code.md)
- [Data rights](../services/data-rights.md)
- [The MCP server](../ai/mcp-server.md)
- [Identity and permissions](../security/identity-and-permissions.md)
- Skill: [stomme-build-dataset-and-dashboard](../../.claude/skills/stomme-build-dataset-and-dashboard/SKILL.md)
