#nullable enable
using System.Linq;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - what a Run script step receives, as the editor's AI is told it: n8n sends its Code node's AI the input's schema, not
    /// its data, and so does this - records with their fields' names and kinds, plain values, one record or text; never a value.
    /// </summary>
    public sealed class PipelineScriptInputTests
    {
        [Fact]
        public void Records_are_told_by_their_fields_names_and_kinds_and_never_a_value()
        {
            var (receives, fields) = PipelineScriptInput.Describe("""[{"id":1,"customer":"Ada Lovelace","paid":true},{"id":2,"customer":"Alan","paid":false,"note":"rush"}]""");

            Assert.Equal(PipelineScriptInput.Records, receives);
            Assert.Equal(new[] { ("id", "number"), ("customer", "text"), ("paid", "true/false"), ("note", "text") }, fields.Select(field => (field.Name, field.Kind)));
            Assert.DoesNotContain(fields, field => field.Name.Contains("Ada") || field.Kind.Contains("Ada"));
        }

        [Fact]
        public void One_record_plain_values_and_text_are_each_told_as_what_they_are()
        {
            var (record, fields) = PipelineScriptInput.Describe("""{"orderId":"A-1","total":12.5}""");
            Assert.Equal(PipelineScriptInput.Record, record);
            Assert.Equal(new[] { ("orderId", "text"), ("total", "number") }, fields.Select(field => (field.Name, field.Kind)));

            Assert.Equal((PipelineScriptInput.Values, 0), Count(PipelineScriptInput.Describe("""["a","b"]""")));
            Assert.Equal((PipelineScriptInput.Text, 0), Count(PipelineScriptInput.Describe("Hello there")));
            Assert.Equal((PipelineScriptInput.Text, 0), Count(PipelineScriptInput.Describe("42")));
            Assert.Equal((PipelineScriptInput.Records, 0), Count(PipelineScriptInput.Describe("[]")));
            Assert.Equal(("", 0), Count(PipelineScriptInput.Describe(null)));
            Assert.Equal(("", 0), Count(PipelineScriptInput.Describe("  ")));

            static (string, int) Count((string Receives, System.Collections.Generic.List<PipelineScriptField> Fields) described) => (described.Receives, described.Fields.Count);
        }
    }
}
