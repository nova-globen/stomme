using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Canvas;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// "Tidy up": each flowchart left to right by its flow - a step one column right of the furthest step
    /// leading to it, rows following the steps that lead to them - each body on its own, a cycle cut where it is met.
    /// </summary>
    public sealed class PipelineAutoLayoutTests
    {
        private static PipelineGraphStep Step(string id, string parent = null) =>
            new() { Id = id, Type = "Stomme.Delay", Parent = parent, ParentPort = parent is null ? null : "Body", Position = new GraphPosition { X = 999, Y = 999 } };

        private static PipelineGraphConnection Link(string from, string to) => new() { SourceId = from, TargetId = to };

        private static Dictionary<string, (double X, double Y)> Laid(PipelineGraph graph)
        {
            AutoLayout.Arrange(graph);
            return graph.Steps.ToDictionary(step => step.Id, step => (step.Position.X, step.Position.Y));
        }

        private static (double X, double Y) At(int column, int row) => (AutoLayout.Left + column * StepPlacement.ColumnWidth, AutoLayout.Top + row * StepPlacement.RowHeight);

        [Fact]
        public void A_chain_runs_left_to_right_and_a_branch_takes_rows_under_its_source()
        {
            // d is listed before b and c, and e is a second start nothing leads to.
            var graph = new PipelineGraph
            {
                Steps = { Step("a"), Step("d"), Step("b"), Step("c"), Step("e") },
                Connections = { Link("a", "b"), Link("a", "c"), Link("b", "d"), Link("c", "d") },
            };

            var laid = Laid(graph);
            Assert.Equal(At(0, 0), laid["a"]);
            Assert.Equal(At(0, 1), laid["e"]);
            Assert.Equal(At(1, 0), laid["b"]);
            Assert.Equal(At(1, 1), laid["c"]);
            Assert.Equal(At(2, 0), laid["d"]); // the join: one column right of the furthest step leading to it
        }

        [Fact]
        public void A_body_is_laid_out_on_its_own_and_a_cycle_does_not_hang()
        {
            var graph = new PipelineGraph
            {
                Steps = { Step("loop"), Step("after"), Step("x", parent: "loop"), Step("y", parent: "loop"), Step("p"), Step("q") },
                Connections = { Link("loop", "after"), Link("x", "y"), Link("p", "q"), Link("q", "p") },
            };

            var laid = Laid(graph);
            Assert.Equal(At(0, 0), laid["loop"]);
            Assert.Equal(At(1, 0), laid["after"]);
            // The body starts at the left again.
            Assert.Equal(At(0, 0), laid["x"]);
            Assert.Equal(At(1, 0), laid["y"]);
            // p and q lead to each other: both are placed, in different columns.
            Assert.NotEqual(laid["p"].X, laid["q"].X);
        }

        // n8n's Tidy up on a selection: only the steps chosen move, laid out by the connections among them (one from a step not
        // chosen leads nowhere), their top left corner kept where theirs was.
        [Fact]
        public void The_steps_chosen_alone_are_tidied_and_kept_where_they_were()
        {
            var graph = new PipelineGraph
            {
                Steps = { Step("a"), Step("b"), Step("c"), Step("d") },
                Connections = { Link("a", "b"), Link("b", "c") },
            };
            graph.Steps[0].Position = new GraphPosition { X = 5, Y = 5 };
            graph.Steps[1].Position = new GraphPosition { X = 700, Y = 300 };
            graph.Steps[2].Position = new GraphPosition { X = 400, Y = 900 };
            graph.Steps[3].Position = new GraphPosition { X = 650, Y = 500 };

            AutoLayout.Arrange(graph, new HashSet<string> { "b", "c", "d" });
            var laid = graph.Steps.ToDictionary(step => step.Id, step => (step.Position.X, step.Position.Y));

            Assert.Equal((5d, 5d), laid["a"]); // not chosen: where it was
            (double X, double Y) Kept((double X, double Y) at) => (at.X - AutoLayout.Left + 400, at.Y - AutoLayout.Top + 300);
            Assert.Equal(Kept(At(0, 0)), laid["b"]); // a leads to it, but a is not chosen: b starts
            Assert.Equal(Kept(At(1, 0)), laid["c"]);
            Assert.Equal(Kept(At(0, 1)), laid["d"]); // nothing chosen leads to it: under b, in graph order
        }
    }
}
