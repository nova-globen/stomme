using System;
using System.Collections.Generic;
using Stomme.SharedKernel.Domain;

namespace NovaGloben.Module.Blogs.Domain
{
    /// <summary>
    /// A VALUE OBJECT for a blog post's title. It has no identity — two <see cref="PostTitle"/>s with the same
    /// normalized value are equal — and is immutable. Constructing one is the invariant: the title is trimmed
    /// and must be non-empty and at most <see cref="MaxLength"/> characters, so an invalid title is
    /// unrepresentable.
    /// <para>
    /// It implements <see cref="IStringValueObject"/>, the sanctioned marker every value object on the seam
    /// uses. On a relational install the host's shared <c>ModuleAggregatesDbContext</c> maps any
    /// <see cref="IStringValueObject"/> property to a plain string column by convention (sized by the optional
    /// public <c>MaxLength</c> const), so this module hand-writes NO EF converter; on a RavenDB or file
    /// install the same marker round-trips it as a clean scalar JSON string. This is the storage-blind payoff:
    /// a rich value object that persists identically on every provider with zero provider code in the module.
    /// </para>
    /// </summary>
    public sealed class PostTitle : ValueObject, IStringValueObject
    {
        public const int MaxLength = 150;

        public string Value { get; }

        private PostTitle(string value) => Value = value;

        /// <summary>
        /// Creates a validated <see cref="PostTitle"/>. Throws <see cref="ArgumentException"/> on an empty or
        /// over-long title — the aggregate's last line of defence, so an invalid title cannot be persisted even
        /// if a caller reaches the domain directly.
        /// </summary>
        public static PostTitle Create(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("A blog post title is required.", nameof(value));
            }

            if (trimmed.Length > MaxLength)
            {
                throw new ArgumentException($"A blog post title must be at most {MaxLength} characters.", nameof(value));
            }

            return new PostTitle(trimmed);
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Value.ToLowerInvariant();
        }

        public override string ToString() => Value;
    }
}
