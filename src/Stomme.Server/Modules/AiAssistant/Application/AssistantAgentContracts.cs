#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// An assistant agent: a specialist, or the orchestrator that routes between them. It takes the
    /// conversation so far and returns a reply plus any artifacts it PROPOSES - it never applies one.
    /// </summary>
    public interface IAssistantAgent
    {
        /// <summary>The agent's stable name - the orchestrator's route, and the <c>agent</c> of every model call it meters.</summary>
        string Name { get; }

        Task<AssistantReply> InvokeAsync(AssistantRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A specialist the orchestrator can route to. A specialist describes itself - an orchestrator holding a
    /// switch of the names it knew would have to be edited for every new specialist (the landing page's, say).
    /// </summary>
    public interface IAssistantSpecialist : IAssistantAgent
    {
        /// <summary>One line the routing model reads to choose it.</summary>
        string Description { get; }
    }

    /// <summary>
    /// The Widget Generator, called by the Dashboard Designer as ONE tool (agent-as-tool): a dataset's bounded summary and a
    /// request in, a theme-free ECharts spec whose column references resolve out - or the reasons it could not.
    /// </summary>
    public interface IWidgetSpecGenerator
    {
        Task<WidgetSpecResult> GenerateAsync(AssistantDataSummary summary, string request, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A conversation or a proposal in a state that refuses the act - closed, full, not proposed, not this kind - or a proposal
    /// its owning module refused on apply. The message is written to be shown; nothing was left half-done.
    /// </summary>
    public sealed class AssistantConflictException : InvalidOperationException, Stomme.Mediator.IExpectedRefusal
    {
        public AssistantConflictException(string message)
            : base(message)
        {
        }

        public AssistantConflictException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed record WidgetSpecResult(bool Succeeded, string? EchartsJson, IReadOnlyList<string> Errors)
    {
        public static WidgetSpecResult Ok(string echartsJson) => new(true, echartsJson, Array.Empty<string>());

        public static WidgetSpecResult Failed(IReadOnlyList<string> errors) => new(false, null, errors);
    }

    /// <summary>
    /// What the agents may READ of the user's workspace. A no-op gateway would leave
    /// the agents seeing nothing; here every read is the owning module's own <c>[Requires]</c>-gated query dispatched through
    /// the mediator AS THE USER, so an agent sees exactly what its user may see. A refused query answers as nothing visible.
    /// </summary>
    public interface IAssistantQueryGateway
    {
        Task<IReadOnlyList<AssistantConnectionInfo>> ListConnectionsAsync(CancellationToken cancellationToken = default);

        Task<IReadOnlyList<AssistantDatasetInfo>> ListDatasetsAsync(CancellationToken cancellationToken = default);

        /// <summary>A dataset's column summary - never rows; masked by the Datasets module for a sensitive dataset - or null.</summary>
        Task<AssistantDataSummary?> GetDataSummaryAsync(Guid datasetId, CancellationToken cancellationToken = default);

        /// <summary>The pipeline step types the editor offers (the engine's catalog).</summary>
        Task<IReadOnlyList<AssistantStepType>> ListStepTypesAsync(CancellationToken cancellationToken = default);

        /// <summary>This site's PUBLISHED help topics matching a text, with their content as plain text; drafts never.</summary>
        Task<IReadOnlyList<AssistantHelpTopic>> SearchHelpAsync(string query, int limit, CancellationToken cancellationToken = default);
    }

    public sealed record AssistantConnectionInfo(Guid Id, string Name, string ConnectorType, string Status);

    public sealed record AssistantDatasetInfo(Guid Id, string Name, string Classification, int RowCountEstimate);

    public sealed record AssistantDataSummary(Guid DatasetId, IReadOnlyList<AssistantColumnSummary> Columns, int RowCountEstimate);

    public sealed record AssistantColumnSummary(string Name, IReadOnlyList<string> Samples, string? Min, string? Max, int UniqueCount, int NullCount);

    /// <summary>A step type as the architect sees it: what it is, its outcome ports, the inputs it takes.</summary>
    public sealed record AssistantStepType(string Type, string DisplayName, string Category, string Kind, string? Description, IReadOnlyList<string> Ports, IReadOnlyList<AssistantStepInput> Inputs);

    /// <summary>A step input; a SENSITIVE one (a password, a token) is never filled by the assistant - its user enters it.</summary>
    public sealed record AssistantStepInput(string Name, string TypeName, string? Description, bool IsSensitive, IReadOnlyList<string>? Options);

    public sealed record AssistantHelpTopic(string Anchor, string Title, string Text);

    /// <summary>
    /// How a proposal is judged and, on the user's explicit apply, written: through the owning module's own
    /// <c>[Requires]</c>-gated commands, dispatched AS THE USER - so an applied artifact carries exactly the user's authority,
    /// the module's invariants, and nothing an agent could add.
    /// </summary>
    public interface IAssistantCommandGateway
    {
        /// <summary>Judges a pipeline draft the way the editor's save does, writing nothing.</summary>
        Task<AssistantPipelineValidationResult> ValidatePipelineAsync(AssistantPipelineDraft draft, CancellationToken cancellationToken = default);

        /// <summary>Creates the pipeline and saves its graph; returns the definition id. Nothing is left behind on a failure.</summary>
        Task<string> CreatePipelineAsync(AssistantPipelineDraft draft, CancellationToken cancellationToken = default);

        /// <summary>Creates the board and its widgets; returns the board's id. Nothing is left behind on a failure.</summary>
        Task<Guid> CreateDashboardAsync(AssistantDashboardDraft draft, CancellationToken cancellationToken = default);
    }

    /// <summary>A pipeline an agent proposes, in the assistant's own shape; the gateway maps it onto the editor's graph.</summary>
    public sealed record AssistantPipelineDraft(string Name, string? Description, IReadOnlyList<AssistantPipelineStepDraft> Steps, IReadOnlyList<AssistantPipelineRelationDraft> Relations);

    /// <summary>
    /// One step. <see cref="Type"/> is a step type from the engine's catalog (not one of a few coarse
    /// kinds), <see cref="Inputs"/> are literal input values by input name, and <see cref="ConnectionId"/> fills the step's
    /// <c>Connection</c> input - only with a connection the user can reach.
    /// </summary>
    public sealed record AssistantPipelineStepDraft(string Type, string Name, Guid? ConnectionId = null, IReadOnlyDictionary<string, string>? Inputs = null);

    /// <summary>An edge by step index. Its condition is the engine's outcome <see cref="Port"/>, not free text.</summary>
    public sealed record AssistantPipelineRelationDraft(int SourceStepIndex, int TargetStepIndex, string? Port = null);

    public sealed record AssistantPipelineValidationResult(bool IsValid, IReadOnlyList<string> Errors)
    {
        public static AssistantPipelineValidationResult Valid { get; } = new(true, Array.Empty<string>());

        public static AssistantPipelineValidationResult Invalid(IReadOnlyList<string> errors) => new(false, errors);
    }

    /// <summary>A board an agent proposes; the gateway maps it onto the Dashboards module's create and add-widget commands.</summary>
    public sealed record AssistantDashboardDraft(string Name, int Columns, int RowHeightPx, IReadOnlyList<AssistantWidgetDraft> Widgets);

    /// <summary>One widget: its kind (Chart, Grid, List or Kpi), its binding, its place, and a chart's theme-free ECharts spec.</summary>
    public sealed record AssistantWidgetDraft(string Title, string Kind, Guid DatasetId, IReadOnlyList<string> Columns, int Column, int Row, int ColumnSpan, int RowSpan, string? ChartSpecJson = null);
}
