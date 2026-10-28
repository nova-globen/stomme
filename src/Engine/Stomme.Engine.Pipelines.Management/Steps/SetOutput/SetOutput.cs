using System.Runtime.CompilerServices;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Steps.SetOutput;

/// <summary>
/// Assigns a given value to a pipeline definition's output.
/// </summary>
[Step("Stomme", "Composition", "Assigns a given value to a the container's output.", Kind = StepKind.Action)]
[PublicAPI]
public class SetOutput : CodeStep
{
    /// <inheritdoc />
    public SetOutput([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The name of the output to assign.
    /// </summary>
    [Input(
        DisplayName = "Output",
        Description = "The output to assign.",
        UIHint = InputUIHints.OutputPicker
    )]
    public Input<string> OutputName { get; set; } = null!;

    /// <summary>
    /// The value to assign to the output.
    /// </summary>
    [Input(Description = "The value to assign.")]
    public Input<object?> OutputValue { get; set; } = null!;

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        var outputName = OutputName.Get(context);
        var outputValue = OutputValue.GetOrDefault(context);
        var ancestorContext = context.ParentStepExecutionContext?.GetAncestors().FirstOrDefault(x => x.StepDescriptor.Outputs.Any(y => y.Name == outputName));

        if (ancestorContext != null)
            SetAncestorStepOutput(context, ancestorContext, outputValue);
        else
            context.PipelineExecutionContext.Output[outputName] = outputValue!;
    }

    private void SetAncestorStepOutput(StepExecutionContext context, StepExecutionContext ancestorContext, object? outputValue)
    {
        var ancestorStep = ancestorContext.Step;
        var ancestorStepDescriptor = ancestorContext.StepDescriptor;
        var outputName = OutputName.Get(context);
        var ancestorOutputDescriptor = ancestorStepDescriptor.Outputs.FirstOrDefault(x => x.Name == outputName);

        if (ancestorOutputDescriptor == null)
            return;
        
        var ancestorOutput = (Output)ancestorOutputDescriptor.ValueGetter(ancestorStep)!;
        ancestorContext.Set(ancestorOutput, outputValue, outputName);

        // If the ancestor step is the root pipeline, we need to update the pipeline execution context's output collection as well.
        if (ancestorContext.ParentStepExecutionContext == null)
            context.PipelineExecutionContext.Output[outputName] = outputValue!;
        else
        {
            // If this step executes in a composite step, we need to update the composite step's output variable as well.
            var variable = context.ExpressionExecutionContext.GetVariable(outputName);

            if (variable != null)
                context.Set(variable, outputValue);

            // Also set the output on the composite step's output property.
            var compositeStepContext = ancestorContext;
            var compositeStep = compositeStepContext.Step;
            var compositeStepDescriptor = compositeStepContext.StepDescriptor;
            var compositeOutputDescriptor = compositeStepDescriptor.Outputs.FirstOrDefault(x => x.Name == outputName);
            var compositeOutput = (Output?)compositeOutputDescriptor?.ValueGetter(compositeStep);
                
            if(compositeOutput != null)
                ancestorContext.ParentStepExecutionContext.Set(compositeOutput, outputValue);
        }
    }
}