#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Persistence.Abstractions;

namespace Stomme.Modules.Collaboration.Application
{
    /// <summary>
    /// The bounded conflict resolver (BL-97 increment 12, the predecessor platform's <c>ConflictResolver</c>) - coordination, not CRDT or OT.
    /// Equal version tokens mean nothing changed concurrently and the proposal applies as it is; otherwise each proposed field
    /// is reconciled against the stored one: a field the concurrent save did not touch applies (both editors' work is kept),
    /// a convergent same-value write is no conflict, a same-field divergence on a <see cref="FieldKind.Text"/> field is
    /// last-writer-wins with a <see cref="ConflictWarning"/>, and on a <see cref="FieldKind.Structured"/> field it rejects
    /// the whole save with the current value. <see cref="ResolveAndSaveAsync"/> retries around the store's
    /// <see cref="ConcurrencyConflictException"/> at most <see cref="MaxSaveAttempts"/> times. Stateless: one per host.
    /// </summary>
    public sealed class ConflictResolver(TimeProvider time) : IConflictResolver
    {
        /// <summary>Deliberately small: under real contention the editor should see the conflict, not spin against it.</summary>
        public const int MaxSaveAttempts = 3;

        public ConflictResolution Resolve(ResourceSnapshot baseSnapshot, ResourceSnapshot currentSnapshot, IReadOnlyList<FieldChange> proposedChanges, int authorUserId)
        {
            ValidateProposal(baseSnapshot, proposedChanges, authorUserId);
            ArgumentNullException.ThrowIfNull(currentSnapshot);
            var merged = new Dictionary<string, string?>(currentSnapshot.Fields, StringComparer.Ordinal);
            if (string.Equals(currentSnapshot.Version, baseSnapshot.Version, StringComparison.Ordinal))
            {
                // Nothing changed since the editor loaded: every proposed change applies as it is.
                foreach (var change in proposedChanges)
                {
                    merged[change.Path] = change.ProposedValue;
                }

                return ConflictResolution.AppliedCleanly(baseSnapshot.Version, currentSnapshot.Version, merged);
            }

            var warnings = new List<ConflictWarning>();
            var conflicts = new List<FieldConflict>();
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var change in proposedChanges)
            {
                var baseValue = baseSnapshot.Fields.GetValueOrDefault(change.Path);
                var currentValue = currentSnapshot.Fields.GetValueOrDefault(change.Path);
                if (string.Equals(currentValue, baseValue, StringComparison.Ordinal) || string.Equals(currentValue, change.ProposedValue, StringComparison.Ordinal))
                {
                    // The concurrent save did not touch this field, or wrote the same value.
                    merged[change.Path] = change.ProposedValue;
                    continue;
                }

                if (change.Kind == FieldKind.Text)
                {
                    // Free text: the last writer wins, and the overwrite is reported.
                    merged[change.Path] = change.ProposedValue;
                    warnings.Add(new ConflictWarning(change.Path, change.ProposedValue, currentValue, authorUserId, currentSnapshot.LastAuthorUserId, now));
                    continue;
                }

                // A structured field changed under the editor: optimistic concurrency rejects the save.
                conflicts.Add(new FieldConflict(change.Path, change.ProposedValue, currentValue));
            }

            return conflicts.Count > 0
                ? ConflictResolution.RejectedWith(baseSnapshot.Version, currentSnapshot.Version, conflicts)
                : ConflictResolution.MergedFieldLevel(baseSnapshot.Version, currentSnapshot.Version, merged, warnings);
        }

        public async Task<ConflictResolution> ResolveAndSaveAsync(
            ResourceSnapshot baseSnapshot,
            IReadOnlyList<FieldChange> proposedChanges,
            int authorUserId,
            Func<CancellationToken, Task<ResourceSnapshot>> loadCurrentAsync,
            Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task> saveAsync,
            CancellationToken cancellationToken = default)
        {
            ValidateProposal(baseSnapshot, proposedChanges, authorUserId);
            ArgumentNullException.ThrowIfNull(loadCurrentAsync);
            ArgumentNullException.ThrowIfNull(saveAsync);
            for (var attempt = 1; ; attempt++)
            {
                var current = await loadCurrentAsync(cancellationToken);
                var resolution = Resolve(baseSnapshot, current, proposedChanges, authorUserId);
                if (resolution.Status == ConflictResolutionStatus.Rejected)
                {
                    return resolution; // nothing to store: the editor re-applies against the current value
                }

                try
                {
                    await saveAsync(resolution.MergedFields, cancellationToken);
                    return resolution;
                }
                catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
                {
                    // Another save won the race between the load and this save: reload and re-resolve.
                }
            }
        }

        private static void ValidateProposal(ResourceSnapshot baseSnapshot, IReadOnlyList<FieldChange> proposedChanges, int authorUserId)
        {
            ArgumentNullException.ThrowIfNull(baseSnapshot);
            ArgumentNullException.ThrowIfNull(proposedChanges);
            if (authorUserId <= 0)
            {
                throw new ArgumentException("Conflict resolution requires the proposing author.", nameof(authorUserId));
            }

            var duplicate = proposedChanges.GroupBy(change => change.Path, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new ArgumentException($"Proposed changes name the field '{duplicate.Key}' more than once.", nameof(proposedChanges));
            }
        }
    }
}
