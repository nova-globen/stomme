using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

public partial class StepExecutionContext
{
    /// <summary>
    /// Adds a new <see cref="PipelineExecutionLogEntry"/> to the execution log of the current <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="eventName">The name of the event.</param>
    /// <param name="message">The message of the event.</param>
    /// <param name="source">The source of the step. For example, the source file name and line number in case of composite steps.</param>
    /// <param name="payload">Any contextual data related to this event.</param>
    /// <returns>Returns the created <see cref="PipelineExecutionLogEntry"/>.</returns>
    public PipelineExecutionLogEntry AddExecutionLogEntry(string eventName, string? message = null, string? source = null, object? payload = null)
    {
        var logEntry = new PipelineExecutionLogEntry(
            Id,
            ParentStepExecutionContext?.Id,
            Step.Id,
            Step.Type,
            Step.Version,
            Step.Name,
            NodeId,
            null,
            _systemClock.UtcNow,
            PipelineExecutionContext.ExecutionLogSequence++,
            eventName,
            message,
            source ?? Step.GetSource(),
            payload);

        PipelineExecutionContext.ExecutionLog.Add(logEntry);
        return logEntry;
    }
}