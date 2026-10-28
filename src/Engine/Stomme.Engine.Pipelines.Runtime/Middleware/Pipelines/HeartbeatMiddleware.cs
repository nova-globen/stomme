using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

namespace Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;

public class PipelineHeartbeatMiddleware(PipelineMiddlewareDelegate next, PipelineHeartbeatGeneratorFactory pipelineHeartbeatGeneratorFactory) : PipelineExecutionMiddleware(next)
{
    public override async ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        using var heartbeat = pipelineHeartbeatGeneratorFactory.CreateHeartbeatGenerator(context);
        await Next(context).ConfigureAwait(false);
    }
}