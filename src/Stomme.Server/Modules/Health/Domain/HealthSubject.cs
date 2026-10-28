#nullable enable
using System;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Health.Domain
{
    /// <summary>
    /// What a rollup or an alert is ABOUT — a contributor's probe, as <c>&lt;contributorId&gt;:&lt;probeName&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a value object rather than a bare string so that the normalisation happens ONCE, at construction,
    /// instead of at each of the places that build a deterministic id from it. That matters more here than it
    /// looks: the id of every rollup and every alert row is derived from this text, so two spellings of the same
    /// subject are two rows, and the alert machine would then see a dependency flapping between two identities
    /// and re-raise forever.
    /// </para>
    /// <para>
    /// <b>It is never used as a QUERY predicate.</b> Strings diverge across the providers this must run on — bare
    /// equality is case-sensitive on EF and the File store and case-INSENSITIVE on RavenDB, and
    /// <c>RavenRepository.FindAsync</c> has no rewriting so <c>Contains</c> throws outright. Rows are therefore
    /// addressed by their deterministic id, and any listing filters on the scalar columns (site, scope, window,
    /// period) and matches the subject in memory. See <c>HealthIds</c>.
    /// </para>
    /// <para>
    /// <see cref="MaxLength"/> is enforced here, not by the store: the relational column is created by the shared
    /// reflective context and a value that is too long would be a provider error at write time on one provider and
    /// a silent truncation on another.
    /// </para>
    /// </remarks>
    public sealed class HealthSubject : IStringValueObject, IEquatable<HealthSubject>
    {
        public const int MaxLength = 100;

        private HealthSubject(string value) => Value = value;

        public string Value { get; }

        /// <summary>Builds a subject from a contributor id and a probe name.</summary>
        public static HealthSubject For(string contributorId, string probeName) =>
            Create(contributorId + ":" + probeName);

        /// <summary>
        /// The factory the stores reconstruct through (<see cref="IStringValueObject"/>), and the one place the
        /// text is normalised: trimmed, lower-cased invariantly, inner whitespace removed.
        /// </summary>
        public static HealthSubject Create(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A health subject cannot be empty.", nameof(value));
            }

            var normalized = value.Trim().ToLowerInvariant().Replace(" ", string.Empty);
            if (normalized.Length > MaxLength)
            {
                throw new ArgumentException(
                    $"A health subject cannot exceed {MaxLength} characters; '{normalized}' is {normalized.Length}.",
                    nameof(value));
            }

            return new HealthSubject(normalized);
        }

        public bool Equals(HealthSubject? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as HealthSubject);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

        public override string ToString() => Value;
    }
}
