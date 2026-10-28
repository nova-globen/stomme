using System.ComponentModel;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// A dynamically provided step with custom properties. This is experimental and may be removed.
/// </summary>
[Browsable(false)]
public class DynamicStep : CodeStep
{
    public IDictionary<string, object?> Properties { get; set; } = new Dictionary<string, object?>();
    public ExecuteStepDelegate ExecuteHandler { get; set; } = _ => ValueTask.CompletedTask;

    protected override ValueTask ExecuteAsync(StepExecutionContext context) => ExecuteHandler(context);
}