namespace Stomme.Engine.Pipelines;

public class DefaultPipelineInstanceVariableWriter(IVariablePersistenceManager variablePersistenceManager) : IPipelineInstanceVariableWriter
{
    public async Task<IEnumerable<ResolvedVariable>> SetVariables(PipelineExecutionContext pipelineExecutionContext, IEnumerable<VariableUpdateValue> variables, CancellationToken cancellationToken = default)
    {
        var pipeline = pipelineExecutionContext.Pipeline;
        var pipelineVariables = pipeline.Variables;
        var rootPipelineStepExecutionContext = pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.ParentStepExecutionContext == null);

        if (rootPipelineStepExecutionContext == null)
            return [];

        await variablePersistenceManager.LoadVariablesAsync(pipelineExecutionContext).ConfigureAwait(false);
        var variablesToUpdate = variables.ToDictionary(x => x.Id, x => x.Value);
        var resolvedVariables = new Dictionary<string, ResolvedVariable>();

        foreach (var pipelineVariable in pipelineVariables)
        {
            var currentValue = pipelineVariable.Get(rootPipelineStepExecutionContext.ExpressionExecutionContext);
            var resolvedVariable = new ResolvedVariable(pipelineVariable, currentValue);
            resolvedVariables[pipelineVariable.Id] = resolvedVariable;

            if (!variablesToUpdate.TryGetValue(pipelineVariable.Id, out var value))
                continue;

            pipelineVariable.Set(rootPipelineStepExecutionContext.ExpressionExecutionContext, value);
            resolvedVariable = resolvedVariable with
            {
                Value = value
            };

            resolvedVariables[pipelineVariable.Id] = resolvedVariable;
        }

        await variablePersistenceManager.SaveVariablesAsync(pipelineExecutionContext).ConfigureAwait(false);
        return resolvedVariables.Values;
    }
}