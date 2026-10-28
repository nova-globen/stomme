#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// The other half of the "Pipeline failed" trigger (BL-229, n8n's error workflow): when a run of one of the site's pipelines
    /// finishes FAULTED, the engine's finished notification reaches this handler on the worker, inside the run's engine tenant,
    /// and it fires the trigger's stimulus twice - under the failed pipeline's definition id and under "every pipeline" - through
    /// the engine's stimulus dispatcher, which carries the tenant, so only that site's pipelines start. What failed rides as the
    /// trigger's input. Never fired: a run of no stored pipeline (a step test, a run made in code), and a run of a pipeline that
    /// itself starts with the trigger - a failure handler that fails would otherwise start itself. Whatever goes wrong here is
    /// logged and never reaches the failed run, whose own state is still to be committed.
    /// </summary>
    public sealed class StartPipelinesOnFailure(IPipelineDefinitionStore definitions, IStimulusDispatcher dispatcher, ILogger<StartPipelinesOnFailure> logger)
        : INotificationHandler<PipelineFinished>
    {
        public async Task HandleAsync(PipelineFinished notification, CancellationToken cancellationToken)
        {
            var state = notification.PipelineState;
            var context = notification.PipelineExecutionContext;
            if (state.SubStatus != PipelineSubStatus.Faulted || state.IsSystem || string.IsNullOrEmpty(state.DefinitionId)
                || context.Nodes.Any(node => node.Step.Type == PipelineFailure.StepType))
            {
                return;
            }

            try
            {
                // A filter with no definition id would match ANY definition, hence the guard above; one that matches none is a run
                // of no stored pipeline.
                var definition = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = state.DefinitionId, VersionOptions = VersionOptions.Latest }, cancellationToken);
                if (definition is null)
                {
                    return;
                }

                var incident = state.Incidents.OrderBy(candidate => candidate.Timestamp).LastOrDefault();
                var step = incident is null ? null : context.FindNodeByStepId(incident.StepId)?.Step;
                var failure = PipelineFailure.Write(state.DefinitionId, definition.Name, state.Id, incident?.StepId, step?.Name, incident?.StepType,
                    incident?.Message ?? "The run failed.", incident?.Timestamp ?? DateTimeOffset.UtcNow);

                foreach (var watched in new[] { state.DefinitionId, PipelineFailure.AnyPipeline })
                {
                    await dispatcher.SendAsync(new DispatchStimulusRequest
                    {
                        StepTypeName = PipelineFailure.StepType,
                        Stimulus = new PipelineFailedStimulus(watched),
                        Metadata = new StimulusMetadata { Input = new Dictionary<string, object> { [PipelineFailed.FailureInputKey] = failure } },
                    }, cancellationToken);
                }

                logger.LogInformation("Run {Run} of pipeline '{Pipeline}' failed at step {Step}; the pipelines watching it are started.", state.Id, definition.Name, incident?.StepId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Run {Run} of pipeline {Definition} failed, and the pipelines watching it could not be started: {Error}", state.Id, state.DefinitionId, ex.Message);
            }
        }
    }
}
