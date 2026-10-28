namespace Stomme.Engine.Pipelines;

public class DefaultPipelineInstanceVariableReader(IVariablePersistenceManager variablePersistenceManager) : IPipelineInstanceVariableReader
{
    public async Task<IEnumerable<ResolvedVariable>> GetVariables(PipelineExecutionContext pipelineExecutionContext, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default)
    {
        var pipeline = pipelineExecutionContext.Pipeline;
        var pipelineVariables = pipeline.Variables;
        var rootPipelineStepExecutionContext = pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Step == pipeline);
        
        if (rootPipelineStepExecutionContext == null) 
            return [];
        
        await variablePersistenceManager.LoadVariablesAsync(pipelineExecutionContext, excludeTags).ConfigureAwait(false);
        var resolvedVariables = new List<ResolvedVariable>();

        foreach (var pipelineVariable in pipelineVariables)
        {
            var value = pipelineVariable.Get(rootPipelineStepExecutionContext.ExpressionExecutionContext);
            resolvedVariables.Add(new(pipelineVariable, value));
        }
        
        return resolvedVariables;
    }
}