using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Set the CorrelationId of the pipeline to a given value.
/// </summary>
[Step("Stomme", "Primitives", "Set the CorrelationId of the pipeline to a given value.")]
[PublicAPI]
public class Correlate : CodeStep
{
    /// <inheritdoc />
    public Correlate([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public Correlate(string correlationId, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        CorrelationId = new(correlationId);
    }

    /// <inheritdoc />
    public Correlate(Variable<string> correlationId, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        CorrelationId = new(correlationId);
    }

    /// <inheritdoc />
    public Correlate(Func<ExpressionExecutionContext, string> correlationId, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        CorrelationId = new(correlationId);
    }

    /// <summary>
    /// The correlation ID to set.
    /// </summary>
    [Description("An expression that evaluates to the value to store as the correlation id")]
    public Input<string> CorrelationId { get; set; } = null!;

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        var correlationId = context.Get(CorrelationId);
        context.PipelineExecutionContext.CorrelationId = correlationId;
    }
}