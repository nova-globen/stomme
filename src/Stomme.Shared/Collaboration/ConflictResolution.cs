#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Collaboration
{
    // ============================================================================================================
    // The conflict-resolution seam (BL-97 increment 12, D114), lifted from the predecessor platform's Collaboration.Abstractions at 183f5c52:
    // bounded coordination, not CRDT or OT. The owning module presents its resource as field paths and values; the
    // resolver reconciles an editor's proposed changes against what is stored now. BCL-ONLY like the other Shared contracts,
    // so a page can show a rejection's conflicts and a merge's warnings. CHANGED for all of it: a user is a Stomme user
    // id (an int), a snapshot's version is an opaque STRING token (Stomme's stores keep no numeric change vector - the
    // owning module supplies what it has, the pipeline graph a digest of its fields), and times are UTC DateTimes.
    // ============================================================================================================

    /// <summary>
    /// How a field merges when two editors change it concurrently: a structured field's same-field conflict rejects the save
    /// with the current value; a free-text field (a script, an expression) is last-writer-wins with a warning.
    /// </summary>
    public enum FieldKind
    {
        /// <summary>A name, a setting, a connection: a concurrent change to the same field rejects the save.</summary>
        Structured = 0,

        /// <summary>A free-text field: a concurrent change to the same field is overwritten, and the overwrite reported.</summary>
        Text = 1,
    }

    /// <summary>
    /// One field an editor proposes to change: its path within the resource (<c>step/3/name</c>), the value to write (null
    /// clears it) and how it merges. Distinct elements have disjoint path prefixes, so their edits never collide.
    /// </summary>
    public sealed record FieldChange
    {
        private FieldChange(string path, string? proposedValue, FieldKind kind)
        {
            Path = path;
            ProposedValue = proposedValue;
            Kind = kind;
        }

        public string Path { get; }

        public string? ProposedValue { get; }

        public FieldKind Kind { get; }

        /// <exception cref="ArgumentException">The path is blank.</exception>
        public static FieldChange Of(string path, string? proposedValue, FieldKind kind = FieldKind.Structured)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return new FieldChange(path.Trim(), proposedValue, kind);
        }
    }

    /// <summary>
    /// A version-stamped, field-level snapshot of a resource as its module presents it: a version token (equal tokens mean
    /// nothing changed in between), the fields as path to value (an absent path and a null value are the same), and, when
    /// the module knows, who wrote the stored state - so a last-writer-wins warning can name whose text it overwrote.
    /// </summary>
    public sealed record ResourceSnapshot
    {
        private ResourceSnapshot(string version, IReadOnlyDictionary<string, string?> fields, int? lastAuthorUserId)
        {
            Version = version;
            Fields = fields;
            LastAuthorUserId = lastAuthorUserId;
        }

        public string Version { get; }

        public IReadOnlyDictionary<string, string?> Fields { get; }

        public int? LastAuthorUserId { get; }

        /// <exception cref="ArgumentException">The version is blank.</exception>
        public static ResourceSnapshot Of(string version, IReadOnlyDictionary<string, string?> fields, int? lastAuthorUserId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
            ArgumentNullException.ThrowIfNull(fields);
            return new ResourceSnapshot(version, new Dictionary<string, string?>(fields, StringComparer.Ordinal), lastAuthorUserId);
        }
    }

    public enum ConflictResolutionStatus
    {
        /// <summary>Nothing changed concurrently: every proposed change applies as it is.</summary>
        Applied = 0,

        /// <summary>A concurrent change was reconciled field by field; any text overwrite is in the warnings.</summary>
        Merged = 1,

        /// <summary>A structured field was changed concurrently: nothing applies, and each conflict carries the current value.</summary>
        Rejected = 2,
    }

    /// <summary>A structured field changed by someone else since the editor loaded it: what they proposed, and what is stored now.</summary>
    public sealed record FieldConflict(string Path, string? ProposedValue, string? CurrentValue);

    /// <summary>A free-text field overwritten last-writer-wins: the value that won, the one it overwrote, whose, and when.</summary>
    public sealed record ConflictWarning(string Path, string? WinningValue, string? OverwrittenValue, int WinningAuthorUserId, int? OverwrittenAuthorUserId, DateTime OccurredUtc);

    /// <summary>
    /// The outcome of reconciling a proposed save: whether it applied, merged or was rejected; the whole field set to store
    /// when it applies (the current fields with the changes laid over them, so a merge keeps both editors' work); and the
    /// warnings and conflicts to show.
    /// </summary>
    public sealed record ConflictResolution
    {
        private ConflictResolution(ConflictResolutionStatus status, string baseVersion, string currentVersion, IReadOnlyDictionary<string, string?> mergedFields, IReadOnlyList<ConflictWarning> warnings, IReadOnlyList<FieldConflict> conflicts)
        {
            Status = status;
            BaseVersion = baseVersion;
            CurrentVersion = currentVersion;
            MergedFields = mergedFields;
            Warnings = warnings;
            Conflicts = conflicts;
        }

        public ConflictResolutionStatus Status { get; }

        public string BaseVersion { get; }

        public string CurrentVersion { get; }

        /// <summary>The whole field set to store; empty when the save was rejected.</summary>
        public IReadOnlyDictionary<string, string?> MergedFields { get; }

        public IReadOnlyList<ConflictWarning> Warnings { get; }

        public IReadOnlyList<FieldConflict> Conflicts { get; }

        public static ConflictResolution AppliedCleanly(string baseVersion, string currentVersion, IReadOnlyDictionary<string, string?> mergedFields) =>
            new(ConflictResolutionStatus.Applied, baseVersion, currentVersion, Require(mergedFields), Array.Empty<ConflictWarning>(), Array.Empty<FieldConflict>());

        public static ConflictResolution MergedFieldLevel(string baseVersion, string currentVersion, IReadOnlyDictionary<string, string?> mergedFields, IReadOnlyList<ConflictWarning> warnings) =>
            new(ConflictResolutionStatus.Merged, baseVersion, currentVersion, Require(mergedFields), Require(warnings), Array.Empty<FieldConflict>());

        /// <exception cref="ArgumentException">No conflict was given - a rejection names at least one.</exception>
        public static ConflictResolution RejectedWith(string baseVersion, string currentVersion, IReadOnlyList<FieldConflict> conflicts)
        {
            Require(conflicts);
            if (conflicts.Count == 0)
            {
                throw new ArgumentException("A rejection requires at least one field conflict.", nameof(conflicts));
            }

            return new ConflictResolution(ConflictResolutionStatus.Rejected, baseVersion, currentVersion, new Dictionary<string, string?>(StringComparer.Ordinal), Array.Empty<ConflictWarning>(), conflicts);
        }

        private static T Require<T>(T value)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(value);
            return value;
        }
    }

    /// <summary>
    /// The conflict-resolution port (the predecessor platform's <c>IConflictResolver</c>): no concurrent change applies as it is; concurrent
    /// changes to disjoint fields merge field by field; a same-field change on a structured field rejects the save with the
    /// current value; on a text field it is last-writer-wins with a warning naming what, and whose, was overwritten.
    /// Artifact-agnostic: the owning module presents its resource as a <see cref="ResourceSnapshot"/> and stores the merged
    /// fields itself.
    /// </summary>
    public interface IConflictResolver
    {
        /// <summary>Reconciles <paramref name="proposedChanges"/>, based on <paramref name="baseSnapshot"/>, against <paramref name="currentSnapshot"/>.</summary>
        /// <exception cref="ArgumentException">A proposed path appears twice, or no author is named.</exception>
        ConflictResolution Resolve(ResourceSnapshot baseSnapshot, ResourceSnapshot currentSnapshot, IReadOnlyList<FieldChange> proposedChanges, int authorUserId);

        /// <summary>
        /// Resolves and stores in one bounded loop: load the current snapshot, resolve, and - unless rejected - store the
        /// merged fields through <paramref name="saveAsync"/>. A store that refuses the write as stale is reloaded and the
        /// changes re-resolved, a bounded number of times; a rejected resolution is returned unstored.
        /// </summary>
        Task<ConflictResolution> ResolveAndSaveAsync(
            ResourceSnapshot baseSnapshot,
            IReadOnlyList<FieldChange> proposedChanges,
            int authorUserId,
            Func<CancellationToken, Task<ResourceSnapshot>> loadCurrentAsync,
            Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task> saveAsync,
            CancellationToken cancellationToken = default);
    }
}
