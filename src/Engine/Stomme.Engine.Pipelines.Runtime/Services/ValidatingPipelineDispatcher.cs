using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Validates the pipeline request before dispatching it to the pipeline dispatcher.
/// </summary>
/// <param name="decoratedService">The pipeline dispatcher to decorate.</param>
/// <param name="dispatcherOptions">The pipeline dispatcher options.</param>
public class ValidatingPipelineDispatcher(IPipelineDispatcher decoratedService, IOptions<PipelineDispatcherOptions> dispatcherOptions) : IPipelineDispatcher
{
    private IPipelineDispatcher DecoratedService { get; set; } = decoratedService;

    /// <inheritdoc />
    public async Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineDefinitionRequest request, DispatchPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!ValidateChannel(options?.Channel))
            return DispatchPipelineResponse.UnknownChannel();

        return await DecoratedService.DispatchAsync(request, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineInstanceRequest request, DispatchPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!ValidateChannel(options?.Channel))
            return DispatchPipelineResponse.UnknownChannel();

        return await DecoratedService.DispatchAsync(request, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DispatchPipelineResponse> DispatchAsync(DispatchTriggerPipelinesRequest request, DispatchPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!ValidateChannel(options?.Channel))
            return DispatchPipelineResponse.UnknownChannel();

        return await DecoratedService.DispatchAsync(request, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DispatchPipelineResponse> DispatchAsync(DispatchResumePipelinesRequest request, DispatchPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!ValidateChannel(options?.Channel))
            return DispatchPipelineResponse.UnknownChannel();

        return await DecoratedService.DispatchAsync(request, options, cancellationToken).ConfigureAwait(false);
    }

    private bool ValidateChannel(string? channelName)
    {
        return string.IsNullOrEmpty(channelName) || GetChannelExists(channelName);
    }

    private bool GetChannelExists(string channelName)
    {
        return dispatcherOptions.Value.Channels.Any(x => x.Name == channelName);
    }
}