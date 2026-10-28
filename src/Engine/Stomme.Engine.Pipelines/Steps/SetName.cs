using System.Runtime.CompilerServices;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Sets a property on the pipeline execution context with the specified name value.
/// </summary>
[Step("Stomme", "Primitives", "Set the name of the pipeline instance to a specified value.")]
[PublicAPI]
public class SetName : CodeStep
{
    /// <inheritdoc />
    public SetName([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public SetName(Input<string> value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Value = value;
    }
    
    /// <summary>
    /// The value to set the pipeline instance's name to.
    /// </summary>
    public Input<string> Value { get; set; } = new("");

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        var value = Value.GetOrDefault(context);
        context.PipelineExecutionContext.Name = value;
    }
}