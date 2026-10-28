#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Stomme.Infrastructure.Governance
{
    /// <summary>The kinds of personally identifiable information the scanner recognises: an e-mail address, a US Social Security Number, a Luhn-valid payment-card number.</summary>
    public enum PiiCategory
    {
        Email = 0,
        SocialSecurityNumber = 1,
        CreditCard = 2,
    }

    /// <summary>One occurrence of PII in scanned text: its category and the matched span.</summary>
    public sealed record PiiMatch(PiiCategory Category, string Value, int Index, int Length);

    /// <summary>What a masked occurrence is replaced with.</summary>
    public static class RedactionMarker
    {
        public const string Value = "[REDACTED]";
    }

    /// <summary>
    /// Scans text for PII and masks it (BL-94 increment 9, lifted from the predecessor platform's Data Governance building block, D110 Q7).
    /// The scanner DETECTS regardless of any setting; whether a consumer masks is that consumer's policy.
    /// </summary>
    public interface IPiiScanner
    {
        /// <summary>Every PII occurrence in <paramref name="text"/>, ordered by position, non-overlapping.</summary>
        IReadOnlyList<PiiMatch> Scan(string text);

        /// <summary><paramref name="text"/> with every occurrence replaced by <see cref="RedactionMarker.Value"/>.</summary>
        string Mask(string text);

        bool ContainsPii(string text);
    }

    /// <summary>The default scanner: three source-generated patterns; card candidates are Luhn-checked so a long digit run that is not a card is not over-masked.</summary>
    public sealed partial class PiiScanner : IPiiScanner
    {
        [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
        private static partial Regex EmailRegex();

        [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.CultureInvariant)]
        private static partial Regex SsnRegex();

        [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.CultureInvariant)]
        private static partial Regex CreditCardCandidateRegex();

        public IReadOnlyList<PiiMatch> Scan(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length == 0)
            {
                return Array.Empty<PiiMatch>();
            }

            var matches = new List<PiiMatch>();
            Collect(matches, EmailRegex(), text, PiiCategory.Email);
            Collect(matches, SsnRegex(), text, PiiCategory.SocialSecurityNumber);
            foreach (var candidate in CreditCardCandidateRegex().EnumerateMatches(text))
            {
                if (IsLuhnValid(text.AsSpan(candidate.Index, candidate.Length)))
                {
                    matches.Add(new PiiMatch(PiiCategory.CreditCard, text.Substring(candidate.Index, candidate.Length), candidate.Index, candidate.Length));
                }
            }

            matches.Sort((a, b) => a.Index.CompareTo(b.Index));
            var result = new List<PiiMatch>(matches.Count);
            var consumedTo = -1;
            foreach (var match in matches)
            {
                if (match.Index <= consumedTo)
                {
                    continue;
                }

                result.Add(match);
                consumedTo = match.Index + match.Length - 1;
            }

            return result;
        }

        public string Mask(string text)
        {
            var matches = Scan(text);
            if (matches.Count == 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            var cursor = 0;
            foreach (var match in matches)
            {
                builder.Append(text, cursor, match.Index - cursor);
                builder.Append(RedactionMarker.Value);
                cursor = match.Index + match.Length;
            }

            builder.Append(text, cursor, text.Length - cursor);
            return builder.ToString();
        }

        public bool ContainsPii(string text) => Scan(text).Count > 0;

        private static void Collect(List<PiiMatch> sink, Regex regex, string text, PiiCategory category)
        {
            foreach (var match in regex.EnumerateMatches(text))
            {
                sink.Add(new PiiMatch(category, text.Substring(match.Index, match.Length), match.Index, match.Length));
            }
        }

        private static bool IsLuhnValid(ReadOnlySpan<char> candidate)
        {
            var sum = 0;
            var digits = 0;
            var doubleNext = false;
            for (var i = candidate.Length - 1; i >= 0; i--)
            {
                var c = candidate[i];
                if (!char.IsAsciiDigit(c))
                {
                    continue;
                }

                var d = c - '0';
                if (doubleNext)
                {
                    d *= 2;
                    if (d > 9)
                    {
                        d -= 9;
                    }
                }

                sum += d;
                doubleNext = !doubleNext;
                digits++;
            }

            return digits is >= 13 and <= 19 && sum % 10 == 0;
        }
    }
}
