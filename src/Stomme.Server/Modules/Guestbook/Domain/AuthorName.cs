using System;
using System.Collections.Generic;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Guestbook.Domain
{
    /// <summary>
    /// A VALUE OBJECT (docs/decisions.md D1; <see cref="ValueObject"/>) for a guestbook signer's name. It has
    /// no identity — two <see cref="AuthorName"/>s with the same normalized value are equal — and it is
    /// immutable. Constructing one is the invariant: the name is trimmed and must be non-empty and at most
    /// <see cref="MaxLength"/> characters. Modelling the name as a value object (rather than a bare string on
    /// the aggregate) means the "a name is valid" rule lives in exactly one place and cannot be bypassed by
    /// assigning a raw string, and it is a small, honest example of the pattern a real domain uses for money,
    /// e-mail addresses, coordinates and so on.
    /// <para>
    /// It implements <see cref="IStringValueObject"/> so the RavenDB store round-trips it as a clean
    /// scalar JSON string through its public <see cref="Create(string)"/> factory, rather than relying on
    /// Newtonsoft matching the private constructor to a <c>{"Value":"..."}</c> object — which happens to work
    /// today but is fragile (a second constructor or a renamed parameter would break it silently). The marker
    /// is the sanctioned, explicit path every value object on the document seam uses (see
    /// <c>ValueObjectJsonConverters</c>). On a relational install the SAME marker drives the convention: the
    /// host's shared <c>ModuleAggregatesDbContext</c> applies a string value converter to any
    /// <see cref="IStringValueObject"/> property automatically, so this module hand-writes NO converter and the
    /// column is a plain string.
    /// </para>
    /// </summary>
    public sealed class AuthorName : ValueObject, IStringValueObject
    {
        public const int MaxLength = 60;

        public string Value { get; }

        private AuthorName(string value) => Value = value;

        /// <summary>
        /// Creates a validated <see cref="AuthorName"/>. Throws <see cref="ArgumentException"/> on an empty or
        /// over-long name — the aggregate's last line of defence behind the command validator, so an invalid
        /// name is unrepresentable even if a caller reaches the domain directly.
        /// </summary>
        public static AuthorName Create(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("An author name is required.", nameof(value));
            }

            if (trimmed.Length > MaxLength)
            {
                throw new ArgumentException($"An author name must be at most {MaxLength} characters.", nameof(value));
            }

            return new AuthorName(trimmed);
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Value.ToLowerInvariant();
        }

        public override string ToString() => Value;
    }
}
