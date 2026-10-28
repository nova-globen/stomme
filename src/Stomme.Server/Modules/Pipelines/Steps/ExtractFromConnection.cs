#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Tenancy;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// Records from one of the site's CONNECTIONS, through its connector (BL-95's residue "a Connection with a vaulted secret",
    /// closed by the extraction port D110 designed for exactly this step): the connection id, an operation and its parameters go
    /// to <see cref="IConnectorExtractionService"/>, which resolves the connection through the run's site, its connector, the
    /// credentials from the site's secret store and the HOST's egress, and answers the records up to the cap. The step never
    /// sees a credential or the aggregate - the author names a connection, not a password - and its result is the records as
    /// the JSON array the other data steps chain on.
    /// </summary>
    /// <remarks>
    /// A refused extraction (no such connection on the site, no connector, missing credentials, a denied egress, a remote
    /// failure) is the port's CATEGORY - <c>EXTRACTION_&lt;OUTCOME&gt;</c> with the port's own words, never the connector's -
    /// honouring <c>On failure</c>. A truncated extract succeeds with <see cref="Truncated"/> set and a log line saying so. The
    /// keys that decide are the pipeline's (authoring and running it); which connections exist is the site's.
    /// </remarks>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Extract records from one of the site's connections through its connector - a database's (PostgreSQL, SQL Server: a SQL query, or an insert, update or delete that returns rows, its values bound), an API's, a directory's; the credentials stay in the site's secret store.", DisplayName = "Extract from connection")]
    public class ExtractFromConnection : DataStep
    {
        /// <summary>The HTTP connectors' per-request headers parameter: lines of <c>Name: value</c>.</summary>
        public const string HeadersParameter = "headers";

        /// <summary>A connector's filters parameter - Supabase's, HubSpot's: one filter a line.</summary>
        public const string FiltersParameter = "filters";

        [Input(DisplayName = "Connection", UIHint = PipelineInputHints.ConnectionPicker, Description = "The id of one of the site's connections (Admin > Connections).", Order = 1)]
        public Input<string>? Connection { get; set; }

        [Input(UIHint = PipelineInputHints.ConnectorOperation, Description = "The connector's operation. Empty: the connector's default.", Order = 2)]
        public Input<string?>? Operation { get; set; }

        [Input(UIHint = PipelineInputHints.SqlQuery, Description = "A SQL connection's statement, as many lines as it takes (n8n's Execute query): a SELECT, or a write that returns rows (RETURNING, OUTPUT). Write each value as @name and give it in Parameters: bound, never spliced into the SQL. An SSH connection's command, as many lines as it takes (n8n's Execute); a RabbitMQ message over several lines. Empty: a 'query' line in Parameters, if any.", Order = 3)]
        public Input<string?>? Query { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "The operation's parameters, one per line: name = value - 'headers' and 'filters' once a header or a filter, as many as it takes. A SQL statement's values as '@name = value' lines, each written @name in the query: bound, never spliced into the SQL.", Order = 4)]
        public Input<string?>? Parameters { get; set; }

        [Input(DisplayName = "Maximum records", Description = "At most this many records are read (1 to 100,000); the rest are left unread and the step says so.", Order = 5)]
        public Input<int> MaxRecords { get; set; } = new(new Literal<int>(ConnectorExtractionLimits.DefaultMaxRecords));

        [Output(DisplayName = "Record count")]
        public Output<int>? RecordCount { get; set; }

        [Output(Description = "True when the cap was reached and records were left unread.")]
        public Output<bool>? Truncated { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var connection = Required(context, Connection, "Connection");
            if (!Guid.TryParse(connection.Trim(), out var connectionId))
            {
                await FailAsync(context, "CONNECTION_INVALID", $"'{connection}' is not a connection id.").ConfigureAwait(false);
                return;
            }

            // Each value rendered on its own (TemplatedLines): a value from the run never becomes another parameter - a second
            // 'query =' cannot replace the statement the author wrote.
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in await TemplatedLines.ReadAsync(Parameters, context, '=').ConfigureAwait(false))
            {
                if (line.Name is null)
                {
                    await FailAsync(context, "PARAMETER_INVALID", $"'{line.Line}' is not 'name = value'.").ConfigureAwait(false);
                    return;
                }

                // The HTTP connectors' 'headers' is lines of 'Name: value', and a connector's 'filters' lines of filters, that the
                // connector splits: one per 'headers =' or 'filters =' line, collected in order, and none holding a line break - a
                // value from the run must not add a header or a filter of its own.
                var lines = string.Equals(line.Name, HeadersParameter, StringComparison.OrdinalIgnoreCase) ? (Key: HeadersParameter, What: "header")
                    : string.Equals(line.Name, FiltersParameter, StringComparison.OrdinalIgnoreCase) ? (Key: FiltersParameter, What: "filter")
                    : default;
                if (lines.Key is not null)
                {
                    if (TemplatedLines.BreaksLine(line.Value))
                    {
                        await FailAsync(context, "PARAMETER_INVALID", $"'{line.Line}' gives a {lines.What} holding a line break; a {lines.What} is one line - one per '{lines.Key} =' line.").ConfigureAwait(false);
                        return;
                    }

                    parameters[lines.Key] = parameters.TryGetValue(lines.Key, out var earlier) ? earlier + "\n" + line.Value : line.Value;
                    continue;
                }

                parameters[line.Name] = line.Value;
            }

            // The statement in its own box (a parameter line holds one line; SQL seldom does).
            if (Query?.GetOrDefault(context) is { } query && !string.IsNullOrWhiteSpace(query))
            {
                if (parameters.ContainsKey("query"))
                {
                    await FailAsync(context, "PARAMETER_INVALID", "The query is given twice: in Query and as a 'query' parameter.").ConfigureAwait(false);
                    return;
                }

                parameters["query"] = query.Trim();
            }

            if (!await StommeSiteScope.EnsureSiteAsync(context).ConfigureAwait(false))
            {
                await FailAsync(context, "SITE_UNAVAILABLE", "The run has no site whose connections it could read.").ConfigureAwait(false);
                return;
            }

            var extraction = context.GetService<IConnectorExtractionService>();
            if (extraction is null)
            {
                await FailAsync(context, "CONNECTIVITY_UNAVAILABLE", "The Connectivity module is not composed in this host.").ConfigureAwait(false);
                return;
            }

            var operation = Operation?.GetOrDefault(context);
            var cap = ConnectorExtractionLimits.Clamp(MaxRecords.GetOrDefault(context));
            // The run's folder as the connector's files (BL-229): an upload reads from it, a download writes into it.
            var result = await extraction.ExtractAsync(connectionId, string.IsNullOrWhiteSpace(operation) ? null : operation.Trim(), parameters, cap, PipelineConnectorFiles.For(context), context.CancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                await FailAsync(context, "EXTRACTION_" + result.Outcome.ToString().ToUpperInvariant(), result.Message).ConfigureAwait(false);
                return;
            }

            context.Set(RecordCount, result.Records.Count);
            context.Set(Truncated, result.Truncated);
            if (result.Truncated)
            {
                context.AddExecutionLogEntry("Truncated", result.Message);
            }

            await SucceedAsync(context, ConnectorRecordJson.Serialize(result.Records)).ConfigureAwait(false);
        }
    }
}
