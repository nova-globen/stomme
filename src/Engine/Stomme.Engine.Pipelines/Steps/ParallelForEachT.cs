using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Schedule a step for each item in parallel.
/// </summary>
/// <typeparam name="T"></typeparam>
[Step("Stomme", "Looping", "Schedule a step for each item in parallel.")]
public class ParallelForEach<T> : Step
{
    private const string ScheduledTagsProperty = nameof(ScheduledTagsProperty);
    private const string CompletedTagsProperty = nameof(CompletedTagsProperty);

    /// <inheritdoc />
    public ParallelForEach(Func<ExpressionExecutionContext, ICollection<T>> @delegate, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(new Input<object>(@delegate), source, line)
    {
    }

    /// <inheritdoc />
    public ParallelForEach(Func<ICollection<T>> @delegate, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(new Input<object>(@delegate), source, line)
    {
    }

    /// <inheritdoc />
    public ParallelForEach(ICollection<T> items, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(new Input<object>(items), source, line)
    {
    }

    /// <inheritdoc />
    public ParallelForEach(Input<object> items, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Items = items;
    }

    /// <inheritdoc />
    public ParallelForEach([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The items to iterate.
    /// </summary>
    [Input(Description = "The items to iterate through.")]
    public Input<object> Items { get; set; } = new(Array.Empty<T>());

    /// <summary>
    /// The <see cref="IStep"/> to execute each iteration.
    /// </summary>
    [Port]
    public IStep Body { get; set; } = null!;

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var items = context.GetItemSource<T>(Items);
        var tags = new List<Guid>();
        var currentIndex = 0;

        await foreach (var item in items.ConfigureAwait(false))
        {
            // For each item, declare a new variable for the work to be scheduled.
            var currentValueVariable = new Variable<T>("CurrentValue", item)
            {
                // TODO: This should be configurable, because this won't work for e.g. file streams and other non-serializable types.
                StorageDriverType = typeof(PipelineInstanceStorageDriver)
            };

            var currentIndexVariable = new Variable<int>("CurrentIndex", currentIndex++) { StorageDriverType = typeof(PipelineInstanceStorageDriver) };
            var variables = new List<Variable> { currentValueVariable, currentIndexVariable };

            // Schedule a body of work for each item.
            var tag = Guid.NewGuid();
            tags.Add(tag);
            await context.ScheduleStepAsync(Body, OnChildCompleted, tag, variables).ConfigureAwait(false);
        }
        
        SetTagList(context, ScheduledTagsProperty, tags);
        SetTagList(context, CompletedTagsProperty, new List<Guid>());
        
        // If there were no items, we're done.
        if (tags.Count == 0)
            await context.CompleteStepAsync().ConfigureAwait(false);
    }

    private async ValueTask OnChildCompleted(StepCompletedContext context)
    {
        var targetContext = context.TargetContext;
        var scheduledTags = GetTagList(targetContext, ScheduledTagsProperty);
        var completedTag = targetContext.Tag.ConvertTo<Guid>();
        var completedTags = GetTagList(targetContext, CompletedTagsProperty);
        
        completedTags.Add(completedTag);
        SetTagList(targetContext, CompletedTagsProperty, completedTags);
            
        // If not all scheduled steps have completed yet, we're not done yet.
        if (!scheduledTags.IsEqualTo(completedTags))
            return;

        // We're done, so complete the step.
        await targetContext.CompleteStepAsync().ConfigureAwait(false);
    }
    
    private ICollection<Guid> GetTagList(StepExecutionContext context, string propertyName)
    {
        // Read the list of tags from the context using the specified property name. The value is stored as JsonArray, so we need to deserialize it.
        var jsonArray = context.GetProperty<JsonArray>(propertyName)!;
        return jsonArray.Select(x => x.ConvertTo<Guid>()).ToList();
    }
    
    private void SetTagList(StepExecutionContext context, string propertyName, ICollection<Guid> tags)
    {
        // Serialize the list of tags to a JsonArray and store it in the context using the specified property name.
        var jsonArray = JsonSerializer.SerializeToNode(tags) as JsonArray;
        context.SetProperty(propertyName, jsonArray);
    }
}