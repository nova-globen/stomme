#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's "This workflow can be called by": which of the site's pipelines may start this one through Execute, Dispatch
    /// or Bulk dispatch pipeline, kept with its graph (<see cref="PipelineGraph.CalledBy"/>) - any of them (null, as before), none,
    /// or the ones it names. A person, the Run API, MCP and its own triggers start it whatever it says: they are held to their own
    /// keys. A call it refuses fails at the CALLER's step, saying why, before the called pipeline runs anything - and the caller's
    /// editor says so while the author writes the call, where n8n says it only when the run fails.
    /// </summary>
    public static class PipelineCallers
    {
        /// <summary>No other pipeline may call it.</summary>
        public const string Nobody = "none";

        /// <summary>
        /// The setting as a pipeline keeps it: null for any pipeline, <see cref="Nobody"/>, or the definition ids of the pipelines
        /// it admits, comma-separated, each once. Anything else that is not empty admits nobody: a setting that names callers and
        /// cannot be read must not open the pipeline to every caller.
        /// </summary>
        public static string? Normalize(string? setting)
        {
            if (string.IsNullOrWhiteSpace(setting) || string.Equals(setting.Trim(), "any", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(setting.Trim(), Nobody, StringComparison.OrdinalIgnoreCase))
            {
                return Nobody;
            }

            var ids = Split(setting);
            return ids.Count == 0 ? Nobody : string.Join(",", ids);
        }

        /// <summary>The setting that admits exactly these pipelines - <see cref="Nobody"/> for none of them.</summary>
        public static string Only(IEnumerable<string>? definitionIds) => Normalize(string.Join(",", definitionIds ?? Enumerable.Empty<string>())) ?? Nobody;

        /// <summary>The pipelines a setting names; none for any pipeline, and none for nobody.</summary>
        public static IReadOnlyList<string> Ids(string? setting) => Normalize(setting) is { } normal && normal != Nobody ? Split(normal) : Array.Empty<string>();

        /// <summary>Whether a pipeline (its definition id; null for one not yet saved) may call the one whose setting this is.</summary>
        public static bool Admits(string? setting, string? callerDefinitionId) => Normalize(setting) switch
        {
            null => true,
            Nobody => false,
            var named => !string.IsNullOrEmpty(callerDefinitionId) && Split(named).Contains(callerDefinitionId, StringComparer.Ordinal),
        };

        /// <summary>Why a call is refused, naming the pipeline called.</summary>
        public static string Refusal(string? setting, string called) => Normalize(setting) == Nobody
            ? $"'{called}' may not be called by another pipeline - its settings say so."
            : $"'{called}' may be called only by the pipelines its settings name, and this pipeline is not one of them.";

        private static List<string> Split(string setting) =>
            setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => !string.Equals(id, Nobody, StringComparison.OrdinalIgnoreCase) && !string.Equals(id, "any", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToList();
    }
}
