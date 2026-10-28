#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Mcp
{
    /// <summary>
    /// Matches a resource URI against a template with <c>{name}</c> segments, e.g.
    /// <c>stomme://pages/{pageId}</c> (D101, BL-80). Lifted from the predecessor platform, with the three refusals below added.
    /// </summary>
    /// <remarks>
    /// A parameter is one WHOLE path segment. Literals compare case-insensitively (a scheme and a host do); the
    /// captured values are returned exactly as sent - never decoded, because the reader that receives them decides what
    /// they mean, and a matcher that decoded <c>%2F</c> would hand a reader a value containing a separator it was
    /// promised it could not contain.
    /// </remarks>
    public static class McpUriTemplate
    {
        /// <returns>
        /// The captured arguments (empty for a template with none) when <paramref name="uri"/> matches; otherwise null.
        /// </returns>
        public static IReadOnlyDictionary<string, string>? Match(string? template, string? uri)
        {
            if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(uri))
            {
                return null;
            }

            var templateSegments = template!.Split('/');
            var uriSegments = uri!.Split('/');
            if (templateSegments.Length != uriSegments.Length)
            {
                return null;
            }

            var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < templateSegments.Length; i++)
            {
                var expected = templateSegments[i];
                var actual = uriSegments[i];

                if (IsParameter(expected))
                {
                    // An empty segment is not a value; and "." / ".." are not values either - a reader that joins a
                    // captured segment into a path must never be handed one that walks out of it.
                    if (actual.Length == 0 || actual == "." || actual == "..")
                    {
                        return null;
                    }

                    // The same name twice would silently keep the LAST value: refuse the template instead.
                    if (!arguments.TryAdd(expected.Substring(1, expected.Length - 2), actual))
                    {
                        return null;
                    }

                    continue;
                }

                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return arguments;
        }

        /// <summary>The <c>{name}</c> parameters a template declares, in order. Empty for a plain URI.</summary>
        public static IReadOnlyList<string> Parameters(string? template)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(template))
            {
                return names;
            }

            foreach (var segment in template!.Split('/'))
            {
                if (IsParameter(segment))
                {
                    names.Add(segment.Substring(1, segment.Length - 2));
                }
            }

            return names;
        }

        private static bool IsParameter(string segment) =>
            segment.Length > 2 && segment[0] == '{' && segment[segment.Length - 1] == '}';
    }
}
