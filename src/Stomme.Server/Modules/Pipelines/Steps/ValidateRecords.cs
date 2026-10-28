#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>How violations accumulate (the predecessor platform's <c>ValidationFailureMode</c>).</summary>
    public enum ValidationFailureMode
    {
        /// <summary>Every record is checked; the step fails when the error count exceeds the threshold.</summary>
        CollectAll,

        /// <summary>The first error fails the step.</summary>
        FailFast,
    }

    /// <summary>
    /// the predecessor platform's Validations as one engine step: field rules (required, type, range, regex) at error or warning
    /// severity, structure checks (row bounds, unique fields), a failure mode and a threshold, a quality score. The
    /// records pass through as the result so the next step still receives them; the violations are an output. The
    /// template aggregate is not ported (D111): the rules ARE the step's inputs; the predecessor platform's Format rules (a separate
    /// module) are not carried.
    /// <para>
    /// <b>Rules</b>, one per line, optionally prefixed <c>warn:</c> for a warning: <c>field required</c>,
    /// <c>field type Number</c> (String, Number, Boolean, Date), <c>field range 0 100</c> (<c>*</c> for open),
    /// <c>field regex ^\d+$</c>.
    /// </para>
    /// </summary>
    [FlowNode(PassedOutcome, FailedOutcome)]
    [Step("Stomme", "Validations", "Check records against field and structure rules.", DisplayName = "Validate records")]
    public class ValidateRecords : DataStep
    {
        public const string PassedOutcome = "Passed";

        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "One per line: field required | field type Number | field range 0 100 | field regex ^\\d+$; prefix warn: for a warning.", Order = 2)]
        public Input<string?>? Rules { get; set; }

        [Input(DisplayName = "Failure mode", DefaultValue = ValidationFailureMode.CollectAll, Order = 3)]
        public Input<ValidationFailureMode> FailureMode { get; set; } = new(new Literal<ValidationFailureMode>(ValidationFailureMode.CollectAll));

        [Input(Description = "How many errors are tolerated (collect-all mode).", DefaultValue = 0, Order = 4)]
        public Input<int> Threshold { get; set; } = new(new Literal<int>(0));

        [Input(DisplayName = "Min rows", Description = "The fewest records allowed; empty for no bound.", Order = 5)]
        public Input<int?>? MinRows { get; set; }

        [Input(DisplayName = "Max rows", Description = "The most records allowed; empty for no bound.", Order = 6)]
        public Input<int?>? MaxRows { get; set; }

        [Input(DisplayName = "Unique fields", Description = "Fields whose values must not repeat, comma-separated.", Order = 7)]
        public Input<string?>? UniqueFields { get; set; }

        [Output]
        public Output<bool>? Passed { get; set; }

        [Output(DisplayName = "Violation count")]
        public Output<int>? ViolationCount { get; set; }

        [Output(DisplayName = "Warning count")]
        public Output<int>? WarningCount { get; set; }

        [Output(Description = "The violations, as a JSON array of messages.")]
        public Output<string>? Violations { get; set; }

        [Output(DisplayName = "Quality score", Description = "The fraction of error-severity field checks that passed, 0 to 1.")]
        public Output<double>? QualityScore { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var records = TextOrLastResult(context, Records);
            if (!Steps.Records.TryParse(records, out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            List<Rule> rules;
            try
            {
                rules = Steps.Records.Lines(Rules?.GetOrDefault(context)).Select(Rule.Parse).ToList();
            }
            catch (FormatException ex)
            {
                await FailAsync(context, "RULE_INVALID", ex.Message).ConfigureAwait(false);
                return;
            }

            var mode = FailureMode.GetOrDefault(context);
            var errors = new List<string>();
            var warnings = 0;
            var checks = 0;
            var failedChecks = 0;
            var failFast = false;
            for (var i = 0; i < rows.Count && !failFast; i++)
            {
                foreach (var rule in rules)
                {
                    var violation = rule.Check(rows[i], i);
                    if (violation is null)
                    {
                        if (!rule.Warning)
                        {
                            checks++;
                        }

                        continue;
                    }

                    if (rule.Warning)
                    {
                        warnings++;
                        continue;
                    }

                    checks++;
                    failedChecks++;
                    errors.Add(violation);
                    if (mode == ValidationFailureMode.FailFast)
                    {
                        failFast = true;
                        break;
                    }
                }
            }

            if (!failFast)
            {
                var min = MinRows?.GetOrDefault(context);
                var max = MaxRows?.GetOrDefault(context);
                if (min is { } minRows && rows.Count < minRows)
                {
                    errors.Add($"Expected at least {minRows} record(s) but found {rows.Count}.");
                }

                if (max is { } maxRows && rows.Count > maxRows)
                {
                    errors.Add($"Expected at most {maxRows} record(s) but found {rows.Count}.");
                }

                foreach (var field in (UniqueFields?.GetOrDefault(context) ?? "").Split(',').Select(f => f.Trim()).Where(f => f.Length > 0))
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in rows)
                    {
                        var value = Steps.Records.Text(row[field]);
                        if (value is not null && !seen.Add(value))
                        {
                            errors.Add($"Duplicate value '{value}' in unique field '{field}'.");
                            break;
                        }
                    }
                }
            }

            var threshold = mode == ValidationFailureMode.CollectAll ? Math.Max(0, Threshold.GetOrDefault(context)) : 0;
            var passed = errors.Count <= threshold;
            context.Set(Passed, passed);
            context.Set(ViolationCount, errors.Count);
            context.Set(WarningCount, warnings);
            context.Set(Violations, JsonSerializer.Serialize(errors));
            context.Set(QualityScore, checks == 0 ? 1.0 : Math.Clamp(1.0 - (double)failedChecks / checks, 0.0, 1.0));
            context.Set(Result, records);
            if (passed)
            {
                await SucceedAsync(context, records, PassedOutcome).ConfigureAwait(false);
                return;
            }

            await FailAsync(context, "VALIDATION_FAILED", $"{errors.Count} violation(s): {errors[0]}").ConfigureAwait(false);
        }

        private enum RuleKind
        {
            Required,
            Type,
            Range,
            Regex,
        }

        private sealed record Rule(string Field, RuleKind Kind, bool Warning, MappedFieldType? ExpectedType, decimal? Min, decimal? Max, Regex? Pattern)
        {
            private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);

            public static Rule Parse(string line)
            {
                var warning = false;
                if (line.StartsWith("warn:", StringComparison.OrdinalIgnoreCase))
                {
                    warning = true;
                    line = line[5..].Trim();
                }

                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                {
                    throw new FormatException($"Rule '{line}' is not 'field required | type | range | regex ...'.");
                }

                var field = parts[0];
                var argument = parts.Length > 2 ? parts[2] : "";
                switch (parts[1].ToLowerInvariant())
                {
                    case "required":
                        return new Rule(field, RuleKind.Required, warning, null, null, null, null);
                    case "type":
                        if (!Enum.TryParse<MappedFieldType>(argument, ignoreCase: true, out var type) || type is MappedFieldType.Json or MappedFieldType.Any)
                        {
                            throw new FormatException($"Rule '{line}' names an unknown type '{argument}' (String, Number, Boolean, Date).");
                        }

                        return new Rule(field, RuleKind.Type, warning, type, null, null, null);
                    case "range":
                        var bounds = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (bounds.Length != 2)
                        {
                            throw new FormatException($"Rule '{line}' needs 'range min max' (* for an open bound).");
                        }

                        return new Rule(field, RuleKind.Range, warning, null, Bound(bounds[0], line), Bound(bounds[1], line), null);
                    case "regex":
                        if (argument.Length == 0)
                        {
                            throw new FormatException($"Rule '{line}' needs a pattern.");
                        }

                        try
                        {
                            return new Rule(field, RuleKind.Regex, warning, null, null, null, new Regex(argument, RegexOptions.None, RegexBudget));
                        }
                        catch (ArgumentException ex)
                        {
                            throw new FormatException($"Rule '{line}' has an invalid pattern: {ex.Message}");
                        }
                    default:
                        throw new FormatException($"Rule '{line}' has an unknown kind '{parts[1]}' (required, type, range, regex).");
                }
            }

            private static decimal? Bound(string text, string line)
            {
                if (text == "*" || text == "-")
                {
                    return null;
                }

                return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new FormatException($"Rule '{line}' has a bound '{text}' that is not a number.");
            }

            public string? Check(JsonObject row, int index)
            {
                var raw = Steps.Records.Text(row[Field]);
                var present = !string.IsNullOrEmpty(raw);
                switch (Kind)
                {
                    case RuleKind.Required:
                        return present ? null : Violation(index, "is required");
                    case RuleKind.Type:
                        if (!present)
                        {
                            return null;
                        }

                        return MapFields.TryConvert(raw!, ExpectedType!.Value, out _) ? null : Violation(index, $"is not a {ExpectedType}");
                    case RuleKind.Range:
                        if (!present)
                        {
                            return null;
                        }

                        if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                        {
                            return Violation(index, "is not numeric");
                        }

                        return (Min is { } min && number < min) || (Max is { } max && number > max)
                            ? Violation(index, $"is out of range [{(Min?.ToString(CultureInfo.InvariantCulture) ?? "*")}, {(Max?.ToString(CultureInfo.InvariantCulture) ?? "*")}]")
                            : null;
                    case RuleKind.Regex:
                        if (!present)
                        {
                            return null;
                        }

                        try
                        {
                            return Pattern!.IsMatch(raw!) ? null : Violation(index, "does not match the pattern");
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            return Violation(index, "could not be matched within the pattern's time budget");
                        }
                    default:
                        return null;
                }
            }

            private string Violation(int index, string detail) => $"Row {index}: field '{Field}' {detail}.";
        }
    }
}
