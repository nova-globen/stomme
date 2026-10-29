#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>One thing a guardrail found in a text: which check, what kind, and where - never the text it matched.</summary>
    public sealed record GuardrailFinding(string Check, string Kind, int Start, int Length)
    {
        /// <summary>A model check's confidence (0 to 1); null for a pattern check.</summary>
        public double? Confidence { get; init; }

        /// <summary>A model check's reason, in the model's words.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>How eager the secret-key check is (n8n's permissiveness).</summary>
    public enum SecretKeyStrictness
    {
        Off,
        Permissive,
        Balanced,
        Strict,
    }

    /// <summary>What a text is checked for by pattern - every list empty and every switch off checks nothing.</summary>
    public sealed class GuardrailPatterns
    {
        public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

        /// <summary>The kinds of personal data looked for (<see cref="TextGuardrails.PersonalDataKinds"/>); empty: none.</summary>
        public IReadOnlyList<string> PersonalData { get; init; } = Array.Empty<string>();

        public SecretKeyStrictness SecretKeys { get; init; }

        /// <summary>Whether URLs are checked at all.</summary>
        public bool Urls { get; init; }

        /// <summary>The hosts a URL may name (a URL naming another is found); empty: every URL is found.</summary>
        public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> AllowedSchemes { get; init; } = new[] { "https" };

        public bool AllowSubdomains { get; init; } = true;

        /// <summary>A URL carrying a user name or password (user:pass@host) is found whatever its host.</summary>
        public bool BlockUserInfo { get; init; } = true;

        public IReadOnlyList<(string Name, Regex Pattern)> Custom { get; init; } = Array.Empty<(string Name, Regex Pattern)>();
    }

    /// <summary>
    /// The pattern half of n8n's Guardrails node, written here (n8n's code is not under a licence this product can take):
    /// keywords, personal data, secret keys, URLs and custom patterns found in a text, each finding its check, its kind and where
    /// it is - never what it matched, so a run's data does not repeat the secret it caught - and the text with each finding
    /// replaced by its kind in brackets (Sanitize). Personal data is held to its checksum where it has one - a card number's
    /// Luhn digit, an IBAN's mod-97, a Swedish personal number's Luhn digit, a Finnish identity code's check character, a PESEL's
    /// and an NHS number's check digit - so a long number is not a card because it is long.
    /// </summary>
    public static class TextGuardrails
    {
        /// <summary>The longest any one pattern may take on a text.</summary>
        public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

        public const string Keyword = "Keywords";
        public const string PersonalDataCheck = "Personal data";
        public const string SecretKeyCheck = "Secret keys";
        public const string UrlCheck = "URLs";
        public const string CustomCheck = "Custom patterns";

        /// <summary>The kinds of personal data found - n8n's entity names where it has the kind.</summary>
        public static readonly IReadOnlyList<string> PersonalDataKinds = new[]
        {
            "EMAIL_ADDRESS", "PHONE_NUMBER", "CREDIT_CARD", "IBAN_CODE", "IP_ADDRESS", "US_SSN", "UK_NHS", "UK_NINO", "SE_PERSONAL_NUMBER",
            "FI_PERSONAL_IDENTITY_CODE", "PL_PESEL", "ES_NIF", "CRYPTO",
        };

        private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private static readonly Regex Email = new(@"(?<![\w.+-])[A-Za-z0-9._%+-]{1,64}@(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,24}\b", Options, PatternTimeout);
        private static readonly Regex Phone = new(@"(?<![\w+])(?:\+\d{1,3}[ .-]?)?(?:\(\d{1,4}\)[ .-]?)?\d{2,4}(?:[ .-]\d{2,4}){1,4}(?![\w-])|(?<![\w+])\+\d{8,15}(?!\w)", Options, PatternTimeout);
        private static readonly Regex CardCandidate = new(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)", Options, PatternTimeout);
        private static readonly Regex IbanCandidate = new(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?\b", Options, PatternTimeout);
        private static readonly Regex IPv4 = new(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])", Options, PatternTimeout);
        private static readonly Regex IPv6Candidate = new(@"(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])", Options, PatternTimeout);
        private static readonly Regex UsSsn = new(@"(?<!\d)(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}(?!\d)", Options, PatternTimeout);
        private static readonly Regex UkNhs = new(@"(?<!\d)\d{3}[ -]?\d{3}[ -]?\d{4}(?!\d)", Options, PatternTimeout);
        private static readonly Regex UkNino = new(@"\b(?!BG|GB|NK|KN|TN|NT|ZZ)[A-CEGHJ-PR-TW-Z][A-CEGHJ-NPR-TW-Z] ?\d{2} ?\d{2} ?\d{2} ?[A-D]\b", Options, PatternTimeout);
        private static readonly Regex SePersonal = new(@"(?<!\d)(?:\d{2})?(\d{2})(\d{2})(\d{2})[-+]?(\d{4})(?!\d)", Options, PatternTimeout);
        private static readonly Regex FiIdentity = new(@"(?<![\w])(\d{6})[-+ABCDEFUVWXY](\d{3})([0-9A-Y])(?![\w])", Options, PatternTimeout);
        private static readonly Regex PlPesel = new(@"(?<!\d)\d{11}(?!\d)", Options, PatternTimeout);
        private static readonly Regex EsNif = new(@"\b(?:(\d{8})|([XYZ])(\d{7}))-?([A-Z])\b", Options, PatternTimeout);
        private static readonly Regex Crypto = new(@"\b(?:bc1[ac-hj-np-z02-9]{25,87}|[13][a-km-zA-HJ-NP-Z1-9]{25,34})\b", Options, PatternTimeout);

        // Secret keys with a vendor's own prefix - found at every strictness.
        private static readonly (string Kind, Regex Pattern)[] KnownKeys =
        {
            ("AWS access key", new Regex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", Options, PatternTimeout)),
            ("GitHub token", new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})\b", Options, PatternTimeout)),
            ("GitLab token", new Regex(@"\bglpat-[A-Za-z0-9_-]{20,}\b", Options, PatternTimeout)),
            ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", Options, PatternTimeout)),
            ("Stripe key", new Regex(@"\b(?:sk|rk|pk)_(?:live|test)_[A-Za-z0-9]{16,}\b", Options, PatternTimeout)),
            ("Anthropic key", new Regex(@"\bsk-ant-[A-Za-z0-9_-]{20,}", Options, PatternTimeout)),
            ("OpenAI key", new Regex(@"\bsk-(?!ant-)(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}", Options, PatternTimeout)),
            ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b", Options, PatternTimeout)),
            ("SendGrid key", new Regex(@"\bSG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}\b", Options, PatternTimeout)),
            ("Private key", new Regex(@"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----", Options, PatternTimeout)),
            ("JSON web token", new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", Options, PatternTimeout)),
            ("Connection string key", new Regex(@"(?i)\b(?:AccountKey|SharedAccessKey|Password|Pwd)=[^;\s'""]{8,}", Options, PatternTimeout)),
        };

        // Balanced also finds a value given to a name that says it is secret: api_key = "...", "token": "...".
        private static readonly Regex Assigned = new(@"(?i)\b(?:api[_-]?key|secret|token|passw(?:or)?d|pwd|client[_-]?secret|access[_-]?key|auth)\b[""']?\s*[:=]\s*[""']?(?<value>[^\s""',;]{8,})", Options, PatternTimeout);

        // Strict also finds any long run of letters and digits that looks random.
        private static readonly Regex LongToken = new(@"(?<![A-Za-z0-9+/_-])[A-Za-z0-9+/_-]{24,}={0,2}(?![A-Za-z0-9+/_-])", Options, PatternTimeout);

        private static readonly Regex UrlCandidate = new(@"\b(?:[A-Za-z][A-Za-z0-9+.-]{1,15}://|(?:mailto|javascript|vbscript|data):|www\.)[^\s<>""'`]+", Options, PatternTimeout);

        /// <summary>Everything the patterns find in the text, in order; overlapping findings of one check kept once.</summary>
        public static IReadOnlyList<GuardrailFinding> Find(string text, GuardrailPatterns patterns)
        {
            var found = new List<GuardrailFinding>();
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }

            foreach (var keyword in patterns.Keywords.Select(each => each.Trim()).Where(each => each.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var whole = new Regex(@"(?<!\w)" + Regex.Escape(keyword) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
                found.AddRange(whole.Matches(text).Select(match => new GuardrailFinding(Keyword, keyword, match.Index, match.Length)));
            }

            foreach (var kind in patterns.PersonalData)
            {
                found.AddRange(PersonalData(text, kind));
            }

            if (patterns.SecretKeys != SecretKeyStrictness.Off)
            {
                found.AddRange(SecretKeys(text, patterns.SecretKeys));
            }

            if (patterns.Urls)
            {
                found.AddRange(Urls(text, patterns));
            }

            foreach (var (name, pattern) in patterns.Custom)
            {
                found.AddRange(pattern.Matches(text).Where(match => match.Length > 0).Select(match => new GuardrailFinding(CustomCheck, name, match.Index, match.Length)));
            }

            return Distinct(found);
        }

        /// <summary>The text with each finding replaced by its kind in brackets - <c>[EMAIL_ADDRESS]</c>, <c>[SECRET_KEY]</c>, <c>[URL]</c>, a custom pattern's name.</summary>
        public static string Sanitize(string text, IReadOnlyList<GuardrailFinding> findings)
        {
            var builder = new StringBuilder(text.Length);
            var at = 0;
            foreach (var finding in Merged(findings))
            {
                builder.Append(text, at, finding.Start - at).Append('[').Append(Placeholder(finding)).Append(']');
                at = finding.Start + finding.Length;
            }

            return builder.Append(text, at, text.Length - at).ToString();
        }

        private static string Placeholder(GuardrailFinding finding) => finding.Check switch
        {
            SecretKeyCheck => "SECRET_KEY",
            UrlCheck => "URL",
            Keyword => "KEYWORD",
            CustomCheck => finding.Kind.Trim().ToUpperInvariant().Replace(' ', '_'),
            _ => finding.Kind,
        };

        // Findings that overlap become one, the first to start (and on a tie the longest) naming it.
        private static IEnumerable<GuardrailFinding> Merged(IEnumerable<GuardrailFinding> findings)
        {
            GuardrailFinding? current = null;
            foreach (var finding in findings.OrderBy(each => each.Start).ThenByDescending(each => each.Length))
            {
                if (current is not null && finding.Start < current.Start + current.Length)
                {
                    var end = Math.Max(current.Start + current.Length, finding.Start + finding.Length);
                    current = current with { Length = end - current.Start };
                    continue;
                }

                if (current is not null)
                {
                    yield return current;
                }

                current = finding;
            }

            if (current is not null)
            {
                yield return current;
            }
        }

        private static List<GuardrailFinding> Distinct(IEnumerable<GuardrailFinding> findings)
        {
            var kept = new List<GuardrailFinding>();
            foreach (var group in findings.GroupBy(each => each.Check))
            {
                kept.AddRange(Merged(group));
            }

            return kept.OrderBy(each => each.Start).ThenBy(each => each.Check, StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<GuardrailFinding> PersonalData(string text, string kind)
        {
            IEnumerable<Match> matches = kind switch
            {
                "EMAIL_ADDRESS" => Email.Matches(text),
                "PHONE_NUMBER" => Phone.Matches(text).Where(match => IsPhone(match.Value)),
                "CREDIT_CARD" => CardCandidate.Matches(text).Where(match => Digits(match.Value) is { Length: >= 13 and <= 19 } digits && Luhn(digits)),
                "IBAN_CODE" => IbanCandidate.Matches(text).Where(match => IsIban(match.Value)),
                "IP_ADDRESS" => IPv4.Matches(text).Concat(IPv6Candidate.Matches(text).Where(match => match.Value.Count(letter => letter == ':') >= 2
                    && IPAddress.TryParse(match.Value, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)),
                "US_SSN" => UsSsn.Matches(text),
                "UK_NHS" => UkNhs.Matches(text).Where(match => IsNhs(Digits(match.Value))),
                "UK_NINO" => UkNino.Matches(text),
                "SE_PERSONAL_NUMBER" => SePersonal.Matches(text).Where(IsSwedishPersonalNumber),
                "FI_PERSONAL_IDENTITY_CODE" => FiIdentity.Matches(text).Where(IsFinnishIdentityCode),
                "PL_PESEL" => PlPesel.Matches(text).Where(match => IsPesel(match.Value)),
                "ES_NIF" => EsNif.Matches(text).Where(IsSpanishNif),
                "CRYPTO" => Crypto.Matches(text),
                _ => Enumerable.Empty<Match>(),
            };

            return matches.Select(match => new GuardrailFinding(PersonalDataCheck, kind, match.Index, match.Length));
        }

        private static IEnumerable<GuardrailFinding> SecretKeys(string text, SecretKeyStrictness strictness)
        {
            foreach (var (kind, pattern) in KnownKeys)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    yield return new GuardrailFinding(SecretKeyCheck, kind, match.Index, match.Length);
                }
            }

            if (strictness >= SecretKeyStrictness.Balanced)
            {
                foreach (Match match in Assigned.Matches(text))
                {
                    var value = match.Groups["value"];
                    yield return new GuardrailFinding(SecretKeyCheck, "Secret value", value.Index, value.Length);
                }
            }

            if (strictness >= SecretKeyStrictness.Strict)
            {
                foreach (Match match in LongToken.Matches(text))
                {
                    if (LooksRandom(match.Value))
                    {
                        yield return new GuardrailFinding(SecretKeyCheck, "Random-looking token", match.Index, match.Length);
                    }
                }
            }
        }

        private static IEnumerable<GuardrailFinding> Urls(string text, GuardrailPatterns patterns)
        {
            var schemes = patterns.AllowedSchemes.Select(scheme => scheme.Trim().TrimEnd(':').ToLowerInvariant()).Where(scheme => scheme.Length > 0).ToHashSet(StringComparer.Ordinal);
            var hosts = patterns.AllowedHosts.Select(Host).Where(host => host.Length > 0).ToList();
            foreach (Match match in UrlCandidate.Matches(text))
            {
                var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']');
                var address = value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + value : value;
                var scheme = address[..address.IndexOf(':')].ToLowerInvariant();
                var allowed = schemes.Contains(scheme);
                if (allowed && Stomme.Shared.Utilities.TryCreateAbsoluteUri(address, out var uri))
                {
                    if (patterns.BlockUserInfo && uri.UserInfo.Length > 0)
                    {
                        allowed = false;
                    }
                    else if (uri.Host.Length > 0)
                    {
                        var host = uri.Host.ToLowerInvariant();
                        allowed = hosts.Any(each => host == each || (patterns.AllowSubdomains && host.EndsWith("." + each, StringComparison.Ordinal)));
                    }
                    else
                    {
                        allowed = hosts.Count == 0 ? false : allowed; // mailto: and the like name no host - allowed only by their scheme and a list
                    }
                }
                else if (allowed)
                {
                    allowed = false; // not a URL anything could follow, but written as one
                }

                if (!allowed)
                {
                    yield return new GuardrailFinding(UrlCheck, scheme, match.Index, value.Length);
                }
            }
        }

        /// <summary>A host as an allowed-URL line names it: example.com, https://example.com/path, *.example.com.</summary>
        private static string Host(string line)
        {
            var value = line.Trim().TrimStart('*', '.');
            if (value.Length == 0)
            {
                return string.Empty;
            }

            return Stomme.Shared.Utilities.TryCreateAbsoluteUri(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, out var uri) ? uri.Host.ToLowerInvariant() : value.ToLowerInvariant();
        }

        private static string Digits(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

        // A phone number: 9 to 15 digits, or 8 to 15 after a + - so never a written date, which has 8 at most - and never an IP
        // address, whose dotted groups look like a number's.
        private static bool IsPhone(string candidate)
        {
            if (IPv4.IsMatch(candidate))
            {
                return false;
            }

            var digits = Digits(candidate);
            return candidate.TrimStart().StartsWith('+') ? digits.Length is >= 8 and <= 15 : digits.Length is >= 9 and <= 15;
        }

        /// <summary>The Luhn check: a card number's last digit, a Swedish personal number's.</summary>
        public static bool Luhn(string digits)
        {
            var sum = 0;
            var doubled = false;
            for (var at = digits.Length - 1; at >= 0; at--)
            {
                var digit = digits[at] - '0';
                if (doubled)
                {
                    digit *= 2;
                    if (digit > 9)
                    {
                        digit -= 9;
                    }
                }

                sum += digit;
                doubled = !doubled;
            }

            return digits.Length > 1 && sum % 10 == 0;
        }

        /// <summary>An IBAN: its country's letters, two check digits, and the whole mod 97 is 1.</summary>
        public static bool IsIban(string candidate)
        {
            var iban = candidate.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            if (iban.Length is < 15 or > 34)
            {
                return false;
            }

            var moved = iban[4..] + iban[..4];
            var numeric = new StringBuilder();
            foreach (var letter in moved)
            {
                numeric.Append(char.IsAsciiDigit(letter) ? letter.ToString() : (letter - 'A' + 10).ToString(CultureInfo.InvariantCulture));
            }

            return BigInteger.Parse(numeric.ToString(), CultureInfo.InvariantCulture) % 97 == 1;
        }

        private static bool IsNhs(string digits)
        {
            if (digits.Length != 10)
            {
                return false;
            }

            var sum = 0;
            for (var at = 0; at < 9; at++)
            {
                sum += (digits[at] - '0') * (10 - at);
            }

            var check = 11 - (sum % 11);
            check = check == 11 ? 0 : check;
            return check != 10 && check == digits[9] - '0';
        }

        private static bool IsSwedishPersonalNumber(Match match)
        {
            var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            day = day > 60 ? day - 60 : day; // a co-ordination number's day
            return month is >= 1 and <= 12 && day is >= 1 and <= 31
                && Luhn(match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value + match.Groups[4].Value);
        }

        private static bool IsFinnishIdentityCode(Match match)
        {
            const string Checks = "0123456789ABCDEFHJKLMNPRSTUVWXY";
            var number = long.Parse(match.Groups[1].Value + match.Groups[2].Value, CultureInfo.InvariantCulture);
            return Checks[(int)(number % 31)] == match.Groups[3].Value[0];
        }

        private static bool IsPesel(string digits)
        {
            int[] weights = [1, 3, 7, 9, 1, 3, 7, 9, 1, 3];
            var sum = 0;
            for (var at = 0; at < 10; at++)
            {
                sum += (digits[at] - '0') * weights[at];
            }

            var month = int.Parse(digits.Substring(2, 2), CultureInfo.InvariantCulture) % 20;
            return month is >= 1 and <= 12 && (10 - (sum % 10)) % 10 == digits[10] - '0';
        }

        private static bool IsSpanishNif(Match match)
        {
            const string Letters = "TRWAGMYFPDXBNJZSQVHLCKE";
            var number = match.Groups[1].Success
                ? match.Groups[1].Value
                : "XYZ".IndexOf(match.Groups[2].Value[0], StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture) + match.Groups[3].Value;
            return Letters[(int)(long.Parse(number, CultureInfo.InvariantCulture) % 23)] == match.Groups[4].Value[0];
        }

        // Random-looking: letters and digits both, and at least 3.5 bits of entropy a character - a word or a path is neither.
        private static bool LooksRandom(string token)
        {
            if (!token.Any(char.IsAsciiDigit) || !token.Any(char.IsAsciiLetter))
            {
                return false;
            }

            var entropy = token.GroupBy(letter => letter).Sum(group =>
            {
                var share = (double)group.Count() / token.Length;
                return -share * Math.Log2(share);
            });
            return entropy >= 3.5;
        }
    }
}
