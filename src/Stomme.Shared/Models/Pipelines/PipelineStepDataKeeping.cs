#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// What a pipeline's runs keep of what each step received and produced (as n8n Enterprise's execution data
    /// redaction does): always, only for a run that fails, or never. A run that is not to keep it has its steps' inputs and outputs,
    /// and what it was started with and returned, emptied a couple of minutes after it ends - its status, times, errors, log and
    /// saved values stay, so it can still be read; a run started with something is then not run again, nor gone on with from a
    /// failed step, without it. Applies once published, to the runs already kept as well.
    /// </summary>
    public static class PipelineStepDataKeeping
    {
        public const string Always = "";

        public const string WhenFailed = "failed";

        public const string Never = "never";

        /// <summary>The choices, as the editor offers them.</summary>
        public static IReadOnlyList<(string Value, string Label)> All { get; } = new[]
        {
            (Always, "Always"),
            (WhenFailed, "Only for a run that fails"),
            (Never, "Never"),
        };

        /// <summary>The setting as stored: a choice this list knows, else always.</summary>
        public static string Normalize(string? value) =>
            All.Any(choice => choice.Value.Length > 0 && string.Equals(choice.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                ? value!.Trim().ToLowerInvariant()
                : Always;

        /// <summary>The setting as a graph holds it: null for always, else the choice.</summary>
        public static string? Stored(string? value) => Normalize(value) is { Length: > 0 } kept ? kept : null;

        /// <summary>The key a step record carries once what it received and produced is no longer kept.</summary>
        public const string NotKeptKey = "stepDataNotKept";
    }
}
