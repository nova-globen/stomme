#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Portability.Application.Format;
using Stomme.Portability;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Portability.Application
{
    /// <summary>One entity's line in an import report.</summary>
    public sealed record ImportReportLine(string EntityType, string EntityId, string DisplayName, EntityImportResult Result, string? TargetId, string? Reason);

    /// <summary>
    /// What an import did. <see cref="Accepted"/> false means NOTHING was imported - every refusal is listed and no
    /// contributor ran; true means the package passed validation and <see cref="Lines"/> says what each entity became.
    /// </summary>
    public sealed record ImportReport(bool Accepted, IReadOnlyList<string> Refusals, IReadOnlyList<ImportReportLine> Lines, PortabilityBundleDocument? Document)
    {
        public static ImportReport Refused(IEnumerable<string> refusals, PortabilityBundleDocument? document = null) =>
            new(false, refusals.ToList(), Array.Empty<ImportReportLine>(), document);
    }

    /// <summary>
    /// The contributors a host has, by entity type. A type claimed by TWO contributors is claimed
    /// by NEITHER: both are excluded and the collision logged at Error - the BL-105 posture, so a packaged module cannot
    /// take over another's type or brick the host. An in-tree duplicate is a build-gate failure instead.
    /// </summary>
    public sealed class PortabilityContributorRegistry
    {
        private readonly Dictionary<string, IPortableEntityContributor> _byType;

        public PortabilityContributorRegistry(IEnumerable<IPortableEntityContributor> contributors, ILogger<PortabilityContributorRegistry> logger)
        {
            _byType = new Dictionary<string, IPortableEntityContributor>(StringComparer.Ordinal);
            foreach (var group in contributors.GroupBy(contributor => contributor.EntityType, StringComparer.Ordinal))
            {
                var claimants = group.ToList();
                if (claimants.Count == 1)
                {
                    _byType[group.Key] = claimants[0];
                }
                else
                {
                    Excluded.Add(group.Key);
                    logger.LogError(
                        "Portability: the entity type {EntityType} is claimed by {Count} contributors ({Contributors}); it is EXCLUDED from export and import until one of them is removed.",
                        group.Key, claimants.Count, string.Join(", ", claimants.Select(claimant => claimant.GetType().FullName)));
                }
            }
        }

        /// <summary>Types claimed more than once, hence by nobody.</summary>
        public List<string> Excluded { get; } = new();

        public IReadOnlyCollection<string> EntityTypes => _byType.Keys;

        public IPortableEntityContributor? Find(string entityType) =>
            _byType.TryGetValue(entityType, out var contributor) ? contributor : null;
    }

    /// <summary>
    /// Imports a configuration package: <b>validate everything, then change anything</b>. The size cap, the
    /// format, the kind, every entity's invariants, a readable schema, no secrets, a contributor per type, the manifest,
    /// the dependency closure and order, and each contributor's own <see cref="IPortableEntityContributor.ValidateImportAsync"/>
    /// are ALL checked before the first <see cref="IPortableEntityContributor.ImportAsync"/> - the predecessor platform ran its providers in
    /// document order and validated afterwards, so a bad entity left side effects with no report.
    /// </summary>
    public sealed class PortabilityImporter(PortabilityContributorRegistry registry, PortabilityBundleSerializer serializer)
    {
        public async Task<ImportReport> ImportAsync(string json, long maxBytes, ImportContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // ---- the size cap, before deserialisation ----------------------------------------------------------------
            var bytes = Encoding.UTF8.GetByteCount(json ?? string.Empty);
            if (bytes > maxBytes)
            {
                return ImportReport.Refused(new[] { $"The package is {bytes:N0} bytes; the limit is {maxBytes:N0}." });
            }

            var read = serializer.Read(json ?? string.Empty);
            if (!read.Success)
            {
                return ImportReport.Refused(read.Report);
            }

            var document = read.Document!;
            var refusals = Validate(document);
            if (refusals.Count > 0)
            {
                return ImportReport.Refused(refusals, document);
            }

            var entities = document.Entities.Select(ToPortable).ToList();
            var ordered = Order(entities, refusals);
            if (refusals.Count > 0)
            {
                return ImportReport.Refused(refusals, document);
            }

            // ---- each contributor's own checks, still before any side effect ------------------------------------------
            foreach (var entity in ordered)
            {
                var reason = await registry.Find(entity.Ref.EntityType)!.ValidateImportAsync(entity, context, cancellationToken);
                if (reason is not null)
                {
                    refusals.Add($"{entity.Ref}: {reason}");
                }
            }

            if (refusals.Count > 0)
            {
                return ImportReport.Refused(refusals, document);
            }

            // ---- only now: the side effects ----------------------------------------------------------------------------
            var lines = new List<ImportReportLine>();
            foreach (var entity in ordered)
            {
                EntityImportOutcome outcome;
                try
                {
                    outcome = await registry.Find(entity.Ref.EntityType)!.ImportAsync(entity, context, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    outcome = new EntityImportOutcome(EntityImportResult.Failed, null, ex.GetType().Name);
                }

                if (outcome.TargetId is { } target)
                {
                    context.IdMap.Map(entity.Ref, target);
                }

                lines.Add(new ImportReportLine(entity.Ref.EntityType, entity.Ref.EntityId, entity.DisplayName, outcome.Result, outcome.TargetId, outcome.Reason));
            }

            return new ImportReport(true, Array.Empty<string>(), lines, document);
        }

        /// <summary>Every refusal a document earns on its own, without asking a contributor. Empty = acceptable.</summary>
        public List<string> Validate(PortabilityBundleDocument document)
        {
            var refusals = new List<string>();
            if (!string.Equals(document.Kind, PortabilityPackageKinds.ConfigTransfer, StringComparison.Ordinal))
            {
                refusals.Add($"A package of kind '{document.Kind}' cannot be imported; only {PortabilityPackageKinds.ConfigTransfer} packages can.");
                return refusals;
            }

            var seen = new HashSet<(string, string)>();
            foreach (var entity in document.Entities)
            {
                var label = $"{entity.EntityType}:{entity.EntityId}";
                if (string.IsNullOrWhiteSpace(entity.EntityType) || string.IsNullOrWhiteSpace(entity.EntityId))
                {
                    refusals.Add($"An entity has a blank type or id ({label}).");
                    continue;
                }

                if (!seen.Add((entity.EntityType, entity.EntityId)))
                {
                    refusals.Add($"{label} appears twice.");
                }

                if (entity.SchemaVersion < 1)
                {
                    refusals.Add($"{label} has schema version {entity.SchemaVersion}; versions start at 1.");
                }

                if (entity.ContainsSecrets)
                {
                    refusals.Add($"{label} is marked as containing secrets; secrets are never imported.");
                }

                if (!IsJson(entity.Payload))
                {
                    refusals.Add($"{label} has a payload that is not JSON.");
                }

                var contributor = registry.Find(entity.EntityType);
                if (contributor is null)
                {
                    refusals.Add($"{label}: nothing on this installation imports '{entity.EntityType}'.");
                }
                else if (entity.SchemaVersion >= 1 && !contributor.ReadableSchemaVersions.Contains(entity.SchemaVersion))
                {
                    refusals.Add($"{label}: schema version {entity.SchemaVersion} is not readable here (readable: {string.Join(", ", contributor.ReadableSchemaVersions.OrderBy(v => v))}).");
                }

                foreach (var dependency in entity.Dependencies ?? Array.Empty<PortabilityBundleRef>())
                {
                    if (string.IsNullOrWhiteSpace(dependency.EntityType) || string.IsNullOrWhiteSpace(dependency.EntityId))
                    {
                        refusals.Add($"{label} has a dependency with a blank type or id.");
                    }
                }
            }

            // The manifest must describe exactly what the package carries.
            var actual = document.Entities
                .GroupBy(entity => (entity.EntityType, entity.SchemaVersion))
                .ToDictionary(group => group.Key, group => group.Count());
            var declared = new Dictionary<(string, int), int>();
            foreach (var line in document.Manifest.Entries)
            {
                declared[(line.EntityType, line.SchemaVersion)] = declared.GetValueOrDefault((line.EntityType, line.SchemaVersion)) + line.Count;
            }

            if (actual.Count != declared.Count || actual.Any(pair => declared.GetValueOrDefault(pair.Key) != pair.Value))
            {
                refusals.Add("The manifest does not match the entities the package carries.");
            }

            return refusals;
        }

        /// <summary>Dependencies first. A dependency missing from the package, or a cycle, refuses the whole package.</summary>
        private static List<PortableEntity> Order(List<PortableEntity> entities, List<string> refusals)
        {
            var byRef = entities.ToDictionary(entity => (entity.Ref.EntityType, entity.Ref.EntityId));
            foreach (var entity in entities)
            {
                foreach (var dependency in entity.Dependencies)
                {
                    if (!byRef.ContainsKey((dependency.EntityType, dependency.EntityId)))
                    {
                        refusals.Add($"{entity.Ref} depends on {dependency}, which the package does not carry.");
                    }
                }
            }

            if (refusals.Count > 0)
            {
                return entities;
            }

            var ordered = new List<PortableEntity>();
            var state = new Dictionary<(string, string), int>(); // 1 = visiting, 2 = done
            foreach (var entity in entities)
            {
                if (!Visit(entity))
                {
                    refusals.Add($"The package's dependencies form a cycle (through {entity.Ref}).");
                    return entities;
                }
            }

            return ordered;

            bool Visit(PortableEntity entity)
            {
                var key = (entity.Ref.EntityType, entity.Ref.EntityId);
                if (state.TryGetValue(key, out var mark))
                {
                    return mark == 2;
                }

                state[key] = 1;
                foreach (var dependency in entity.Dependencies)
                {
                    if (!Visit(byRef[(dependency.EntityType, dependency.EntityId)]))
                    {
                        return false;
                    }
                }

                state[key] = 2;
                ordered.Add(entity);
                return true;
            }
        }

        private static PortableEntity ToPortable(PortabilityBundleEntity entity) =>
            new(new PortableEntityRef(entity.EntityType, entity.EntityId),
                entity.DisplayName ?? string.Empty,
                entity.SchemaVersion,
                entity.Payload,
                entity.ContainsSecrets,
                (entity.Dependencies ?? Array.Empty<PortabilityBundleRef>()).Select(d => new PortableEntityRef(d.EntityType, d.EntityId)).ToList());

        private static bool IsJson(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                using var _ = JsonDocument.Parse(payload);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
