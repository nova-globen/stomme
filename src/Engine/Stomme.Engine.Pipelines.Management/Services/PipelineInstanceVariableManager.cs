using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Management.Services;

public class PipelineInstanceVariableManager(
    IPipelineInstanceManager pipelineInstanceManager,
    IPipelineDefinitionService pipelineDefinitionService,
    IServiceProvider serviceProvider,
    IPipelineInstanceVariableReader variableReader,
    IPipelineInstanceVariableWriter variableWriter) : IPipelineInstanceVariableManager
{
    public async Task<IEnumerable<ResolvedVariable>> GetVariablesAsync(string pipelineInstanceId, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default)
    {
        var pipelineExecutionContext = await GetPipelineExecutionContextAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);
        if (pipelineExecutionContext == null) return [];
        return await variableReader.GetVariables(pipelineExecutionContext, excludeTags, cancellationToken).ConfigureAwait(false);
    }

    public Task<IEnumerable<ResolvedVariable>> GetVariablesAsync(PipelineExecutionContext pipelineExecutionContext, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default)
    {
        return variableReader.GetVariables(pipelineExecutionContext, excludeTags, cancellationToken);
    }

    public async Task<IEnumerable<ResolvedVariable>> GetVariablesAsync(PipelineInstance pipelineInstance, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default)
    {
        return await GetVariablesAsync(pipelineInstance.PipelineState, excludeTags, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IEnumerable<ResolvedVariable>> GetVariablesAsync(PipelineState pipelineState, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default)
    {
        var pipelineExecutionContext = await GetPipelineExecutionContextAsync(pipelineState, cancellationToken).ConfigureAwait(false);
        if (pipelineExecutionContext == null) return [];
        return await variableReader.GetVariables(pipelineExecutionContext, excludeTags, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IEnumerable<ResolvedVariable>> SetVariablesAsync(string pipelineInstanceId, IEnumerable<VariableUpdateValue> variables, CancellationToken cancellationToken = default)
    {
        var pipelineExecutionContext = await GetPipelineExecutionContextAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);
        if (pipelineExecutionContext == null) return [];
        var resolvedVariables = await variableWriter.SetVariables(pipelineExecutionContext, variables, cancellationToken).ConfigureAwait(false);
        await pipelineInstanceManager.SaveAsync(pipelineExecutionContext, cancellationToken).ConfigureAwait(false);
        return resolvedVariables;
    }

    public Task<IEnumerable<ResolvedVariable>> SetVariablesAsync(PipelineExecutionContext pipelineExecutionContext, IEnumerable<VariableUpdateValue> variables, CancellationToken cancellationToken = default)
    {
        return variableWriter.SetVariables(pipelineExecutionContext, variables, cancellationToken);
    }

    private async Task<PipelineExecutionContext?> GetPipelineExecutionContextAsync(string pipelineInstanceId, CancellationToken cancellationToken)
    {
        var pipelineInstance = await pipelineInstanceManager.FindByIdAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);

        if (pipelineInstance == null)
            return null;

        var pipelineState = pipelineInstance.PipelineState;

        return await GetPipelineExecutionContextAsync(pipelineState, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PipelineExecutionContext?> GetPipelineExecutionContextAsync(PipelineState pipelineState, CancellationToken cancellationToken)
    {
        var pipelineGraph = await pipelineDefinitionService.FindPipelineGraphAsync(pipelineState.DefinitionVersionId, cancellationToken).ConfigureAwait(false);

        if (pipelineGraph == null)
            return null;

        return await PipelineExecutionContext.CreateAsync(
            serviceProvider,
            pipelineGraph,
            pipelineState,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}