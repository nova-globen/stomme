#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Stomme.Models.Pipelines
{
    /// <summary>What a run is, for the values every expression can read beside the pipeline's variables.</summary>
    public sealed record PipelineRunFacts(DateTimeOffset Now, string? RunId = null, string? CorrelationId = null, DateTimeOffset? StartedAt = null,
        string? PipelineId = null, string? PipelineName = null, int? Version = null, IReadOnlyDictionary<string, string>? SiteVariables = null,
        IReadOnlyDictionary<string, object?>? Input = null, string? TimeZone = null);

    /// <summary>
    /// The values every expression and condition can read beside the pipeline's variables (BL-229, n8n's <c>$now</c>,
    /// <c>$today</c>, <c>$execution</c> and <c>$workflow</c>): <c>{{ $now }}</c> - the moment, ISO 8601 to the second, in the
    /// pipeline's time zone (UTC unless it has one, <see cref="PipelineTimeZone"/>); <c>{{ $today }}</c> - the date there; <c>{{ $run.id }}</c>, <c>{{ $run.correlationId }}</c>, <c>{{ $run.startedAt }}</c>;
    /// <c>{{ $pipeline.id }}</c>, <c>{{ $pipeline.name }}</c>, <c>{{ $pipeline.version }}</c>; <c>{{ $vars.name }}</c> - the site's
    /// variables (n8n's <c>$vars</c>, <see cref="PipelineSiteVariables"/>); <c>{{ $input.name }}</c> - what the run was started with
    /// (the Run API's or MCP's input, what Execute pipeline sends a sub-pipeline - n8n's "When executed by another workflow"),
    /// the engine's own <c>__</c> keys left out. A built-in answers before a
    /// variable of the same name. ONE list, read by the engine when a step runs and by the editor for its previews, its
    /// suggestions and its problems, so what the author sees is what the step gets.
    /// </summary>
    public static class PipelineBuiltIns
    {
        public const string Now = "$now";
        public const string Today = "$today";
        public const string Run = "$run";
        public const string Pipeline = "$pipeline";
        public const string Vars = "$vars";
        public const string Input = "$input";

        /// <summary>Every built-in name.</summary>
        public static readonly IReadOnlyList<string> Names = new[] { Now, Today, Run, Pipeline, Vars, Input };

        /// <summary>
        /// A run's input as <c>$input</c> reads it: without the keys the engine and the module keep for themselves (<c>__…</c>), and a
        /// value that is a JSON object or list as one - the Run API and MCP send text - so a path reads into it.
        /// </summary>
        public static Dictionary<string, object?> InputOf(IEnumerable<KeyValuePair<string, object?>>? input) =>
            (input ?? Array.Empty<KeyValuePair<string, object?>>())
                .Where(pair => !pair.Key.StartsWith("__", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value is string text ? Structured(text) : pair.Value, StringComparer.Ordinal);

        private static object? Structured(string text)
        {
            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            {
                return text;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return text;
            }
        }

        /// <summary>Whether a name is a built-in's - read from the run, never a variable.</summary>
        public static bool IsBuiltIn(string? name) => name is not null && Names.Contains(name, StringComparer.Ordinal);

        /// <summary>
        /// The built-ins' values for a run: moments as ISO 8601 text (so a condition compares them as dates), the run and the
        /// pipeline as records whose fields a path reads - and the editor suggests.
        /// </summary>
        public static IReadOnlyList<(string Name, object? Value)> Values(PipelineRunFacts facts)
        {
            ArgumentNullException.ThrowIfNull(facts);
            // BL-229: read in the pipeline's zone (PipelineTimeZone) - UTC when it has none, as before.
            var zone = PipelineTimeZone.Find(facts.TimeZone);
            return new (string, object?)[]
            {
                (Now, PipelineTimeZone.Moment(facts.Now, zone)),
                (Today, PipelineTimeZone.Date(facts.Now, zone)),
                (Run, JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = facts.RunId,
                    ["correlationId"] = facts.CorrelationId,
                    ["startedAt"] = facts.StartedAt is { } started ? PipelineTimeZone.Moment(started, zone) : null,
                })),
                (Pipeline, JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = facts.PipelineId,
                    ["name"] = facts.PipelineName,
                    ["version"] = facts.Version,
                })),
                (Vars, JsonSerializer.SerializeToElement(facts.SiteVariables ?? new Dictionary<string, string>())),
                (Input, JsonSerializer.SerializeToElement(InputOf(facts.Input))),
            };
        }

        /// <summary>A lookup that answers the built-ins from the run and every other name from <paramref name="variables"/>.</summary>
        public static Func<string, (bool Found, object? Value)> With(Func<string, (bool Found, object? Value)> variables, PipelineRunFacts facts)
        {
            ArgumentNullException.ThrowIfNull(variables);
            var values = Values(facts);
            return name => IsBuiltIn(name) ? (true, values.First(builtIn => builtIn.Name == name).Value) : variables(name);
        }
    }
}
