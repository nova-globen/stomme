#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acornima;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229 - whether a script parses as its step runs it: this site's JavaScript as a script (no top-level return), n8n's Code
    /// node as a function's body (its return) - both in STRICT mode, as <see cref="JavaScriptSandbox"/> runs every script - said
    /// where an author counts, from 1. Parsing runs nothing. The script writer checks what the model wrote with it, and the editor
    /// checks what the author writes (<see cref="CheckScriptsQuery"/>, n8n's Code node linting).
    /// </summary>
    public static class PipelineScriptSyntax
    {
        /// <summary>What a script is written for, as a Run script step's Style holds it - empty is this site's JavaScript.</summary>
        public static bool TryStyle(string? text, out ScriptStyle style)
        {
            style = ScriptStyle.JavaScript;
            return string.IsNullOrWhiteSpace(text) || (Enum.TryParse(text.Trim(), ignoreCase: true, out style) && Enum.IsDefined(style));
        }

        /// <summary>Why <paramref name="script"/> cannot run as written - it does not parse, said with where; null when it parses.</summary>
        public static string? Problem(string script, ScriptStyle style)
        {
            // n8n's styles run the script as a function's body (its return), so it is parsed as one - a line above it. And it is
            // parsed by the sandbox's own engine with its own options, not a parser set up apart: a parser set up apart called a
            // top-level return illegal, which the sandbox runs (found by the test that holds the two to one rule).
            var asFunction = style != ScriptStyle.JavaScript;
            try
            {
                Jint.Engine.PrepareScript(asFunction ? "(function () {\n" + script + "\n})" : script, strict: true);
                return null;
            }
            catch (Exception ex) when (ParseErrorOf(ex) is { } parse)
            {
                // The parser counts columns from 0 (found driving it: "column 0"); an author counts both from 1.
                return $"It does not parse - line {(asFunction ? parse.LineNumber - 1 : parse.LineNumber)}, column {parse.Column + 1}: {parse.Description}";
            }
        }

        // What any script here can read without declaring it: the language's own, as the sandbox's engine has them.
        private static readonly string[] BuiltIns =
        {
            "Object", "Array", "String", "Number", "Boolean", "Symbol", "BigInt", "Math", "JSON", "Date", "RegExp", "Error", "TypeError",
            "RangeError", "SyntaxError", "ReferenceError", "EvalError", "URIError", "AggregateError", "Map", "Set", "WeakMap", "WeakSet",
            "WeakRef", "Promise", "Proxy", "Reflect", "Intl", "ArrayBuffer", "SharedArrayBuffer", "DataView", "Int8Array", "Uint8Array",
            "Uint8ClampedArray", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array", "Float32Array", "Float64Array", "BigInt64Array",
            "BigUint64Array", "parseInt", "parseFloat", "isNaN", "isFinite", "encodeURIComponent", "decodeURIComponent", "encodeURI",
            "decodeURI", "escape", "unescape", "NaN", "Infinity", "undefined", "globalThis", "eval", "Function", "arguments",
        };

        // What a Run script step's script can read, by what it is written for (RunScript, N8nCode).
        private static string[] OwnNames(ScriptStyle style) => style switch
        {
            ScriptStyle.N8nAllItems => new[] { "input", "log", "items", "console", "$input", "$json" },
            ScriptStyle.N8nEachItem => new[] { "input", "log", "items", "console", "$input", "$json", "item", "$itemIndex" },
            _ => new[] { "input", "log" },
        };

        /// <summary>
        /// The first name a Run script step's script reads that it can see nowhere - not the language's, not its own for what it is
        /// written for, not one of its <paramref name="variables"/>, not one it declares - said with where; null when it reads none,
        /// or does not parse (<see cref="Problem"/> says that). Strict mode makes such a read fail when its line runs (n8n's
        /// linter flags the same). A declaration anywhere counts everywhere, and a name only asked about with typeof is fine: a
        /// warning that is sometimes missed is better than one that is wrong.
        /// </summary>
        public static string? UnknownName(string script, ScriptStyle style, IEnumerable<string> variables)
        {
            var asFunction = style != ScriptStyle.JavaScript;
            Acornima.Ast.Script program;
            try
            {
                program = Jint.Engine.PrepareScript(asFunction ? "(function () {\n" + script + "\n})" : script, strict: true).Program;
            }
            catch (Exception ex) when (ParseErrorOf(ex) is not null)
            {
                return null;
            }

            var known = new HashSet<string>(BuiltIns, StringComparer.Ordinal);
            known.UnionWith(OwnNames(style));
            known.UnionWith(variables ?? Array.Empty<string>());
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var read = new List<Acornima.Ast.Identifier>();
            Walk(program, null, binding: false, declared, read);

            var unknown = read.Where(name => !known.Contains(name.Name) && !declared.Contains(name.Name)).OrderBy(name => name.Start).FirstOrDefault();
            if (unknown is null)
            {
                return null;
            }

            var at = unknown.Location.Start;
            return $"'{unknown.Name}' is not a name this script can see - line {(asFunction ? at.Line - 1 : at.Line)}, column {at.Column + 1}";
        }

        // Every name the script declares, and every one it reads - not a property after a '.', an object's key, a class member's
        // name or a label.
        private static void Walk(Acornima.Ast.Node node, Acornima.Ast.Node? parent, bool binding, HashSet<string> declared, List<Acornima.Ast.Identifier> read)
        {
            switch (node)
            {
                case Acornima.Ast.Identifier name:
                    if (binding)
                    {
                        declared.Add(name.Name);
                    }
                    else if (!NotARead(name, parent))
                    {
                        read.Add(name);
                    }

                    return;
                case Acornima.Ast.VariableDeclarator declarator:
                    Walk(declarator.Id, declarator, binding: true, declared, read);
                    if (declarator.Init is not null)
                    {
                        Walk(declarator.Init, declarator, binding: false, declared, read);
                    }

                    return;
                case Acornima.Ast.IFunction function:
                    if (function.Id is not null)
                    {
                        declared.Add(function.Id.Name);
                    }

                    foreach (var parameter in function.Params)
                    {
                        Walk(parameter, node, binding: true, declared, read);
                    }

                    Walk(function.Body, node, binding: false, declared, read);
                    return;
                case Acornima.Ast.CatchClause caught:
                    if (caught.Param is not null)
                    {
                        Walk(caught.Param, caught, binding: true, declared, read);
                    }

                    Walk(caught.Body, caught, binding: false, declared, read);
                    return;
                case Acornima.Ast.UnaryExpression { Operator: Operator.TypeOf, Argument: Acornima.Ast.Identifier }:
                    return; // typeof asks whether a name is there - it never fails
                case Acornima.Ast.IClass shape when shape.Id is not null:
                    declared.Add(shape.Id.Name);
                    break;
            }

            foreach (var child in node.ChildNodes)
            {
                Walk(child, node, binding, declared, read);
            }
        }

        private static bool NotARead(Acornima.Ast.Identifier name, Acornima.Ast.Node? parent) => parent switch
        {
            Acornima.Ast.MemberExpression member => !member.Computed && ReferenceEquals(member.Property, name),
            Acornima.Ast.Property property => !property.Computed && !property.Shorthand && ReferenceEquals(property.Key, name),
            Acornima.Ast.MethodDefinition method => !method.Computed && ReferenceEquals(method.Key, name),
            Acornima.Ast.PropertyDefinition field => !field.Computed && ReferenceEquals(field.Key, name),
            Acornima.Ast.LabeledStatement or Acornima.Ast.BreakStatement or Acornima.Ast.ContinueStatement => true,
            _ => false,
        };

        private static ParseErrorException? ParseErrorOf(Exception? ex)
        {
            for (; ex is not null; ex = ex.InnerException)
            {
                if (ex is ParseErrorException parse)
                {
                    return parse;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// n8n's Code node linting: the scripts the editor holds, each checked to parse as its step runs it, nothing run and nothing
    /// stored - answered in the order asked, null for a script that parses. Bounded, as a page could send anything.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record CheckScriptsQuery(PipelineScriptCheckRequest Request) : IQuery<PipelineScriptCheckView>;

    public sealed class CheckScriptsQueryHandler : IQueryHandler<CheckScriptsQuery, PipelineScriptCheckView>
    {
        public const int MaxScripts = 200;

        /// <summary>The most of a script's variables' names taken - a step has a handful.</summary>
        public const int MaxNames = 200;

        public Task<PipelineScriptCheckView> Handle(CheckScriptsQuery request, CancellationToken ct)
        {
            var scripts = request.Request?.Scripts ?? new List<PipelineScriptCheck>();
            if (scripts.Count > MaxScripts)
            {
                throw new ArgumentException($"At most {MaxScripts} scripts are checked at once.", nameof(request));
            }

            var view = new PipelineScriptCheckView();
            foreach (var check in scripts)
            {
                var script = check?.Script ?? string.Empty;
                if (script.Length > WriteScriptCommandHandler.MaxScriptLength)
                {
                    throw new ArgumentException($"A script checked is at most {WriteScriptCommandHandler.MaxScriptLength} characters.", nameof(request));
                }

                if (!PipelineScriptSyntax.TryStyle(check?.Style, out var style))
                {
                    throw new ArgumentException($"'{check?.Style}' is not what a script is written for: JavaScript, N8nAllItems or N8nEachItem.", nameof(request));
                }

                view.Problems.Add(script.Trim().Length == 0
                    ? null
                    : PipelineScriptSyntax.Problem(script, style)
                        ?? (check?.Names is { } names ? PipelineScriptSyntax.UnknownName(script, style, names.Take(MaxNames)) : null));
            }

            return Task.FromResult(view);
        }
    }
}
