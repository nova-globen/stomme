#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Datasets;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Datasets.Domain
{
    /// <summary>
    /// A schema-aware tabular dataset (BL-94 increment 9, D110): a REFERENCE to its origin (never a copy), an inferred
    /// schema, a metadata-only summary, a bounded sample of rows for preview, and a classification that only ever
    /// escalates. Tenant-owned and site-scoped, named with a normalised twin (D49); its id is the ORIGIN's
    /// (<see cref="DatasetIdentity"/>), so two registrations of one origin are one row on every store. The published
    /// summary is masked and raw rows are refused for Confidential AND Restricted data (one classification order, Q1).
    /// </summary>
    public sealed class DatasetAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private DatasetAggregate()
        {
            TenantId = null!;
            Name = string.Empty;
            NameNormalized = string.Empty;
            OriginReferenceNormalized = string.Empty;
            Schema = DatasetSchema.Empty;
            Summary = DatasetSummary.Empty;
            SampleRowsJson = "[]";
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Name { get; private set; }

        public string NameNormalized { get; private set; }

        public OriginKind OriginKind { get; private set; }

        /// <summary>The origin reference: a pipeline id, a file id, a connection id plus query, a seed key; null when the origin is the name alone.</summary>
        public string? OriginReference { get; private set; }

        /// <summary>The reference (or, without one, the name) normalised - what <see cref="DatasetIdentity"/> hashes and what a lookup compares (D49).</summary>
        public string OriginReferenceNormalized { get; private set; }

        public DataClassification Classification { get; private set; }

        public DatasetSchema Schema { get; private set; }

        public DatasetSummary Summary { get; private set; }

        /// <summary>A bounded sample of rows (a JSON array, at most <see cref="DatasetLimits.MaxSampleRows"/>) for the preview. Not the data.</summary>
        public string SampleRowsJson { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public DateTime LastRefreshedUtc { get; private set; }

        public bool IsSensitive => DatasetClassificationPolicy.IsSensitive(Classification);

        public bool RawRowsAllowed => DatasetClassificationPolicy.RawRowsAllowed(Classification);

        /// <summary>The summary as consumers see it: masked (no samples, no min, no max) when the data is sensitive. A method, not a property: the module store maps a value-object PROPERTY as a column, and this one is computed.</summary>
        public DatasetSummary PublishedSummary() => IsSensitive ? Summary.Masked() : Summary;

        public static string NormalizeName(string? name) => (name ?? string.Empty).Trim().ToUpperInvariant();

        public static string NormalizeReference(string? reference) => (reference ?? string.Empty).Trim().ToUpperInvariant();

        public static DatasetAggregate Register(TenantId tenantId, int siteId, string name, OriginKind originKind, string? originReference, DataClassification classification,
            DatasetSchema schema, DatasetSummary summary, string? sampleRowsJson, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A dataset belongs to a site.");
            }

            var cleanName = name.Trim();
            if (cleanName.Length > DatasetLimits.MaxNameLength)
            {
                throw new ArgumentException($"A dataset name must be at most {DatasetLimits.MaxNameLength} characters.", nameof(name));
            }

            EnsureSchemaMatchesSummary(schema, summary);
            var reference = string.IsNullOrWhiteSpace(originReference) ? null : originReference.Trim();
            var dataset = new DatasetAggregate
            {
                Id = DatasetIdentity.For(tenantId, siteId, originKind, reference ?? cleanName),
                TenantId = tenantId,
                SiteId = siteId,
                Name = cleanName,
                NameNormalized = NormalizeName(cleanName),
                OriginKind = originKind,
                OriginReference = reference,
                OriginReferenceNormalized = NormalizeReference(reference ?? cleanName),
                Classification = classification,
                Schema = schema,
                Summary = summary,
                SampleRowsJson = BoundedSample(sampleRowsJson),
                CreatedUtc = nowUtc,
                LastRefreshedUtc = nowUtc,
            };
            dataset.RaiseDomainEvent(new DatasetRegisteredEvent(dataset.Id, dataset.Name));
            return dataset;
        }

        /// <summary>
        /// Fresh data: the schema, the summary and the sample are replaced; a derived classification may only ESCALATE the
        /// level (a later sample that introduces PII or a secret column tightens it; a refresh never lowers it).
        /// </summary>
        public void Refresh(DatasetSchema schema, DatasetSummary summary, string? sampleRowsJson, DataClassification? derivedClassification, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(summary);
            EnsureSchemaMatchesSummary(schema, summary);

            var schemaChanged = !Schema.Equals(schema);
            Schema = schema;
            Summary = summary;
            SampleRowsJson = BoundedSample(sampleRowsJson);
            LastRefreshedUtc = nowUtc;
            if (schemaChanged)
            {
                RaiseDomainEvent(new DatasetSchemaChangedEvent(Id));
            }

            if (derivedClassification is { } derived && derived > Classification)
            {
                var previous = Classification;
                Classification = derived;
                RaiseDomainEvent(new DatasetReclassifiedEvent(Id, previous, derived));
            }

            RaiseDomainEvent(new DatasetRefreshedEvent(Id));
        }

        /// <summary>A registration for the same origin under a new name moves the name; the id (the origin's) stays.</summary>
        public void Rename(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var cleanName = name.Trim();
            if (cleanName.Length > DatasetLimits.MaxNameLength)
            {
                throw new ArgumentException($"A dataset name must be at most {DatasetLimits.MaxNameLength} characters.", nameof(name));
            }

            Name = cleanName;
            NameNormalized = NormalizeName(cleanName);
        }

        public void MarkDeleted() => RaiseDomainEvent(new DatasetDeletedEvent(Id, Name));

        public DatasetDescriptor ToDescriptor() => new(
            Id, Name, OriginKind, OriginReference, Classification,
            new DatasetSchemaDescriptor(Schema.Columns.Select(column => new DatasetColumnDescriptor(column.Name, column.DataType, column.SemanticType, column.FormatKey)).ToList()),
            new DatasetSummaryDescriptor(PublishedSummary().Columns.Select(column => new DatasetColumnSummaryDescriptor(column.Name, column.Samples, column.Min, column.Max, column.UniqueCount, column.NullCount)).ToList(), Summary.RowCountEstimate),
            LastRefreshedUtc);

        private static string BoundedSample(string? sampleRowsJson)
        {
            if (string.IsNullOrWhiteSpace(sampleRowsJson))
            {
                return "[]";
            }

            try
            {
                using var document = JsonDocument.Parse(sampleRowsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new ArgumentException("A dataset's sample is a JSON array of rows.", nameof(sampleRowsJson));
                }

                if (document.RootElement.GetArrayLength() > DatasetLimits.MaxSampleRows)
                {
                    throw new ArgumentException($"A dataset's sample holds at most {DatasetLimits.MaxSampleRows} rows.", nameof(sampleRowsJson));
                }
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("A dataset's sample is a JSON array of rows.", nameof(sampleRowsJson), ex);
            }

            return sampleRowsJson;
        }

        private static void EnsureSchemaMatchesSummary(DatasetSchema schema, DatasetSummary summary)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(summary);
            var schemaColumns = schema.Columns.Select(column => column.Name).OrderBy(name => name, StringComparer.Ordinal);
            var summaryColumns = summary.Columns.Select(column => column.Name).OrderBy(name => name, StringComparer.Ordinal);
            if (!schemaColumns.SequenceEqual(summaryColumns))
            {
                throw new InvalidOperationException("The data summary must describe exactly the schema's columns.");
            }
        }
    }

    /// <summary>A dataset's id is its ORIGIN's: SHA-256 over the site, the kind and the normalised reference, folded to a Guid - the same on every store, so a race to register one origin lands on one key.</summary>
    public static class DatasetIdentity
    {
        private const string Namespace = "stomme.datasets/v1";

        /// <summary>
        /// The id is the TENANT's, the site's and the origin's. The tenant is in the hash because the storage-blind module
        /// store is one per installation and a site id repeats across tenants (the site-per-alias topology): an id derived
        /// from the site and the origin alone COLLIDED on the store's primary key when a second tenant on the same site id
        /// registered the same origin - measured by the demo-content two-tenant case (BL-94 increment 14), the BL-111
        /// class one level down.
        /// </summary>
        public static Guid For(TenantId tenantId, int siteId, OriginKind originKind, string reference)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            var text = $"{Namespace}|{tenantId.Value:N}|{siteId}|{(int)originKind}|{DatasetAggregate.NormalizeReference(reference)}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var bytes = new byte[16];
            for (var i = 0; i < 16; i++)
            {
                bytes[i] = (byte)(hash[i] ^ hash[i + 16]);
            }

            return new Guid(bytes);
        }
    }

    /// <summary>One schema column; <c>FormatKey</c> names a Formats preset or definition (id or name) for renderers that honour it.</summary>
    public sealed record DatasetColumn(string Name, ColumnDataType DataType, string? SemanticType = null, string? FormatKey = null);

    /// <summary>Per-column metadata only: a few sample values, min and max, unique and null counts. Never the data.</summary>
    public sealed record DatasetColumnSummary(string Name, IReadOnlyList<string> Samples, string? Min, string? Max, int UniqueCount, int NullCount);

    /// <summary>The ordered columns as ONE JSON string column on every store (the D101 precedent), read back typed.</summary>
    public sealed class DatasetSchema : ValueObject, IStringValueObject
    {
        public const int MaxLength = 64_000;

        public static readonly DatasetSchema Empty = new("[]");

        private DatasetSchema(string value) => Value = value;

        public string Value { get; }

        public IReadOnlyList<DatasetColumn> Columns => DatasetJson.Read<List<DatasetColumn>>(Value) ?? new List<DatasetColumn>();

        public IReadOnlyList<string> ColumnNames => Columns.Select(column => column.Name).ToList();

        public static DatasetSchema From(IReadOnlyList<DatasetColumn> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);
            if (columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != columns.Count)
            {
                throw new ArgumentException("A schema's column names are unique.", nameof(columns));
            }

            return new DatasetSchema(DatasetJson.Write(columns, MaxLength, "schema"));
        }

        public static DatasetSchema Create(string value) => new(string.IsNullOrWhiteSpace(value) ? "[]" : value);

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>The metadata-only summary as ONE JSON string column: the column summaries and a row-count estimate.</summary>
    public sealed class DatasetSummary : ValueObject, IStringValueObject
    {
        public const int MaxLength = 128_000;

        public static readonly DatasetSummary Empty = From(Array.Empty<DatasetColumnSummary>(), 0);

        private DatasetSummary(string value) => Value = value;

        public string Value { get; }

        public IReadOnlyList<DatasetColumnSummary> Columns => Document.Columns;

        public int RowCountEstimate => Document.RowCountEstimate;

        private SummaryDocument Document => DatasetJson.Read<SummaryDocument>(Value) ?? new SummaryDocument(new List<DatasetColumnSummary>(), 0);

        public static DatasetSummary From(IReadOnlyList<DatasetColumnSummary> columns, int rowCountEstimate)
        {
            ArgumentNullException.ThrowIfNull(columns);
            return new DatasetSummary(DatasetJson.Write(new SummaryDocument(columns, rowCountEstimate), MaxLength, "summary"));
        }

        public static DatasetSummary Create(string value) => new(string.IsNullOrWhiteSpace(value) ? Empty.Value : value);

        /// <summary>A copy with the sample values and min/max stripped - what a sensitive dataset publishes.</summary>
        public DatasetSummary Masked() =>
            From(Columns.Select(column => new DatasetColumnSummary(column.Name, Array.Empty<string>(), null, null, column.UniqueCount, column.NullCount)).ToList(), RowCountEstimate);

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;

        private sealed record SummaryDocument(IReadOnlyList<DatasetColumnSummary> Columns, int RowCountEstimate);
    }

    internal static class DatasetJson
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        public static string Write<T>(T value, int maxLength, string what)
        {
            var json = JsonSerializer.Serialize(value, Options);
            if (json.Length > maxLength)
            {
                throw new ArgumentException($"A dataset's {what} must serialise to at most {maxLength} characters.");
            }

            return json;
        }

        public static T? Read<T>(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }

    public sealed record DatasetRegisteredEvent(Guid DatasetId, string Name) : DomainEvent;

    public sealed record DatasetRefreshedEvent(Guid DatasetId) : DomainEvent;

    /// <summary>A refresh escalated the classification; <c>From</c> is always strictly below <c>To</c>.</summary>
    public sealed record DatasetReclassifiedEvent(Guid DatasetId, DataClassification From, DataClassification To) : DomainEvent;

    public sealed record DatasetSchemaChangedEvent(Guid DatasetId) : DomainEvent;

    public sealed record DatasetDeletedEvent(Guid DatasetId, string Name) : DomainEvent;
}
