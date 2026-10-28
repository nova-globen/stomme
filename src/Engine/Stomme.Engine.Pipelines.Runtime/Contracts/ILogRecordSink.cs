using Stomme.Engine.Common;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a sink for storing log records.
/// </summary>
public interface ILogRecordSink<in T> where T : ILogRecord
{
    /// <summary>
    /// Persists the execution logs of a pipeline.
    /// </summary>
    Task PersistExecutionLogsAsync(PipelineExecutionContext context, CancellationToken cancellationToken = default);
}