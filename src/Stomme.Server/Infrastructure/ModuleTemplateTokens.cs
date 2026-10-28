#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// What the module generator (the host's "Create Module", <c>POST api/ModuleDefinition</c>) needs beyond the plain
    /// tokens: a permission-key resource made from the module's name, the package feed a generated module restores the SDK
    /// from, and which template files are copied byte for byte (BL-86 increment 7).
    /// </summary>
    public static class ModuleTemplateTokens
    {
        /// <summary>The framework's package feed, where the slim SDK is published: nuget.org.</summary>
        public const string PackageFeed = "https://api.nuget.org/v3/index.json";

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".ico", ".jpg", ".jpeg", ".gif", ".webp", ".dll", ".pdb", ".nupkg", ".zip", ".woff", ".woff2",
        };

        /// <summary>True for a file the generator must copy as it is - token replacement would re-encode it.</summary>
        public static bool IsBinary(string fileName) => BinaryExtensions.Contains(Path.GetExtension(fileName ?? string.Empty));

        /// <summary>
        /// The release number of the framework version a module is generated for - <c>10.5.0-preview.1</c> is <c>10.5.0</c> -
        /// for its <c>Stomme.Framework</c> package type, whose version NuGet refuses with a label (NU5000, measured on the
        /// preview's gate, BL-192). The SDK reference keeps the label: that is the package on the feed.
        /// </summary>
        public static string ReleaseNumber(string? version) => (version ?? string.Empty).Split('-', '+')[0];

        /// <summary>
        /// The module's name as a permission-key resource: lower-case, words joined by hyphens - <c>KnowledgeBase</c> is
        /// <c>knowledge-base</c>, <c>My Notes</c> is <c>my-notes</c>. A key segment must be lower-case (D99).
        /// </summary>
        public static string ModuleKey(string? name)
        {
            var builder = new StringBuilder();
            var text = name ?? string.Empty;
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                if (char.IsLetterOrDigit(character))
                {
                    if (char.IsUpper(character) && i > 0 && char.IsLetterOrDigit(text[i - 1]) && !char.IsUpper(text[i - 1]) && builder.Length > 0 && builder[^1] != '-')
                    {
                        builder.Append('-');
                    }

                    builder.Append(char.ToLowerInvariant(character));
                }
                else if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }

            return builder.ToString().Trim('-');
        }
    }
}
