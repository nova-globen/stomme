namespace Stomme.Engine.Pipelines.Runtime;

public interface ITriggerInvoker
{
    Task<StartPipelineResponse> InvokeAsync(InvokeTriggerRequest request, CancellationToken cancellationToken = default);
}