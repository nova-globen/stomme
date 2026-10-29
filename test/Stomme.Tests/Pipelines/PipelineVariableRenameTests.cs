#nullable enable
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// A variable renamed, and every expression, condition and case that reads it - n8n renames a node and the
    /// expressions reading it follow. A binding needs nothing (it names the id); a value is sent as typed and never changed; a
    /// script and a record's rules holding the old name are said, to be checked.
    /// </summary>
    public sealed class PipelineVariableRenameTests
    {
        [Theory]
        [InlineData("Hello {{ who }}", "Hello {{ person }}")]
        [InlineData("{{who.name}}, {{ who[0] }}, {{ who | upper }} and {{  who  }}", "{{person.name}}, {{ person[0] }}, {{ person | upper }} and {{  person  }}")]
        [InlineData("{{ whom }} {{ $input.who }} {{ \"who\" }} {{ order.who }}", "{{ whom }} {{ $input.who }} {{ \"who\" }} {{ order.who }}")]
        [InlineData(null, null)]
        public void A_template_reads_the_new_name_where_it_read_the_old(string? template, string? expected) =>
            Assert.Equal(expected, PipelineTemplate.Rename(template, "who", "person"));

        private static PipelineCondition Rule(string left, string right) =>
            new() { Rules = { new PipelineConditionRule { Left = left, Operator = PipelineConditions.EqualsOperator, Right = right } } };

        [Fact]
        public void Every_expression_condition_and_case_that_reads_it_follows_the_new_name()
        {
            var catalog = new[] { new StepCatalogEntry { Type = "Stomme.FilterRecords", DisplayName = "Filter records", Inputs = { new StepInputView { Name = "Condition", UIHint = PipelineInputHints.RecordCondition } } } };
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who" }, new PipelineGraphVariable { Id = "v-other", Name = "other" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.WriteLine", Name = "Greet", InputExpressions = { ["Text"] = "Hi {{ who }}", ["Other"] = "{{ other }}" } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.FlowDecision", Name = "Old enough?", InputConditions = { ["Condition"] = Rule("{{ who.age }}", "{{ other }}") } },
                    new PipelineGraphStep { Id = "c", Type = "Stomme.FlowSwitch", Name = "Which?", Cases = { new PipelineGraphCase { Label = "Ada", Condition = Rule("{{ who }}", "Ada") } } },
                    new PipelineGraphStep { Id = "d", Type = "Stomme.WriteLine", Name = "As typed", Inputs = { ["Text"] = "{{ who }} for another system" } },
                    new PipelineGraphStep { Id = "e", Type = "Stomme.RunScript", Name = "Count it", InputScripts = { ["Script"] = "return who.length + other.who;" } },
                    new PipelineGraphStep { Id = "f", Type = "Stomme.FilterRecords", Name = "Keep some", Inputs = { ["Condition"] = "{\"rules\":[{\"left\":\"{{ who }}\",\"operator\":\"equals\",\"right\":\"x\"}]}" } },
                    new PipelineGraphStep { Id = "g", Type = "Stomme.WriteLine", Name = "Bound", InputVariables = { ["Text"] = "v-who" } },
                },
            };

            var outcome = PipelineVariableRenames.Apply(graph, "v-who", " person ", catalog);

            Assert.Equal("person", graph.Variables[0].Name);
            Assert.Equal(3, outcome.Places);
            Assert.Equal(new[] { "Count it", "Keep some" }, outcome.ToCheck);
            Assert.Equal(("Hi {{ person }}", "{{ other }}"), (graph.Steps[0].InputExpressions["Text"], graph.Steps[0].InputExpressions["Other"]));
            Assert.Equal(("{{ person.age }}", "{{ other }}"), (graph.Steps[1].InputConditions["Condition"].Rules[0].Left, graph.Steps[1].InputConditions["Condition"].Rules[0].Right));
            Assert.Equal("{{ person }}", graph.Steps[2].Cases[0].Condition.Rules[0].Left);
            Assert.Equal("{{ who }} for another system", graph.Steps[3].Inputs["Text"]); // a value is sent as typed
            Assert.Equal("return who.length + other.who;", graph.Steps[4].InputScripts["Script"]); // said, not changed
            Assert.Equal("v-who", graph.Steps[6].InputVariables["Text"]);
        }

        [Theory]
        [InlineData("", "A variable needs a name.")]
        [InlineData("my name", "'my name' could not be read in an expression - use letters, digits and _, starting with a letter or _.")]
        [InlineData("1st", "'1st' could not be read in an expression - use letters, digits and _, starting with a letter or _.")]
        [InlineData("OTHER", "There is already a variable named 'OTHER'.")]
        [InlineData("Who", null)] // its own name, in another case
        [InlineData("person_2", null)]
        public void A_new_name_must_be_one_an_expression_can_read_and_no_other_variables(string name, string? problem) =>
            Assert.Equal(problem, PipelineVariableRenames.Problem(name, new List<PipelineGraphVariable> { new() { Id = "v-who", Name = "who" }, new() { Id = "v-other", Name = "other" } }, "v-who"));
    }
}
