#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace Stomme.Models.Notifications
{
    /// <summary>
    /// A stored notification's body as a person reads it in the inbox (BL-229, found driving "Ask for approval": the inbox showed
    /// the subject alone, so an approver was told a decision waits but not what, nor where). Bodies are stored as the channels
    /// wrote them - the template renderer's HTML-encoded text (<c>&amp;#xA;</c> for a line break), the site's own error notices
    /// with <c>&lt;br /&gt;</c> - so the text is made plain: a break is a line, every other tag goes, entities are decoded, and
    /// the page shows it as TEXT (never markup). An address on the site's own origin becomes a link; any other stays text - an
    /// inbox is no place to send a person elsewhere.
    /// </summary>
    public static class NotificationText
    {
        private static readonly Regex Break = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Tag = new(@"<[^>]*>", RegexOptions.CultureInvariant);

        private static readonly Regex Address = new(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The body as plain text: a break a line, other tags dropped, entities decoded.</summary>
        public static string Plain(string? body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return "";
            }

            var text = Tag.Replace(Break.Replace(body, "\n"), "");
            return WebUtility.HtmlDecode(text).Replace("\r\n", "\n").Trim();
        }

        /// <summary>
        /// The text as runs to show: plain text, or a link (its <c>Href</c> set) where an address is on <paramref name="siteOrigin"/>
        /// (scheme, host and port, e.g. <c>https://example.test</c>). Punctuation that ends a sentence is not part of an address.
        /// </summary>
        public static IReadOnlyList<(string Text, string? Href)> Runs(string? text, string? siteOrigin)
        {
            var runs = new List<(string Text, string? Href)>();
            var value = text ?? "";
            var at = 0;
            foreach (Match match in Address.Matches(value))
            {
                var address = match.Value.TrimEnd('.', ',', ';', ':', ')', '!', '?');
                if (!IsOnSite(address, siteOrigin))
                {
                    continue;
                }

                if (match.Index > at)
                {
                    runs.Add((value[at..match.Index], null));
                }

                runs.Add((address, address));
                at = match.Index + address.Length;
            }

            if (at < value.Length)
            {
                runs.Add((value[at..], null));
            }

            return runs;
        }

        private static bool IsOnSite(string address, string? siteOrigin) =>
            !string.IsNullOrEmpty(siteOrigin)
            && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && Uri.TryCreate(siteOrigin, UriKind.Absolute, out var origin)
            && string.Equals(uri.GetLeftPart(UriPartial.Authority), origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }
}
