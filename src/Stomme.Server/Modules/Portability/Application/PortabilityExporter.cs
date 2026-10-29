#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Portability;

namespace Stomme.Modules.Portability.Application
{
    /// <summary>
    /// Collects what a package carries: for a configuration package, the requested entities AND the
    /// closure of what they depend on, breadth first; for a subject-data package, every subject contributor's rows,
    /// de-duplicated by (type, id).
    /// </summary>
    /// <remarks>
    /// <b>Refuses rather than ships a broken package:</b> a dependency no contributor returns, a type nobody contributes and
    /// a configuration entity marked as holding a secret each throw - the build is recorded as Failed with the reason, and
    /// no artifact exists that an import would later refuse.
    /// </remarks>
    public sealed class PortabilityExporter(PortabilityContributorRegistry registry)
    {
        /// <param name="roots">Entity type to the ids wanted - null ids meaning every entity of that type on the site.</param>
        public async Task<IReadOnlyList<PortableEntity>> ExportConfigAsync(
            int siteId, IReadOnlyDictionary<string, IReadOnlyCollection<string>?> roots, CancellationToken cancellationToken = default)
        {
            var collected = new Dictionary<(string, string), PortableEntity>();
            var pending = new Queue<(string Type, IReadOnlyCollection<string>? Ids)>(roots.Select(root => (root.Key, root.Value)));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (type, ids) = pending.Dequeue();
                var contributor = registry.Find(type)
                    ?? throw new InvalidOperationException($"Nothing on this installation exports '{type}'.");

                var wanted = ids?.Where(id => !collected.ContainsKey((type, id))).Distinct(StringComparer.Ordinal).ToList();
                if (wanted is { Count: 0 })
                {
                    continue;
                }

                var exported = await contributor.ExportAsync(new PortabilityScope(siteId, wanted), cancellationToken);
                foreach (var entity in exported)
                {
                    if (!string.Equals(entity.Ref.EntityType, type, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"The '{type}' contributor returned an entity of type '{entity.Ref.EntityType}'.");
                    }

                    if (entity.ContainsSecrets)
                    {
                        throw new InvalidOperationException($"{entity.Ref} is marked as containing a secret; a configuration package never carries one.");
                    }

                    collected[(type, entity.Ref.EntityId)] = entity;
                }

                if (wanted is not null)
                {
                    var missing = wanted.Where(id => !collected.ContainsKey((type, id))).ToList();
                    if (missing.Count > 0)
                    {
                        throw new InvalidOperationException($"'{type}' {string.Join(", ", missing)} could not be exported from this site.");
                    }
                }

                foreach (var dependencies in exported
                    .SelectMany(entity => entity.Dependencies)
                    .Where(dependency => !collected.ContainsKey((dependency.EntityType, dependency.EntityId)))
                    .GroupBy(dependency => dependency.EntityType, StringComparer.Ordinal))
                {
                    pending.Enqueue((dependencies.Key, dependencies.Select(dependency => dependency.EntityId).Distinct(StringComparer.Ordinal).ToList()));
                }
            }

            return collected.Values.ToList();
        }

        public static async Task<IReadOnlyList<PortableEntity>> ExportSubjectAsync(
            IEnumerable<ISubjectDataContributor> contributors, SubjectScope scope, CancellationToken cancellationToken = default)
        {
            var collected = new Dictionary<(string, string), PortableEntity>();
            foreach (var contributor in contributors)
            {
                foreach (var entity in await contributor.ExportForSubjectAsync(scope, cancellationToken))
                {
                    collected.TryAdd((entity.Ref.EntityType, entity.Ref.EntityId), entity);
                }
            }

            return collected.Values.ToList();
        }
    }
}
