#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The ONE JavaScript sandbox the steps share (Jint): strict mode, no CLR or host access (Jint
    /// exposes none unless asked), bounded by statements, wall-clock and memory from <see cref="ScriptStepOptions"/>.
    /// Interpreter-level isolation - fit for bounded expression-style scripts, not a substitute for a process boundary
    /// against hostile code, which is why the switch is off by default.
    /// </summary>
    internal static class JavaScriptSandbox
    {
        public sealed record Evaluation(bool Succeeded, string? Value, IReadOnlyList<string> Log, string? Error);

        public sealed record ValueEvaluation(bool Succeeded, object? Value, string? Error);

        /// <summary>
        /// An EXPRESSION's value - the variables given by name (a record or a list, held as JSON text or a JSON
        /// value, as the JavaScript value it is; anything else as itself), the completion value as .NET: text, a number, true or
        /// false, nothing - and a record or a list as its JSON text, the way the data steps pass records.
        /// </summary>
        public static ValueEvaluation EvaluateValue(string script, IReadOnlyDictionary<string, object?> variables, ScriptStepOptions options)
        {
            try
            {
                var engine = new Jint.Engine(o => o
                    .Strict()
                    .LimitMemory(options.MemoryLimitBytes)
                    .MaxStatements(options.StatementLimit)
                    .TimeoutInterval(TimeSpan.FromSeconds(options.TimeoutSeconds)));

                foreach (var (name, value) in variables)
                {
                    if (!IsIdentifier(name))
                    {
                        continue; // a variable whose name JavaScript cannot spell is not in scope
                    }

                    engine.SetValue(name, ToJs(engine, value));
                }

                var completion = engine.Evaluate(script);
                object? result = completion switch
                {
                    _ when completion.IsUndefined() || completion.IsNull() => null,
                    _ when completion.IsString() => completion.AsString(),
                    _ when completion.IsBoolean() => completion.AsBoolean(),
                    _ when completion.IsNumber() => completion.AsNumber() is var number && number == Math.Floor(number) && Math.Abs(number) < 9e15 ? (object)(long)number : number,
                    _ => Stringify(engine, completion),
                };
                return new ValueEvaluation(true, result, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ValueEvaluation(false, null, ex.Message);
            }
        }

        // Only plain values cross into the sandbox - text, a number, true or false - and everything else as the JSON it
        // serialises to, parsed there: never a .NET object, whose members a script could reach through Jint's wrapper.
        private static JsValue ToJs(Jint.Engine engine, object? value)
        {
            switch (value)
            {
                case null:
                    return JsValue.Null;
                case bool flag:
                    return flag ? JsBoolean.True : JsBoolean.False;
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return new JsNumber(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                case DateTime or DateTimeOffset:
                    return new JsString(((IFormattable)value).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                case string text when !(text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')):
                    return new JsString(text);
            }

            string json;
            try
            {
                json = value switch
                {
                    string text => text,
                    System.Text.Json.JsonElement element => element.GetRawText(),
                    _ => System.Text.Json.JsonSerializer.Serialize(value),
                };
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Text.Json.JsonException)
            {
                return new JsString(value.ToString() ?? string.Empty);
            }

            try
            {
                engine.SetValue("__stomme_json", json);
                return engine.Evaluate("JSON.parse(__stomme_json)");
            }
            catch (Jint.Runtime.JavaScriptException)
            {
                return new JsString(value as string ?? json); // not JSON after all: the text itself
            }
        }
        private static string Stringify(Jint.Engine engine, JsValue value)
        {
            engine.SetValue("__stomme_result", value);
            return engine.Evaluate("JSON.stringify(__stomme_result)").AsString();
        }

        private static bool IsIdentifier(string name) =>
            !string.IsNullOrEmpty(name) && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');

        public static Evaluation Evaluate(string script, IReadOnlyDictionary<string, string?> variables, ScriptStepOptions options)
        {
            var log = new List<string>();
            try
            {
                var engine = new Jint.Engine(o => o
                    .Strict()
                    .LimitMemory(options.MemoryLimitBytes)
                    .MaxStatements(options.StatementLimit)
                    .TimeoutInterval(TimeSpan.FromSeconds(options.TimeoutSeconds)));

                engine.SetValue("log", new Action<object?>(logged => log.Add(logged?.ToString() ?? "")));
                foreach (var (name, value) in variables)
                {
                    engine.SetValue(name, value is null ? JsValue.Null : JsValue.FromObject(engine, value));
                }

                var completion = engine.Evaluate(script);
                var result = completion.IsUndefined() || completion.IsNull() ? null : completion.ToString();
                return new Evaluation(true, result, log, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Jint's statement, memory and timeout overflows and every JS runtime error land here.
                return new Evaluation(false, null, log, ex.Message);
            }
        }
    }
}
