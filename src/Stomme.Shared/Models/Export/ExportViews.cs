#nullable enable
using System;

namespace Stomme.Models.Export
{
    /// <summary>What a client may ask for. Everything else about an export is decided by the server.</summary>
    /// <remarks>
    /// There is deliberately NO site, tenant, user or timestamp here: the server takes those from the request it
    /// resolved, never from the body. the predecessor platform's export request carried the tenant in the body.
    /// </remarks>
    public sealed class ExportRequest
    {
        /// <summary>The canonical source key, e.g. <c>stomme.log</c>.</summary>
        public string SourceKey { get; set; } = string.Empty;

        /// <summary><c>Csv</c>, <c>Xlsx</c>, <c>Json</c> or <c>Pdf</c>.</summary>
        public string Format { get; set; } = string.Empty;

        /// <summary>The source's own arguments, opaque to the engine and validated by the source.</summary>
        public string? ArgumentsJson { get; set; }

        /// <summary>One character: comma, semicolon, tab or pipe. Null means comma.</summary>
        public string? CsvDelimiter { get; set; }

        public bool IncludeHeader { get; set; } = true;

        public bool JsonIndented { get; set; }

        /// <summary><c>A4</c>, <c>A3</c>, <c>Letter</c> or <c>Legal</c>, any case. Null means A4. Anything else is refused.</summary>
        public string? PdfPageSize { get; set; }

        /// <summary><c>Portrait</c> or <c>Landscape</c>, any case. Null means portrait. Anything else is refused.</summary>
        public string? PdfOrientation { get; set; }

        /// <summary>
        /// Null or true leaves formula neutralisation ON. False turns it off and needs <c>export:manage</c>.
        /// </summary>
        public bool? NeutraliseFormulas { get; set; }
    }

    /// <summary>An export as its requester - or a manager - sees it.</summary>
    /// <remarks>
    /// A failure is a CODE. There is no message member for exception text to reach a requester through.
    /// </remarks>
    public sealed class ExportJobView
    {
        public Guid Id { get; set; }

        public int RequesterUserId { get; set; }

        public string SourceKey { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? FailureCode { get; set; }

        public DateTime RequestedOnUtc { get; set; }

        public DateTime? CompletedOnUtc { get; set; }

        public DateTime? ExpiresOnUtc { get; set; }

        public long? RowCount { get; set; }

        public long? SizeBytes { get; set; }

        /// <summary>Whether the artifact can be downloaded right now: Ready, unexpired, and still where it belongs.</summary>
        public bool CanDownload { get; set; }
    }

    /// <summary>A recurring export (BL-95): the request, a name, and a five-field UTC cron. The owner is the caller.</summary>
    public sealed class ExportScheduleRequest
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>minute hour day-of-month month day-of-week, in UTC; runs at least 15 minutes apart.</summary>
        public string Cron { get; set; } = string.Empty;

        public ExportRequest Request { get; set; } = new();
    }

    /// <summary>A schedule as its owner - or a manager - sees it. Its runs are ordinary exports, requested as the owner.</summary>
    public sealed class ExportScheduleView
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public int OwnerUserId { get; set; }

        public string SourceKey { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

        public string Cron { get; set; } = string.Empty;

        public bool IsPaused { get; set; }

        /// <summary><c>None</c>, <c>Manual</c>, <c>OwnerNotAllowed</c> or <c>SourceUnavailable</c>.</summary>
        public string PauseReason { get; set; } = string.Empty;

        public DateTime? NextRunOnUtc { get; set; }

        public DateTime? LastRunOnUtc { get; set; }

        public Guid? LastJobId { get; set; }
    }

    /// <summary>A source a user may export from, as offered by the request form.</summary>
    public sealed class ExportSourceView
    {
        public string Key { get; set; } = string.Empty;

        /// <summary>False for a source that can produce several tables - CSV is refused for those.</summary>
        public bool SingleTable { get; set; }
    }
}
