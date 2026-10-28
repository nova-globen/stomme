using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.Stimuli;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Steps;

/// <summary>
/// Notifies the application that a task with a given name is requested to start.
/// When the application fulfilled the task, it is expected to report back to the pipeline engine in order to resume the pipeline. 
/// </summary>
[Step("Stomme", "Primitives", "Requests a given task to be run. ", Kind = StepKind.Action)]
[UsedImplicitly]
public class RunTask : Step<object>
{
    private static readonly object BookmarkPropertyKey = new();

    /// <summary>
    /// The key that is used for sending and receiving step input.
    /// </summary>
    public const string InputKey = "RunTaskInput";

    /// <summary>
    /// The name of the task being requested.
    /// </summary>
    [Input(Description = "The name of the task being requested.")]
    public Input<string> TaskName { get; set; } = null!;

    /// <summary>
    /// The name of the task being requested.
    /// </summary>
    [Input(Description = "Any additional parameters to send to the task.")]
    public Input<IDictionary<string, object>?> Payload { get; set; } = null!;

    /// <summary>
    /// The ID of the task that was requested to run.
    /// </summary>
    [Output(Description = "The ID of the task that was requested to run.")]
    public Output<string> TaskId { get; set; } = null!;

    /// <inheritdoc />
    [JsonConstructor]
    private RunTask(string? source = null, int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public RunTask(MemoryBlockReference output, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(output, source, line)
    {
    }

    /// <inheritdoc />
    public RunTask(Output<object>? output, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(output, source, line)
    {
    }

    /// <inheritdoc />
    public RunTask(string taskName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(new Literal<string>(taskName), source, line)
    {
    }

    /// <inheritdoc />
    public RunTask(Func<string> taskName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<string>(Expression.DelegateExpression(taskName), new MemoryBlockReference()), source, line)
    {
    }

    /// <inheritdoc />
    public RunTask(Func<ExpressionExecutionContext, string?> taskName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<string>(Expression.DelegateExpression(taskName), new MemoryBlockReference()), source, line)
    {
    }

    /// <inheritdoc />
    public RunTask(Variable<string> taskName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line) => TaskName = new Input<string>(taskName);

    /// <inheritdoc />
    public RunTask(Literal<string> taskName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line) => TaskName = new Input<string>(taskName);

    /// <inheritdoc />
    public RunTask(Input<string> taskName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line) => TaskName = taskName;

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        // Create bookmark.
        var taskName = TaskName.Get(context);
        var identityGenerator = context.GetRequiredService<IIdentityGenerator>();
        var taskId = identityGenerator.GenerateId();
        var stimulus = new RunTaskStimulus(taskId, taskName);
        context.CreateBookmark(stimulus, ResumeAsync, includeStepInstanceId: false);
        
        // Dispatch task request.
        var taskParams = Payload.GetOrDefault(context);
        var runTaskRequest = new RunTaskRequest(context, taskId, taskName, taskParams);
        var dispatcher = context.GetRequiredService<ITaskDispatcher>();

        // Set the task ID output.
        TaskId.Set(context, taskId);
        
        // Dispatch the task request.
        await dispatcher.DispatchAsync(runTaskRequest, context.CancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ResumeAsync(StepExecutionContext context)
    {
        var input = context.GetPipelineInput<object>(InputKey);
        context.Set(Result, input);
        await context.CompleteStepAsync().ConfigureAwait(false);
    }
}