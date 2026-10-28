using System.Collections.ObjectModel;
using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// A base class for steps that control a collection of steps.
/// </summary>
public abstract class Container : Step, IVariableContainer
{
    /// <inheritdoc />
    protected Container(string? source = null, int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The <see cref="IStep"/>s to execute.
    /// </summary>
    public ICollection<IStep> Steps { get; set; } = new List<IStep>();

    /// <summary>
    /// The variables available to this scope.
    /// </summary>
    public ICollection<Variable> Variables { get; set; } = new Collection<Variable>();

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        // Ensure variables have names.
        EnsureNames(Variables);

        // Register variables.
        context.ExpressionExecutionContext.Memory.Declare(Variables);

        // Schedule children.
        await ScheduleChildrenAsync(context).ConfigureAwait(false);
    }

    private void EnsureNames(IEnumerable<Variable> variables)
    {
        var count = 0;

        foreach (var variable in variables)
            if (string.IsNullOrWhiteSpace(variable.Name))
                variable.Name = $"Variable{++count}";
    }

    /// <summary>
    /// Schedule the <see cref="Steps"/> for execution.
    /// </summary>
    protected virtual ValueTask ScheduleChildrenAsync(StepExecutionContext context)
    {
        ScheduleChildren(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Schedule the <see cref="Steps"/> for execution.
    /// </summary>
    protected virtual void ScheduleChildren(StepExecutionContext context)
    {
    }
}