using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Runtime.Stimuli;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class EventPublisher(IStimulusSender stimulusSender, IStimulusDispatcher stimulusDispatcher) : IEventPublisher
{
    /// <inheritdoc />
    public async Task PublishAsync(
        string eventName,
        string? correlationId = null,
        string? pipelineInstanceId = null,
        string? stepInstanceId = null,
        object? payload = null,
        bool asynchronous = false,
        CancellationToken cancellationToken = default)
    {
        var stimulus = new EventStimulus(eventName);
        var pipelineInput = new Dictionary<string, object>
        {
            [Event.EventInputPipelineInputKey] = payload ?? new Dictionary<string, object>()
        };
        var metadata = new StimulusMetadata
        {
            CorrelationId = correlationId,
            StepInstanceId = stepInstanceId,
            PipelineInstanceId = pipelineInstanceId,
            Input = pipelineInput
        };
        var triggerName = StepTypeNameHelper.GenerateTypeName<Event>();
        if (asynchronous)
        {
            await stimulusDispatcher.SendAsync(new()
            {
                StepTypeName = triggerName,
                Stimulus = stimulus,
                Metadata = metadata
            }, cancellationToken).ConfigureAwait(false);
        }
        else
            await stimulusSender.SendAsync(triggerName, stimulus, metadata, cancellationToken).ConfigureAwait(false);
    }
}