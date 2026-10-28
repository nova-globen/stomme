using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Assign a pipeline variable a value.
/// </summary>
[Browsable(false)]
[Step("Stomme", "Primitives", "Assign value to a pipeline variable.")]
[PublicAPI]
public class SetVariable<T> : CodeStep
{
    /// <inheritdoc />
    public SetVariable([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public SetVariable(Variable<T> variable, Input<T> value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Variable = variable;
        Value = value;
    }

    /// <inheritdoc />
    public SetVariable(Variable<T> variable, Variable<T> value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(variable, new Input<T>(value), source, line)
    {
    }

    /// <inheritdoc />
    public SetVariable(Variable<T> variable, Func<ExpressionExecutionContext, T> value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(variable, new Input<T>(value), source, line)
    {
    }

    /// <inheritdoc />
    public SetVariable(Variable<T> variable, Func<T> value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(variable, new Input<T>(value), source, line)
    {
    }

    /// <inheritdoc />
    public SetVariable(Variable<T> variable, T value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(variable, new Input<T>(value), source, line)
    {
    }

    /// <summary>
    /// The variable to assign the value to.
    /// </summary>
    [Input(Description = "The variable to assign the value to.")]
    public Variable<T> Variable { get; set; } = null!;

    /// <summary>
    /// The value to assign.
    /// </summary>
    [Input(Description = "The value to assign.")]
    public Input<T> Value { get; set; } = new(new Literal<T>());

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        var value = context.Get(Value);
        Variable.Set(context, value);
    }
}

/// <summary>
/// Assign a pipeline variable a value.
/// </summary>
[Step("Stomme", "Primitives", "Assign a value to a pipeline variable.")]
[PublicAPI]
public class SetVariable : CodeStep
{
    /// <inheritdoc />
    public SetVariable([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The variable to assign the value to.
    /// </summary>
    [Input(Description = "The variable to assign the value to.")]
    public Variable? Variable { get; set; }

    /// <summary>
    /// The value to assign.
    /// </summary>
    [Input(Description = "The value to assign.")]
    public Input<object?> Value { get; set; } = new(default(object));

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        // Always refer to the variable by ID to ensure that the variable is resolved from the correct scope.
        var variableId = Variable?.Id; 
        var variable = context.ExpressionExecutionContext.EnumerateVariablesInScope().FirstOrDefault(x => x.Id == variableId);
     
        if (variable == null)
            throw new InvalidOperationException($"Variable '{variableId}' not found.");
        
        var value = context.Get(Value);
        variable.Set(context, value);
    }
}