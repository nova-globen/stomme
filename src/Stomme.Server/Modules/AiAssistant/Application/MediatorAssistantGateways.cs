#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.Help;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.ContextualHelp.Application;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Pipelines.Application;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// The agents' reads (BL-97 increment 10, D113): each is the owning module's own <c>[Requires]</c>-gated query, sent
    /// through the mediator AS THE USER - so an agent sees exactly what its user may. A query the user may not run answers as
    /// nothing visible (an empty list, no summary), never as an error the model could read something into.
    /// </summary>
    public sealed partial class MediatorAssistantQueryGateway(IMediator mediator, ILogger<MediatorAssistantQueryGateway> logger) : IAssistantQueryGateway
    {
        /// <summary>A topic's text is bounded before it reaches a prompt.</summary>
        public const int MaxTopicTextLength = 4000;

        /// <summary>At most this many terms of a question are searched.</summary>
        public const int MaxSearchTerms = 8;

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "are", "but", "not", "you", "your", "all", "any", "can", "how", "what", "when", "where", "which",
            "who", "why", "with", "this", "that", "from", "into", "does", "have", "has", "was", "were", "will", "would", "should",
            "could", "there", "their", "about", "then", "than", "them", "they", "its", "our", "use", "using",
        };

        public async Task<IReadOnlyList<AssistantConnectionInfo>> ListConnectionsAsync(CancellationToken cancellationToken = default) =>
            (await OrNothingAsync(() => mediator.Send(new ListConnectionsQuery(), cancellationToken), (IReadOnlyList<ConnectionView>)Array.Empty<ConnectionView>()))
                .Select(connection => new AssistantConnectionInfo(connection.Id, connection.Name, connection.ConnectorType, connection.Status))
                .ToList();

        public async Task<IReadOnlyList<AssistantDatasetInfo>> ListDatasetsAsync(CancellationToken cancellationToken = default) =>
            (await OrNothingAsync(() => mediator.Send(new ListDatasetsQuery(), cancellationToken), (IReadOnlyList<DatasetDescriptor>)Array.Empty<DatasetDescriptor>()))
                .Select(dataset => new AssistantDatasetInfo(dataset.Id, dataset.Name, dataset.Classification.ToString(), dataset.Summary.RowCountEstimate))
                .ToList();

        public async Task<AssistantDataSummary?> GetDataSummaryAsync(Guid datasetId, CancellationToken cancellationToken = default)
        {
            // The Datasets module has already masked a sensitive dataset's summary (no samples, no min, no max).
            var dataset = await OrNothingAsync(() => mediator.Send(new GetDatasetQuery(datasetId), cancellationToken), null);
            return dataset is null
                ? null
                : new AssistantDataSummary(
                    dataset.Id,
                    dataset.Summary.Columns.Select(column => new AssistantColumnSummary(column.Name, column.Samples, column.Min, column.Max, column.UniqueCount, column.NullCount)).ToList(),
                    dataset.Summary.RowCountEstimate);
        }

        public async Task<IReadOnlyList<AssistantStepType>> ListStepTypesAsync(CancellationToken cancellationToken = default) =>
            (await OrNothingAsync(() => mediator.Send(new ListStepsQuery(), cancellationToken), (IReadOnlyList<StepCatalogEntry>)Array.Empty<StepCatalogEntry>()))
                .Select(ToStepType)
                .ToList();

        public async Task<IReadOnlyList<AssistantHelpTopic>> SearchHelpAsync(string query, int limit, CancellationToken cancellationToken = default)
        {
            // The Help module matches ONE phrase; a question is searched term by term and its topics ranked by the terms hit.
            var hits = new Dictionary<(string Anchor, string Locale), (HelpTopicSummary Topic, int Count)>();
            foreach (var term in Terms(query))
            {
                var found = await OrNothingAsync(() => mediator.Send(new ListPublishedHelpTopicsQuery(term), cancellationToken), (IReadOnlyList<HelpTopicSummary>)Array.Empty<HelpTopicSummary>());
                foreach (var topic in found.Where(topic => topic.IsPublished))
                {
                    var key = (topic.Anchor, topic.Locale);
                    hits[key] = hits.TryGetValue(key, out var hit) ? (hit.Topic, hit.Count + 1) : (topic, 1);
                }
            }

            var topics = new List<AssistantHelpTopic>();
            foreach (var (topic, _) in hits.Values.OrderByDescending(hit => hit.Count).ThenBy(hit => hit.Topic.Anchor, StringComparer.Ordinal).Take(Math.Max(0, limit)))
            {
                var view = await OrNothingAsync(() => mediator.Send(new ResolveHelpTopicQuery(topic.Anchor, topic.Locale), cancellationToken), null);
                if (view is not null)
                {
                    topics.Add(new AssistantHelpTopic(view.Anchor, view.Title, PlainText(view.SafeHtml)));
                }
            }

            return topics;
        }

        /// <summary>The distinct words of a question worth searching: three letters or more, not a stop word, at most eight.</summary>
        public static IReadOnlyList<string> Terms(string? query) =>
            Word().Matches(query ?? string.Empty)
                .Select(match => match.Value.ToLowerInvariant())
                .Where(word => word.Length >= 3 && !StopWords.Contains(word))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxSearchTerms)
                .ToList();

        /// <summary>The sanitised HTML of a topic as the text a prompt carries, bounded.</summary>
        public static string PlainText(string? safeHtml)
        {
            var text = WebUtility.HtmlDecode(Tag().Replace(safeHtml ?? string.Empty, " "));
            text = Whitespace().Replace(text, " ").Trim();
            return text.Length <= MaxTopicTextLength ? text : text[..MaxTopicTextLength];
        }

        private static AssistantStepType ToStepType(StepCatalogEntry entry) =>
            new(
                entry.Type,
                entry.DisplayName ?? entry.Type,
                entry.Category ?? string.Empty,
                entry.Kind ?? string.Empty,
                entry.Description,
                (IReadOnlyList<string>?)entry.Ports ?? Array.Empty<string>(),
                (entry.Inputs ?? new List<StepInputView>())
                    .Select(input => new AssistantStepInput(input.Name, input.TypeName ?? string.Empty, input.Description, input.IsSensitive, input.Options))
                    .ToList());

        private async Task<T> OrNothingAsync<T>(Func<Task<T>> send, T nothing)
        {
            try
            {
                return await send();
            }
            catch (AccessDeniedException ex)
            {
                // What the user may not see is, to the agent, not there.
                logger.LogDebug("An assistant read was refused for the current user: {Reason}", ex.Message);
                return nothing;
            }
        }

        [GeneratedRegex(@"[\p{L}\p{N}]+")]
        private static partial Regex Word();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex Tag();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();
    }

    /// <summary>
    /// The assistant's writes (D113): ONLY on the user's explicit apply, and only through the owning module's own gated
    /// commands sent AS THE USER - create pipeline then save its graph; create board then add each widget. A failure part
    /// way removes what was created (through the module's own delete) and rethrows, so an apply is all or nothing.
    /// </summary>
    public sealed class MediatorAssistantCommandGateway(IMediator mediator, ILogger<MediatorAssistantCommandGateway> logger) : IAssistantCommandGateway
    {
        public async Task<AssistantPipelineValidationResult> ValidatePipelineAsync(AssistantPipelineDraft draft, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var catalog = await mediator.Send(new ListStepsQuery(), cancellationToken);
            var errors = AssistantPipelineGraph.Check(draft, catalog).ToList();
            if (errors.Count == 0)
            {
                // The engine is the judge of its own document - the same judgement the editor's save makes, nothing stored.
                errors.AddRange(await mediator.Send(new ValidateGraphQuery(AssistantPipelineGraph.ToGraph(draft)), cancellationToken));
            }

            return errors.Count == 0 ? AssistantPipelineValidationResult.Valid : AssistantPipelineValidationResult.Invalid(errors);
        }

        public async Task<string> CreatePipelineAsync(AssistantPipelineDraft draft, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var validation = await ValidatePipelineAsync(draft, cancellationToken);
            if (!validation.IsValid)
            {
                throw new AssistantConflictException("The pipeline cannot be created: " + string.Join("; ", validation.Errors));
            }

            PipelineView created;
            try
            {
                created = await mediator.Send(new CreatePipelineCommand(draft.Name, draft.Description), cancellationToken);
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                throw Refused("pipeline", ex);
            }

            try
            {
                _ = await mediator.Send(new SaveGraphCommand(created.DefinitionId, AssistantPipelineGraph.ToGraph(draft)), cancellationToken)
                    ?? throw new InvalidOperationException($"The new pipeline '{created.DefinitionId}' has no draft to save its graph into.");
                return created.DefinitionId;
            }
            catch (Exception ex)
            {
                await UndoAsync(() => mediator.Send(new DeletePipelineCommand(created.DefinitionId), CancellationToken.None), "pipeline", created.DefinitionId);
                if (IsRefusal(ex))
                {
                    throw Refused("pipeline", ex);
                }

                throw;
            }
        }

        public async Task<Guid> CreateDashboardAsync(AssistantDashboardDraft draft, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var widgets = draft.Widgets ?? Array.Empty<AssistantWidgetDraft>();
            var kinds = widgets.Select(widget => Enum.TryParse<WidgetKind>(widget.Kind, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
                    ? kind
                    : throw new AssistantConflictException($"Widget '{widget.Title}' has kind '{widget.Kind}', which is not a widget kind."))
                .ToList();

            Guid dashboardId;
            try
            {
                dashboardId = await mediator.Send(new CreateDashboardCommand(draft.Name, draft.Columns, draft.RowHeightPx), cancellationToken);
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                throw Refused("dashboard", ex);
            }

            try
            {
                for (var index = 0; index < widgets.Count; index++)
                {
                    var widget = widgets[index];
                    await mediator.Send(
                        new AddWidgetCommand(
                            dashboardId, widget.Title, kinds[index], widget.DatasetId, widget.Columns ?? Array.Empty<string>(),
                            widget.Column, widget.Row, widget.ColumnSpan, widget.RowSpan,
                            ChartSpecJson: kinds[index] == WidgetKind.Chart ? widget.ChartSpecJson : null),
                        cancellationToken);
                }

                return dashboardId;
            }
            catch (Exception ex)
            {
                await UndoAsync(() => mediator.Send(new DeleteDashboardCommand(dashboardId), CancellationToken.None), "dashboard", dashboardId.ToString());
                if (IsRefusal(ex))
                {
                    throw Refused("dashboard", ex);
                }

                throw;
            }
        }

        /// <summary>
        /// The owning module REFUSED the proposal (its validation, its invariants, the engine's judgement) - a message written to
        /// be shown. Anything else - a denied key, a cancellation, a fault - propagates as it is.
        /// </summary>
        private static bool IsRefusal(Exception ex) =>
            ex is FluentValidation.ValidationException or ArgumentException || (ex is InvalidOperationException && ex is not AssistantConflictException);

        private static AssistantConflictException Refused(string kind, Exception ex) =>
            new($"The {kind} could not be created: " + (ex is FluentValidation.ValidationException validation
                ? string.Join("; ", validation.Errors.Select(failure => failure.ErrorMessage))
                : ex is ArgumentException argument && argument.ParamName is not null ? argument.Message.Replace($" (Parameter '{argument.ParamName}')", string.Empty, StringComparison.Ordinal) : ex.Message), ex);

        private async Task UndoAsync<T>(Func<Task<T>> delete, string kind, string id)
        {
            try
            {
                await delete();
            }
            catch (Exception ex)
            {
                // The original failure is what the user sees; a leftover it could not remove is reported, never silent.
                logger.LogError(ex, "An assistant apply failed part way and the {Kind} '{Id}' it created could not be removed.", kind, id);
            }
        }
    }

    /// <summary>
    /// A pipeline draft as the editor's graph (BL-95 (c)): steps get ids by position, a connection id fills the step's
    /// <see cref="ConnectionInput"/> input, and steps are laid out by their depth from the first one. <see cref="Check"/> is
    /// what the assistant refuses before the engine judges: an unknown type, an input the type does not take, a SENSITIVE
    /// input (the user enters a secret in the editor, never an agent), a connection on a step that takes none, a relation
    /// to a step that is not there or from a port the step does not have.
    /// </summary>
    public static class AssistantPipelineGraph
    {
        /// <summary>The input a step reads its connection from (<c>ExtractFromConnection</c>, BL-95).</summary>
        public const string ConnectionInput = "Connection";

        private const double ColumnWidth = 260;
        private const double RowHeight = 140;

        public static IEnumerable<string> Check(AssistantPipelineDraft draft, IReadOnlyList<StepCatalogEntry> catalog)
        {
            var steps = draft.Steps ?? Array.Empty<AssistantPipelineStepDraft>();
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                yield return "The pipeline needs a name.";
            }

            if (steps.Count == 0)
            {
                yield return "The pipeline needs at least one step.";
            }

            var types = catalog.GroupBy(entry => entry.Type, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.Version).First(), StringComparer.Ordinal);
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var label = $"Step {index} ('{step.Name}')";
                if (string.IsNullOrWhiteSpace(step.Type) || !types.TryGetValue(step.Type, out var type))
                {
                    yield return $"{label} has type '{step.Type}', which is not a step type ListStepTypes returns.";
                    continue;
                }

                var inputs = (type.Inputs ?? new List<StepInputView>()).ToDictionary(input => input.Name, StringComparer.Ordinal);
                foreach (var name in (step.Inputs ?? new Dictionary<string, string>()).Keys)
                {
                    if (!inputs.TryGetValue(name, out var input))
                    {
                        yield return $"{label} sets '{name}', which '{step.Type}' does not take.";
                    }
                    else if (input.IsSensitive)
                    {
                        yield return $"{label} sets the sensitive input '{name}'; leave it for the user to enter in the editor.";
                    }
                }

                if (step.ConnectionId is not null && !inputs.ContainsKey(ConnectionInput))
                {
                    yield return $"{label} names a connection, but '{step.Type}' does not read one.";
                }
            }

            foreach (var relation in draft.Relations ?? Array.Empty<AssistantPipelineRelationDraft>())
            {
                if (relation.SourceStepIndex < 0 || relation.SourceStepIndex >= steps.Count || relation.TargetStepIndex < 0 || relation.TargetStepIndex >= steps.Count)
                {
                    yield return $"The relation {relation.SourceStepIndex} -> {relation.TargetStepIndex} names a step that is not in the pipeline.";
                    continue;
                }

                if (relation.SourceStepIndex == relation.TargetStepIndex)
                {
                    yield return $"Step {relation.SourceStepIndex} is connected to itself.";
                }

                if (!string.IsNullOrWhiteSpace(relation.Port) && types.TryGetValue(steps[relation.SourceStepIndex].Type ?? string.Empty, out var source)
                    && !(source.Ports ?? new List<string>()).Contains(relation.Port.Trim(), StringComparer.Ordinal))
                {
                    yield return $"Step {relation.SourceStepIndex} has no outcome port '{relation.Port}'.";
                }
            }
        }

        public static PipelineGraph ToGraph(AssistantPipelineDraft draft)
        {
            var steps = draft.Steps ?? Array.Empty<AssistantPipelineStepDraft>();
            var relations = (draft.Relations ?? Array.Empty<AssistantPipelineRelationDraft>())
                .Where(relation => relation.SourceStepIndex >= 0 && relation.SourceStepIndex < steps.Count && relation.TargetStepIndex >= 0 && relation.TargetStepIndex < steps.Count)
                .ToList();
            var depth = Depths(steps.Count, relations);
            var rowAt = new Dictionary<int, int>();

            var graph = new PipelineGraph();
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var inputs = new Dictionary<string, string>(step.Inputs ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                if (step.ConnectionId is { } connection)
                {
                    inputs[ConnectionInput] = connection.ToString();
                }

                var row = rowAt.TryGetValue(depth[index], out var taken) ? taken : 0;
                rowAt[depth[index]] = row + 1;
                graph.Steps.Add(new PipelineGraphStep
                {
                    Id = StepId(index),
                    Type = step.Type,
                    Name = step.Name,
                    Position = new GraphPosition { X = depth[index] * ColumnWidth, Y = row * RowHeight },
                    Inputs = inputs,
                });
            }

            foreach (var relation in relations)
            {
                graph.Connections.Add(new PipelineGraphConnection
                {
                    SourceId = StepId(relation.SourceStepIndex),
                    SourcePort = string.IsNullOrWhiteSpace(relation.Port) ? null : relation.Port.Trim(),
                    TargetId = StepId(relation.TargetStepIndex),
                });
            }

            return graph;
        }

        public static string StepId(int index) => "step-" + (index + 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>Each step's column: the longest path to it from a step nothing points at (a cycle does not loop this).</summary>
        private static int[] Depths(int count, IReadOnlyList<AssistantPipelineRelationDraft> relations)
        {
            var depth = new int[count];
            for (var pass = 0; pass < count; pass++)
            {
                var changed = false;
                foreach (var relation in relations.Where(relation => relation.SourceStepIndex != relation.TargetStepIndex))
                {
                    if (depth[relation.TargetStepIndex] < depth[relation.SourceStepIndex] + 1 && depth[relation.SourceStepIndex] + 1 < count)
                    {
                        depth[relation.TargetStepIndex] = depth[relation.SourceStepIndex] + 1;
                        changed = true;
                    }
                }

                if (!changed)
                {
                    break;
                }
            }

            return depth;
        }
    }
}
