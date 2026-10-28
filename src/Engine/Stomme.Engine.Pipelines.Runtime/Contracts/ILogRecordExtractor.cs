using Stomme.Engine.Common;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Extracts execution log records.
/// </summary>
public interface ILogRecordExtractor<T> where T: ILogRecord
{
    /// <summary>
    /// Extracts execution logs from a pipeline execution context.
    /// </summary>
    Task<IEnumerable<T>> ExtractLogRecordsAsync(PipelineExecutionContext context);
}