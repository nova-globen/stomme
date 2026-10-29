#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Stomme.Export;
using Stomme.Modules.DataExport.Sources;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Pipelines;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// One pipeline run's execution log as an export (re-booked from BL-85; the predecessor platform had no such source): key
    /// <c>pipelines.run-log</c>, arguments <c>{"runId": "..."}</c>, one table - sequence, time, step, step type, event,
    /// message - every line of the run, in order, under <c>pipelines:view</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It enters the engine's tenant itself.</b> A request reaches the engine's stores through
    /// <c>EngineTenantMiddleware</c>, which pushes the site's engine tenant; an export runs in the export JOB, which has no
    /// request, only the site the job resolved (<c>SiteState.Alias</c>, set per site with its cache). So every read here -
    /// the run and each page of its log - runs inside <see cref="IEngineTenantBridge.Enter"/> for that site's engine tenant,
    /// and a run of another site does not exist for it.
    /// </para>
    /// <para>
    /// The log is read a page at a time, by sequence, while the renderer pulls rows - never the whole log in memory; the
    /// page's own <c>GetRunLogQuery</c> keeps its 1,000-line window, which a file of the run does not.
    /// </para>
    /// </remarks>
    public sealed class PipelineRunLogExportSource(IEngineTenantBridge bridge, IPipelineInstanceStore instances, IPipelineExecutionLogStore log) : IExportSource
    {
        public const string SourceKey = "pipelines.run-log";
        public const int PageSize = 500;

        private static readonly string[] Members = { "runId" };

        private static readonly ExportColumn[] ColumnSet =
        {
            new("Sequence", ExportValueKind.Integer),
            new("Time (UTC)", ExportValueKind.DateTimeUtc),
            new("Step", ExportValueKind.Text),
            new("Step type", ExportValueKind.Text),
            new("Event", ExportValueKind.Text),
            new("Message", ExportValueKind.Text),
        };

        public string Key => SourceKey;

        public string RequiredPermission => PipelinesPermissions.View;

        public TableCardinality TableCardinality => TableCardinality.Single;

        public string? Validate(ExportSourceContext context) => TryParse(context?.ArgumentsJson, out _, out var error) ? null : error;

        public async Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryParse(context.ArgumentsJson, out var runId, out var error))
            {
                throw new InvalidOperationException(error);
            }

            var tenant = await bridge.ResolveCurrentSiteAsync(ct)
                ?? throw new InvalidOperationException("A pipeline run belongs to a site; none is resolved.");
            using (bridge.Enter(tenant))
            {
                if (await instances.FindAsync(new PipelineInstanceFilter { Id = runId }, ct) is null)
                {
                    throw new InvalidOperationException($"Pipeline run '{runId}' does not exist.");
                }
            }

            return new RunLogDocument(bridge, tenant, log, runId);
        }

        internal static bool TryParse(string? json, out string runId, out string error)
        {
            runId = string.Empty;
            if (!ExportArguments.TryReadObject(json, Members, out var members, out error))
            {
                return false;
            }

            if (!members.TryGetValue("runId", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                error = "The arguments need a 'runId'.";
                return false;
            }

            runId = value.GetString()!.Trim();
            return true;
        }

        private sealed class RunLogDocument(IEngineTenantBridge bridge, Tenant tenant, IPipelineExecutionLogStore log, string runId) : ITabularDocument, ITabularTable
        {
            public string Name => "Run log";

            public IReadOnlyList<ExportColumn> Columns => ColumnSet;

            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return this;
                await Task.CompletedTask;
            }

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                for (var offset = 0; ; offset += PageSize)
                {
                    ct.ThrowIfCancellationRequested();

                    ICollection<PipelineExecutionLogRecord> page;
                    using (bridge.Enter(tenant))
                    {
                        page = (await log.FindManyAsync(
                            new PipelineExecutionLogRecordFilter { PipelineInstanceId = runId },
                            PageArgs.FromRange(offset, PageSize),
                            new PipelineExecutionLogRecordOrder<long>(x => x.Sequence, OrderDirection.Ascending), ct)).Items;
                    }

                    foreach (var record in page)
                    {
                        yield return new object?[] { record.Sequence, record.Timestamp.UtcDateTime, record.StepName, record.StepType, record.EventName, record.Message };
                    }

                    if (page.Count < PageSize)
                    {
                        yield break;
                    }
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
