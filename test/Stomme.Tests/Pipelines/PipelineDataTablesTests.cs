#nullable enable
using System.Linq;
using Bunit;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Canvas;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Table view of a step's data: each input or output holding a list of records - as JSON, or as the JSON text
    /// the data steps pass - is a table, one column per field in the order fields first appear; a list of plain values is one
    /// column; anything else is no table, and the JSON stays the default view.
    /// </summary>
    public sealed class PipelineDataTablesTests : BunitContext
    {
        private const string Records = "{ \"Text\": \"name,town\", \"Result\": \"[{\\\"name\\\":\\\"Ada\\\",\\\"town\\\":\\\"London\\\"},{\\\"name\\\":\\\"Grace\\\",\\\"age\\\":85}]\" }";

        [Fact]
        public void A_list_of_records_is_a_table_and_nothing_else_is()
        {
            var table = Assert.Single(PipelineDataTables.Of(Records));
            Assert.Equal("Result", table.Name);
            Assert.Equal(new[] { "name", "town", "age" }, table.Columns);
            Assert.Equal(new[] { "Ada", "London", "" }, table.Rows[0]);
            Assert.Equal(new[] { "Grace", "", "85" }, table.Rows[1]);
            Assert.Equal(2, table.Total);

            // A list as JSON (not text), a list of plain values, and a list on its own.
            var direct = Assert.Single(PipelineDataTables.Of("{ \"Items\": [ { \"n\": 1, \"tags\": [\"a\"] } ] }"));
            Assert.Equal(new[] { "1", "[\"a\"]" }, direct.Rows[0]);
            var plain = Assert.Single(PipelineDataTables.Of("{ \"Numbers\": [1, 2, 3] }"));
            Assert.Equal(new[] { PipelineDataTables.ValueColumn }, plain.Columns);
            Assert.Equal(new[] { "1", "2", "3" }, plain.Rows.Select(row => row[0]));
            Assert.Single(PipelineDataTables.Of("[{\"a\":1}]"));

            // No table: no list, an empty one, a mixed one, cut or broken JSON, nothing at all.
            Assert.Empty(PipelineDataTables.Of("{ \"Message\": \"hello\", \"Count\": 3 }"));
            Assert.Empty(PipelineDataTables.Of("{ \"Result\": \"[]\" }"));
            Assert.Empty(PipelineDataTables.Of("{ \"Result\": [ {\"a\":1}, [2] ] }"));
            Assert.Empty(PipelineDataTables.Of("{ \"Result\": \"[{\\\"a\\\":1}\" } … (cut)"));
            Assert.Empty(PipelineDataTables.Of(null));
        }

        // n8n's Schema view: each field, the one kind its values share (or "mixed", or "empty"), and the first value it has.
        [Fact]
        public void A_tables_schema_says_what_each_field_holds_with_an_example()
        {
            var fields = Assert.Single(PipelineDataTables.Of(Records)).Fields;
            Assert.Equal(new[] { ("name", "text", "Ada"), ("town", "text", "London"), ("age", "number", "85") }, fields.Select(field => (field.Name, field.Kind, field.Example)));

            var json = "[{\"when\":\"2026-10-06\",\"ok\":true,\"amount\":\"120.50\",\"tags\":[\"a\"],\"who\":{\"n\":1},\"note\":\"\",\"odd\":1},"
                + "{\"when\":\"2026-10-07T09:00:00Z\",\"ok\":false,\"amount\":\"9\",\"odd\":\"x\"}]";
            var kinds = Assert.Single(PipelineDataTables.Of(json)).Fields.ToDictionary(field => field.Name, field => field.Kind);
            Assert.Equal("date", kinds["when"]);
            Assert.Equal("true/false", kinds["ok"]);
            Assert.Equal("number as text", kinds["amount"]);
            Assert.Equal("list", kinds["tags"]);
            Assert.Equal("record", kinds["who"]);
            Assert.Equal("empty", kinds["note"]);
            Assert.Equal("mixed", kinds["odd"]);
            Assert.All(kinds.Values, kind => Assert.Contains(kind, PipelineDataTables.Kinds));
            Assert.Equal(("(value)", "number"), (Assert.Single(Assert.Single(PipelineDataTables.Of("[1, 2]")).Fields).Name, Assert.Single(Assert.Single(PipelineDataTables.Of("[1, 2]")).Fields).Kind));
        }

        // n8n's "3 items" on the canvas: a result's count, from JSON, from the JSON text the data steps pass, or from a list.
        [Fact]
        public void A_results_count_is_its_items_when_it_is_a_list_and_nothing_otherwise()
        {
            Assert.Equal(2, PipelineDataTables.CountOf("[{\"a\":1},{\"a\":2}]"));
            Assert.Equal(3, PipelineDataTables.CountOf(System.Text.Json.JsonDocument.Parse("[1,2,3]").RootElement.Clone()));
            Assert.Equal(1, PipelineDataTables.CountOf(System.Text.Json.JsonDocument.Parse("\"[{\\\"a\\\":1}]\"").RootElement.Clone()));
            Assert.Equal(0, PipelineDataTables.CountOf("[]"));
            Assert.Equal(2, PipelineDataTables.CountOf(new System.Collections.Generic.List<object> { 1, 2 }));
            Assert.Null(PipelineDataTables.CountOf("hello"));
            Assert.Null(PipelineDataTables.CountOf("[not json"));
            Assert.Null(PipelineDataTables.CountOf(42));
            Assert.Null(PipelineDataTables.CountOf(null));
        }

        [Fact]
        public void A_long_list_shows_its_first_rows_and_says_how_many_there_are()
        {
            var rows = string.Join(",", Enumerable.Range(1, 120).Select(i => $"{{\"n\":{i}}}"));
            var table = Assert.Single(PipelineDataTables.Of("{ \"Result\": [" + rows + "] }"));
            Assert.Equal((PipelineDataTables.MaxRows, 120), (table.Rows.Count, table.Total));

            var wide = "{" + string.Join(",", Enumerable.Range(1, 30).Select(i => $"\"c{i}\":{i}")) + "}";
            Assert.Equal(PipelineDataTables.MaxColumns, Assert.Single(PipelineDataTables.Of("[" + wide + "]")).Columns.Count);
        }

        [Fact]
        public void The_view_offers_a_table_only_for_records_and_keeps_the_json_as_its_default()
        {
            var cut = Render<DataView>(parameters => parameters.Add(view => view.Id, "out").Add(view => view.Label, "Output").Add(view => view.Json, Records));
            Assert.Contains("Ada", cut.Find("pre#out").TextContent);

            cut.Find("#out-as-table").Click();
            Assert.Empty(cut.FindAll("pre#out"));
            Assert.Equal(new[] { "name", "town", "age" }, cut.FindAll("#out-table-0 th").Select(th => th.TextContent.Trim()));
            Assert.Equal("2 rows", cut.Find("#out-table-0 .db-pipeline-editor__data-table-count").TextContent.Trim());
            Assert.Equal("Grace", cut.FindAll("#out-table-0 tbody tr")[1].QuerySelector("td")!.TextContent.Trim());

            cut.Find("#out-as-schema").Click();
            Assert.Empty(cut.FindAll("pre#out"));
            Assert.Equal(new[] { "name text Ada", "town text London", "age number 85" },
                cut.FindAll("#out-schema-0 tbody tr").Select(tr => string.Join(" ", tr.QuerySelectorAll("td").Select(td => td.TextContent.Trim()))));
            Assert.Equal("Read from 2 records", cut.Find("#out-schema-0 .db-pipeline-editor__data-table-count").TextContent.Trim());

            cut.Find("#out-as-json").Click();
            Assert.NotNull(cut.Find("pre#out"));

            var plain = Render<DataView>(parameters => parameters.Add(view => view.Id, "msg").Add(view => view.Json, "{ \"Message\": \"hello\" }"));
            Assert.Empty(plain.FindAll("#msg-as-table"));
            var nothing = Render<DataView>(parameters => parameters.Add(view => view.Id, "none").Add(view => view.Json, (string?)null));
            Assert.Equal("(nothing)", nothing.Find("pre#none").TextContent);
        }

        // BL-229 - n8n's copy of a step's data: the JSON whole to the clipboard (the editor's script), then "Copied" - until the
        // data shown changes; nothing to copy, no button.
        [Fact]
        public void Copy_puts_the_json_on_the_clipboard_and_says_so()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            var cut = Render<DataView>(parameters => parameters.Add(view => view.Id, "out").Add(view => view.Label, "Output").Add(view => view.Json, Records));
            Assert.Equal("Copy", cut.Find("#out-copy").TextContent.Trim());
            cut.Find("#out-copy").Click();
            Assert.Equal(Records, JSInterop.VerifyInvoke("stommePipelineEditorWriteClipboard").Arguments.Single());
            Assert.Equal("Copied", cut.Find("#out-copy").TextContent.Trim());

            cut.Render(parameters => parameters.Add(view => view.Json, "{ \"Message\": \"other\" }"));
            Assert.Equal("Copy", cut.Find("#out-copy").TextContent.Trim());

            var nothing = Render<DataView>(parameters => parameters.Add(view => view.Id, "none").Add(view => view.Json, (string?)null));
            Assert.Empty(nothing.FindAll("#none-copy"));
        }

        // BL-229 - n8n's search in a step's data: a table keeps the rows holding the text (any cell, any case), a schema the fields
        // named so, the JSON marks each place it occurs - each with how many; short data that is not a list has no search.
        [Fact]
        public void The_search_keeps_the_rows_and_fields_holding_the_text_and_marks_it_in_the_json()
        {
            var cut = Render<DataView>(parameters => parameters.Add(view => view.Id, "out").Add(view => view.Label, "Output").Add(view => view.Json, Records));
            cut.Find("#out-search").Input("london");
            Assert.Equal(new[] { "London" }, cut.FindAll("pre#out mark").Select(mark => mark.TextContent)); // as the data has it
            Assert.Equal("Found once", cut.Find("#out-search-count").TextContent.Trim());
            Assert.Equal(Records, cut.Find("pre#out").TextContent); // the JSON whole, only marked

            cut.Find("#out-as-table").Click();
            Assert.Equal(new[] { "Ada" }, cut.FindAll("#out-table-0 tbody tr").Select(tr => tr.QuerySelector("td")!.TextContent.Trim()));
            Assert.Equal("1 of 2 rows", cut.Find("#out-search-count").TextContent.Trim());

            cut.Find("#out-as-schema").Click();
            cut.Find("#out-search").Input("A"); // "name" and "age" - names, not values
            Assert.Equal(new[] { "name", "age" }, cut.FindAll("#out-schema-0 tbody tr").Select(tr => tr.QuerySelector("td")!.TextContent.Trim()));
            Assert.Equal("2 of 3 fields", cut.Find("#out-search-count").TextContent.Trim());

            cut.Find("#out-search").Input("");
            Assert.Equal(3, cut.FindAll("#out-schema-0 tbody tr").Count);
            Assert.Empty(cut.FindAll("#out-search-count"));

            var plain = Render<DataView>(parameters => parameters.Add(view => view.Id, "msg").Add(view => view.Json, "{ \"Message\": \"hello\" }"));
            Assert.Empty(plain.FindAll("#msg-search"));
            var longText = Render<DataView>(parameters => parameters.Add(view => view.Id, "long").Add(view => view.Json, "{ \"Text\": \"" + new string('x', 500) + "\" }"));
            Assert.NotEmpty(longText.FindAll("#long-search"));
        }
    }
}
