using System.Dynamic;
using System.Text.Json;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class StepTestRunner(
    IServiceProvider serviceProvider,
    IPipelineExecutionPipeline executionPipeline,
    IIdentityGenerator identityGenerator)
    : IStepTestRunner
{
    /// <inheritdoc />
    public async Task<StepExecutionContext> RunAsync(PipelineGraph pipelineGraph, IStep step, CancellationToken cancellationToken = default)
    {
        var id = identityGenerator.GenerateId();
        var pipelineExecutionContext = await PipelineExecutionContext.CreateAsync(serviceProvider, pipelineGraph, id, cancellationToken).ConfigureAwait(false);
        var variableTestValues = GetVariableTestValues(pipelineGraph);

        foreach (var variable in pipelineGraph.Pipeline.Variables)
        {
            var variableValue = variableTestValues.TryGetValue(variable.Id, out var value) ? value : variable.Value;
            variable.Set(pipelineExecutionContext.ExpressionExecutionContext!, variableValue);
        }

        pipelineExecutionContext.ScheduleStep(step);
        pipelineExecutionContext.TransitionTo(PipelineSubStatus.Executing);

        await executionPipeline.ExecuteAsync(pipelineExecutionContext).ConfigureAwait(false);
        var stepExecutionContext = pipelineExecutionContext
            .StepExecutionContexts
            .First(x => x.Step == step);
        return stepExecutionContext;
    }

    private IDictionary<string, object?> GetVariableTestValues(PipelineGraph pipelineGraph)
    {
        var variableTestValues = pipelineGraph.Pipeline.CustomProperties.TryGetValue("VariableTestValues", out var variableTestValuesObj) ? variableTestValuesObj : null;
        
        if(variableTestValues is null)
            return new Dictionary<string, object?>();

        if (variableTestValues is JsonElement jsonElement)
            variableTestValues = JsonSerializer.Deserialize<ExpandoObject>(jsonElement.GetRawText())!;

        var expandoObject = (ExpandoObject)variableTestValues;
        var parsedValues = new Dictionary<string, object?>();

        foreach (var (key, value) in expandoObject)
        {
            var variableDefinition = pipelineGraph.Pipeline.Variables.FirstOrDefault(x => x.Id == key);
            
            if (variableDefinition == null)
                continue;

            var variableType = variableDefinition.GetVariableType();
            var variableValue = value.ConvertTo(variableType);
            parsedValues[key] = variableValue;
        }
        
        return parsedValues;
    }
}