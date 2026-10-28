using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

public partial class PipelineExecutionContext
{
    /// <summary>
    /// Adds a new <see cref="PipelineExecutionLogEntry"/> to the execution log of the current <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="eventName">The name of the event.</param>
    /// <param name="message">The message of the event.</param>
    /// <param name="payload">Any contextual data related to this event.</param>
    /// <returns>Returns the created <see cref="PipelineExecutionLogEntry"/>.</returns>
    public PipelineExecutionLogEntry AddExecutionLogEntry(string eventName, string? message = null, object? payload = null)
    {
        var logEntry = new PipelineExecutionLogEntry(
            Id,
            null,
            Pipeline.Id,
            Pipeline.Type,
            Pipeline.Identity.Version,
            Pipeline.Name,
            Pipeline.Identity.Id,
            null,
            SystemClock.UtcNow,
            ExecutionLogSequence++,
            eventName,
            message,
            Pipeline.GetSource(),
            payload);

        ExecutionLog.Add(logEntry);
        return logEntry;
    }
}