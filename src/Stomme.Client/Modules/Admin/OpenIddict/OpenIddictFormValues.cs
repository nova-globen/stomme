using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Modules.Admin.OpenIddict
{
    /// <summary>
    /// The one-value-per-line convention the client/scope forms use for their list fields (scopes, redirect
    /// URIs, resources). Shared by Add/Edit/Scope so the round trip — stored list to textarea to stored list —
    /// is symmetric; a mismatch here silently drops a redirect URI, which fails only at authorization time.
    /// </summary>
    internal static class OpenIddictFormValues
    {
        /// <summary>Splits a textarea's contents into a trimmed, blank-free list.</summary>
        public static List<string> SplitLines(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            return value
                .Replace("\r\n", "\n")
                .Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrEmpty(item))
                .ToList();
        }

        /// <summary>Renders a stored list back into a textarea's contents.</summary>
        public static string JoinLines(IEnumerable<string> values)
        {
            return values == null ? string.Empty : string.Join("\n", values);
        }
    }
}
