using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Behaviors;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// A strongly-typed for-each construct where <typeparamref name="T"/> is the item type.
/// </summary>
/// <typeparam name="T">The type of items in the collection to iterate over.</typeparam>
public class ForEach<T> : Step
{
    private const string CurrentIndexProperty = "CurrentIndex";

    /// <inheritdoc />
    public ForEach(Func<ExpressionExecutionContext, ICollection<T>> @delegate, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(new Input<ICollection<T>>(@delegate), source, line)
    {
    }
    
    /// <inheritdoc />
    public ForEach(Func<ICollection<T>> @delegate, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(new Input<ICollection<T>>(@delegate), source, line)
    {
    }

    /// <inheritdoc />
    public ForEach(ICollection<T> items, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(new Input<ICollection<T>>(items), source, line)
    {
    }
    
    /// <inheritdoc />
    public ForEach(Input<ICollection<T>> items, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Items = items;
    }
    
    /// <inheritdoc />
    public ForEach([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
    {
        Behaviors.Add<BreakBehavior>(this);
    }

    /// <summary>
    /// The set of values to iterate.
    /// </summary>
    [Input(Description = "The set of values to iterate.")]
    public Input<ICollection<T>> Items { get; set; } = new(Array.Empty<T>());

    /// <summary>
    /// The step to execute for each iteration.
    /// </summary>
    [Port]
    public IStep? Body { get; set; }

    /// <summary>
    /// The current value being iterated will be assigned to the specified <see cref="MemoryBlockReference"/>.
    /// </summary>
    [Output(Description = "Assign the current value to the specified variable.")]
    public Output<T>? CurrentValue { get; set; }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        // Execute first iteration.
        await HandleIteration(context).ConfigureAwait(false);
    }

    private async Task HandleIteration(StepExecutionContext context)
    {
        var isBreaking = context.GetIsBreaking();
        
        if (isBreaking)
        {
            await context.CompleteStepAsync().ConfigureAwait(false);
            return;
        }
        
        var currentIndex = context.GetProperty<int>(CurrentIndexProperty);
        var items = context.Get(Items)!.ToList();

        if (currentIndex >= items.Count)
        {
            await context.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        var currentValue = items[currentIndex];
        context.Set(CurrentValue, currentValue);

        if (Body != null)
        {
            var variables = new[]
            {
                new Variable("CurrentIndex", currentIndex),
                new Variable("CurrentValue", currentValue)
            };
            await context.ScheduleStepAsync(Body, OnChildCompleted, variables: variables).ConfigureAwait(false);
        }
        else
            await context.CompleteStepAsync().ConfigureAwait(false);

        // Increment index.
        context.UpdateProperty<int>(CurrentIndexProperty, x => x + 1);
    }

    private async ValueTask OnChildCompleted(StepCompletedContext context)
    {
        await HandleIteration(context.TargetContext).ConfigureAwait(false);
    }
}