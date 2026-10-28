namespace Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

public abstract class PipelineExecutionMiddleware : IPipelineExecutionMiddleware
{
    protected PipelineExecutionMiddleware(PipelineMiddlewareDelegate next) => Next = next;
    protected PipelineMiddlewareDelegate Next { get; }
    public abstract ValueTask InvokeAsync(PipelineExecutionContext context);
}