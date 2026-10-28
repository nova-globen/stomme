namespace Stomme.Engine.Pipelines.Runtime;

public class TriggerInvoker(IPipelineStarter pipelineStarter) : ITriggerInvoker
{
    public async Task<StartPipelineResponse> InvokeAsync(InvokeTriggerRequest request, CancellationToken cancellationToken = default)
    {
        var startRequest = new StartPipelineRequest
        {
            Pipeline = request.Pipeline,
            CorrelationId = request.CorrelationId,
            Input = request.Input,
            Variables = request.Variables,
            Properties = request.Properties,
            ParentId = request.ParentPipelineInstanceId,
            TriggerStepId = request.StepId,
        };
                
        return await pipelineStarter.StartPipelineAsync(startRequest, cancellationToken).ConfigureAwait(false);
    }
}