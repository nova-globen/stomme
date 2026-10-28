using System.Runtime.CompilerServices;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;
// ReSharper disable ExplicitCallerInfoArgument

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Faults the pipeline.
/// </summary>
[Step("Stomme", "Primitives", "Faults the pipeline.")]
[PublicAPI]
public class Fault : Step
{
    /// <inheritdoc />
    public Fault([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }
    
    /// <summary>
    /// Creates a fault step.
    /// </summary>
    public static Fault Create(string code, string category, string type, string? message = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
    {
        return new(source, line)
        {
            Code = new(code),
            Message = new(message),
            Category = new(category),
            FaultType = new(type)
        };
    }

    /// <summary>
    /// Code to categorize the fault.
    /// </summary>
    [Input(Description = "Code to categorize the fault.")]
    public Input<string?> Code { get; set; } = null!;
    
    /// <summary>
    /// Category to categorize the fault. Examples: HTTP, Alteration, Azure, etc.
    /// </summary>
    [Input(Description = "Category to categorize the fault. Examples: HTTP, Alteration, Azure, etc.")]
    public Input<string?> Category { get; set; } = null!;
    
    /// <summary>
    /// The type of fault. Examples: System, Business, Integration, etc.
    /// </summary>
    [Input(
        DisplayName = "Type",
        Description = "The type of fault. Examples: System, Business, Integration, etc."
        )]
    public Input<string?> FaultType { get; set; } = null!;

    /// <summary>
    /// The message to include with the fault.
    /// </summary>
    [Input(Description = "The message to include with the fault.")]
    public Input<string?> Message { get; set; } = null!;

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        var code = Code.GetOrDefault(context) ?? "0";
        var category = Category.GetOrDefault(context) ?? "General";
        var type = FaultType.GetOrDefault(context) ?? "System";
        var message = Message.GetOrDefault(context);
        throw new FaultException(code, category, type, message);
    }
}