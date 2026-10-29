#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// A call to a run's call address (<see cref="WaitForCall"/>): the token hashed into the stimulus the run's bookmark
    /// was keyed by, and DISPATCHED through the engine's stimulus path - never awaited, as a webhook's run is not.
    /// The engine resumes the run waiting for it, or queues the call for a minute when the run has not reached its wait yet. What
    /// a caller is told does not depend on whether a run waits: the token is the only secret, and it is never logged.
    /// </summary>
    public sealed class PipelineCallReceiver(IStimulusDispatcher dispatcher, ILogger<PipelineCallReceiver> logger)
    {
        /// <summary>Dispatches the call; false when the token cannot be a call address's.</summary>
        public async Task<bool> ReceiveAsync(string token, string body, string? contentType, CancellationToken ct)
        {
            if (!PipelineCalls.IsToken(token))
            {
                return false;
            }

            await dispatcher.SendAsync(new DispatchStimulusRequest
            {
                StepTypeName = PipelineCalls.WaitStepType,
                Stimulus = new CallStimulus(PipelineCalls.Hash(token)),
                Metadata = new StimulusMetadata { Input = WaitForCall.CallInput(body, contentType) },
            }, ct).ConfigureAwait(false);
            logger.LogInformation("A call to a run's call address was dispatched ({Length} characters).", body.Length);
            return true;
        }
    }
}
