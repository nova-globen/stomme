namespace Stomme.Engine.Pipelines;

internal class PipelineExecutionState : IDisposable
{
    private readonly StepExecutionContext _stepExecutionContext;

    public PipelineExecutionState(StepExecutionContext stepExecutionContext)
    {
        _stepExecutionContext = stepExecutionContext;
        _stepExecutionContext.IsExecuting = stepExecutionContext.PipelineExecutionContext.IsExecuting = true;
    }

    public void Dispose()
    {
        _stepExecutionContext.IsExecuting = _stepExecutionContext.PipelineExecutionContext.IsExecuting = false;
    }
}