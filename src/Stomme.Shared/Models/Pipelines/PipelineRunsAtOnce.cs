#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// How many runs of a pipeline may go at once (BL-229): any number, one at a time, one at a time per correlation id - a
    /// schedule firing again while the last run still works, a webhook called twice for one order - or one at a time with the
    /// rest in turn (n8n's concurrency control). A start "one at a time" refuses is not made: a trigger's start is skipped (the
    /// engine's activation strategy), and the Run button and Retry say why. "In turn" makes it a run that waits - Queued - and
    /// starts when the runs before it have ended, oldest first. A run waiting for a time or a decision is still going.
    /// </summary>
    public static class PipelineRunsAtOnce
    {
        public const string Any = "";

        public const string One = "one";

        public const string OnePerCorrelation = "one-per-correlation";

        public const string OneInTurn = "one-in-turn";

        /// <summary>The choices, as the editor offers them.</summary>
        public static IReadOnlyList<(string Value, string Label)> All { get; } = new[]
        {
            (Any, "Any number at once"),
            (One, "One at a time"),
            (OnePerCorrelation, "One at a time per correlation id"),
            (OneInTurn, "One at a time, the rest wait their turn"),
        };

        /// <summary>The setting as stored: a choice this list knows, else any number.</summary>
        public static string Normalize(string? value) =>
            All.Any(choice => choice.Value.Length > 0 && string.Equals(choice.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                ? value!.Trim().ToLowerInvariant()
                : Any;
    }
}
