using System.Collections.Generic;
using System;
using System.Linq;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Canvas;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Undo and redo, and copy and paste: the rules the editor's keyboard and buttons ride on.
    /// </summary>
    public sealed class PipelineEditHistoryTests
    {
        private static readonly DateTime Now = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Undo_and_redo_walk_the_changes_and_a_new_change_drops_the_redo()
        {
            var history = new EditHistory();
            history.Reset("A");
            Assert.False(history.CanUndo);

            Assert.True(history.Record("B", null, Now));
            Assert.True(history.Record("C", null, Now.AddSeconds(1)));
            Assert.False(history.Record("C", null, Now.AddSeconds(2))); // nothing changed

            Assert.Equal("B", history.Undo());
            Assert.Equal("A", history.Undo());
            Assert.Null(history.Undo());
            Assert.Equal("B", history.Redo());

            history.Record("D", null, Now.AddSeconds(3));
            Assert.False(history.CanRedo);
            Assert.Equal("B", history.Undo());
        }

        [Fact]
        public void Typing_in_one_field_is_one_step_to_undo()
        {
            var history = new EditHistory();
            history.Reset("empty");
            history.Record("h", "step-1/Text", Now);
            history.Record("he", "step-1/Text", Now.AddMilliseconds(300));
            history.Record("hello", "step-1/Text", Now.AddMilliseconds(600));
            // Another field is another step, and so is the same field after a pause.
            history.Record("hello+x", "step-2/Text", Now.AddMilliseconds(900));
            history.Record("hello+x!", "step-2/Text", Now.AddSeconds(5));

            Assert.Equal("hello+x", history.Undo());
            Assert.Equal("hello", history.Undo());
            Assert.Equal("empty", history.Undo());
            Assert.Null(history.Undo());
        }

        [Fact]
        public void The_history_keeps_at_most_its_capacity()
        {
            var history = new EditHistory();
            history.Reset("0");
            for (var change = 1; change <= EditHistory.Capacity + 20; change++)
            {
                history.Record(change.ToString(), null, Now.AddSeconds(change));
            }

            var undone = 0;
            while (history.Undo() is not null)
            {
                undone++;
            }

            Assert.Equal(EditHistory.Capacity, undone);
        }

        private static PipelineGraph Graph() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "read", Type = "Stomme.ParseCsv", Name = "Read", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["Text"] = "a,b" } },
                new PipelineGraphStep { Id = "each", Type = "Stomme.ForEach", Name = "Each", Position = new GraphPosition { X = 290, Y = 40 } },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Parent = "each", ParentPort = "Body", Position = new GraphPosition { X = 30, Y = 40 } },
                new PipelineGraphStep { Id = "deep", Type = "Stomme.If", Parent = "each", ParentPort = "Body", Position = new GraphPosition { X = 290, Y = 40 } },
                new PipelineGraphStep { Id = "deeper", Type = "Stomme.WriteLine", Parent = "deep", ParentPort = "Then" },
                new PipelineGraphStep { Id = "end", Type = "Stomme.WriteLine", Name = "End", Position = new GraphPosition { X = 550, Y = 40 } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "each" },
                new PipelineGraphConnection { SourceId = "each", TargetId = "end" },
                new PipelineGraphConnection { SourceId = "say", TargetId = "deep" },
            },
        };

        [Fact]
        public void A_copy_takes_what_the_chosen_steps_hold_and_the_connections_among_them_only()
        {
            var copy = StepClipboard.Copy(Graph(), new[] { "read", "each" });

            Assert.Equal(new[] { "read", "each", "say", "deep", "deeper" }, copy.Steps.Select(step => step.Id));
            // The chosen steps lose their place; what they hold keeps its own.
            Assert.Null(copy.Steps[0].Parent);
            Assert.Equal(("each", "Body"), (copy.Steps[2].Parent, copy.Steps[2].ParentPort));
            // read -> each and say -> deep are inside the copy; each -> end leaves it, so it is left out.
            Assert.Equal(new[] { ("read", "each"), ("say", "deep") }, copy.Connections.Select(c => (c.SourceId, c.TargetId)));
        }

        [Fact]
        public void A_paste_is_new_steps_in_the_body_shown_with_their_insides_and_connections_rewired()
        {
            var copy = StepClipboard.Copy(Graph(), new[] { "each" });
            var next = 0;
            var pasted = StepClipboard.Paste(copy, "outer", "Then", () => "new-" + ++next);

            Assert.Equal(new[] { "new-1", "new-2", "new-3", "new-4" }, pasted.Steps.Select(step => step.Id));
            var each = pasted.Steps[0];
            Assert.Equal(("outer", "Then"), (each.Parent, each.ParentPort));
            Assert.Equal((290 + StepClipboard.PasteOffset, 40 + StepClipboard.PasteOffset), (each.Position.X, each.Position.Y));
            // Its body comes with it, under the NEW id, at the positions it had inside.
            Assert.Equal(("new-1", "Body", 30d), (pasted.Steps[1].Parent, pasted.Steps[1].ParentPort, pasted.Steps[1].Position.X));
            Assert.Equal(("new-3", "Then"), (pasted.Steps[3].Parent, pasted.Steps[3].ParentPort));
            Assert.Equal(("new-2", "new-3"), (pasted.Connections.Single().SourceId, pasted.Connections.Single().TargetId));

            // Pasted at the pipeline's own level, it sits in no body.
            var top = StepClipboard.Paste(copy, null, "ignored", () => Guid.NewGuid().ToString("N"));
            Assert.Equal(((string)null, (string)null), (top.Steps[0].Parent, top.Steps[0].ParentPort));
        }

        // A copy that travels to another pipeline (the system clipboard) carries the variables it uses - read or saved by id, named
        // by an expression, a condition, a case or a script - and a paste re-points them to the pipeline's own by name, adding
        // only those it lacks.
        [Fact]
        public void A_copy_carries_the_variables_it_uses_and_a_paste_matches_them_by_name_or_adds_them()
        {
            var variables = new List<PipelineGraphVariable>
            {
                new() { Id = "v-rows", Name = "rows" },
                new() { Id = "v-who", Name = "who", TypeName = "String", Value = "world" },
                new() { Id = "v-limit", Name = "limit", Value = "100" },
                new() { Id = "v-js", Name = "factor", Value = "1.25" },
                new() { Id = "v-err", Name = "err" },
                new() { Id = "v-unused", Name = "unused" },
            };
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.ParseCsv", OutputVariables = { ["Result"] = "v-rows" } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "Hello {{ who }}" }, FailedPath = true, FailureVariable = "v-err" },
                    new PipelineGraphStep { Id = "c", Type = "Stomme.FlowDecision", InputConditions = { ["Condition"] = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ rows.length }}", Operator = "greater", Right = "{{ limit }}" } } } } },
                    new PipelineGraphStep { Id = "d", Type = "Stomme.WriteLine", InputScripts = { ["Text"] = "rows.length * factor" } },
                },
            };

            var copy = StepClipboard.Copy(graph, new[] { "a", "b", "c", "d" });
            var used = StepClipboard.VariablesUsed(copy, variables);
            Assert.Equal(new[] { "rows", "who", "limit", "factor", "err" }, used.Select(variable => variable.Name)); // err: where b's error is saved

            // Pasted into a pipeline that has 'rows' under another id and 'Who' differently cased: re-pointed; the rest added.
            var next = 0;
            var pasted = StepClipboard.Paste(copy, null, null, () => "p" + ++next);
            Assert.True(pasted.Steps[1].FailedPath);
            var theirs = new List<PipelineGraphVariable> { new() { Id = "their-rows", Name = "rows" }, new() { Id = "their-who", Name = "Who" }, new() { Id = "their-err", Name = "err" } };
            var added = StepClipboard.MergeVariables(pasted, used, theirs);

            Assert.Equal(new[] { "limit", "factor" }, added.Select(variable => variable.Name));
            Assert.Equal(("v-limit", "100"), (added[0].Id, added[0].Value));
            Assert.Equal("their-rows", pasted.Steps[0].OutputVariables["Result"]);
            Assert.Equal("their-err", pasted.Steps[1].FailureVariable);

            // Pasted back into the pipeline it came from: nothing to add, nothing re-pointed.
            var again = StepClipboard.Paste(copy, null, null, () => "q" + ++next);
            Assert.Empty(StepClipboard.MergeVariables(again, used, variables));
            Assert.Equal("v-rows", again.Steps[0].OutputVariables["Result"]);
        }

        [Fact]
        public void Fit_centres_the_steps_and_never_zooms_past_their_own_size()
        {
            // One step: it stays at its size, centred - the library's own fit blew it up to fill the canvas.
            var one = CanvasFit.Compute(1200, 800, new[] { (30d, 40d, (double?)220d, (double?)100d) })!.Value;
            Assert.Equal((1d, 600 - 140d, 400 - 90d), one);

            // A long pipeline is zoomed out to fit inside the margin, and centred; an unmeasured step counts at its default size.
            var wide = CanvasFit.Compute(1200, 800, new[] { (0d, 0d, (double?)null, (double?)null), (2200d, 300d, (double?)null, (double?)null) })!.Value;
            var expectedZoom = (1200 - 2 * CanvasFit.Margin) / (2200 + CanvasFit.DefaultStepWidth);
            Assert.Equal(expectedZoom, wide.Zoom, 6);
            Assert.Equal(600 - (2200 + CanvasFit.DefaultStepWidth) / 2 * expectedZoom, wide.PanX, 6);

            // Nor out past the floor.
            Assert.Equal(CanvasFit.MinZoom, CanvasFit.Compute(1200, 800, new[] { (0d, 0d, (double?)1d, (double?)1d), (100_000d, 0d, (double?)1d, (double?)1d) })!.Value.Zoom);
        }

        // n8n's zoom to fit on open: whether every step shows whole at the view a pipeline opens with.
        [Fact]
        public void A_view_shows_its_steps_only_when_each_lies_wholly_inside_the_canvas()
        {
            var inside = new[] { (80d, 80d, (double?)192d, (double?)90d), (380d, 80d, (double?)192d, (double?)90d) };
            Assert.True(CanvasFit.Shows(1200, 800, 1, 0, 0, inside));
            Assert.False(CanvasFit.Shows(745, 538, 1, 0, 0, inside.Append((680d, 80d, (double?)192d, (double?)125d)))); // the third step past the right edge (measured: 952-1144 against 1016)
            Assert.False(CanvasFit.Shows(1200, 800, 1, -100, 0, inside)); // panned: the first step's left off the canvas
            Assert.True(CanvasFit.Shows(1200, 800, 0.5, 0, 0, inside.Append((1800d, 600d, (double?)192d, (double?)90d)))); // zoomed out, it fits
            Assert.False(CanvasFit.Shows(1200, 800, 1, 0, 0, new[] { (1000d, 0d, (double?)null, (double?)null) })); // unmeasured: its default 220 wide
            Assert.True(CanvasFit.Shows(1200, 800, 1, 0, 0, Array.Empty<(double, double, double?, double?)>()));
        }

        [Fact]
        public void Fit_leaves_a_canvas_with_no_size_or_no_steps_alone()
        {
            // A canvas not drawn yet measures 0 x 0: the library's own fit threw there and took the editor down (driven).
            Assert.Null(CanvasFit.Compute(0, 0, new[] { (30d, 40d, (double?)220d, (double?)100d) }));
            Assert.Null(CanvasFit.Compute(double.NaN, 800, new[] { (30d, 40d, (double?)220d, (double?)100d) }));
            Assert.Null(CanvasFit.Compute(1200, 800, Array.Empty<(double, double, double?, double?)>()));
        }
    }
}
