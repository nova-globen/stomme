#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - a code box edits like code (n8n's code editor): the editor's own script (<c>pipelines-editor.js</c>), run as it is in
    /// a JavaScript engine with a page's globals stubbed, its <c>stommeCodeBox</c> edits called on a text and a selection. Tab indents
    /// the caret or every line a selection touches, Shift+Tab takes it back, Enter keeps the indentation (one more after an opening
    /// bracket, the closing one to a line of its own), and the caret's place is said as a script's problem says it.
    /// </summary>
    public sealed class PipelineCodeBoxScriptTests
    {
        private const string PageStubs = """
            var listeners = [];
            var window = { addEventListener: function () { } };
            var document = { addEventListener: function (name) { listeners.push(name); }, getElementById: function () { return null; }, querySelector: function () { return null; } };
            var navigator = {};
            var intervals = 0;
            var setInterval = function () { intervals++; };

            """;

        private static readonly Lazy<Jint.Engine> Script = new(() =>
        {
            var engine = new Jint.Engine();
            engine.Execute(PageStubs);
            engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "wwwroot", "Modules", "Pipelines", "pipelines-editor.js")));
            return engine;
        });

        // An edit's outcome as (text, start, end), the selection shown with | for the caret or [ ] around what is selected.
        private static string Edit(string edit, string marked)
        {
            var (value, start, end) = Unmark(marked);
            var answer = Script.Value.Evaluate($"JSON.stringify(window.stommeCodeBox.{edit}({JsonSerializer.Serialize(value)}, {start}, {end}))").ToString();
            using var json = JsonDocument.Parse(answer);
            return Mark(json.RootElement.GetProperty("value").GetString()!, json.RootElement.GetProperty("start").GetInt32(), json.RootElement.GetProperty("end").GetInt32());
        }

        private static (string Value, int Start, int End) Unmark(string marked)
        {
            var caret = marked.IndexOf('|');
            if (caret >= 0)
            {
                return (marked.Remove(caret, 1), caret, caret);
            }

            var start = marked.IndexOf('[');
            var end = marked.IndexOf(']') - 1;
            return (marked.Remove(start, 1).Remove(end, 1), start, end);
        }

        private static string Mark(string value, int start, int end) =>
            start == end ? value.Insert(start, "|") : value.Insert(end, "]").Insert(start, "[");

        [Fact]
        public void The_editors_script_loads_on_a_page_and_listens_for_the_code_boxes_keys()
        {
            var listening = Script.Value.Evaluate("listeners.join(',')").ToString();
            Assert.Contains("keydown", listening);
            Assert.Contains("keyup", listening);

            // And the editor's list of keys says them, the way out of the box too.
            var listed = System.Linq.Enumerable.Select(Stomme.Modules.Pipelines.Editor.Shortcuts, shortcut => shortcut.Keys);
            Assert.Contains("Tab and Shift+Tab in a script", listed);
            Assert.Contains("Escape, then Tab, in a script", listed);
        }

        [Theory]
        [InlineData("a|b", "a  |b")] // two spaces at the caret
        [InlineData("|\nabc", "  |\nabc")] // at the very start, before a line break
        [InlineData("a[bc]d", "a  |d")] // a selection within a line is replaced, as an editor does
        [InlineData("[one\ntwo]\nthree", "  [one\n  two]\nthree")] // every line a selection touches
        [InlineData("x\n[one\ntwo\n]three", "x\n  [one\n  two\n]three")] // not the line it only reaches the start of
        public void Tab_indents_the_caret_or_every_line_a_selection_touches(string before, string after) =>
            Assert.Equal(after, Edit("indent", before));

        [Theory]
        [InlineData("    a|b", "  a|b")] // two spaces back, the caret with its text
        [InlineData(" |ab", "|ab")] // as much as there is
        [InlineData("\t|ab", "|ab")] // a tab
        [InlineData("  [one\n    two]\nthree", "[one\n  two]\nthree")]
        [InlineData("|ab", "|ab")] // nothing to take back
        [InlineData("|\n  abc", "|\n  abc")] // the empty first line, not the next one
        public void Shift_tab_takes_the_indent_back(string before, string after) =>
            Assert.Equal(after, Edit("outdent", before));

        [Theory]
        [InlineData("  var x = 1;|", "  var x = 1;\n  |")] // the line's indentation kept
        [InlineData("if (a) {|", "if (a) {\n  |")] // one more after an opening bracket
        [InlineData("  call(|)", "  call(\n    |\n  )")] // the closing bracket to a line of its own
        [InlineData("[x]|", "[x]\n|")] // a closed bracket opens nothing
        [InlineData("a[bc]d", "a\n|d")] // a selection is replaced
        public void Enter_keeps_the_indentation(string before, string after) =>
            Assert.Equal(after, Edit("newline", before));

        [Theory]
        [InlineData("|", 1, 1)]
        [InlineData("var x = 1;\nreturn |x;", 2, 8)]
        [InlineData("a\n\n|", 3, 1)]
        [InlineData("|\nabc", 1, 1)] // the text's start, a line break after it
        public void The_caret_is_said_as_a_scripts_problem_says_where(string marked, int line, int column)
        {
            var (value, start, _) = Unmark(marked);
            var where = Script.Value.Evaluate($"JSON.stringify(window.stommeCodeBox.position({JsonSerializer.Serialize(value)}, {start}))").ToString();
            Assert.Equal($"{{\"line\":{line},\"column\":{column}}}", where);
        }

        private static string Pieces(string text, string language)
        {
            var json = Script.Value.Evaluate($"JSON.stringify(window.stommeCodeBox.tokens({JsonSerializer.Serialize(text)}, '{language}'))").ToString();
            using var pieces = JsonDocument.Parse(json);
            return string.Join("|", System.Linq.Enumerable.Select(pieces.RootElement.EnumerateArray(), piece => piece[0].GetString() + ":" + piece[1].GetString()));
        }

        // Its code coloured: a script cut into the pieces a code editor colours - keywords, strings, numbers, comments, and the
        // names a script reads that are not its own (n8n's $json, $input) - every character in exactly one piece.
        [Fact]
        public void A_script_is_cut_into_the_pieces_a_code_editor_colours()
        {
            Assert.Equal(
                "keyword:var|plain: total = |number:0|plain:; |comment:// add|plain:\n|keyword:for|plain: (|keyword:const|plain: o |keyword:of|plain: |variable:$input|plain:.all()) total += o.json.amount * |number:1.5|plain:;",
                Pieces("var total = 0; // add\nfor (const o of $input.all()) total += o.json.amount * 1.5;", "javascript"));
            Assert.Equal(
                "string:'it\\'s'|plain: + |string:\"a \\\"b\\\"\"|plain: + |string:`t ${x}`|plain: + |string:'open",
                Pieces("'it\\'s' + \"a \\\"b\\\"\" + `t ${x}` + 'open", "javascript")); // one left open ends with its line
            Assert.Equal("string:'open|plain:\n|keyword:let", Pieces("'open\nlet", "javascript")); // the next line is code again
            Assert.Equal("plain:x1 1x y", Pieces("x1 1x y", "javascript")); // a number inside a name is the name's
            Assert.Equal("comment:/* a\nb */|plain: x", Pieces("/* a\nb */ x", "javascript"));
        }

        [Fact]
        public void A_statement_is_cut_into_the_pieces_its_sql_has()
        {
            Assert.Equal(
                "keyword:SELECT|plain: name |keyword:FROM|plain: orders |comment:-- big|plain:\n|keyword:WHERE|plain: total > |variable:@limit|plain: |keyword:AND|plain: note = |string:'it''s'",
                Pieces("SELECT name FROM orders -- big\nWHERE total > @limit AND note = 'it''s'", "sql"));
        }

        // Drawn as HTML behind the box, so the author's text is escaped - a script holding markup shows it, never runs it.
        [Fact]
        public void The_coloured_copy_shows_markup_in_a_script_as_text()
        {
            var html = Script.Value.Evaluate($"window.stommeCodeBox.highlight({JsonSerializer.Serialize("a < b && '<img src=x onerror=alert(1)>'")}, 'javascript')").ToString();
            Assert.Equal("a &lt; b &amp;&amp; <span class=\"db-code-string\">'&lt;img src=x onerror=alert(1)&gt;'</span>", html);

            // And every character of a text drawn once, in order.
            var text = "if (a) {\n  log('x'); // y\n}\n/* z";
            var joined = Script.Value.Evaluate($"window.stommeCodeBox.tokens({JsonSerializer.Serialize(text)}, 'javascript').map(function (p) {{ return p[1]; }}).join('')").ToString();
            Assert.Equal(text, joined);
        }

        private const string Names = """
            [{"name":"input","kind":"global"},{"name":"log","kind":"global"},{"name":"JSON","kind":"global"},
             {"name":"parse","kind":"member","of":"JSON","insert":"parse()","back":1},{"name":"stringify","kind":"member","of":"JSON","insert":"stringify()","back":1},
             {"name":"limit","kind":"variable"},{"name":"total","kind":"field"},{"name":"customer","kind":"field"}]
            """;

        private static string Offered(string marked, bool asked = false)
        {
            var (value, caret, _) = Unmark(marked);
            return Script.Value.Evaluate($"window.stommeCodeBox.completions({JsonSerializer.Serialize(value)}, {caret}, {Names}, {(asked ? "true" : "false")}).items.map(function (i) {{ return i.name; }}).join(',')").ToString();
        }

        // n8n's completion in its Code node: what may be written where the caret is - the script's own names, its variables' and
        // the words it holds as a word is begun; after a '.', a known name's members or else the fields of what it receives; all
        // of it on Ctrl+Space; nothing in a string or a comment, and never just the word already written.
        [Fact]
        public void What_may_be_written_at_the_caret_is_offered()
        {
            Assert.Equal("input", Offered("var rows = in|"));
            Assert.Equal("parse,stringify", Offered("JSON.|"));
            Assert.Equal("stringify", Offered("JSON.st|"));
            Assert.Equal("customer,total", Offered("rows[0].|")); // no name known before the '.': the fields
            Assert.Equal("total", Offered("row.to|"));
            Assert.Equal("subtotal", Offered("var subtotal = 1;\nsub|")); // a word the script holds
            Assert.Equal("", Offered("log('in|"));
            Assert.Equal("", Offered("// in|"));
            Assert.Equal("", Offered("input|")); // the word already written is not offered
            Assert.Equal("", Offered("x = 1 + |")); // nothing begun, nothing asked
            Assert.Equal("limit,JSON,input,log", Offered("x = 1 + |", asked: true)); // asked: the variables first, then the script's names
            Assert.Equal("input", Offered("'done' + in|")); // a string closed is behind the caret
        }

        [Fact]
        public void A_completion_chosen_writes_what_it_says_with_the_caret_where_it_goes()
        {
            var made = Script.Value.Evaluate("JSON.stringify(window.stommeCodeBox.complete('x = JSON.pa;', 11, 9, { name: 'parse', insert: 'parse()', back: 1 }))").ToString();
            Assert.Equal("{\"value\":\"x = JSON.parse();\",\"start\":15,\"end\":15}", made); // between the brackets
            var named = Script.Value.Evaluate("JSON.stringify(window.stommeCodeBox.complete('row.to', 6, 4, { name: 'total', kind: 'field' }))").ToString();
            Assert.Equal("{\"value\":\"row.total\",\"start\":9,\"end\":9}", named);
        }

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Stomme.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
