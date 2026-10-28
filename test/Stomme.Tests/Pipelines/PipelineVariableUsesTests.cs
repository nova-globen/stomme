#nullable enable
using System.Collections.Generic;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - where a variable is used: the steps that set it (an output saved to it, a Set Variable step's variable) and those
    /// that read it (an input bound to it, an expression, a condition or a Switch case naming it, a script with it as a word -
    /// not a longer word holding it).
    /// </summary>
    public sealed class PipelineVariableUsesTests
    {
        private static readonly List<StepCatalogEntry> Catalog = new()
        {
            new() { Type = "Stomme.SetVariable", Inputs = { new() { Name = "Variable", UIHint = PipelineInputHints.VariablePicker } } },
        };

        [Fact]
        public void A_variables_setters_and_readers_are_found_in_every_place_that_names_it()
        {
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-orders", Name = "orders" }, new PipelineGraphVariable { Id = "v-idle", Name = "idle" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "fetch", Type = "Stomme.Fetch", OutputVariables = { ["Result"] = "v-orders" } },
                    new PipelineGraphStep { Id = "reset", Type = "Stomme.SetVariable", InputVariables = { ["Variable"] = "v-orders" } },
                    new PipelineGraphStep { Id = "bound", Type = "Stomme.WriteLine", InputVariables = { ["Text"] = "v-orders" } },
                    new PipelineGraphStep { Id = "said", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "{{ orders[0].id | default: \"none\" }}" } },
                    new PipelineGraphStep { Id = "tested", Type = "Stomme.FlowDecision", InputConditions = { ["Condition"] = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ orders | length }}", Operator = "greater", Right = "0" } } } } },
                    new PipelineGraphStep { Id = "cased", Type = "Stomme.FlowSwitch", Cases = { new PipelineGraphCase { Label = "Many", Condition = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ orders.length }}" } } } } } },
                    new PipelineGraphStep { Id = "scripted", Type = "Stomme.WriteLine", InputScripts = { ["Text"] = "orders.length + ' orders'" } },
                    new PipelineGraphStep { Id = "namesake", Type = "Stomme.WriteLine", InputScripts = { ["Text"] = "orders_total * 2" }, InputExpressions = { ["Other"] = "{{ $vars.orders }}" } },
                },
            };

            var uses = PipelineVariableUses.Of(graph, "v-orders", Catalog);
            Assert.Equal(new[] { "fetch", "reset" }, uses.SetBy);
            Assert.Equal(new[] { "bound", "said", "tested", "cased", "scripted" }, uses.ReadBy); // not orders_total, not $vars.orders

            var idle = PipelineVariableUses.Of(graph, "v-idle", Catalog);
            Assert.Empty(idle.SetBy);
            Assert.Empty(idle.ReadBy);
            Assert.Empty(PipelineVariableUses.Of(graph, "v-gone", Catalog).ReadBy);
        }
    }
}
