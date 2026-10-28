using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Stomme.SharedKernel.Domain;

namespace NovaGloben.Module.KnowledgeBase.Domain
{
    /// <summary>
    /// An article's title: a VALUE OBJECT marked <see cref="IStringValueObject"/>, so every store the host can choose
    /// (EF, RavenDB, File) keeps it as one plain string column or field - the sanctioned, non-fragile way (D79) - while the
    /// domain keeps its rule in one place.
    /// </summary>
    public sealed class ArticleTitle : ValueObject, IStringValueObject
    {
        public const int MaxLength = 150;

        private ArticleTitle(string value) => Value = value;

        public string Value { get; }

        public static ArticleTitle Create(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("An article needs a title.", nameof(value));
            }

            if (trimmed.Length > MaxLength)
            {
                throw new ArgumentException($"An article title is at most {MaxLength} characters.", nameof(value));
            }

            return new ArticleTitle(trimmed);
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>
    /// The URL segment of an article or a category: lower-case ASCII letters, digits and single hyphens. It is stored
    /// ALREADY NORMALISED and every lookup normalises its argument the same way, because string equality differs between
    /// the stores (case-sensitive on EF and File, case-insensitive on RavenDB) - normalise the data, do not translate the
    /// query (D49). A slug derived from a title drops what it cannot express.
    /// </summary>
    public static class Slug
    {
        public const int MaxLength = 80;

        /// <summary>The slug a free text becomes: lower-case, runs of anything else collapsed to one hyphen, trimmed.</summary>
        public static string From(string text)
        {
            var builder = new StringBuilder();
            var pendingHyphen = false;
            foreach (var character in (text ?? string.Empty).Normalize(NormalizationForm.FormD))
            {
                if (character is >= 'a' and <= 'z' or >= '0' and <= '9' || character is >= 'A' and <= 'Z')
                {
                    if (pendingHyphen && builder.Length > 0)
                    {
                        builder.Append('-');
                    }

                    builder.Append(char.ToLowerInvariant(character));
                    pendingHyphen = false;
                }
                else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    pendingHyphen = true;
                }
            }

            var slug = builder.ToString();
            return slug.Length > MaxLength ? slug.Substring(0, MaxLength).TrimEnd('-') : slug;
        }

        /// <summary>A slug given explicitly: normalised as <see cref="From"/> does, and refused when nothing is left.</summary>
        public static string Create(string value)
        {
            var slug = From(value);
            if (slug.Length == 0)
            {
                throw new ArgumentException("A slug needs at least one letter or digit.", nameof(value));
            }

            return slug;
        }

        /// <summary>Whether a value is already a normalised slug.</summary>
        public static bool IsNormalised(string value) => !string.IsNullOrEmpty(value) && From(value) == value;
    }
}
