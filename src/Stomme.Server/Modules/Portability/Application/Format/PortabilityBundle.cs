#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Stomme.Portability;

namespace Stomme.Modules.Portability.Application.Format
{
    /// <summary>
    /// The version of the package interchange format. A reader accepts the same major at a minor
    /// not newer than its own, and refuses anything else with a reason - never a best-effort partial read.
    /// </summary>
    public sealed record BundleFormatVersion(int Major, int Minor)
    {
        /// <summary>1.1 carries what 1.0 dropped on the wire: display names, dependencies, the site and an int exporter.</summary>
        public static BundleFormatVersion Current { get; } = new(1, 1);

        public bool IsReadableBy(BundleFormatVersion runtime)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return Major == runtime.Major && Minor <= runtime.Minor;
        }

        public override string ToString() => $"{Major}.{Minor}";
    }

    public static class PortabilityPackageKinds
    {
        public const string ConfigTransfer = "ConfigTransfer";
        public const string SubjectData = "SubjectData";
    }

    public sealed record PortabilityBundleRef(string EntityType, string EntityId);

    /// <summary>One entity on the wire.</summary>
    public sealed record PortabilityBundleEntity(
        string EntityType,
        string EntityId,
        string? DisplayName,
        int SchemaVersion,
        IReadOnlyList<PortabilityBundleRef>? Dependencies,
        bool ContainsSecrets,
        string Payload);

    public sealed record PortabilityBundleManifestEntry(string EntityType, int SchemaVersion, int Count);

    public sealed record PortabilityBundleManifest(BundleFormatVersion Format, IReadOnlyList<PortabilityBundleManifestEntry> Entries);

    /// <summary>Who produced the package, when, where. 1.0 carried a Guid exporter; it is read as "unknown".</summary>
    public sealed record PortabilityBundleProvenance(int? ExportedByUserId, DateTime ExportedAtUtc, string? SourceInstance);

    /// <summary>The person a subject-data package is about.</summary>
    public sealed record PortabilityBundleSubject(string SubjectId, string SubjectType);

    /// <summary>
    /// The package document (format 1.1): <c>{kind, siteRef, subject?, provenance, manifest{format, entries}, entities}</c>.
    /// The format version stays on the manifest where 1.0 put it, so one reader handles both.
    /// </summary>
    public sealed record PortabilityBundleDocument(
        string Kind,
        string? SiteRef,
        PortabilityBundleSubject? Subject,
        PortabilityBundleProvenance Provenance,
        PortabilityBundleManifest Manifest,
        IReadOnlyList<PortabilityBundleEntity> Entities)
    {
        /// <summary>
        /// Builds a document from what the contributors produced, in the DETERMINISTIC order the format promises: entities
        /// by type then id (ordinal), dependencies likewise, manifest lines by type then schema version.
        /// </summary>
        public static PortabilityBundleDocument Create(
            string kind, string? siteRef, PortabilityBundleSubject? subject, PortabilityBundleProvenance provenance, IEnumerable<PortableEntity> entities)
        {
            var ordered = entities
                .OrderBy(entity => entity.Ref.EntityType, StringComparer.Ordinal)
                .ThenBy(entity => entity.Ref.EntityId, StringComparer.Ordinal)
                .Select(entity => new PortabilityBundleEntity(
                    entity.Ref.EntityType,
                    entity.Ref.EntityId,
                    entity.DisplayName,
                    entity.SchemaVersion,
                    entity.Dependencies
                        .OrderBy(dependency => dependency.EntityType, StringComparer.Ordinal)
                        .ThenBy(dependency => dependency.EntityId, StringComparer.Ordinal)
                        .Select(dependency => new PortabilityBundleRef(dependency.EntityType, dependency.EntityId))
                        .ToList(),
                    entity.ContainsSecrets,
                    entity.PayloadJson))
                .ToList();

            var manifest = new PortabilityBundleManifest(
                BundleFormatVersion.Current,
                ordered.GroupBy(entity => (entity.EntityType, entity.SchemaVersion))
                    .OrderBy(group => group.Key.EntityType, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.SchemaVersion)
                    .Select(group => new PortabilityBundleManifestEntry(group.Key.EntityType, group.Key.SchemaVersion, group.Count()))
                    .ToList());

            return new PortabilityBundleDocument(kind, siteRef, subject, provenance, manifest, ordered);
        }
    }

    /// <summary>A read: the document, or why there is none.</summary>
    public sealed record BundleReadResult(PortabilityBundleDocument? Document, IReadOnlyList<string> Report)
    {
        public bool Success => Document is not null;

        public static BundleReadResult Ok(PortabilityBundleDocument document) => new(document, Array.Empty<string>());

        public static BundleReadResult Rejected(string reason) => new(null, new[] { reason });
    }

    /// <summary>
    /// System.Text.Json, one fixed options instance, records in declaration order - so one package always serialises to
    /// the same bytes. Reading checks the format version before anything else looks at the document.
    /// </summary>
    public sealed class PortabilityBundleSerializer
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string Serialize(PortabilityBundleDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            return JsonSerializer.Serialize(document, Options);
        }

        public BundleReadResult Read(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return BundleReadResult.Rejected("The package is empty.");
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (JsonException ex)
            {
                return BundleReadResult.Rejected($"The package is not valid JSON: {ex.Message}");
            }

            if (root is not JsonObject document)
            {
                return BundleReadResult.Rejected("The package is not a JSON object.");
            }

            // The version first: nothing else in a document of an unknown format is read.
            BundleFormatVersion? format;
            try
            {
                format = document["manifest"]?["format"]?.Deserialize<BundleFormatVersion>(Options);
            }
            catch (JsonException)
            {
                format = null;
            }

            if (format is null)
            {
                return BundleReadResult.Rejected("The package has no format version.");
            }

            if (!format.IsReadableBy(BundleFormatVersion.Current))
            {
                return BundleReadResult.Rejected($"Package format {format} is not readable by this installation (format {BundleFormatVersion.Current}).");
            }

            // 1.0 carried the exporter as a Guid and the instant as "exportedAt": read as unknown and as that instant.
            if (document["provenance"] is JsonObject provenance)
            {
                if (provenance["exportedByUserId"] is null)
                {
                    provenance.Remove("exportedBy");
                }

                if (provenance["exportedAtUtc"] is null && provenance["exportedAt"] is JsonNode legacyAt)
                {
                    provenance["exportedAtUtc"] = legacyAt.DeepClone();
                }
            }

            try
            {
                var read = document.Deserialize<PortabilityBundleDocument>(Options);
                if (read is null || read.Provenance is null || read.Manifest?.Entries is null || read.Entities is null || string.IsNullOrWhiteSpace(read.Kind))
                {
                    return BundleReadResult.Rejected("The package is missing its kind, provenance, manifest or entities.");
                }

                return BundleReadResult.Ok(read with
                {
                    Provenance = read.Provenance with { ExportedAtUtc = DateTime.SpecifyKind(read.Provenance.ExportedAtUtc.ToUniversalTime(), DateTimeKind.Utc) },
                });
            }
            catch (JsonException ex)
            {
                return BundleReadResult.Rejected($"The package does not have the expected shape: {ex.Message}");
            }
        }
    }
}
