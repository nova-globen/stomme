using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A variable named after a step, for the step's result read elsewhere (the editor's "earlier steps' results", an
    /// n8n workflow's <c>$('Node')</c> brought over): the step's letters and digits in lower case joined by underscores, never a
    /// name the pipeline already has - one rule, so the editor and an import name alike.
    /// </summary>
    public static class PipelineVariableNames
    {
        private static readonly Regex NotAWord = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string For(string stepName, IEnumerable<string> taken)
        {
            var words = NotAWord.Split((stepName ?? "").ToLowerInvariant()).Where(word => word.Length > 0).ToList();
            var name = words.Count == 0 ? "result" : string.Join("_", words);
            if (char.IsDigit(name[0]))
            {
                name = "step_" + name;
            }

            var names = new HashSet<string>((taken ?? Enumerable.Empty<string>()).Where(known => known is not null), StringComparer.OrdinalIgnoreCase);
            var unique = name;
            for (var count = 2; names.Contains(unique); count++)
            {
                unique = name + "_" + count;
            }

            return unique;
        }
    }
}
