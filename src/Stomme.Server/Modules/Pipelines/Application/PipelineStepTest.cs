#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// Test ONE step (n8n's "execute step"): the step - with what its bodies hold - run on its own, in this
    /// site, with the variables holding the values the author gives (the run shown's, the starting values, or values pinned by
    /// hand), and what it received and produced shown as a run's steps are. Nothing is stored: no run, no version. The step
    /// RUNS - an HTTP request is sent, a file written - so it takes <c>pipelines:run</c>, and what it received and produced is
    /// shown to an author only, a secret input hidden even then, as for a run.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Run)]
    public sealed record TestStepCommand(PipelineGraph Graph, string StepId, IReadOnlyDictionary<string, string>? Values, bool UpToHere = false, bool Whole = false, string? DefinitionId = null) : ICommand<StepTestResult>;

    public sealed class TestStepCommandHandler(
        IStepRegistry registry, IRegistriesPopulator populator, IIdentityGenerator identities, IStepSerializer steps,
        IPipelineRunner runner, IStepExecutionMapper mapper, IAccessGuard guard)
        : ICommandHandler<TestStepCommand, StepTestResult>
    {
        /// <summary>How long a step test may take before it is stopped.</summary>
        public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

        public async Task<StepTestResult> Handle(TestStepCommand request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request.Graph);
            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            var alone = request.Whole ? Whole(request.Graph, request.Values)
                : request.UpToHere ? UpTo(request.Graph, request.StepId, request.Values)
                : Alone(request.Graph, request.StepId, request.Values);
            if (alone is null)
            {
                return new StepTestResult { Status = "Refused", Error = request.Whole ? "The pipeline has no steps to test." : "There is no such step in the pipeline." };
            }

            string body;
            try
            {
                body = PipelineGraphJudge.Body(alone, null, registry, identities, steps);
            }
            catch (ArgumentException ex)
            {
                return new StepTestResult { Status = "Refused", Error = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal) };
            }

            // (n8n's pinned data): a pinned step hands its pinned result on in place of running - every one the test holds
            // but the step tested itself, as testing it is how its result is had again.
            var pinned = alone.Steps.Where(step => step.PinnedResult is not null && (request.Whole || step.Id != request.StepId))
                .ToDictionary(step => step.Id, step => step.PinnedResult!, StringComparer.Ordinal);
            if (pinned.Count > 0)
            {
                body = MarkPinned(body, pinned);
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Limit);
            Stomme.Engine.Pipelines.Models.RunPipelineResult result;
            try
            {
                // Marked as a step test, so the commit gate stores nothing of it - and with the pipeline tested, whose calls these are
                // (PipelineCallerPolicy).
                var properties = new Dictionary<string, object> { [StepTestCommitGate.PropertyKey] = true };
                if (!string.IsNullOrEmpty(request.DefinitionId))
                {
                    properties[Reliability.PipelineCallerPolicy.TestedPipelineKey] = request.DefinitionId;
                }

                result = await runner.RunAsync(steps.Deserialize(body), new Stomme.Engine.Pipelines.Options.RunPipelineOptions
                {
                    Properties = properties,
                }, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new StepTestResult { Status = "Stopped", Error = $"The step took longer than {Limit.TotalSeconds:0} seconds and was stopped." };
            }

            var author = await guard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct);
            var records = new List<Stomme.Engine.Pipelines.Runtime.Entities.StepExecutionRecord>();
            foreach (var context in result.Journal.StepExecutionContexts)
            {
                records.Add(await mapper.MapAsync(context));
            }

            var incident = result.PipelineState.Incidents.FirstOrDefault();
            return new StepTestResult
            {
                Status = result.PipelineState.SubStatus.ToString(),
                Error = incident?.Message,
                Steps = ListRunStepsQueryHandler.ToViews(records, registry, author).ToList(),
                Log = result.Journal.PipelineExecutionLogEntries
                    .Where(entry => !string.IsNullOrEmpty(entry.Message))
                    .Select(entry => entry.Message!)
                    .ToList(),
            };
        }

        /// <summary>
        /// The step on its own: it, what its bodies hold and the connections among them, at the pipeline's own level, with the
        /// variables holding the values given (by name) - else their starting values.
        /// </summary>
        internal static PipelineGraph? Alone(PipelineGraph graph, string stepId, IReadOnlyDictionary<string, string>? values)
        {
            var step = graph.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
            if (step is null)
            {
                return null;
            }

            var taken = new HashSet<string>(StringComparer.Ordinal) { stepId };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var held in graph.Steps.Where(candidate => candidate.Parent is not null && taken.Contains(candidate.Parent) && !taken.Contains(candidate.Id)).ToList())
                {
                    taken.Add(held.Id);
                    grew = true;
                }
            }

            return new PipelineGraph
            {
                Steps = graph.Steps.Where(candidate => taken.Contains(candidate.Id))
                    .Select(candidate => candidate.Id == stepId ? Copy(candidate, parent: null, port: null) : Copy(candidate, candidate.Parent, candidate.ParentPort))
                    .ToList(),
                Connections = graph.Connections.Where(connection => taken.Contains(connection.SourceId ?? "") && taken.Contains(connection.TargetId ?? "")).ToList(),
                Variables = (graph.Variables ?? new List<PipelineGraphVariable>()).Select(variable => new PipelineGraphVariable
                {
                    Id = variable.Id,
                    Name = variable.Name,
                    TypeName = variable.TypeName,
                    Value = values is not null && variable.Name is not null && values.TryGetValue(variable.Name, out var given) ? given : variable.Value,
                }).ToList(),
            };
        }

        /// <summary>
        /// (n8n tests a step with what leads to it): the step and every step that leads to it in its own flowchart - each
        /// one a connection path reaches it from - with what their bodies hold, lifted to the pipeline's own level when the step
        /// sits in a body; the connections among them; the variables as <see cref="Alone"/> gives them. A step that leads here
        /// and is switched off stays off, as a run would skip it; the step tested runs whatever its switch says.
        /// </summary>
        internal static PipelineGraph? UpTo(PipelineGraph graph, string stepId, IReadOnlyDictionary<string, string>? values)
        {
            var step = graph.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
            if (step is null)
            {
                return null;
            }

            // The step's own flowchart: the steps beside it, and the connections among them.
            var level = graph.Steps.Where(candidate => candidate.Parent == step.Parent && candidate.ParentPort == step.ParentPort).Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            var leading = new HashSet<string>(StringComparer.Ordinal) { stepId };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var connection in graph.Connections.Where(connection => connection.TargetId is not null && leading.Contains(connection.TargetId)
                    && connection.SourceId is not null && level.Contains(connection.SourceId) && !leading.Contains(connection.SourceId)).ToList())
                {
                    leading.Add(connection.SourceId!);
                    grew = true;
                }
            }

            var taken = new HashSet<string>(leading, StringComparer.Ordinal);
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var held in graph.Steps.Where(candidate => candidate.Parent is not null && taken.Contains(candidate.Parent) && !taken.Contains(candidate.Id)).ToList())
                {
                    taken.Add(held.Id);
                    grew = true;
                }
            }

            var cut = Alone(graph, stepId, values)!;
            cut.Steps = graph.Steps.Where(candidate => taken.Contains(candidate.Id))
                .Select(candidate =>
                {
                    var copy = leading.Contains(candidate.Id) ? Copy(candidate, parent: null, port: null) : Copy(candidate, candidate.Parent, candidate.ParentPort);
                    copy.Disabled = candidate.Id != stepId && candidate.Disabled;
                    return copy;
                })
                .ToList();
            cut.Connections = graph.Connections.Where(connection => taken.Contains(connection.SourceId ?? "") && taken.Contains(connection.TargetId ?? "")).ToList();
            return cut;
        }

        /// <summary>
        /// (n8n's "Test workflow"): the whole pipeline as it stands on the canvas, saved or not - every step, every
        /// connection, a switched-off step skipped as a run skips it - so the same document a published run would start from;
        /// the variables as <see cref="Alone"/> gives them. Null when there is no step at all.
        /// </summary>
        internal static PipelineGraph? Whole(PipelineGraph graph, IReadOnlyDictionary<string, string>? values)
        {
            if (graph.Steps.Count == 0)
            {
                return null;
            }

            var whole = Alone(graph, graph.Steps[0].Id, values)!;
            whole.Steps = graph.Steps.Select(step =>
            {
                var copy = Copy(step, step.Parent, step.ParentPort);
                copy.Disabled = step.Disabled;
                return copy;
            }).ToList();
            whole.Connections = graph.Connections.ToList();
            return whole;
        }

        // The steps of the body the test runs, by id, given their pinned results as the property the step middleware reads.
        internal static string MarkPinned(string body, IReadOnlyDictionary<string, string> pinned)
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(body) is not { } root)
            {
                return body;
            }

            void Visit(System.Text.Json.Nodes.JsonNode? node)
            {
                if (node is System.Text.Json.Nodes.JsonObject step)
                {
                    if (step["id"] is System.Text.Json.Nodes.JsonValue id && id.TryGetValue<string>(out var stepId) && pinned.TryGetValue(stepId, out var result)
                        && step["type"] is not null && step["customProperties"] is System.Text.Json.Nodes.JsonObject custom)
                    {
                        custom[Reliability.StepReliability.PinnedKey] = result;
                    }

                    foreach (var (_, child) in step.ToList())
                    {
                        Visit(child);
                    }
                }
                else if (node is System.Text.Json.Nodes.JsonArray list)
                {
                    foreach (var child in list.ToList())
                    {
                        Visit(child);
                    }
                }
            }

            Visit(root);
            return root.ToJsonString();
        }

        private static PipelineGraphStep Copy(PipelineGraphStep step, string? parent, string? port) => new()
        {
            Id = step.Id,
            Type = step.Type,
            Version = step.Version,
            Name = step.Name,
            Position = step.Position,
            Comment = step.Comment,
            Retries = step.Retries,
            RetryDelaySeconds = step.RetryDelaySeconds,
            ContinueOnError = step.ContinueOnError,
            FailedPath = step.FailedPath,
            FailureVariable = step.FailureVariable,
            Inputs = new Dictionary<string, string>(step.Inputs ?? new Dictionary<string, string>()),
            InputVariables = new Dictionary<string, string>(step.InputVariables ?? new Dictionary<string, string>()),
            InputExpressions = new Dictionary<string, string>(step.InputExpressions ?? new Dictionary<string, string>()),
            InputScripts = new Dictionary<string, string>(step.InputScripts ?? new Dictionary<string, string>()),
            InputConditions = (step.InputConditions ?? new Dictionary<string, PipelineCondition>()).ToDictionary(pair => pair.Key, pair => PipelineConditions.Copy(pair.Value)),
            Cases = PipelineOutcomes.Copy(step.Cases),
            OutputVariables = new Dictionary<string, string>(step.OutputVariables ?? new Dictionary<string, string>()),
            PinnedResult = step.PinnedResult,
            Parent = parent,
            ParentPort = parent is null ? null : port,
        };
    }
}
