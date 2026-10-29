#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A pipeline's TAGS (n8n's workflow tags): short labels a team sorts many pipelines by - "billing", "nightly",
    /// "customer A" - shown on the pipelines list and filtered by. The one rule for both sides: trimmed, inner spaces folded to
    /// one, at most <see cref="MaxLength"/> characters of letters, digits, spaces, '-', '_' and '.', the same tag once whatever
    /// its case (the first spelling kept), at most <see cref="MaxTags"/>, in the order given.
    /// </summary>
    public static class PipelineTags
    {
        public const int MaxTags = 10;

        public const int MaxLength = 30;

        /// <summary>The tags kept: each made tidy, the ones that cannot be a tag refused with the reason.</summary>
        public static List<string> Normalize(IEnumerable<string?>? tags, out string? error)
        {
            error = null;
            var kept = new List<string>();
            foreach (var raw in tags ?? Enumerable.Empty<string?>())
            {
                var tag = string.Join(' ', (raw ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (tag.Length == 0 || kept.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (tag.Length > MaxLength || !tag.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.'))
                {
                    error = $"'{tag}' cannot be a tag: at most {MaxLength} letters, digits, spaces, '-', '_' or '.'.";
                    return kept;
                }

                kept.Add(tag);
            }

            if (kept.Count > MaxTags)
            {
                error = $"A pipeline takes at most {MaxTags} tags.";
                kept = kept.Take(MaxTags).ToList();
            }

            return kept;
        }

        /// <summary>Tags as typed: separated by commas.</summary>
        public static List<string> Parse(string? text, out string? error) => Normalize((text ?? "").Split(','), out error);
    }
}
