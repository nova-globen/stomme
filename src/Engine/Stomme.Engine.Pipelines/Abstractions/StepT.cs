using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Base class for custom steps that return a result.
/// </summary>
public abstract class Step<T> : Step, IStepWithResult<T>
{
    /// <inheritdoc />
    protected Step(string? source = default, int? line = default) : base(source, line)
    {
    }

    /// <inheritdoc />
    protected Step(string stepType, int version = 1, string? source = default, int? line = default) : base(stepType, version, source, line)
    {
    }

    /// <inheritdoc />
    protected Step(MemoryBlockReference? output, string? source = default, int? line = default) : this(source, line)
    {
        if (output != null) Result = new Output<T>(output);
    }

    /// <inheritdoc />
    protected Step(Output<T>? output, string? source = default, int? line = default) : this(source, line)
    {
        Result = output;
    }

    /// <summary>
    /// The result of the step.
    /// </summary>
    public Output<T>? Result { get; set; }

    Output? IStepWithResult.Result
    {
        get => Result;
        set => Result = (Output<T>?)value;
    }
}