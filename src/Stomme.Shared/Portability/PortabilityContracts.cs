#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Portability
{
    /// <summary>
    /// Names one portable entity: its type (a contributor's <see cref="IPortableEntityContributor.EntityType"/>) and its id
    /// on the SOURCE site (BL-82, D107). Ids are strings so an int, a Guid and a natural key all travel.
    /// </summary>
    public sealed record PortableEntityRef
    {
        public PortableEntityRef(string entityType, string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityType))
            {
                throw new ArgumentException("A portable entity has a type.", nameof(entityType));
            }

            if (string.IsNullOrWhiteSpace(entityId))
            {
                throw new ArgumentException("A portable entity has an id.", nameof(entityId));
            }

            EntityType = entityType;
            EntityId = entityId;
        }

        public string EntityType { get; }

        public string EntityId { get; }

        public override string ToString() => $"{EntityType}:{EntityId}";
    }

    /// <summary>
    /// One entity in a package: what it is, a human name, the schema its payload was written in, the payload itself (JSON
    /// the contributor defines - an explicit field ALLOWLIST, never a serialised row), whether it holds a secret, and what
    /// it depends on (imported first).
    /// </summary>
    public sealed record PortableEntity
    {
        public PortableEntity(PortableEntityRef reference, string displayName, int schemaVersion, string payloadJson, bool containsSecrets = false, IReadOnlyList<PortableEntityRef>? dependencies = null)
        {
            ArgumentNullException.ThrowIfNull(reference);
            if (schemaVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(schemaVersion), "A schema version starts at 1.");
            }

            Ref = reference;
            DisplayName = displayName ?? string.Empty;
            SchemaVersion = schemaVersion;
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
            ContainsSecrets = containsSecrets;
            Dependencies = dependencies ?? Array.Empty<PortableEntityRef>();
        }

        public PortableEntityRef Ref { get; }

        public string DisplayName { get; }

        public int SchemaVersion { get; }

        public string PayloadJson { get; }

        public bool ContainsSecrets { get; }

        public IReadOnlyList<PortableEntityRef> Dependencies { get; }
    }

    /// <summary>What to export: a site, and optionally only these ids of the contributor's type.</summary>
    public sealed record PortabilityScope(int SiteId, IReadOnlyCollection<string>? EntityIds = null);

    /// <summary>Whose data to export, on which site.</summary>
    public sealed record SubjectScope(int SiteId, int UserId);

    public enum ConflictStrategy
    {
        /// <summary>An entity that already exists on the target is left as it is.</summary>
        Skip = 0,

        /// <summary>An entity that already exists is overwritten with the package's values.</summary>
        Overwrite = 1,

        /// <summary>An entity that already exists is imported beside it under a new name.</summary>
        Rename = 2,
    }

    /// <summary>Source id to target id, so a dependant imported later points at what its dependency became.</summary>
    public interface IPortableIdMap
    {
        void Map(PortableEntityRef source, string targetId);

        string? Resolve(PortableEntityRef source);
    }

    public sealed class PortableIdMap : IPortableIdMap
    {
        private readonly Dictionary<(string, string), string> _map = new();

        public void Map(PortableEntityRef source, string targetId) => _map[(source.EntityType, source.EntityId)] = targetId;

        public string? Resolve(PortableEntityRef source) => _map.TryGetValue((source.EntityType, source.EntityId), out var target) ? target : null;
    }

    /// <summary>The import's target and rules. The site is the REQUEST's own; nothing in a package chooses it.</summary>
    public sealed record ImportContext(int SiteId, ConflictStrategy Strategy, IPortableIdMap IdMap);

    public enum EntityImportResult
    {
        Imported = 0,
        Skipped = 1,
        Overwritten = 2,
        Renamed = 3,
        Failed = 4,
    }

    public sealed record EntityImportOutcome(EntityImportResult Result, string? TargetId = null, string? Reason = null);

    /// <summary>
    /// A module's CONFIGURATION contribution to a package: one entity type it can export from a site and import onto one.
    /// Registered SCOPED and explicitly by the module's startup (D107 item 16) - it reads tenant data.
    /// </summary>
    /// <remarks>
    /// <b>Validate first.</b> The engine calls <see cref="ValidateImportAsync"/> for EVERY entity of a package before it
    /// calls <see cref="ImportAsync"/> for ANY: a refusal anywhere leaves no side effect anywhere (D107 item 3).
    /// </remarks>
    public interface IPortableEntityContributor
    {
        string EntityType { get; }

        int CurrentSchemaVersion { get; }

        IReadOnlyCollection<int> ReadableSchemaVersions { get; }

        Task<IReadOnlyList<PortableEntity>> ExportAsync(PortabilityScope scope, CancellationToken cancellationToken = default);

        /// <summary>Null when the entity may be imported here; otherwise why not. No side effects.</summary>
        Task<string?> ValidateImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default);

        Task<EntityImportOutcome> ImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A module's contribution to a person's DATA package (a subject-access request): what it holds about one user on one
    /// site. Export only - subject data is never imported (D107 Q4). Registered SCOPED and explicitly.
    /// </summary>
    public interface ISubjectDataContributor
    {
        string SubjectDataType { get; }

        Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default);
    }
}
