using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Attributes;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Represents an inline code step that can be used to execute arbitrary .NET code from a pipeline.
/// </summary>
[Browsable(false)]
[Step("Stomme", "Primitives", "Evaluate a Boolean condition to determine which path to execute next.")]
[PublicAPI]
public class Inline : CodeStep
{
    private readonly Func<StepExecutionContext, ValueTask> _step = _ => default;

    /// <inheritdoc />
    public Inline([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }
    
    /// <inheritdoc />
    public Inline(Func<StepExecutionContext, ValueTask> step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        _step = step;
    }

    /// <inheritdoc />
    public Inline(Func<ValueTask> step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(_ => step(), source, line)
    {
    }

    /// <inheritdoc />
    public Inline(Action<StepExecutionContext> step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(c =>
    {
        step(c);
        return new ValueTask();
    }, source, line)
    {
    }

    /// <inheritdoc />
    public Inline(Action step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(c =>
    {
        step();
        return new ValueTask();
    }, source, line)
    {
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context) => await _step(context).ConfigureAwait(false);

    /// <summary>
    /// Creates a new <see cref="Inline"/> step from the specified delegate.
    /// </summary>
    public static Inline From(Func<StepExecutionContext, ValueTask> step) => new(step);
    
    /// <summary>
    /// Creates a new <see cref="Inline"/> step from the specified delegate.
    /// </summary>
    public static Inline From(Func<ValueTask> step) => new(step);
    
    /// <summary>
    /// Creates a new <see cref="Inline"/> step from the specified delegate.
    /// </summary>
    public static Inline From(Action<StepExecutionContext> step) => new(step);
    
    /// <summary>
    /// Creates a new <see cref="Inline"/> step from the specified delegate.
    /// </summary>
    public static Inline From(Action step) => new(step);

    /// <summary>
    /// Creates a new <see cref="Inline"/> step from the specified delegate.
    /// </summary>
    public static Inline<T> From<T>(Func<StepExecutionContext, ValueTask<T>> step) => new(step);
    
    /// <summary>
    /// Creates a new <see cref="Inline"/> step from the specified delegate.
    /// </summary>
    public static Inline<T> From<T>(Func<ValueTask<T>> step) => new(step);
    
    /// <summary>
    /// Creates a new <see cref="Inline"/> step from the specified delegate.
    /// </summary>
    public static Inline<T> From<T>(Func<StepExecutionContext, T> step) => new(step);
    
    /// <summary>
    /// Creates a new <see cref="Inline"/> step from the specified delegate.
    /// </summary>
    public static Inline<T> From<T>(Func<T> step) => new(step);
}

/// <summary>
/// Represents an inline code step that can be used to execute arbitrary .NET code from a pipeline and return a value.
/// </summary>
[PublicAPI]
public class Inline<T> : CodeStep<T>
{
    private readonly Func<StepExecutionContext, ValueTask<T>> _step;

    /// <inheritdoc />
    public Inline(Func<StepExecutionContext, ValueTask<T>> step, MemoryBlockReference? output = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) 
        : base(output, source, line)
    {
        _step = step;
    }

    /// <inheritdoc />
    public Inline(Func<ValueTask<T>> step, MemoryBlockReference? output = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) 
        : this(_ => step(), output, source, line)
    {
    }

    /// <inheritdoc />
    public Inline(Func<StepExecutionContext, T> step, MemoryBlockReference? output = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) 
        : this(c =>
    {
        var result = step(c);
        return new ValueTask<T>(result);
    }, output, source, line)
    {
    }

    /// <inheritdoc />
    public Inline(Func<T> step, MemoryBlockReference? output = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) 
        : this(c =>
    {
        var result = step();
        return new ValueTask<T>(result);
    }, output, source, line)
    {
    }
    
    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var result = await _step(context).ConfigureAwait(false);
        context.Set(Result, result);
    }
}