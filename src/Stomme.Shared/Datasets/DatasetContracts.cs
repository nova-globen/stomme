#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Datasets
{
    // ============================================================================================================
    // The Datasets module's Shared contracts: the unified classification order, the
    // consumer-facing catalog and the producer-facing registrar ports, the delete guard's port, the keys. BCL-only.
    // ============================================================================================================

    /// <summary>Where a dataset's data comes from; the origin is a REFERENCE, never a copy.</summary>
    public enum OriginKind
    {
        /// <summary>The output of a pipeline run (refreshed when the run completes).</summary>
        PipelineOutput = 0,

        /// <summary>An uploaded file (the file id).</summary>
        Upload = 1,

        /// <summary>A query against a Connection (the connection id and the query).</summary>
        ExternalQuery = 2,

        /// <summary>A built-in demo seed (the seed key).</summary>
        DemoSeed = 3,
    }

    /// <summary>
    /// The ONE classification order (D110 Q1): <c>Public &lt; Internal &lt; Confidential &lt; Restricted</c> - Restricted is
    /// PII, the top. A classification only ever ESCALATES on a refresh. For BOTH top levels raw rows are refused on
    /// preview, as an export source and over MCP, and summaries are masked (the predecessor platform refused rows for Confidential alone
    /// and ranked Restricted below it).
    /// </summary>
    public enum DataClassification
    {
        Public = 0,
        Internal = 1,
        Confidential = 2,
        Restricted = 3,
    }

    /// <summary>The rule in one place: what a classification allows.</summary>
    public static class DatasetClassificationPolicy
    {
        /// <summary>Confidential and Restricted: summaries masked, raw rows refused everywhere.</summary>
        public static bool IsSensitive(DataClassification classification) => classification >= DataClassification.Confidential;

        public static bool RawRowsAllowed(DataClassification classification) => !IsSensitive(classification);

        public static string RawRowsRefusal(DataClassification classification) => $"Raw rows are not available for {classification} data.";
    }

    /// <summary>The data type of a column inferred from the data.</summary>
    public enum ColumnDataType
    {
        String = 0,
        Number = 1,
        Boolean = 2,
        Date = 3,
        Json = 4,
    }

    /// <summary>The bounds a sample is held to: the rows kept for preview, and the bytes a registration may carry.</summary>
    public static class DatasetLimits
    {
        public const int MaxSampleRows = 50;

        public const int MaxSampleBytes = 1_048_576;

        public const int MaxNameLength = 200;
    }

    /// <summary>A dataset column projected for consumers; <c>FormatKey</c> names a Formats preset or definition (id or name) when the column carries one.</summary>
    public sealed record DatasetColumnDescriptor(string Name, ColumnDataType DataType, string? SemanticType, string? FormatKey = null);

    public sealed record DatasetSchemaDescriptor(IReadOnlyList<DatasetColumnDescriptor> Columns);

    /// <summary>A per-column summary projected for consumers; a sensitive dataset's is masked (no samples, no min, no max).</summary>
    public sealed record DatasetColumnSummaryDescriptor(string Name, IReadOnlyList<string> Samples, string? Min, string? Max, int UniqueCount, int NullCount);

    public sealed record DatasetSummaryDescriptor(IReadOnlyList<DatasetColumnSummaryDescriptor> Columns, int RowCountEstimate);

    /// <summary>A consumer-facing view of a dataset: identity, origin, classification, schema, and the already-masked summary.</summary>
    public sealed record DatasetDescriptor(
        Guid Id,
        string Name,
        OriginKind OriginKind,
        string? OriginReference,
        DataClassification Classification,
        DatasetSchemaDescriptor Schema,
        DatasetSummaryDescriptor Summary,
        DateTime LastRefreshedUtc)
    {
        public bool IsSensitive => DatasetClassificationPolicy.IsSensitive(Classification);
    }

    /// <summary>The bounded preview rows; refused (<see cref="Allowed"/> false, an empty array) for a sensitive dataset.</summary>
    public sealed record DatasetPreviewRows(bool Allowed, string RowsJson, string Reason);

    /// <summary>
    /// The consumer-facing read port: a site's datasets with their schema and classification-masked summary, and a
    /// bounded, classification-gated preview. A consumer (a dashboard, an export source, MCP) binds to datasets through
    /// this port and never sees the aggregate. Every read is the request's site's.
    /// </summary>
    public interface IDatasetCatalog
    {
        Task<IReadOnlyList<DatasetDescriptor>> ListDescriptorsAsync(CancellationToken ct = default);

        Task<DatasetDescriptor?> GetDescriptorAsync(Guid datasetId, CancellationToken ct = default);

        Task<DatasetPreviewRows> GetPreviewRowsAsync(Guid datasetId, CancellationToken ct = default);
    }

    /// <summary>A producer's registration: the name, the origin, the sample rows as a JSON array, and the classification the producer asserts (escalated, never lowered, by the classifier).</summary>
    /// <summary>What a producer registers: the sample rows the schema and summary are inferred from, and - when the producer knows it - the FULL row count, which replaces the sample's (an upload counts every row of the file).</summary>
    public sealed record DatasetRegistration(
        string Name,
        OriginKind OriginKind,
        string? OriginReference,
        string SampleJson,
        DataClassification Classification = DataClassification.Internal,
        int? RowCountEstimate = null);

    /// <summary>The producer-facing write port: register a dataset for an origin, or refresh the one that origin already has. The id is the origin's, so two registrations of one origin are one row.</summary>
    public interface IDatasetRegistrar
    {
        Task<Guid> RegisterOrRefreshAsync(DatasetRegistration registration, CancellationToken ct = default);
    }

    /// <summary>A consumer that references datasets by id names the references, so a dataset in use is not deleted.</summary>
    public interface IDatasetUsageProvider
    {
        Task<IReadOnlyList<string>> FindReferencesAsync(Guid datasetId, CancellationToken ct = default);
    }

    /// <summary>What a registration carries over the wire: the same origin registered again refreshes its one row; the classification asserted is escalated by the classifier, never lowered.</summary>
    public sealed class RegisterDatasetRequest
    {
        public string Name { get; set; } = string.Empty;

        public OriginKind OriginKind { get; set; } = OriginKind.Upload;

        public string? OriginReference { get; set; }

        public string SampleJson { get; set; } = "[]";

        public DataClassification Classification { get; set; } = DataClassification.Internal;
    }

    /// <summary>Fresh rows for an existing dataset.</summary>
    public sealed class RefreshDatasetRequest
    {
        public string SampleJson { get; set; } = "[]";
    }

    /// <summary>A dataset from a file the file manager holds: the file's id; the name defaults to the file's stem; the classification asserted is escalated by the classifier, never lowered.</summary>
    public sealed class UploadDatasetRequest
    {
        public int FileId { get; set; }

        public string? Name { get; set; }

        public DataClassification Classification { get; set; } = DataClassification.Internal;
    }

    /// <summary>The Datasets module's permission KEYS: view (the list, the schema, the masked summary), manage (register, refresh, delete), preview (raw rows - the higher privilege).</summary>
    public static class DatasetPermissions
    {
        public const string Resource = "datasets";

        public const string View = "datasets:view";

        public const string Manage = "datasets:manage";

        public const string Preview = "datasets:preview";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage, Preview };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { "Administrators" }),
            (Manage, new[] { "Administrators" }),
            (Preview, new[] { "Administrators" }),
        };
    }
}
