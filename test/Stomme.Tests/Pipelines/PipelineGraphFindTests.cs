using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Find on the canvas: a step is found by its name, its type, its comment or what it holds - an input, an
    /// expression, a script, a condition, a case - case-insensitively, wherever it sits (the bodies named, outermost first), one
    /// match per step in the order drawn, at most as many as asked; the text around a long match is cut to it.
    /// </summary>
    public sealed class PipelineGraphFindTests
    {
        private static readonly List<StepCatalogEntry> Catalog = new()
        {
            new() { Type = "Stomme.SendHttpRequest", DisplayName = "HTTP request", Inputs = { new() { Name = "Url", DisplayName = "URL" } } },
            new() { Type = "Stomme.ForEach", DisplayName = "For Each", Bodies = { "Body" } },
            new() { Type = "Stomme.If", DisplayName = "If", Bodies = { "Then", "Else" } },
            new() { Type = "Stomme.WriteLine", DisplayName = "Write Line", Inputs = { new() { Name = "Text", DisplayName = "Text" } } },
        };

        private static PipelineGraph Graph() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "fetch", Type = "Stomme.SendHttpRequest", Name = "Fetch orders", Inputs = { ["Url"] = "https://shop.example.com/api/orders" } },
                new PipelineGraphStep { Id = "each", Type = "Stomme.ForEach", Name = "Each order" },
                new PipelineGraphStep { Id = "check", Type = "Stomme.If", Parent = "each", ParentPort = "Body", Comment = "Big orders go to finance" },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Parent = "check", ParentPort = "Then", InputExpressions = { ["Text"] = "Order {{ order.id }} is over the LIMIT" } },
            },
        };

        [Fact]
        public void A_step_is_found_by_name_type_comment_or_what_it_holds_wherever_it_sits()
        {
            Assert.Equal(new[] { ("fetch", "Fetch orders", "name", "") }, Found("FETCH"));
            Assert.Equal(new[] { ("each", "Each order", "type", "") }, Found("for each"));
            Assert.Equal(new[] { ("check", "If", "comment", "in 'Each order' › Body") }, Found("finance"));
            Assert.Equal(new[] { ("say", "Write Line", "expression of 'Text'", "in 'Each order' › Body › 'If' › Then") }, Found("limit"));
            Assert.Equal(new[] { ("fetch", "Fetch orders", "input 'URL'", "") }, Found("shop.example"));

            // One match per step, in the order drawn: "order" is in two names, a comment and an expression.
            Assert.Equal(new[] { "fetch", "each", "check", "say" }, PipelineGraphFind.Find(Graph(), Catalog, "order").Select(match => match.StepId));
            Assert.Single(PipelineGraphFind.Find(Graph(), Catalog, "order", max: 1));
        }

        [Fact]
        public void Nothing_typed_finds_nothing_and_a_long_match_is_cut_around_it()
        {
            Assert.Empty(PipelineGraphFind.Find(Graph(), Catalog, "  "));
            Assert.Empty(PipelineGraphFind.Find(Graph(), Catalog, "nowhere"));
            Assert.Empty(PipelineGraphFind.Find(null, Catalog, "order"));

            var graph = Graph();
            graph.Steps[0].Comment = new string('a', 100) + " the needle " + new string('b', 100);
            var match = Assert.Single(PipelineGraphFind.Find(graph, Catalog, "needle"));
            Assert.Contains("the needle", match.Snippet);
            Assert.StartsWith("…", match.Snippet);
            Assert.EndsWith("…", match.Snippet);
            Assert.Equal(PipelineGraphFind.SnippetLength + 2, match.Snippet.Length);
        }

        private static (string, string, string, string)[] Found(string text) =>
            PipelineGraphFind.Find(Graph(), Catalog, text).Select(match => (match.StepId, match.Step, match.Field, match.Where)).ToArray();
    }
}
