#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.ActivationValidators;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// How many runs of a pipeline may go at once (<see cref="PipelineRunsAtOnce"/>), where the engine and the module
    /// read it. The setting lives in the graph (so versions, files and the collaborating merge carry it), and every save writes it
    /// onto the definition's engine options as an activation strategy - the engine's trigger starts (a schedule, a webhook, an
    /// event) consult that, and skip a start it refuses. A start the module makes itself - the Run button, Retry, MCP's
    /// run_pipeline - is a dispatch the engine does not judge, so the module asks <see cref="RefusalAsync"/> first and says why.
    /// "One at a time, the rest in turn" refuses nothing: every start of it is stored and waits its turn
    /// (<see cref="Reliability.PipelineTurns"/>).
    /// </summary>
    public static class PipelineRunPolicy
    {
        /// <summary>
        /// The engine commit strategy a pipeline that saves its progress names (n8n's "save execution progress"): the run is
        /// stored after each step the scheduler runs - registered by the module's startup, the engine's own StepExecuted strategy.
        /// </summary>
        public const string SaveProgressStrategy = "Stomme.SaveProgress";

        /// <summary>Registers <see cref="SaveProgressStrategy"/> with the engine: the engine's own commit after each step it runs.</summary>
        public static void UseSaveProgress(Stomme.Engine.Features.Services.IModule module) =>
            module.Configure<Stomme.Engine.Pipelines.CommitStates.CommitStrategiesFeature>(strategies => strategies.Add(
                SaveProgressStrategy, "Save progress after each step", "The run is stored after each step it runs.",
                new Stomme.Engine.Pipelines.CommitStates.Strategies.StepExecutedPipelineStrategy()));

        /// <summary>The graph's settings onto the definition's engine options: how many runs at once, and whether a run is stored after each step.</summary>
        public static void Apply(PipelineDefinition definition, string? runsAtOnce, bool? saveProgress = null)
        {
            definition.Options ??= new PipelineOptions();
            definition.Options.CommitStrategyName = saveProgress == true ? SaveProgressStrategy : null;
            definition.Options.ActivationStrategyType = PipelineRunsAtOnce.Normalize(runsAtOnce) switch
            {
                PipelineRunsAtOnce.One => typeof(SingletonStrategy),
                PipelineRunsAtOnce.OnePerCorrelation => typeof(CorrelatedSingletonStrategy),
                PipelineRunsAtOnce.OneInTurn => typeof(Reliability.InTurnStrategy),
                _ => null,
            };
        }

        /// <summary>
        /// Why a new run of the published definition may not start now - a run of it still going, in all or for the correlation
        /// id - or null when it may. Read from the definition's options, as the engine's own starts read it.
        /// </summary>
        public static async Task<string?> RefusalAsync(IPipelineInstanceStore instances, PipelineDefinition published, string? correlationId, CancellationToken ct)
        {
            var strategy = published.Options?.ActivationStrategyType;
            if (strategy == typeof(SingletonStrategy))
            {
                var going = await instances.CountAsync(new PipelineInstanceFilter { DefinitionId = published.DefinitionId, PipelineStatus = PipelineStatus.Running }, ct);
                return going > 0 ? "This pipeline runs one at a time, and a run of it is still going (or waiting) - wait for it to end, or cancel it." : null;
            }

            if (strategy == typeof(CorrelatedSingletonStrategy))
            {
                var going = await instances.CountAsync(new PipelineInstanceFilter { DefinitionId = published.DefinitionId, PipelineStatus = PipelineStatus.Running, CorrelationId = correlationId }, ct);
                return going > 0 ? $"This pipeline runs one at a time per correlation id, and a run for '{correlationId ?? "(none)"}' is still going (or waiting) - wait for it to end, or cancel it." : null;
            }

            return null;
        }
    }
}
