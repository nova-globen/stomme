using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Behaviors;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Base class for custom steps with auto-complete behavior.
/// </summary>
[PublicAPI]
public abstract class CodeStep : Step
{
    /// <inheritdoc />
    protected CodeStep(string? source = default, int? line = default) : base(source, line)
    {
        Behaviors.Add<AutoCompleteBehavior>(this);
    }

    /// <inheritdoc />
    protected CodeStep(string stepType, int version = 1, string? source = default, int? line = default) : base(stepType, version, source, line)
    {
        Type = stepType;
    }
}

/// <summary>
/// Base class for custom steps with auto-complete behavior that return a result.
/// </summary>
[PublicAPI]
public abstract class CodeStepWithResult : CodeStep
{
    /// <inheritdoc />
    protected CodeStepWithResult(string? source = default, int? line = default) : base(source, line)
    {
    }

    /// <inheritdoc />
    protected CodeStepWithResult(string stepType, int version = 1, string? source = default, int? line = default) : base(stepType, version, source, line)
    {
    }

    /// <inheritdoc />
    protected CodeStepWithResult(MemoryBlockReference? output, string? source = default, int? line = default) : base(source, line)
    {
        if (output != null) Result = new Output(output);
    }

    /// <inheritdoc />
    protected CodeStepWithResult(Output? output, string? source = default, int? line = default) : base(source, line)
    {
        Result = output;
    }

    /// <summary>
    /// The result of the step.
    /// </summary>
    public Output? Result { get; set; }
}

/// <summary>
/// Base class for custom steps with auto-complete behavior that return a result.
/// </summary>
public abstract class CodeStep<T> : CodeStep, IStepWithResult<T>
{
    /// <inheritdoc />
    protected CodeStep(string? source = default, int? line = default) : base(source, line)
    {
    }
    
    /// <inheritdoc />
    protected CodeStep(string stepType, int version = 1, string? source = default, int? line = default) : base(stepType, version, source, line)
    {
    }

    /// <inheritdoc />
    protected CodeStep(MemoryBlockReference? output, string? source = default, int? line = default) : this(source, line)
    {
        if (output != null) Result = new Output<T>(output);
    }

    /// <inheritdoc />
    protected CodeStep(Output<T>? output, string? source = default, int? line = default) : this(source, line)
    {
        Result = output;
    }

    /// <summary>
    /// The result of the step.
    /// </summary>
    [Output] public Output<T>? Result { get; set; }

    Output? IStepWithResult.Result
    {
        get => Result;
        set => Result = (Output<T>?)value;
    }
}