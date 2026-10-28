using System;
using System.Collections.Generic;
using System.Text.Json;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 increment 5 - an input's expression: <c>{{ name.path }}</c> placeholders over the pipeline's variables, the ONE
    /// renderer the engine runs and the editor previews with.
    /// </summary>
    public sealed class PipelineTemplateTests
    {
        // As the data steps pass records: JSON text in a variable.
        private const string People = "[{\"name\":\"Ada\",\"town\":\"London\",\"age\":36},{\"name\":\"Grace\",\"town\":\"Arlington\",\"age\":85}]";

        private static readonly Dictionary<string, object> Variables = new()
        {
            ["people"] = People,
            ["count"] = 2,
            ["ratio"] = 0.5,
            ["ok"] = true,
            ["person"] = new Dictionary<string, object> { ["name"] = "Ada", ["town"] = "London" },
            ["nothing"] = null,
            ["list"] = new List<object> { "a", "b" },
        };

        private static (bool, object) Lookup(string name) => Variables.TryGetValue(name, out var value) ? (true, value) : (false, null);

        [Theory]
        [InlineData("Hello {{ person.name }} from {{person.town}}", "Hello Ada from London")]
        [InlineData("{{ people[1].name }} is {{ people[1].age }}", "Grace is 85")]
        [InlineData("{{ count }} people, ratio {{ ratio }}, ok {{ ok }}", "2 people, ratio 0.5, ok true")]
        [InlineData("[{{ nothing }}] [{{ person.missing }}] [{{ people[9].name }}]", "[] [] []")]
        [InlineData("first: {{ people[0] }}", "first: {\"name\":\"Ada\",\"town\":\"London\",\"age\":36}")]
        [InlineData("{{ list }} and {{ person.NAME }}", "[\"a\",\"b\"] and Ada")]
        [InlineData("no placeholders at all", "no placeholders at all")]
        public void Placeholders_in_text_become_the_values_text(string template, string expected) =>
            Assert.Equal(expected, PipelineTemplate.Render(template, Lookup));

        [Fact]
        public void A_template_that_is_one_placeholder_is_the_value_itself()
        {
            Assert.Equal(2, PipelineTemplate.Render("{{ count }}", Lookup));
            Assert.Equal(true, PipelineTemplate.Render("{{ok}}", Lookup));
            Assert.Equal(85L, Assert.IsType<long>(PipelineTemplate.Render("{{ people[1].age }}", Lookup)));
            Assert.Equal("London", PipelineTemplate.Render("{{ people[0].town }}", Lookup));
            Assert.Same(Variables["list"], PipelineTemplate.Render("{{ list }}", Lookup));
            // A record from JSON text stays JSON text - what the data steps take.
            Assert.Equal(JsonDocument.Parse(People).RootElement[0].GetRawText(), PipelineTemplate.Render("{{ people[0] }}", Lookup));
            Assert.Null(PipelineTemplate.Render("{{ nothing }}", Lookup));
        }

        // How the engine gives back a variable of any kind once a paused run is read again: a JSON node. It reads as the same value
        // did before the pause - found building sub-pipelines, where "{{ who }}" came out "\"Ada\"" and "{{ who.name }}" empty.
        [Fact]
        public void A_value_held_as_a_json_node_reads_as_the_same_value_did_before()
        {
            var nodes = new Dictionary<string, object>
            {
                ["name"] = System.Text.Json.Nodes.JsonValue.Create("Ada"),
                ["count"] = System.Text.Json.Nodes.JsonValue.Create(3),
                ["person"] = System.Text.Json.Nodes.JsonNode.Parse("{\"name\":\"Ada\",\"town\":\"London\"}"),
                ["people"] = System.Text.Json.Nodes.JsonNode.Parse(People),
                ["saved"] = System.Text.Json.Nodes.JsonValue.Create("{\"name\":\"Grace\"}"), // a record saved as text
            };
            (bool, object) Read(string name) => nodes.TryGetValue(name, out var value) ? (true, value) : (false, null);

            Assert.Equal("Ada and 3 from London; Grace, 85", PipelineTemplate.Render("{{ name }} and {{ count }} from {{ person.town }}; {{ saved.name }}, {{ people[1].age }}", Read));
            Assert.Equal("Ada", PipelineTemplate.Render("{{ name }}", Read));
            Assert.Equal(3L, PipelineTemplate.Render("{{ count }}", Read));
            Assert.Equal("{\"name\":\"Ada\",\"town\":\"London\"}", PipelineTemplate.Render("{{ person }}", Read));
            Assert.Equal("ADA, 2", PipelineTemplate.Render("{{ name | upper }}, {{ people | length }}", Read));
        }

        [Fact]
        public void A_name_no_variable_has_fails_loudly_and_says_which()
        {
            var thrown = Assert.Throws<PipelineTemplateException>(() => PipelineTemplate.Render("Hi {{ persn.name }}", Lookup));
            Assert.Equal("There is no variable named 'persn'.", thrown.Message);
            Assert.Throws<PipelineTemplateException>(() => PipelineTemplate.Render("{{ }}", Lookup));
        }

        [Fact]
        public void Suggestions_offer_each_variable_and_the_fields_of_what_it_holds_and_follow_what_is_typed()
        {
            var known = new (string, object)[] { ("people", People), ("person", Variables["person"]), ("count", 2) };

            var all = PipelineTemplate.Suggest("Hello ", known, max: 50);
            Assert.Contains("{{ people }}", all);
            Assert.Contains("{{ people[0].town }}", all);
            Assert.Contains("{{ person.name }}", all);
            Assert.Contains("{{ count }}", all);

            // After an open {{ only what contains the text typed since.
            Assert.Equal(new[] { "{{ people[0].town }}", "{{ person.town }}" }, PipelineTemplate.Suggest("Hi {{ tow", known));

            // Taking one completes the open placeholder, or adds it at the end.
            Assert.Equal("Hi {{ person.town }}", PipelineTemplate.Accept("Hi {{ tow", "{{ person.town }}"));
            Assert.Equal("Hi {{ count }}", PipelineTemplate.Accept("Hi ", "{{ count }}"));
        }

        [Fact]
        public void The_names_a_template_reads_are_listed_once_each()
        {
            Assert.Equal(new[] { "person", "people" }, PipelineTemplate.Names("{{ person.name }} {{people[0]}} {{ person.town }}"));
            Assert.Empty(PipelineTemplate.Names("plain text"));
            Assert.True(PipelineTemplate.HasPlaceholders("a {{ b }}"));
            Assert.False(PipelineTemplate.HasPlaceholders("a { b }"));
        }
    }
}
