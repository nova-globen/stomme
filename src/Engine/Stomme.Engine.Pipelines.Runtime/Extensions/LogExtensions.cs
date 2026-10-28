using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Extensions;

/// <summary>
/// Provides extension methods for sanitizing log messages.
/// </summary>
public static class LogExtensions
{
    /// <summary>
    /// Sanitizes a log message by replacing null characters with the string "\0".
    /// This is especially useful when storing messages in PostgreSQL, which does not allow null characters in strings.
    /// </summary>
    public static PipelineExecutionLogRecord SanitizeLogMessage(this PipelineExecutionLogRecord record)
    {
        record.Message = record.Message.SanitizeLogMessage();
        return record;
    }

    /// <summary>
    /// Sanitizes a log message by replacing null characters with the string "\0".
    /// This is especially useful when storing messages in PostgreSQL, which does not allow null characters in strings.
    /// </summary>
    public static StepExecutionRecord SanitizeLogMessage(this StepExecutionRecord record)
    {
        if (record.Exception != null)
            record.Exception = record.Exception with
            {
                Message = record.Exception.Message.SanitizeLogMessage()!
            };

        return record;
    }

    /// <summary>
    /// Sanitizes a log message by replacing null characters with the string "\0".
    /// This is especially useful when storing messages in PostgreSQL, which does not allow null characters in strings.
    /// </summary>
    public static string? SanitizeLogMessage(this string? message)
    {
        return message?.Replace("\0", "\\0");
    }
}