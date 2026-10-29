#nullable enable
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Steps
{
    public enum LogMessageLevel
    {
        Information,
        Warning,
        Error,
    }

    /// <summary>
    /// Write a message to the run's execution log: the entry is on the run's page and in
    /// the run log API as the step's own event, and it goes to the host's logger at the same level.
    /// </summary>
    [Step("Stomme", "Control Flow", "Write a message to the run's execution log.", DisplayName = "Log")]
    public class LogMessage : CodeStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The message to record.")]
        public Input<string> Message { get; set; } = null!;

        [Input(DefaultValue = LogMessageLevel.Information)]
        public Input<LogMessageLevel> Level { get; set; } = new(new Literal<LogMessageLevel>(LogMessageLevel.Information));

        protected override void Execute(StepExecutionContext context)
        {
            var message = Message?.GetOrDefault(context) ?? "";
            var level = Level.GetOrDefault(context);
            context.AddExecutionLogEntry(level.ToString(), message);
            var logger = context.GetService<ILoggerFactory>()?.CreateLogger("Stomme.Pipelines");
            logger?.Log(level switch
            {
                LogMessageLevel.Error => LogLevel.Error,
                LogMessageLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Information,
            }, "Pipeline {PipelineInstance} log: {Message}", context.PipelineExecutionContext.Id, message);
        }
    }
}
