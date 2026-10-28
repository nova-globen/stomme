using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;
using Humanizer;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class IdentityGraphService(IStepVisitor stepVisitor, IStepRegistryLookupService stepRegistryLookup, ILogger<IdentityGraphService> logger) : IIdentityGraphService
{
    /// <inheritdoc />
    public async Task AssignIdentitiesAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        await AssignIdentitiesAsync((IStep)pipeline, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AssignIdentitiesAsync(IStep root, CancellationToken cancellationToken = default)
    {
        var graph = await stepVisitor.VisitAsync(root, cancellationToken).ConfigureAwait(false);
        await AssignIdentitiesAsync(graph).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AssignIdentitiesAsync(StepNode root) => AssignIdentitiesAsync(root.Flatten().ToList());

    /// <inheritdoc />
    public async Task AssignIdentitiesAsync(ICollection<StepNode> flattenedList)
    {
        var identityCounters = new Dictionary<string, int>();

        foreach (var node in flattenedList)
        {
            node.Step.Id = CreateId(node, identityCounters, flattenedList);
            node.Step.NodeId = node.NodeId;
            await AssignInputOutputsAsync(node.Step).ConfigureAwait(false);

            if (node.Step is IVariableContainer variableContainer)
                AssignVariables(variableContainer);
        }
    }

    /// <inheritdoc />
    public async Task AssignInputOutputsAsync(IStep step)
    {
        var stepDescriptor = await stepRegistryLookup.FindAsync(step.Type, step.Version).ConfigureAwait(false);
        
        if (stepDescriptor == null!)
        {
            logger.LogWarning("Step descriptor not found for step type {StepType}. Skipping identity assignment", step.Type);
            return;
        }
        
        var inputDictionary = stepDescriptor.GetWrappedInputProperties(step); 

        foreach (var (inputName, input) in inputDictionary)
        {
            var blockReference = input?.MemoryBlockReference();

            if (blockReference == null!) 
                continue;
            
            if (string.IsNullOrEmpty(blockReference.Id))
                blockReference.Id = $"{step.Id}:input-{inputName.Humanize().Kebaberize()}";
        }
        
        var outputs = step.GetOutputs();

        foreach (var output in outputs)
        {
            var blockReference = output.Value.MemoryBlockReference();

            if (blockReference == null!) 
                continue;
            
            if (string.IsNullOrEmpty(blockReference.Id))
                blockReference.Id = $"{step.Id}:output-{output.Name.Humanize().Kebaberize()}";
        }
    }

    /// <inheritdoc />
    public void AssignVariables(IVariableContainer step)
    {
        var variables = step.Variables;
        var seed = 0;

        foreach (var variable in variables)
            variable.Id = variable.Id != null! ? variable.Id : $"{step.Id}:variable-{++seed}";
    }

    private string CreateId(StepNode stepNode, IDictionary<string, int> identityCounters, ICollection<StepNode> allNodes)
    {
        if (!string.IsNullOrWhiteSpace(stepNode.Step.Id))
            return stepNode.Step.Id;

        while (true)
        {
            var fullTypeName = stepNode.Step.Type;
            var shortTypeName = fullTypeName.Split('.').Last();
            var index = GetNextIndexFor(shortTypeName, identityCounters);
            var name = $"{shortTypeName}{index + 1}";

            if (allNodes.All(x => x.Step.Id != name))
                return name;
        }
    }

    private int GetNextIndexFor(string stepType, IDictionary<string, int> identityCounters)
    {
        if (!identityCounters.TryGetValue(stepType, out var index))
        {
            identityCounters[stepType] = index;
        }
        else
        {
            index = identityCounters[stepType] + 1;
            identityCounters[stepType] = index;
        }

        return index;
    }
}