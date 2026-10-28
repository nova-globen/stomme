#nullable enable
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;

namespace Stomme.Tests.Pipelines
{
    /// <summary>What a test step writes into, resolved through the step's execution context.</summary>
    public sealed class ResultSink
    {
        public string? Value { get; set; }
    }

    /// <summary>
    /// A one-shot step with a plain settable property, so it survives the engine's JSON materialiser (a definition
    /// is text in the store, and a step is rebuilt from that text by type name and properties - a constructor
    /// parameter would not come back). Registered on the module with <c>AddStep&lt;SinkStep&gt;()</c> where a
    /// persisted definition is run; the in-memory runner path needs no registration.
    /// </summary>
    [Step("Stomme.Tests", "Tests", "Writes its value to the sink.")]
    public sealed class SinkStep : CodeStep
    {
        public string? Value { get; set; }

        protected override void Execute(StepExecutionContext context) =>
            context.GetRequiredService<ResultSink>().Value = Value;
    }

    /// <summary>BL-229 increment 9: fails its first try (marking the sink), then writes its value - a retry's witness.</summary>
    [Step("Stomme.Tests", "Tests", "Fails its first try, then writes its value to the sink.")]
    public sealed class FailOnceSinkStep : CodeStep
    {
        public const string FailedOnce = "(failed once)";

        public string? Value { get; set; }

        protected override void Execute(StepExecutionContext context)
        {
            var sink = context.GetRequiredService<ResultSink>();
            if (sink.Value != FailedOnce)
            {
                sink.Value = FailedOnce;
                throw new System.InvalidOperationException("the first try fails");
            }

            sink.Value = Value;
        }
    }
}
