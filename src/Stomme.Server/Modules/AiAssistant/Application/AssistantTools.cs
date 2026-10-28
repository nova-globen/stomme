#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// The read-only workspace tools every agent may call (lifted from the predecessor platform's <c>AssistantQueryTools</c>). The descriptions
    /// are what the model reads; the cancellation token is supplied by the runtime and left out of the schema.
    /// </summary>
    internal sealed class AssistantQueryTools(IAssistantQueryGateway gateway)
    {
        [Description("Lists the data connections available to the user (id, name, connector type, status).")]
        public Task<IReadOnlyList<AssistantConnectionInfo>> ListConnections(CancellationToken cancellationToken = default) =>
            gateway.ListConnectionsAsync(cancellationToken);

        [Description("Lists the datasets available to the user (id, name, classification, estimated row count).")]
        public Task<IReadOnlyList<AssistantDatasetInfo>> ListDatasets(CancellationToken cancellationToken = default) =>
            gateway.ListDatasetsAsync(cancellationToken);

        [Description("Gets a dataset's column summary - metadata and statistics only, never raw rows - by its id.")]
        public Task<AssistantDataSummary?> GetDataSummary([Description("The dataset id (GUID).")] Guid datasetId, CancellationToken cancellationToken = default) =>
            gateway.GetDataSummaryAsync(datasetId, cancellationToken);

        public IReadOnlyList<AITool> AsTools() =>
        [
            AIFunctionFactory.Create(ListConnections),
            AIFunctionFactory.Create(ListDatasets),
            AIFunctionFactory.Create(GetDataSummary),
        ];
    }

    /// <summary>
    /// The Pipeline Architect's tools (the predecessor platform's <c>AssistantPipelineTools</c>): the step catalog, and
    /// <see cref="ProposePipeline"/>, which refuses a connection the user cannot reach BEFORE validating, then judges the draft
    /// the way the editor's save does. Only an authorized, valid draft is captured; anything else goes back to the model to
    /// fix, and is never staged.
    /// </summary>
    internal sealed class AssistantPipelineTools(IAssistantQueryGateway queryGateway, IAssistantCommandGateway commandGateway)
    {
        public AssistantPipelineDraft? ProposedDraft { get; private set; }

        [Description("Lists the pipeline step types this installation offers: each type's name, kind (Trigger, Action...), outcome ports and inputs. A sensitive input is never set by the assistant.")]
        public Task<IReadOnlyList<AssistantStepType>> ListStepTypes(CancellationToken cancellationToken = default) =>
            queryGateway.ListStepTypesAsync(cancellationToken);

        [Description(
            "Proposes a pipeline design once it is complete. Provide the pipeline name, an optional description, the ordered " +
            "steps (each with a step type from ListStepTypes, a name, an optional connection id from ListConnections, and " +
            "optional literal input values by input name), and the relations connecting steps by their zero-based index with " +
            "an optional outcome port. Returns an error to fix and call again if the draft references a connection the user " +
            "cannot access or fails validation.")]
        public async Task<string> ProposePipeline([Description("The proposed pipeline definition.")] AssistantPipelineDraft draft, CancellationToken cancellationToken = default)
        {
            ProposedDraft = null;
            if (draft is null)
            {
                return "No pipeline was given. Call ProposePipeline with the complete design.";
            }

            var unauthorized = await FindUnauthorizedConnectionsAsync(draft, cancellationToken);
            if (unauthorized.Count > 0)
            {
                return "The draft references connections you are not authorized to use: " + string.Join(", ", unauthorized) +
                    ". Use only the connections returned by ListConnections, then call ProposePipeline again.";
            }

            var validation = await commandGateway.ValidatePipelineAsync(draft, cancellationToken);
            if (!validation.IsValid)
            {
                return "The pipeline draft is invalid: " + string.Join("; ", validation.Errors) + ". Fix these and call ProposePipeline again.";
            }

            ProposedDraft = draft;
            return $"Pipeline '{draft.Name}' validated and proposed. It will be shown to the user to apply.";
        }

        public IReadOnlyList<AITool> AsTools() =>
        [
            AIFunctionFactory.Create(ListStepTypes),
            AIFunctionFactory.Create(ProposePipeline),
        ];

        /// <summary>A connection named by a step's id OR written into its <c>Connection</c> input is checked the same way.</summary>
        private async Task<IReadOnlyList<string>> FindUnauthorizedConnectionsAsync(AssistantPipelineDraft draft, CancellationToken cancellationToken)
        {
            var referenced = new List<string>();
            foreach (var step in draft.Steps ?? Array.Empty<AssistantPipelineStepDraft>())
            {
                if (step.ConnectionId is { } id)
                {
                    referenced.Add(id.ToString());
                }

                if (step.Inputs is not null && step.Inputs.TryGetValue(AssistantPipelineGraph.ConnectionInput, out var written) && !string.IsNullOrWhiteSpace(written))
                {
                    referenced.Add(written.Trim());
                }
            }

            if (referenced.Count == 0)
            {
                return Array.Empty<string>();
            }

            var authorized = (await queryGateway.ListConnectionsAsync(cancellationToken)).Select(connection => connection.Id).ToHashSet();
            return referenced
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(reference => !Guid.TryParse(reference, out var id) || !authorized.Contains(id))
                .ToList();
        }
    }

    /// <summary>
    /// The Dashboard Designer's tools (the predecessor platform's <c>AssistantDashboardTools</c>): the Widget Generator as ONE tool, and
    /// <see cref="ProposeDashboard"/>. <b>ADDED:</b> a widget bound to a dataset the user cannot see is refused the way the
    /// architect refuses a connection, and a widget kind the Dashboards module does not have is refused before the user
    /// meets it on apply.
    /// </summary>
    internal sealed class AssistantDashboardTools(IAssistantQueryGateway queryGateway, IWidgetSpecGenerator widgetGenerator)
    {
        private static readonly string[] Kinds = Enum.GetNames<Stomme.Dashboards.WidgetKind>();

        public AssistantDashboardDraft? ProposedDraft { get; private set; }

        [Description(
            "Generates a theme-free ECharts chart spec for a dataset from a short description (e.g. 'bar chart of revenue by " +
            "month'). Returns the spec as JSON to embed in a chart widget's chartSpecJson, or an error if the dataset is not " +
            "accessible or a spec could not be produced.")]
        public async Task<string> GenerateWidgetSpec(
            [Description("The dataset id (GUID) to chart.")] Guid datasetId,
            [Description("A short natural-language description of the chart to produce.")] string description,
            CancellationToken cancellationToken = default)
        {
            var summary = await queryGateway.GetDataSummaryAsync(datasetId, cancellationToken);
            if (summary is null)
            {
                return $"Dataset {datasetId} is not accessible; choose a dataset returned by ListDatasets.";
            }

            var result = await widgetGenerator.GenerateAsync(summary, description ?? string.Empty, cancellationToken);
            return result.Succeeded ? result.EchartsJson! : "Could not generate a chart spec: " + string.Join("; ", result.Errors);
        }

        [Description(
            "Proposes the dashboard once its grid and widgets are designed. Provide the dashboard name, the grid column count " +
            "and row height, and the widgets (each with a title, a kind - Chart, Grid, List or Kpi - the dataset id and columns " +
            "it binds, its grid position and span, and for a Chart widget the chartSpecJson from GenerateWidgetSpec). Returns an " +
            "error to fix and call again if a widget is not acceptable.")]
        public async Task<string> ProposeDashboard([Description("The proposed dashboard definition.")] AssistantDashboardDraft draft, CancellationToken cancellationToken = default)
        {
            ProposedDraft = null;
            if (draft is null)
            {
                return "No dashboard was given. Call ProposeDashboard with the complete design.";
            }

            var widgets = draft.Widgets ?? Array.Empty<AssistantWidgetDraft>();
            var errors = new List<string>();
            if (widgets.Count == 0)
            {
                errors.Add("A dashboard needs at least one widget.");
            }

            var visible = widgets.Count == 0 ? new HashSet<Guid>() : (await queryGateway.ListDatasetsAsync(cancellationToken)).Select(dataset => dataset.Id).ToHashSet();
            foreach (var widget in widgets)
            {
                if (!Kinds.Contains(widget.Kind, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Widget '{widget.Title}' has kind '{widget.Kind}'; use one of {string.Join(", ", Kinds)}.");
                }
                else if (string.Equals(widget.Kind, nameof(Stomme.Dashboards.WidgetKind.Chart), StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(widget.ChartSpecJson))
                {
                    errors.Add($"Chart widget '{widget.Title}' has no chart spec; call GenerateWidgetSpec first.");
                }

                if (!visible.Contains(widget.DatasetId))
                {
                    errors.Add($"Widget '{widget.Title}' binds dataset {widget.DatasetId}, which is not accessible; use a dataset returned by ListDatasets.");
                }
            }

            if (errors.Count > 0)
            {
                return "The dashboard draft is invalid: " + string.Join("; ", errors) + ". Fix these and call ProposeDashboard again.";
            }

            ProposedDraft = draft;
            return $"Dashboard '{draft.Name}' proposed. It will be shown to the user to apply.";
        }

        public IReadOnlyList<AITool> AsTools() =>
        [
            AIFunctionFactory.Create(GenerateWidgetSpec),
            AIFunctionFactory.Create(ProposeDashboard),
        ];
    }

    /// <summary>
    /// The Widget Generator's authoring tool (the predecessor platform's <c>WidgetSpecTools</c>): a submitted spec is checked (theme-free, every
    /// <c>$Column</c> in the dataset) and returned to the model with its problems; at most <see cref="MaxAttempts"/> submissions.
    /// </summary>
    internal sealed class WidgetSpecTools(AssistantDataSummary summary)
    {
        /// <summary>The first attempt plus two repairs.</summary>
        public const int MaxAttempts = 3;

        private readonly IReadOnlyList<string> _columnNames = summary.Columns.Select(column => column.Name).ToList();

        public string? AcceptedSpec { get; private set; }

        public IReadOnlyList<string> LastErrors { get; private set; } = Array.Empty<string>();

        public int Attempts { get; private set; }

        [Description(
            "Submits the chart as an ECharts option object in JSON. The spec must be theme-free (no color, backgroundColor, " +
            "fontFamily, fontSize or textStyle properties - the renderer applies the theme) and may reference dataset columns " +
            "only as $ColumnName using the columns provided. If it reports errors, fix them and call SubmitChartSpec again.")]
        public string SubmitChartSpec([Description("The ECharts option object as a JSON string.")] string echartsJson, CancellationToken cancellationToken = default)
        {
            if (Attempts >= MaxAttempts)
            {
                return "The maximum number of attempts is spent. Stop and do not resubmit.";
            }

            Attempts++;
            var errors = ChartSpecValidator.Validate(echartsJson, _columnNames);
            if (errors.Count == 0)
            {
                AcceptedSpec = echartsJson;
                LastErrors = Array.Empty<string>();
                return "The chart spec is valid and accepted.";
            }

            LastErrors = errors;
            var detail = string.Join("; ", errors);
            return Attempts >= MaxAttempts
                ? $"The chart spec is still invalid after the maximum attempts: {detail}. Stop and do not resubmit."
                : $"The chart spec is invalid: {detail}. Fix these and call SubmitChartSpec again.";
        }

        public IReadOnlyList<AITool> AsTools() => [AIFunctionFactory.Create(SubmitChartSpec)];
    }

    /// <summary>
    /// The orchestrator's one routing tool (the predecessor platform's <c>OrchestratorRoutingTools</c>): the model names the specialists, the
    /// code dispatches. Only known names are kept, in the order given, once each.
    /// </summary>
    internal sealed class OrchestratorRoutingTools(IReadOnlySet<string> knownSpecialists)
    {
        public List<string> SelectedSpecialists { get; } = new();

        public string? Rationale { get; private set; }

        [Description(
            "Routes the user's request to one or more specialist agents that should handle it. Choose one specialist for a " +
            "focused request, or several to fan out a combined request (for example a pipeline AND a dashboard). Call this " +
            "exactly once with the chosen specialist names.")]
        public string Route(
            [Description("The specialist agent names to invoke, in order. Use only names from the available specialists list.")] string[] specialists,
            [Description("A one-sentence rationale for this routing decision.")] string rationale)
        {
            Rationale = rationale;
            foreach (var candidate in specialists ?? Array.Empty<string>())
            {
                var name = candidate?.Trim();
                if (!string.IsNullOrEmpty(name) && knownSpecialists.Contains(name) && !SelectedSpecialists.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    SelectedSpecialists.Add(name);
                }
            }

            return SelectedSpecialists.Count > 0
                ? $"Routing to: {string.Join(", ", SelectedSpecialists)}."
                : "No matching specialist was named; the request will be handled as general chat.";
        }

        public IReadOnlyList<AITool> AsTools() => [AIFunctionFactory.Create(Route)];
    }

    /// <summary>
    /// the predecessor platform's <c>ChartSpecValidator</c>, verbatim in its rules: a generated spec must be theme-free (the renderer injects the
    /// theme) and every <c>$Column</c> it names must be in the dataset's summary. The Dashboards module refuses a themed spec
    /// again on apply; this check exists so the MODEL can repair before the user sees anything.
    /// </summary>
    internal static partial class ChartSpecValidator
    {
        private static readonly string[] ForbiddenThemeKeys = ["color", "colors", "backgroundColor", "fontFamily", "fontSize", "fontWeight", "textStyle"];

        public static IReadOnlyList<string> Validate(string? echartsJson, IReadOnlyList<string> columnNames)
        {
            if (string.IsNullOrWhiteSpace(echartsJson))
            {
                return ["The chart spec is empty."];
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(echartsJson);
            }
            catch (JsonException ex)
            {
                return [$"The chart spec is not valid JSON: {ex.Message}"];
            }

            var errors = new List<string>();
            using (document)
            {
                var forbidden = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectForbiddenKeys(document.RootElement, forbidden);
                errors.AddRange(forbidden.Select(key => $"The chart spec must be theme-free; remove the '{key}' property (the renderer applies the theme)."));
            }

            var known = columnNames.ToHashSet(StringComparer.Ordinal);
            foreach (Match match in ColumnReference().Matches(echartsJson))
            {
                var column = match.Groups[1].Value;
                if (!known.Contains(column))
                {
                    errors.Add($"Column reference ${column} is not in the dataset schema.");
                }
            }

            return errors.Distinct().ToList();
        }

        private static void CollectForbiddenKeys(JsonElement element, SortedSet<string> forbidden)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (Array.Exists(ForbiddenThemeKeys, key => string.Equals(key, property.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            forbidden.Add(property.Name);
                        }

                        CollectForbiddenKeys(property.Value, forbidden);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        CollectForbiddenKeys(item, forbidden);
                    }

                    break;
            }
        }

        [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)")]
        private static partial Regex ColumnReference();
    }

    /// <summary>The ONE serialized form of a draft: an agent writes it into the artifact, the applier reads it back (the predecessor platform's <c>AssistantDraftJson</c>).</summary>
    internal static class AssistantDraftJson
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

        public static string Serialize<T>(T draft) => JsonSerializer.Serialize(draft, Options);

        public static T Deserialize<T>(string specJson) =>
            JsonSerializer.Deserialize<T>(specJson, Options) ?? throw new InvalidOperationException("The artifact spec is empty or malformed.");
    }
}
