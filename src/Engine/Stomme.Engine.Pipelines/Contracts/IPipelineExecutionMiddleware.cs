namespace Stomme.Engine.Pipelines;

public interface IPipelineExecutionMiddleware
{
    ValueTask InvokeAsync(PipelineExecutionContext context);
}