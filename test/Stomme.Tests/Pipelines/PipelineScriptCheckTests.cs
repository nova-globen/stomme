#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's Code node linting: the editor's scripts checked to parse as their step runs them - this site's JavaScript as
    /// a script, n8n's as a function's body, both STRICT as the sandbox runs them - nothing run, answered in the order asked.
    /// </summary>
    public sealed class PipelineScriptCheckTests
    {
        private static Task<PipelineScriptCheckView> Check(params PipelineScriptCheck[] scripts) =>
            new CheckScriptsQueryHandler().Handle(new CheckScriptsQuery(new PipelineScriptCheckRequest { Scripts = scripts.ToList() }), CancellationToken.None);

        [Fact]
        public async Task Each_script_is_answered_in_the_order_asked_null_for_one_that_parses()
        {
            var checkedScripts = await Check(
                new PipelineScriptCheck { Script = "var x = 1;\nx + 1" },
                new PipelineScriptCheck { Script = "var x = 1;\nx +;" },
                new PipelineScriptCheck { Script = "return $input.all();", Style = "N8nAllItems" },
                new PipelineScriptCheck { Script = "  " },
                new PipelineScriptCheck { Script = "orders.length + ' orders'", Style = "" });

            Assert.Equal(5, checkedScripts.Problems.Count);
            Assert.Null(checkedScripts.Problems[0]);
            Assert.StartsWith("It does not parse - line 2, column 4: ", checkedScripts.Problems[1]);
            Assert.Equal(new string?[] { null, null, null }, checkedScripts.Problems.Skip(2));
        }

        // ONE rule with the sandbox: what the check refuses fails when the step runs it, and what it passes runs - strict mode
        // included, which a sloppy parse would let through (a `with`, an old octal literal), and a top-level return, which the
        // sandbox runs and a parser set up apart called illegal (this test found it).
        [Theory]
        [InlineData("1 + 1", false)]
        [InlineData("var x = 1;\nreturn x;", false)]
        [InlineData("with ({ a: 1 }) { a }", true)]
        [InlineData("var o = 010; o", true)]
        [InlineData("let x = ;", true)]
        [InlineData("JSON.stringify([1, 2].map(n => n * 2))", false)]
        public void What_the_check_refuses_the_sandbox_cannot_run(string script, bool refused)
        {
            var problem = PipelineScriptSyntax.Problem(script, ScriptStyle.JavaScript);
            var ran = JavaScriptSandbox.Evaluate(script, new Dictionary<string, string?>(), new ScriptStepOptions { Enabled = true });

            Assert.Equal(refused, problem is not null);
            Assert.Equal(!refused, ran.Succeeded);
        }

        // n8n's linter: a name a Run script step's script reads and can see nowhere - not the language's, not its own for what it
        // is written for, not a variable of the step, not one it declares - is said with where; what is not a read (a property
        // after a '.', an object's key, a label, typeof) is not one.
        [Theory]
        [InlineData("var rows = JSON.parse(input);\nrows.filter(r => r.total > limt)", "JavaScript", "'limt' is not a name this script can see - line 2, column 28")]
        [InlineData("var rows = JSON.parse(input); rows.length + limit", "JavaScript", null)]
        [InlineData("function f() { return g(); } function g() { return 1; } f()", "JavaScript", null)] // declared later counts
        [InlineData("var o = { total: 1 }; o.total; outer: for (;;) { break outer; }", "JavaScript", null)]
        [InlineData("typeof missing === 'undefined'", "JavaScript", null)]
        [InlineData("var { a, b: c } = { a: 1, b: 2 }; [1].map(n => n * a + c)", "JavaScript", null)]
        [InlineData("try { log(x) } catch (e) { log(e) }", "JavaScript", "'x' is not a name this script can see - line 1, column 11")]
        [InlineData("total = 1", "JavaScript", "'total' is not a name this script can see - line 1, column 1")] // strict: no global made
        [InlineData("return { json: { total: $json.total, at: $itemIndex } };", "N8nEachItem", null)]
        [InlineData("const all = $input.all();\nreturn item;", "N8nAllItems", "'item' is not a name this script can see - line 2, column 8")] // each item's only
        [InlineData("var x = ;", "JavaScript", null)] // does not parse: the parse check says that
        public void A_name_the_script_can_see_nowhere_is_said(string script, string style, string? expected)
        {
            Assert.True(PipelineScriptSyntax.TryStyle(style, out var written));
            Assert.Equal(expected, PipelineScriptSyntax.UnknownName(script, written, new[] { "limit" }));
        }

        // ONE rule with the sandbox here too: a name said to be unknown fails the script when it runs; one the step gives runs.
        [Fact]
        public void A_name_said_to_be_unknown_fails_in_the_sandbox()
        {
            var variables = new Dictionary<string, string?> { ["limit"] = "100" };
            var options = new ScriptStepOptions { Enabled = true };
            Assert.NotNull(PipelineScriptSyntax.UnknownName("limt + 1", ScriptStyle.JavaScript, variables.Keys));
            Assert.Contains("limt", JavaScriptSandbox.Evaluate("limt + 1", variables, options).Error);
            Assert.Null(PipelineScriptSyntax.UnknownName("Number(limit) + 1", ScriptStyle.JavaScript, variables.Keys));
            Assert.True(JavaScriptSandbox.Evaluate("Number(limit) + 1", variables, options).Succeeded);
        }

        // Asked with the names, a script that parses is checked for them too; asked without (an input's JavaScript, which reads every
        // variable in scope), it is checked to parse only.
        [Fact]
        public async Task Names_are_checked_only_where_they_are_all_known()
        {
            var checkedScripts = await Check(
                new PipelineScriptCheck { Script = "limt * 2", Names = new() { "limit" } },
                new PipelineScriptCheck { Script = "limt * 2" });

            Assert.Equal(new[] { "'limt' is not a name this script can see - line 1, column 1", null }, checkedScripts.Problems);
        }

        [Fact]
        public async Task A_page_cannot_send_anything_it_likes()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Check(Enumerable.Range(0, 201).Select(_ => new PipelineScriptCheck { Script = "1" }).ToArray()));
            await Assert.ThrowsAsync<ArgumentException>(() => Check(new PipelineScriptCheck { Script = new string('1', 20001) }));
            var style = await Assert.ThrowsAsync<ArgumentException>(() => Check(new PipelineScriptCheck { Script = "1", Style = "Python" }));
            Assert.StartsWith("'Python' is not what a script is written for", style.Message);
            Assert.Empty((await Check()).Problems);
        }
    }
}
