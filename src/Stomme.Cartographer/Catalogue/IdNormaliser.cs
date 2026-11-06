using System.Globalization;
using System.Text.RegularExpressions;
using Stomme.Cartographer.Survey;

namespace Stomme.Cartographer.Catalogue;

/// <summary>An id as the catalogue keys it (ADR 0020 rule 1): padding stripped, a suffix kept apart, a lost possessive dropped.</summary>
/// <param name="Key">The normalised id, suffix included when there is one (BL-68, BL-4b, BL-26(b), BL-1(v1)).</param>
/// <param name="Base">The id without its suffix (BL-4 for BL-4b).</param>
/// <param name="Scheme">The scheme's name.</param>
/// <param name="Number">The scheme's number, when the scheme is numeric.</param>
/// <param name="Suffix">A suffix after the number (b, (b), (v1)), or null.</param>
public sealed record NormalisedId(string Key, string Base, string Scheme, int? Number, string? Suffix);

/// <summary>
/// Normalises ids before counting or linking (ADR 0020 rule 1): D001 is D1; a suffixed key (BL-4b, BL-1 (v1),
/// BL-26(b)) is kept as a key of its own, so that the catalogue can decide whether a row defines it or it is a
/// sub-label of its base; a possessive that lost its apostrophe (BL-78s) is its base; a placeholder in prose
/// (BL-xx) is not an id.
/// </summary>
public static class IdNormaliser
{
    private static readonly Regex SuffixPattern = new(@"^\s?(?:(?<letters>[a-z]{1,2})(?![A-Za-z0-9])|\((?<paren>[a-z]?[0-9]{0,3})\))", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The id as the catalogue keys it, or null when no confirmed scheme matches the text.</summary>
    public static NormalisedId? Normalise(string? text, IReadOnlyList<IdScheme> schemes)
    {
        ArgumentNullException.ThrowIfNull(schemes);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var candidate = text.Trim().Trim('*', '`', '_', '[', ']', ' ');
        foreach (var scheme in schemes.Where(s => s.Confirmed && !string.IsNullOrWhiteSpace(s.Regex)))
        {
            Regex regex;
            try
            {
                // A scheme's regex usually ends in a word boundary, which a suffix (BL-4b) defeats; the key is
                // a single token here, so the boundary is not needed.
                var pattern = scheme.Regex.EndsWith(@"\b", StringComparison.Ordinal) ? scheme.Regex[..^2] : scheme.Regex;
                regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                continue;
            }

            Match match;
            try
            {
                match = regex.Match(candidate);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success || match.Index > 0)
            {
                continue;
            }

            var core = match.Value;
            int? number = null;
            if (scheme.NumericGroup is { } group && group > 0 && group < match.Groups.Count && match.Groups[group].Success
                && int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                number = n;
                var g = match.Groups[group];
                core = match.Value[..(g.Index - match.Index)] + n.ToString(CultureInfo.InvariantCulture) + match.Value[(g.Index - match.Index + g.Length)..];
            }

            var rest = candidate[(match.Index + match.Length)..];
            var suffix = SuffixOf(rest);
            if (suffix is null && rest.Length > 0 && char.IsAsciiLetterOrDigit(rest[0]))
            {
                // More digits or letters follow (D2020 against D<nnn>): not this scheme's id.
                continue;
            }

            if (suffix == "s")
            {
                // A possessive that lost its apostrophe is the base id (ADR 0020 rule 1).
                suffix = null;
            }

            return new NormalisedId(suffix is null ? core : core + suffix, core, scheme.Name, number, suffix);
        }

        return null;
    }

    private static string? SuffixOf(string rest)
    {
        if (rest.Length == 0)
        {
            return null;
        }

        var match = SuffixPattern.Match(rest);
        if (!match.Success)
        {
            return null;
        }

        if (match.Groups["letters"].Success)
        {
            return match.Groups["letters"].Value;
        }

        var inner = match.Groups["paren"].Value;
        return inner.Length == 0 ? null : "(" + inner + ")";
    }
}
