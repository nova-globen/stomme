#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 increment 10 - a condition built from rules, ONE evaluator for the engine and the editor: numbers compare as numbers,
    /// ISO dates as dates, anything else as text (ordinal, or ignoring case when asked); a list contains an item; empty, true and
    /// false read what a person means; all or any; a name no variable has is an error, as in an expression.
    /// </summary>
    public sealed class PipelineConditionTests
    {
        private static readonly Dictionary<string, object?> Values = new(StringComparer.Ordinal)
        {
            ["status"] = "Shipped",
            ["total"] = "129.5",
            ["count"] = 9,
            ["due"] = "2026-10-06T12:00:00Z",
            ["tags"] = "[\"red\",\"blue\"]",
            ["note"] = "",
            ["order"] = "{\"customer\":\"Ada\",\"items\":[]}",
            ["paid"] = "true",
        };

        private static bool Holds(string left, string op, string right = "", bool ignoreCase = false) =>
            PipelineConditions.Evaluate(new PipelineCondition { IgnoreCase = ignoreCase, Rules = { new PipelineConditionRule { Left = left, Operator = op, Right = right } } }, Lookup);

        private static (bool, object?) Lookup(string name) => Values.TryGetValue(name, out var value) ? (true, value) : (false, null);

        [Theory]
        [InlineData("{{ status }}", "equals", "Shipped", true)]
        [InlineData("{{ status }}", "equals", "shipped", false)]
        [InlineData("{{ status }}", "not-equals", "Open", true)]
        [InlineData("{{ status }}", "contains", "hip", true)]
        [InlineData("{{ status }}", "starts-with", "Ship", true)]
        [InlineData("{{ status }}", "ends-with", "ped", true)]
        [InlineData("{{ total }}", "greater", "100", true)]
        [InlineData("{{ count }}", "greater", "10", false)]   // 9 < 10 as numbers, though "9" > "10" as text
        [InlineData("{{ count }}", "at-least", "9", true)]
        [InlineData("{{ count }}", "at-most", "9.0", true)]
        [InlineData("{{ count }}", "less", "{{ total }}", true)]
        [InlineData("{{ due }}", "greater", "2026-10-06T09:00:00+02:00", true)] // 12:00Z after 07:00Z
        [InlineData("{{ tags }}", "contains", "blue", true)]  // an item of the list
        [InlineData("{{ tags }}", "contains", "blu", false)]  // not part of one
        [InlineData("{{ order.customer }}", "equals", "Ada", true)]
        public void A_rule_compares_numbers_as_numbers_dates_as_dates_and_text_as_text(string left, string op, string right, bool expected) =>
            Assert.Equal(expected, Holds(left, op, right));

        [Fact]
        public void Text_can_compare_ignoring_case()
        {
            Assert.True(Holds("{{ status }}", "equals", "SHIPPED", ignoreCase: true));
            Assert.True(Holds("{{ status }}", "contains", "SHIP", ignoreCase: true));
        }

        [Theory]
        [InlineData("{{ note }}", "empty", true)]
        [InlineData("{{ order.items }}", "empty", true)]
        [InlineData("{{ order.missing }}", "empty", true)]
        [InlineData("{{ status }}", "empty", false)]
        [InlineData("{{ status }}", "not-empty", true)]
        [InlineData("{{ paid }}", "true", true)]
        [InlineData("{{ paid }}", "false", false)]
        [InlineData("{{ count }}", "true", false)]
        public void Empty_true_and_false_read_what_a_person_means(string left, string op, bool expected) =>
            Assert.Equal(expected, Holds(left, op));

        [Fact]
        public void All_needs_every_rule_and_any_needs_one()
        {
            var condition = new PipelineCondition
            {
                Rules =
                {
                    new PipelineConditionRule { Left = "{{ status }}", Operator = "equals", Right = "Shipped" },
                    new PipelineConditionRule { Left = "{{ count }}", Operator = "greater", Right = "100" },
                },
            };
            Assert.False(PipelineConditions.Evaluate(condition, Lookup));
            condition.Match = PipelineConditions.Any;
            Assert.True(PipelineConditions.Evaluate(condition, Lookup));
            Assert.Equal("{{ status }} is equal to Shipped or {{ count }} is greater than 100", PipelineConditions.Describe(condition));
        }

        [Fact]
        public void A_name_no_variable_has_and_an_incomplete_condition_are_errors()
        {
            Assert.Contains("no variable named 'nobody'", Assert.Throws<PipelineTemplateException>(() => Holds("{{ nobody }}", "empty")).Message);
            Assert.Contains("needs at least one rule", Assert.Throws<PipelineTemplateException>(() => PipelineConditions.Evaluate(new PipelineCondition(), Lookup)).Message);
            Assert.Contains("tests nothing", PipelineConditions.Incomplete(new PipelineCondition { Rules = { new PipelineConditionRule() } }));
            Assert.Contains("is not a comparison", PipelineConditions.Incomplete(new PipelineCondition { Rules = { new PipelineConditionRule { Left = "x", Operator = "like" } } }));
        }

        [Fact]
        public void A_condition_is_written_and_read_back_and_names_the_variables_it_reads()
        {
            var condition = new PipelineCondition
            {
                Match = PipelineConditions.Any,
                IgnoreCase = true,
                Rules =
                {
                    new PipelineConditionRule { Left = "{{ order.status }}", Operator = "equals", Right = "{{ wanted }}" },
                    new PipelineConditionRule { Left = "{{ note }}", Operator = "empty", Right = "{{ ignored }}" },
                },
            };

            var back = PipelineConditions.Read(PipelineConditions.Write(condition));

            Assert.Equal(PipelineConditions.Write(condition), PipelineConditions.Write(back));
            Assert.Equal(new[] { "order", "wanted", "note" }, PipelineConditions.Names(back)); // a one-sided rule's right side is not read
            Assert.Empty(PipelineConditions.Read("not json").Rules);
            Assert.Empty(PipelineConditions.Read(null).Rules);
        }
    }
}
