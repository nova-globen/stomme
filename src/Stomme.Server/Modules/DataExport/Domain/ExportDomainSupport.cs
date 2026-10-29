#nullable enable
using System;

namespace Stomme.Modules.DataExport.Domain
{
    /// <summary>Where an export is in its life. Stored as its integer value; never renumber.</summary>
    public enum ExportJobStatus
    {
        Queued = 0,
        Rendering = 1,
        Ready = 2,
        Failed = 3,
        Cancelled = 4,
        Expired = 5,
    }

    /// <summary>
    /// Why an export failed - the ONLY thing about a failure a requester is shown.
    /// </summary>
    /// <remarks>
    /// A failure message is never stored on the job: rendering <c>ex.Message</c> to the requester would hand connection
    /// strings, file paths and SQL fragments to whoever asked for the export. The text goes to the log; the row
    /// carries a code. Stored as its integer value; never renumber.
    /// </remarks>
    public enum ExportFailureCode
    {
        Unknown = 0,
        SourceUnavailable = 1,
        NotAuthorized = 2,
        InvalidArguments = 3,
        RenderError = 4,
        ArtifactTooLarge = 5,
        TimedOut = 6,
        AttemptsExhausted = 7,
        StorageError = 8,
    }

    /// <summary>What happened to an export, for the audit trail. Stored as its integer value; never renumber.</summary>
    public enum ExportAuditAction
    {
        Requested = 0,
        Completed = 1,
        Failed = 2,
        Cancelled = 3,
        Downloaded = 4,
        Expired = 5,
    }

    /// <summary>
    /// The export domain's UTC rule.
    /// </summary>
    /// <remarks>
    /// Inputs are REQUIRED to be <see cref="DateTimeKind.Utc"/>; persisted values are re-stamped UTC on read,
    /// because a relational provider materialises a scalar DateTime column as
    /// <see cref="DateTimeKind.Unspecified"/> while RavenDB and the File store hand back UTC - and
    /// <see cref="DateTime"/> equality ignores the kind, so the divergence passes every naive round-trip
    /// assertion and only shows when something converts the value.
    /// </remarks>
    internal static class ExportInstant
    {
        public static DateTime Require(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", parameterName);
            }

            return value;
        }

        public static DateTime Stamp(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        public static DateTime? Stamp(DateTime? value) =>
            value is { } instant ? DateTime.SpecifyKind(instant, DateTimeKind.Utc) : null;
    }
}
