#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Modules.Notifications.Application.Templates;
using Fluid;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>The target type of a mapped field (the predecessor platform's <c>FieldType</c>).</summary>
    public enum MappedFieldType
    {
        String,
        Number,
        Boolean,
        Date,
        Json,

        /// <summary>The value as it is, whatever it holds - a number stays a number, a list a list (n8n's Rename Keys and
        /// the fields its Edit Fields keeps move a value without converting it).</summary>
        Any,
    }

    /// <summary>
    /// the predecessor platform's Transformations as one engine step: field mappings with typed conversion and defaults, computed
    /// fields as Liquid templates over the record (Fluid, bounded by the Notifications renderer's options - no host
    /// access by construction), pass-through of unmapped fields, and an error threshold the step fails above (the
    /// partial output stays in the result for diagnosis). The template aggregate is not ported: the mappings
    /// ARE the step's inputs; the predecessor platform's Format references (a separate module) and its JavaScript computed fields are
    /// not carried in this increment.
    /// <para>
    /// <b>Mappings</b>, one per line: <c>source.path =&gt; destination : Type = default</c>, the type one of
    /// <see cref="MappedFieldType"/> (String when omitted), the default optional and used when the source is absent
    /// or does not convert. <b>Computed</b>, one per line: <c>destination = {{ liquid over the record's fields }}</c>,
    /// or <c>destination = js: expression</c> over the same fields in the bounded JavaScript sandbox - refused as an
    /// error unless <c>Pipelines:Scripts:Enabled</c>. A computed field is text unless it names a type, as a mapping does
    /// (<c>total : Number = js: price * quantity</c>, BL-229 - n8n's Edit Fields types every field it sets): the result
    /// converted, a result that does not convert an error, and one that is empty no value. <b>Leave out</b> names the fields
    /// passed through that are not (n8n's "all input fields except"; with a mapping from the old name, its Rename Keys).
    /// </para>
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Map, convert and compute fields of records.", DisplayName = "Map fields")]
    public class MapFields : DataStep
    {
        private static readonly FluidParser Parser = new();

        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "One per line: source.path => destination : Type = default (Type: String, Number, Boolean, Date, Json, or Any - the value as it is).", Order = 2)]
        public Input<string?>? Mappings { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "One per line: destination : Type = {{ liquid over the record's fields }}, or destination : Type = js: expression (Pipelines:Scripts:Enabled). Type is optional: text when left out.", Order = 3)]
        public Input<string?>? Computed { get; set; }

        [Input(DisplayName = "Pass through unmapped", Description = "Keep every source field that no mapping names.", DefaultValue = false, Order = 4)]
        public Input<bool> PassThroughUnmapped { get; set; } = new(new Literal<bool>(false));

        [Input(DisplayName = "Leave out", Description = "Fields passed through that are left out, separated by commas.", Order = 5)]
        public Input<string?>? LeaveOut { get; set; }

        [Input(DisplayName = "Error threshold", Description = "How many conversion or expression errors are tolerated before the step fails.", DefaultValue = 0, Order = 6)]
        public Input<int> ErrorThreshold { get; set; } = new(new Literal<int>(0));

        [Output(DisplayName = "Error count")]
        public Output<int>? ErrorCount { get; set; }

        [Output(Description = "The errors, as a JSON array of messages.")]
        public Output<string>? Errors { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            var scripts = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Scripts ?? new ScriptStepOptions();
            List<Mapping> mappings;
            List<ComputedField> computed;
            try
            {
                mappings = Steps.Records.Lines(Mappings?.GetOrDefault(context)).Select(Mapping.Parse).ToList();
                computed = Steps.Records.Lines(Computed?.GetOrDefault(context)).Select(ParseComputed).ToList();
            }
            catch (FormatException ex)
            {
                await FailAsync(context, "MAPPING_INVALID", ex.Message).ConfigureAwait(false);
                return;
            }

            var errors = new List<string>();
            var passThrough = PassThroughUnmapped.GetOrDefault(context);
            var leftOut = (LeaveOut?.GetOrDefault(context) ?? "").Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            var output = new List<JsonObject>();
            foreach (var row in rows)
            {
                var target = new JsonObject();
                if (passThrough)
                {
                    foreach (var property in row.Where(property => !leftOut.Contains(property.Key)))
                    {
                        target[property.Key] = property.Value?.DeepClone();
                    }
                }

                foreach (var mapping in mappings)
                {
                    target[mapping.Destination] = mapping.Type == MappedFieldType.Any
                        ? Steps.Records.ReadPath(row, mapping.Source)?.DeepClone() ?? (mapping.Default is null ? null : JsonValue.Create(mapping.Default))
                        : Convert(mapping, Steps.Records.Text(Steps.Records.ReadPath(row, mapping.Source)), errors);
                }

                foreach (var field in computed)
                {
                    var value = field.Template is not null
                        ? await EvaluateAsync(field.Destination, field.Template, row, errors).ConfigureAwait(false)
                        : EvaluateScript(field.Destination, field.Script!, row, scripts, errors);
                    target[field.Destination] = Typed(field, value, errors);
                }

                output.Add(target);
            }

            var threshold = Math.Max(0, ErrorThreshold.GetOrDefault(context));
            context.Set(ErrorCount, errors.Count);
            context.Set(Errors, JsonSerializer.Serialize(errors));
            var result = Steps.Records.ToJson(output);
            if (errors.Count > threshold)
            {
                context.Set(Result, result);
                await FailAsync(context, "ERROR_THRESHOLD_EXCEEDED", $"{errors.Count} error(s) exceed the threshold of {threshold}: {errors[0]}").ConfigureAwait(false);
                return;
            }

            await SucceedAsync(context, result).ConfigureAwait(false);
        }

        private static JsonNode? Convert(Mapping mapping, string? raw, List<string> errors)
        {
            if (raw is null)
            {
                return mapping.Default is null ? null : ConvertOrDefault(mapping, mapping.Default, errors);
            }

            return ConvertOrDefault(mapping, raw, errors);
        }

        private static JsonNode? ConvertOrDefault(Mapping mapping, string raw, List<string> errors)
        {
            if (TryConvert(raw, mapping.Type, out var value))
            {
                return value;
            }

            errors.Add($"'{raw}' could not be converted to {mapping.Type} for field '{mapping.Destination}'.");
            return mapping.Default is null ? null : JsonValue.Create(mapping.Default);
        }

        internal static bool TryConvert(string raw, MappedFieldType type, out JsonNode? value)
        {
            value = null;
            switch (type)
            {
                case MappedFieldType.String:
                case MappedFieldType.Any:
                    value = JsonValue.Create(raw);
                    return true;
                case MappedFieldType.Number:
                    if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                    {
                        value = JsonValue.Create(number);
                        return true;
                    }

                    return false;
                case MappedFieldType.Boolean:
                    if (bool.TryParse(raw, out var b))
                    {
                        value = JsonValue.Create(b);
                        return true;
                    }

                    return false;
                case MappedFieldType.Date:
                    if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    {
                        value = JsonValue.Create(date.ToString("O", CultureInfo.InvariantCulture));
                        return true;
                    }

                    return false;
                case MappedFieldType.Json:
                    try
                    {
                        value = JsonNode.Parse(raw);
                        return true;
                    }
                    catch (JsonException)
                    {
                        value = JsonValue.Create(raw);
                        return true;
                    }
                default:
                    return false;
            }
        }

        private sealed record ComputedField(string Destination, IFluidTemplate? Template, string? Script, MappedFieldType? Type);

        private static ComputedField ParseComputed(string line)
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                throw new FormatException($"Computed field '{line}' is not 'destination = template'.");
            }

            var destination = line[..equals].Trim();
            var source = line[(equals + 1)..].Trim();
            MappedFieldType? type = null;
            var colon = destination.IndexOf(':');
            if (colon >= 0)
            {
                var typeName = destination[(colon + 1)..].Trim();
                if (!Enum.TryParse(typeName, ignoreCase: true, out MappedFieldType named))
                {
                    throw new FormatException($"Computed field '{line}' names an unknown type '{typeName}' (String, Number, Boolean, Date, Json, Any).");
                }

                type = named;
                destination = destination[..colon].Trim();
            }

            if (destination.Length == 0)
            {
                throw new FormatException($"Computed field '{line}' needs a destination.");
            }

            if (source.StartsWith("js:", StringComparison.OrdinalIgnoreCase))
            {
                return new ComputedField(destination, null, source[3..].Trim(), type);
            }

            if (!Parser.TryParse(source, out var template, out var error))
            {
                throw new FormatException($"Computed field '{destination}' has an invalid Liquid template: {error}");
            }

            return new ComputedField(destination, template, null, type);
        }

        // A computed field's result as the type it names: text as it came when it names none (or String); converted
        // otherwise - an empty result is no value, one that does not convert an error the threshold counts.
        private static JsonNode? Typed(ComputedField field, JsonNode? value, List<string> errors)
        {
            if (field.Type is null or MappedFieldType.String or MappedFieldType.Any || value is null)
            {
                return value;
            }

            var text = Steps.Records.Text(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (TryConvert(text, field.Type.Value, out var converted))
            {
                return converted;
            }

            errors.Add($"'{text}' could not be converted to {field.Type} for field '{field.Destination}'.");
            return null;
        }

        private static JsonNode? EvaluateScript(string destination, string script, JsonObject row, ScriptStepOptions options, List<string> errors)
        {
            if (!options.Enabled)
            {
                errors.Add($"Computed field '{destination}' needs Pipelines:Scripts:Enabled for a js: expression.");
                return null;
            }

            var evaluation = JavaScriptSandbox.Evaluate(script, row.ToDictionary(p => p.Key, p => Steps.Records.Text(p.Value)), options);
            if (!evaluation.Succeeded)
            {
                errors.Add($"Computed field '{destination}' failed: {evaluation.Error}");
                return null;
            }

            return evaluation.Value is null ? null : JsonValue.Create(evaluation.Value);
        }

        private static async Task<JsonNode?> EvaluateAsync(string destination, IFluidTemplate template, JsonObject row, List<string> errors)
        {
            try
            {
                var context = new TemplateContext(NotificationTemplateRenderer.Options);
                foreach (var property in row)
                {
                    context.SetValue(property.Key, Steps.Records.Text(property.Value) ?? "");
                }

                return JsonValue.Create(await template.RenderAsync(context, NullEncoder.Default).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"Computed field '{destination}' failed: {ex.Message}");
                return null;
            }
        }

        private sealed record Mapping(string Source, string Destination, MappedFieldType Type, string? Default)
        {
            public static Mapping Parse(string line)
            {
                var arrow = line.IndexOf("=>", StringComparison.Ordinal);
                var arrowLength = 2;
                if (arrow < 0)
                {
                    arrow = line.IndexOf("->", StringComparison.Ordinal);
                }

                if (arrow <= 0)
                {
                    throw new FormatException($"Mapping '{line}' is not 'source => destination : Type = default'.");
                }

                var source = line[..arrow].Trim();
                var rest = line[(arrow + arrowLength)..].Trim();
                string? @default = null;
                var equals = rest.IndexOf('=');
                if (equals >= 0)
                {
                    @default = rest[(equals + 1)..].Trim();
                    rest = rest[..equals].Trim();
                }

                var type = MappedFieldType.String;
                var colon = rest.IndexOf(':');
                if (colon >= 0)
                {
                    var typeName = rest[(colon + 1)..].Trim();
                    if (!Enum.TryParse(typeName, ignoreCase: true, out type))
                    {
                        throw new FormatException($"Mapping '{line}' names an unknown type '{typeName}' (String, Number, Boolean, Date, Json, Any).");
                    }

                    rest = rest[..colon].Trim();
                }

                if (source.Length == 0 || rest.Length == 0)
                {
                    throw new FormatException($"Mapping '{line}' needs a source and a destination.");
                }

                return new Mapping(source, rest, type, @default);
            }
        }
    }
}
