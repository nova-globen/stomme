#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// n8n's Evaluation trigger: where an evaluation's test cases enter. An evaluation runs the published pipeline once per
    /// row of a data table, each run STARTING HERE with the row (a JSON object of its columns) - or the one column a pipeline's usual trigger would give, a message, say - as this step's result, the whole row its Row output. A pipeline
    /// holds it beside its usual trigger; it is never a run's start otherwise (<see cref="IStartsOnlyWhenNamed"/>), so a webhook's,
    /// a schedule's or the Run button's runs are what they were.
    /// </summary>
    [Step("Stomme", "Triggers", "Where an evaluation's test cases enter: each row of the chosen data table runs the pipeline from here, the row - or one of its columns - as this step's result. Never a run's start otherwise.", DisplayName = "When evaluating")]
    public class EvaluationTrigger : CodeStep<string>, IStartsOnlyWhenNamed
    {
        public const string StepTypeName = PipelineEvaluations.TriggerType;
        public const string EvaluationInputKey = "__EvaluationId";
        public const string RowIdInputKey = "__EvaluationRowId";
        public const string RowInputKey = "__EvaluationRow";

        [Input(Description = "The row's column whose value is this step's result - what the pipeline's usual trigger gives, a message, say. Empty: the whole row, a JSON object.", Order = 1)]
        public Input<string?>? Column { get; set; }

        [Output(Description = "The whole row, a JSON object of its id and columns: saved to a variable named row, '{{ row.expected }}' reads a column.")]
        public Output<string>? Row { get; set; }

        protected override void Execute(StepExecutionContext context)
        {
            var row = context.TryGetPipelineInput<string>(RowInputKey, out var given) && !string.IsNullOrEmpty(given) ? given : "{}";
            context.Set(Row, row);
            var column = (Column?.GetOrDefault(context) ?? "").Trim();
            context.SetResult(column.Length == 0 ? row : ValueOf(row, column));
        }

        // A column's value as text (a value that is no text, as JSON); a column the row does not have is the author's mistake.
        private static string ValueOf(string row, string column)
        {
            var values = JsonNode.Parse(row) as JsonObject ?? new JsonObject();
            var found = values.FirstOrDefault(property => string.Equals(property.Key, column, StringComparison.OrdinalIgnoreCase));
            if (found.Key is null)
            {
                throw new FaultException("EVALUATION_NO_COLUMN", "Pipelines", "Configuration",
                    $"The test case has no column '{column}' - its columns: {string.Join(", ", values.Select(property => property.Key))}.");
            }

            return found.Value switch
            {
                null => "",
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                var other => other.ToJsonString(),
            };
        }

        /// <summary>The evaluation and the row this run is a test case of; nulls when it is no evaluation's run.</summary>
        public static (string? EvaluationId, string? RowId) Of(StepExecutionContext context) =>
            context.TryGetPipelineInput<string>(EvaluationInputKey, out var evaluation) && !string.IsNullOrEmpty(evaluation)
                && context.TryGetPipelineInput<string>(RowIdInputKey, out var row) && !string.IsNullOrEmpty(row)
                ? (evaluation, row)
                : (null, null);
    }

    /// <summary>What an <see cref="Evaluation"/> step records for the row.</summary>
    public enum EvaluationOperation
    {
        /// <summary>'name: number' lines, each a metric.</summary>
        RecordMetrics,

        /// <summary>One named output: what the pipeline produced for the row, shown beside it.</summary>
        RecordOutput,

        /// <summary>A metric scored from how an actual value matches the expected one.</summary>
        ScoreMatch,

        /// <summary>The site's language model scores, 1 to 5, how correct the answer is against the expected one (n8n's Correctness).</summary>
        JudgeCorrectness,

        /// <summary>The site's language model scores, 1 to 5, how helpful the answer is to the question (n8n's Helpfulness).</summary>
        JudgeHelpfulness,
    }

    /// <summary>How <see cref="EvaluationOperation.ScoreMatch"/> compares.</summary>
    public enum EvaluationMatch
    {
        /// <summary>1 when the texts are the same (trimmed), else 0.</summary>
        Exact,

        /// <summary>1 when the texts are the same ignoring case, else 0 - a category, say.</summary>
        IgnoringCase,

        /// <summary>1 when the actual text contains the expected one, ignoring case, else 0.</summary>
        Contains,

        /// <summary>0 to 1, how alike the texts are (edit distance).</summary>
        Similarity,

        /// <summary>1 when both are numbers no further apart than the tolerance, else 0.</summary>
        Number,
    }

    /// <summary>
    /// n8n's Evaluation node: records, for the row an evaluation's run is a test case of, metrics (scores the evaluation
    /// averages), an output (shown beside the row), or a metric scored from how an actual value matches the expected one. In a run
    /// that is no evaluation's it records nothing and the previous result passes through, so the step can stay in the pipeline.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Testing", "Record metrics or an output for the evaluation's row, score how a value matches the expected one, or have the site's language model judge an answer. Records nothing outside an evaluation.", DisplayName = "Evaluation")]
    public class Evaluation : DataStep
    {
        [Input(DefaultValue = EvaluationOperation.RecordMetrics, Description = "Record metrics: 'name: number' lines. Record output: one named value. Score a match: compare Actual with Expected into a metric. Judge correctness or helpfulness: the site's language model scores Actual from 1 to 5.", Order = 1)]
        public Input<EvaluationOperation> Operation { get; set; } = new(new Literal<EvaluationOperation>(EvaluationOperation.RecordMetrics));

        [Input(UIHint = InputUIHints.MultiLine, Description = "Record metrics: one 'name: number' per line - 'correct: {{ score }}'.", Order = 2)]
        public Input<string?>? Metrics { get; set; }

        [Input(DisplayName = "Name", Description = "Record output: the output's name. Score a match: the metric's name (match when empty).", Order = 3)]
        public Input<string?>? MetricName { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "Record output: the value - '{{ reply }}'. Empty: the previous result.", Order = 4)]
        public Input<string?>? Value { get; set; }

        [Input(Description = "Score a match: what the row expects - '{{ row.expected }}'.", Order = 5)]
        public Input<string?>? Expected { get; set; }

        [Input(Description = "Score a match: what the pipeline produced. Empty: the previous result.", Order = 6)]
        public Input<string?>? Actual { get; set; }

        [Input(DisplayName = "Compare as", DefaultValue = EvaluationMatch.Exact, Description = "Exact, Ignoring case, Contains (1 or 0), Similarity (0 to 1) or Number (1 within the tolerance).", Order = 7)]
        public Input<EvaluationMatch> Match { get; set; } = new(new Literal<EvaluationMatch>(EvaluationMatch.Exact));

        [Input(Description = "Score a match as Number: how far apart the two may be and still match.", Order = 8)]
        public Input<string?>? Tolerance { get; set; }

        [Input(Description = "Judge correctness or helpfulness: the question Actual answers - '{{ row.question }}'.", Order = 9)]
        public Input<string?>? Question { get; set; }

        [Output(Description = "Score a match: the score, 0 to 1. Judge: the model's score, 1 to 5.")]
        public Output<string?>? Score { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var (evaluationId, rowId) = EvaluationTrigger.Of(context);
            var passing = TextOrLastResult(context, null);
            Dictionary<string, decimal>? metrics = null;
            Dictionary<string, string>? outputs = null;
            var operation = Operation.GetOrDefault(context);
            switch (operation)
            {
                case EvaluationOperation.RecordMetrics:
                    metrics = PipelineEvaluations.ParseMetrics(Metrics?.GetOrDefault(context), out var error);
                    if (metrics is null)
                    {
                        await FailAsync(context, "EVALUATION_INVALID", error!).ConfigureAwait(false);
                        return;
                    }

                    break;
                case EvaluationOperation.RecordOutput:
                    var name = (MetricName?.GetOrDefault(context) ?? "").Trim();
                    if (name.Length == 0 || name.Length > PipelineEvaluations.MaxNameLength)
                    {
                        await FailAsync(context, "EVALUATION_INVALID", $"Record output needs a name of at most {PipelineEvaluations.MaxNameLength} characters.").ConfigureAwait(false);
                        return;
                    }

                    var value = TextOrLastResult(context, Value) ?? "";
                    outputs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [name] = value.Length > PipelineEvaluations.MaxOutputLength ? value[..PipelineEvaluations.MaxOutputLength] + "…" : value,
                    };
                    break;
                case EvaluationOperation.ScoreMatch:
                    var metric = (MetricName?.GetOrDefault(context) ?? "").Trim();
                    metric = metric.Length == 0 ? "match" : metric;
                    if (metric.Length > PipelineEvaluations.MaxNameLength)
                    {
                        await FailAsync(context, "EVALUATION_INVALID", $"A metric's name is at most {PipelineEvaluations.MaxNameLength} characters.").ConfigureAwait(false);
                        return;
                    }

                    var score = ScoreOf(Match.GetOrDefault(context), Expected?.GetOrDefault(context), TextOrLastResult(context, Actual), Tolerance?.GetOrDefault(context));
                    context.Set(Score, score.ToString(CultureInfo.InvariantCulture));
                    metrics = new Dictionary<string, decimal>(StringComparer.Ordinal) { [metric] = score };
                    break;
                case EvaluationOperation.JudgeCorrectness:
                case EvaluationOperation.JudgeHelpfulness:
                    var correctness = operation == EvaluationOperation.JudgeCorrectness;
                    var judged = (MetricName?.GetOrDefault(context) ?? "").Trim();
                    judged = judged.Length == 0 ? (correctness ? "correctness" : "helpfulness") : judged;
                    if (judged.Length > PipelineEvaluations.MaxNameLength)
                    {
                        await FailAsync(context, "EVALUATION_INVALID", $"A metric's name is at most {PipelineEvaluations.MaxNameLength} characters.").ConfigureAwait(false);
                        return;
                    }

                    if (evaluationId is null || rowId is null)
                    {
                        // A model call costs the site's quota: outside an evaluation there is nothing to judge for.
                        context.AddExecutionLogEntry("Not evaluating", "The run is no evaluation's - the model was not asked and nothing recorded.");
                        await SucceedAsync(context, passing).ConfigureAwait(false);
                        return;
                    }

                    var (verdict, judgeCode, judgeError) = await JudgeAsync(context, correctness, Question?.GetOrDefault(context), Expected?.GetOrDefault(context), TextOrLastResult(context, Actual)).ConfigureAwait(false);
                    if (verdict is null)
                    {
                        await FailAsync(context, judgeCode!, judgeError!).ConfigureAwait(false);
                        return;
                    }

                    context.Set(Score, verdict.Value.Score.ToString(CultureInfo.InvariantCulture));
                    metrics = new Dictionary<string, decimal>(StringComparer.Ordinal) { [judged] = verdict.Value.Score };
                    if (!string.IsNullOrWhiteSpace(verdict.Value.Reason))
                    {
                        var reasonName = judged + " reason";
                        outputs = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [reasonName.Length > PipelineEvaluations.MaxNameLength ? judged : reasonName] = verdict.Value.Reason.Length > PipelineEvaluations.MaxOutputLength ? verdict.Value.Reason[..PipelineEvaluations.MaxOutputLength] + "…" : verdict.Value.Reason,
                        };
                    }

                    break;
            }

            if (evaluationId is null || rowId is null)
            {
                context.AddExecutionLogEntry("Not evaluating", "The run is no evaluation's - nothing recorded.");
                await SucceedAsync(context, passing).ConfigureAwait(false);
                return;
            }

            var keyValues = context.GetService<IKeyValueStore>()
                ?? throw new FaultException("EVALUATION_UNAVAILABLE", "Pipelines", "Configuration", "The engine's key-value store is not composed in this host.");
            try
            {
                await PipelineEvaluationStore.RecordAsync(keyValues, context.GetService<ITenantAccessor>()?.TenantId, evaluationId, rowId, metrics, outputs, context.CancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await FailAsync(context, "EVALUATION_INVALID", ex.Message).ConfigureAwait(false);
                return;
            }

            context.AddExecutionLogEntry("Recorded", metrics is not null
                ? "Recorded " + string.Join(", ", metrics.Select(pair => $"{pair.Key} {pair.Value.ToString(CultureInfo.InvariantCulture)}")) + " for the row."
                : $"Recorded the output '{outputs!.Keys.First()}' for the row.");
            await SucceedAsync(context, passing).ConfigureAwait(false);
        }

        /// <summary>What the judge is told, for correctness: the scale, and to answer with a score and a reason as JSON.</summary>
        public const string CorrectnessPrompt =
            "You judge whether an ANSWER is correct, comparing it with the REFERENCE answer (and the QUESTION, when there is one). " +
            "Score it from 1 to 5: 5 - it says what the reference says, fully correct; 4 - correct, with small omissions; 3 - partly correct; " +
            "2 - mostly wrong; 1 - wrong, or it contradicts the reference. Wording and length do not matter, only whether it is right. " +
            "Answer only with a JSON object: {\"score\": <1 to 5>, \"reason\": \"<one sentence>\"}.";

        /// <summary>What the judge is told, for helpfulness.</summary>
        public const string HelpfulnessPrompt =
            "You judge how helpful an ANSWER is to a QUESTION. Score it from 1 to 5: 5 - it answers the question fully and directly; " +
            "4 - helpful, with small gaps; 3 - partly helpful; 2 - barely helpful; 1 - not helpful, or off the subject. " +
            "Answer only with a JSON object: {\"score\": <1 to 5>, \"reason\": \"<one sentence>\"}.";

        // One call to the site's model as the judge: its score from 1 to 5 and its reason, or the failure as the step reports it.
        private static async Task<((decimal Score, string? Reason)? Verdict, string? Code, string? Error)> JudgeAsync(StepExecutionContext context, bool correctness, string? question, string? expected, string? answer)
        {
            if (correctness && string.IsNullOrWhiteSpace(expected))
            {
                return (null, "EVALUATION_INVALID", "Judge correctness needs Expected - the answer it should be.");
            }

            if (!correctness && string.IsNullOrWhiteSpace(question))
            {
                return (null, "EVALUATION_INVALID", "Judge helpfulness needs the Question the answer is to.");
            }

            var asked = (string.IsNullOrWhiteSpace(question) ? "" : "QUESTION:\n" + question.Trim() + "\n\n")
                + (correctness ? "REFERENCE:\n" + expected!.Trim() + "\n\n" : "")
                + "ANSWER:\n" + (answer ?? "").Trim();
            var (reply, code, error) = await AiModelCall.AskAsync(context, new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, correctness ? CorrectnessPrompt : HelpfulnessPrompt),
                new(Microsoft.Extensions.AI.ChatRole.User, asked),
            }, json: true).ConfigureAwait(false);
            if (reply is null)
            {
                return (null, code, error);
            }

            var found = AiModelCall.Object(reply, out var notJson);
            var score = found?["score"] switch
            {
                System.Text.Json.Nodes.JsonValue value when value.TryGetValue<decimal>(out var number) => number,
                System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => (decimal?)null,
            };
            if (score is not { } judged || judged < 1 || judged > 5)
            {
                return (null, "AI_NO_SCORE", "The model gave no score from 1 to 5" + (notJson is null ? "" : " - " + notJson) + ".");
            }

            return ((Math.Round(judged, 2), found!["reason"] is System.Text.Json.Nodes.JsonValue reason && reason.TryGetValue<string>(out var said) ? said.Trim() : null), null, null);
        }

        /// <summary>The score, 0 to 1, of an actual value against the expected one.</summary>
        public static decimal ScoreOf(EvaluationMatch match, string? expected, string? actual, string? tolerance)
        {
            var e = (expected ?? "").Trim();
            var a = (actual ?? "").Trim();
            return match switch
            {
                EvaluationMatch.Exact => string.Equals(e, a, StringComparison.Ordinal) ? 1m : 0m,
                EvaluationMatch.IgnoringCase => string.Equals(e, a, StringComparison.OrdinalIgnoreCase) ? 1m : 0m,
                EvaluationMatch.Contains => a.Contains(e, StringComparison.OrdinalIgnoreCase) ? 1m : 0m,
                EvaluationMatch.Similarity => PipelineEvaluations.Similarity(e, a),
                EvaluationMatch.Number =>
                    decimal.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber)
                    && decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualNumber)
                    && Math.Abs(expectedNumber - actualNumber) <= (decimal.TryParse((tolerance ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var within) ? Math.Abs(within) : 0m)
                        ? 1m
                        : 0m,
                _ => 0m,
            };
        }
    }

    /// <summary>
    /// n8n's Evaluation "check if evaluating": goes on down Evaluating in an evaluation's run and down Not evaluating
    /// otherwise - so a pipeline skips what a test case must not do (send the email, charge the card) and keeps the rest.
    /// </summary>
    [FlowNode(EvaluatingOutcome, NotEvaluatingOutcome)]
    [Step("Stomme", "Testing", "Go on down Evaluating in an evaluation's run, down Not evaluating otherwise - to skip what a test case must not do.", DisplayName = "If evaluating")]
    public class IfEvaluating : Step
    {
        public const string EvaluatingOutcome = "Evaluating";
        public const string NotEvaluatingOutcome = "Not evaluating";

        protected override async ValueTask ExecuteAsync(StepExecutionContext context) =>
            await context.CompleteStepWithOutcomesAsync(EvaluationTrigger.Of(context).EvaluationId is null ? NotEvaluatingOutcome : EvaluatingOutcome).ConfigureAwait(false);
    }
}
