---
name: stomme-build-dataset-and-dashboard
description: Use when data must be shown as a dashboard or registered as a dataset - turning a file, a connection's records or module data into a Nova Globen Stomme dataset, building a board of charts, grids, KPIs or pivots over it, or when asked for "a dashboard", "a report page", "charts of X", "a dataset from Y" or "show this data to administrators".
---

# Build a dataset and a dashboard

You will have a dataset on the site, registered either from an uploaded file or from your module's code (here: records
read through a connection), and a published dashboard whose widgets are bound to it. The dataset carries an inferred
schema and a classification that decides whether its rows may be shown at all. Read
[datasets, dashboards, formats and cloud cost](../../../docs/data/datasets-dashboards-formats.md) for how they work, and
[using connections in code](../../../docs/connectors/using-connections-in-code.md) for the extraction port.

Use the built-in modules. Do not build your own chart page or table store for this.

## Steps

### A. A dataset from a file (no code)

1. Upload the CSV, JSON (an array of objects) or XLSX file through the file manager (or the file picker on the
   Datasets page).
2. Open `/admin/datasets` as an administrator. Under **Register a dataset from a file** choose the folder and the file,
   give a name (it defaults to the file's) and a classification, and press **Register from file**. The row count is
   the whole file's; the first 50 rows are the preview sample. The origin is `file:<file id>`: registering the same file
   again (after uploading a new version of it) refreshes the same dataset.
3. Check the classification the list shows. It may be higher than the one you chose (see step C.3).

Over the API: `POST api/Datasets/upload` with `{ "fileId": 2, "name": "Sales by region", "classification": 1 }`
(the classification as its number, see Common mistakes).

### B. A dataset from code

1. **Write the producer.** It reads records through a connection and registers them as a dataset. Create
   `Server/Orders/OrdersDataset.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Linq;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Connectivity;
   using Stomme.Datasets;

   namespace Stomme.Application.Server.Orders
   {
       /// <summary>Registers (or refreshes) the "Acme orders" dataset from a connection's records.</summary>
       public sealed class OrdersDataset(IConnectorExtractionService extraction, IDatasetRegistrar registrar)
       {
           public const string Name = "Acme orders";

           public async Task<Guid> RefreshAsync(Guid connectionId, CancellationToken ct = default)
           {
               var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
               {
                   ["path"] = "/v1/orders",
                   ["records_path"] = "orders",
               };

               var result = await extraction.ExtractAsync(connectionId, "read", parameters, 10_000, ct);
               if (!result.Success)
               {
                   throw new InvalidOperationException($"The orders could not be read: {result.Message}");
               }

               // The sample is capped at 1 MiB and only the first rows are kept, so send those and give the full count.
               var sample = ConnectorRecordJson.Serialize(result.Records.Take(DatasetLimits.MaxSampleRows).ToList());
               var registration = new DatasetRegistration(
                   Name,
                   OriginKind.ExternalQuery,
                   $"connection:{connectionId:N}:orders",
                   sample,
                   DataClassification.Internal,
                   result.Records.Count);

               return await registrar.RegisterOrRefreshAsync(registration, ct);
           }
       }
   }
   ```

   The `OriginReference` is stable, so every refresh updates the same dataset, and widgets bound to it keep working.

2. **Gate an endpoint that runs it.** Create `Server/Orders/OrdersDatasetController.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Connectivity;
   using Stomme.Controllers;
   using Stomme.Datasets;
   using Stomme.Infrastructure;
   using Stomme.Infrastructure.Security;
   using Stomme.Shared;
   using Microsoft.AspNetCore.Http;
   using Microsoft.AspNetCore.Mvc;

   namespace Stomme.Application.Server.Orders
   {
       [Route(ControllerRoutes.ApiRoute)]
       public sealed class OrdersDatasetController : ModuleControllerBase
       {
           private readonly OrdersDataset _dataset;

           public OrdersDatasetController(OrdersDataset dataset, ILogManager logger, IHttpContextAccessor accessor)
               : base(logger, accessor)
           {
               _dataset = dataset;
           }

           // POST api/OrdersDataset/{connectionId}: register or refresh the dataset; answers its id.
           [HttpPost("{connectionId:guid}")]
           [RequiresPermissionKey(DatasetPermissions.Manage)]
           [RequiresPermissionKey(ConnectionPermissions.View)]
           public async Task<IActionResult> Refresh(Guid connectionId, CancellationToken ct)
           {
               try
               {
                   return Ok(new { datasetId = await _dataset.RefreshAsync(connectionId, ct) });
               }
               catch (InvalidOperationException ex)
               {
                   return BadRequest(new { error = ex.Message });
               }
           }
       }
   }
   ```

   `IDatasetRegistrar` checks no permission; the endpoint holds both keys. The caller must send the antiforgery token
   with the POST, as every write from a browser must (see
   [API and client services](../../../docs/modules/api-and-client-services.md)).

3. **Register the producer.** Add `services.AddScoped<OrdersDataset>();` to your module's
   `IServerStartup.ConfigureServices`, or create `Server/Orders/OrdersServerStartup.cs`:

   ```csharp
   using Stomme.Infrastructure;
   using Microsoft.AspNetCore.Builder;
   using Microsoft.AspNetCore.Hosting;
   using Microsoft.Extensions.DependencyInjection;

   namespace Stomme.Application.Server.Orders
   {
       public class OrdersServerStartup : IServerStartup
       {
           public void ConfigureServices(IServiceCollection services)
           {
               services.AddScoped<OrdersDataset>();
           }

           public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
           {
           }

           public void ConfigureMvc(IMvcBuilder mvcBuilder)
           {
           }
       }
   }
   ```

4. **To refresh on a schedule,** run `OrdersDataset.RefreshAsync` from a background job, inside a scope where the site's
   alias is set (`ITenantManager.SetAliasAsync(tenantId, siteId)`): without it the connection is not found (the
   producer above throws "The orders could not be read: The connection was not found."), and the registrar itself needs
   a site and a tenant. See the [stomme-add-background-job](../stomme-add-background-job/SKILL.md) skill.

### C. The dashboard

1. Open `/admin/dashboards`, type a name under **New dashboard** and press **Create**. The board starts as a draft
   with 12 columns and 40-pixel rows; **Open** it, and change the grid in the builder's **Columns** and **Row height
   (px)** with **Apply layout** if you need to.
2. Add widgets in the builder's **Add widget** form: a title, the kind (Kpi is preselected), the dataset (the picker
   shows each with its classification), the bound columns (comma-separated), the position (Column, Row, Width,
   Height) and the refresh. Widgets may not overlap. Per kind:
   - **Kpi**: the dataset's row count. No column is required.
   - **Grid**: the rows. Bind the columns to show.
   - **Chart**: bind the columns the chart uses and give an ECharts spec (the spec field appears when the kind is
     Chart) with `"$column"` tokens in a `dataset.source`, no colours or fonts:

     ```json
     { "dataset": { "source": { "status": "$status", "total": "$total" } },
       "xAxis": { "type": "category" }, "yAxis": { "type": "value" },
       "series": [ { "type": "bar", "encode": { "x": "status", "y": "total" } } ] }
     ```
   - **Pivot**: the first bound column groups (a count per group), the second is summed when numeric.
   - **List**: a summary per bound column.
   Choose the refresh: `Manual`, `Interval` (at least 30 seconds; less is refused) or `EventDriven`. An open board on an
   Interactive Server page shows a **live** badge and re-reads every widget bound to a dataset as soon as that dataset
   is refreshed, whatever the widget's refresh kind. A board running in the browser (WebAssembly) gets those updates
   over the dashboards hub, which needs `Transport:SignalR:Enabled`; without it, it refreshes on intervals or by
   **Refresh widgets**.
3. **Check the classification before you expect rows.** A dataset whose sample holds e-mail addresses, US social
   security numbers or card numbers is `Restricted`; one with a column named like a secret (password, token, api key)
   is `Confidential`. Both show a masked summary and **no rows** in any widget, preview, export or MCP answer (a Grid or
   Pivot widget says "Row-level data is unavailable for Restricted data."; a Chart draws nothing). If the rows must be shown, leave those columns out of the records
   before registering. Never try to lower a classification; it only escalates.
4. Publish the board (**Publish** in the list, or `POST api/Dashboards/{id}/publish`).

## Verify

1. `dotnet build` is clean. Run the app, sign in as an administrator.
2. Call `POST api/OrdersDataset/<connection id>` from the signed-in session (or run the job). It answers a dataset id.
   Call it again: the **same** id, and `/admin/datasets` still lists one "Acme orders".
3. On `/admin/datasets` the dataset shows the full row count, the inferred column types and the classification you
   expected. **Preview** shows at most 50 rows for an `Internal` dataset and a refusal for a sensitive one.
4. On `/admin/dashboards` open the board: every widget draws, the chart shows bars, the grid shows rows, the KPI the
   row count. A widget bound to a sensitive dataset shows no rows: a Grid or Pivot says "Row-level data is unavailable
   for ... data.", a Chart draws nothing, a KPI still shows the row count and a List the masked summary.
   Switch the site to dark mode: the chart takes the theme's colours.
5. With the board open, refresh the dataset (call the endpoint again, or re-register the file after uploading a new
   version). On an Interactive Server page the widgets redraw with the new rows by themselves; otherwise press
   **Refresh widgets**.
6. Sign in as a user without `datasets:manage` and call the endpoint: 403, nothing registered. A user without
   `dashboards:view` gets 403 from `api/Dashboards` and cannot open the board.
7. The browser console is clean on both pages.

## Common mistakes

- **"A dataset named ... already exists for another origin".** The cause: the name is taken by a dataset with a
  different `OriginReference` (a changed reference, or an upload under that name). The fix: keep the reference stable,
  or choose another name.
- **The widget shows the count but no rows, and the preview is refused.** The cause: the classifier made the dataset
  `Confidential` or `Restricted`. The fix: register without the personal or secret-named columns.
- **From a job: "The connection was not found", or the registration throws "none is resolved".** The cause: no site
  or tenant in the scope. The fix: set the site's alias first.
- **The registration is refused before anything is read.** The cause: the sample JSON is over 1 MiB. The fix: send
  only the first `DatasetLimits.MaxSampleRows` records and pass the full count as `RowCountEstimate`.
- **The chart draws only axes.** The cause: the `"$column"` tokens are inside `encode`, which takes dimension names.
  The fix: put the tokens in `dataset.source` and name them in `encode`.
- **"A chart spec must be theme-free".** The cause: a `color` or font property in the spec. The fix: remove it; the
  renderer applies the theme.
- **The widget says "The widget's data was not loaded: Widget '...' binds column '...', which does not exist in
  dataset '...'".** The cause: a bound column is not in the dataset's schema - a typo (adding the widget does not
  check the columns; reading its data does), or a refresh that dropped the column. The fix: bind columns the schema
  has (the Schema button on `/admin/datasets`), and keep the producer's columns stable.
- **An API call with `"kind": "Chart"` is refused (400) - on Stomme before 10.73.2.** Enumerations were read as numbers
  only; since 10.73.2 a name works too (responses still carry the numbers). On an older version send `0` for `Chart`, `1`
  for `Grid`, `2` for `List`, `3` for `Kpi`, `4` for `Pivot`; classifications
  `0` Public, `1` Internal, `2` Confidential, `3` Restricted; refresh `0` Manual, `1` Interval, `2` EventDriven.
- **A dataset cannot be deleted: "Dataset '...' is referenced by: widget '...' on dashboard '...'".** The cause: a
  widget is bound to it. The fix: remove those widgets first.
