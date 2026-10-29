#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Collaboration;
using Stomme.Models.Pipelines;
using Stomme.Modules.Collaboration.Application;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The pipeline graph as the conflict resolver's fields (<see cref="PipelineGraphFields"/>): a graph
    /// round-trips through its fields in the order it was drawn, an id or a name with a slash cannot fake a path, equal graphs
    /// carry equal version tokens, an editor's changes are exactly the fields it changed and each merges by its kind, a merge
    /// that leaves something pointing at a removed step is a conflict, and a path reads as a person would say it.
    /// </summary>
    public sealed class PipelineGraphFieldsTests
    {
        private static PipelineGraph Sample() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "extract", Type = "Stomme.ParseCsv", Name = "Extract", Position = new GraphPosition { X = 10.5, Y = 20 }, Inputs = { ["Text"] = "k\nv\n" } },
                new PipelineGraphStep { Id = "check", Type = "Stomme.ValidateRecords", Version = 2, Name = "Check", Position = new GraphPosition { X = 250, Y = 20 }, Inputs = { ["Rules"] = "k required", ["OnFailure"] = "Continue" } },
                new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "done" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "extract", SourcePort = "Done", TargetId = "check" },
                new PipelineGraphConnection { SourceId = "check", TargetId = "log" },
            },
        };

        [Fact]
        public void A_graph_round_trips_through_its_fields_in_the_order_it_was_drawn()
        {
            var graph = Sample();
            var fields = PipelineGraphFields.ToFields(graph);

            Assert.Equal("Stomme.ValidateRecords@2", fields["step/check"]);
            Assert.Equal("10.5,20", fields["step/extract/position"]);
            Assert.Equal("k required", fields["step/check/input/Rules"]);
            Assert.Equal(PipelineGraphFields.Present, fields["connection/extract/Done/check"]);
            Assert.Equal(PipelineGraphFields.Present, fields["connection/check//log"]);

            var back = PipelineGraphFields.ToGraph(fields, graph);
            Assert.Equal(new[] { "extract", "check", "log" }, back.Steps.Select(step => step.Id));
            Assert.Equal(("Stomme.ValidateRecords", 2, "Check"), (back.Steps[1].Type, back.Steps[1].Version, back.Steps[1].Name));
            Assert.Equal((10.5, 20d), (back.Steps[0].Position.X, back.Steps[0].Position.Y));
            Assert.Equal(graph.Steps[1].Inputs, back.Steps[1].Inputs);
            Assert.Equal(new[] { ("extract", "Done", "check"), ("check", (string?)null, "log") }, back.Connections.Select(c => (c.SourceId, (string?)c.SourcePort, c.TargetId)));
            Assert.Equal(PipelineGraphFields.Version(fields), PipelineGraphFields.Version(PipelineGraphFields.ToFields(back)));
        }

        // An input's expression is a field of its own, so a collaborating save carries it; it is free text, so
        // two editors changing it is last-writer-wins with a warning, like a script.
        // A variable's kept test value is a text field of its own - a collaborating save carries it, and two authors
        // keeping different values is last-writer-wins with a warning.
        [Fact]
        public void A_kept_test_value_round_trips_as_a_text_field()
        {
            var graph = Sample();
            graph.Variables.Add(new PipelineGraphVariable { Id = "who", Name = "who", TypeName = "String", Value = "world", TestValue = "Ada" });

            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("Ada", fields["variable/who/test-value"]);
            var back = PipelineGraphFields.ToGraph(fields, graph);
            Assert.Equal(("world", "Ada"), (back.Variables.Single().Value, back.Variables.Single().TestValue));
            Assert.Equal(PipelineGraphFields.Version(fields), PipelineGraphFields.Version(PipelineGraphFields.ToFields(back)));

            var none = Sample();
            none.Variables.Add(new PipelineGraphVariable { Id = "who", Name = "who", TypeName = "String", Value = "world" });
            Assert.DoesNotContain("variable/who/test-value", PipelineGraphFields.ToFields(none).Keys);
            Assert.Null(PipelineGraphFields.ToGraph(PipelineGraphFields.ToFields(none), none).Variables.Single().TestValue);

            var edited = Sample();
            edited.Variables.Add(new PipelineGraphVariable { Id = "who", Name = "who", TypeName = "String", Value = "world", TestValue = "Grace" });
            var change = Assert.Single(PipelineGraphFields.Changes(graph, edited));
            Assert.Equal(("variable/who/test-value", FieldKind.Text), (change.Path, change.Kind));
            Assert.Equal("variable 'who', its kept test value", PipelineGraphFields.Describe(change.Path, graph));
        }

        // How many runs may go at once is a structured field of its own - a collaborating save carries it, and two
        // authors changing it is a conflict; absent, the graph has exactly the fields it always had.
        [Fact]
        public void How_many_runs_go_at_once_round_trips_as_a_structured_field()
        {
            var graph = Sample();
            graph.RunsAtOnce = PipelineRunsAtOnce.One;

            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("one", fields["pipeline/runs-at-once"]);
            Assert.Equal(PipelineRunsAtOnce.One, PipelineGraphFields.ToGraph(fields, graph).RunsAtOnce);
            Assert.DoesNotContain("pipeline/runs-at-once", PipelineGraphFields.ToFields(Sample()).Keys);
            Assert.Null(PipelineGraphFields.ToGraph(PipelineGraphFields.ToFields(Sample()), Sample()).RunsAtOnce);

            var edited = Sample();
            edited.RunsAtOnce = PipelineRunsAtOnce.OnePerCorrelation;
            var change = Assert.Single(PipelineGraphFields.Changes(graph, edited));
            Assert.Equal(("pipeline/runs-at-once", FieldKind.Structured, "one-per-correlation"), (change.Path, change.Kind, change.ProposedValue));
            Assert.Equal("how many runs of the pipeline may go at once", PipelineGraphFields.Describe(change.Path, graph));
        }

        // BL-229, n8n's "save execution progress": a structured field - absent while off - that a file carries too.
        [Fact]
        public void Saving_progress_round_trips_as_a_structured_field()
        {
            var graph = Sample();
            graph.SaveProgress = true;
            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("true", fields["pipeline/save-progress"]);
            Assert.True(PipelineGraphFields.ToGraph(fields, graph).SaveProgress);
            Assert.DoesNotContain("pipeline/save-progress", PipelineGraphFields.ToFields(Sample()).Keys);

            var change = Assert.Single(PipelineGraphFields.Changes(Sample(), graph));
            Assert.Equal(("pipeline/save-progress", FieldKind.Structured, "true"), (change.Path, change.Kind, change.ProposedValue));
            Assert.Equal("whether the pipeline stores a run after each step", PipelineGraphFields.Describe(change.Path, graph));
            Assert.True(PipelineExchange.Read(PipelineExchange.Write("Watched", null, graph)).Graph.SaveProgress); // a file carries it
        }

        // Not keeping the runs that finish well is a structured field - absent while they are kept.
        [Fact]
        public void Not_keeping_the_runs_that_finish_well_round_trips_as_a_structured_field()
        {
            var graph = Sample();
            graph.KeepSuccessfulRuns = false;
            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("false", fields["pipeline/keep-successful-runs"]);
            Assert.False(PipelineGraphFields.ToGraph(fields, graph).KeepSuccessfulRuns);
            Assert.DoesNotContain("pipeline/keep-successful-runs", PipelineGraphFields.ToFields(Sample()).Keys);
            Assert.Null(PipelineGraphFields.ToGraph(PipelineGraphFields.ToFields(Sample()), Sample()).KeepSuccessfulRuns);

            var change = Assert.Single(PipelineGraphFields.Changes(Sample(), graph));
            Assert.Equal(("pipeline/keep-successful-runs", FieldKind.Structured, "false"), (change.Path, change.Kind, change.ProposedValue));
            Assert.Equal("whether the pipeline keeps the runs that finish well", PipelineGraphFields.Describe(change.Path, graph));
            Assert.False(PipelineExchange.Read(PipelineExchange.Write("Lean", null, graph)).Graph.KeepSuccessfulRuns); // a file carries it
        }

        // What the runs keep of their steps' data is a structured field - absent while they keep it always; a value it
        // does not know is always.
        [Fact]
        public void What_the_runs_keep_of_their_steps_data_round_trips_as_a_structured_field()
        {
            var graph = Sample();
            graph.KeepStepData = " NEVER ";
            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("never", fields["pipeline/keep-step-data"]);
            Assert.Equal("never", PipelineGraphFields.ToGraph(fields, graph).KeepStepData);
            Assert.DoesNotContain("pipeline/keep-step-data", PipelineGraphFields.ToFields(Sample()).Keys);
            Assert.Null(PipelineGraphFields.ToGraph(PipelineGraphFields.ToFields(Sample()), Sample()).KeepStepData);

            var change = Assert.Single(PipelineGraphFields.Changes(Sample(), graph));
            Assert.Equal(("pipeline/keep-step-data", FieldKind.Structured, "never"), (change.Path, change.Kind, change.ProposedValue));
            Assert.Equal("what the pipeline's runs keep of their steps' data", PipelineGraphFields.Describe(change.Path, graph));
            Assert.Equal("never", PipelineExchange.Read(PipelineExchange.Write("Private", null, graph)).Graph.KeepStepData); // a file carries it
            Assert.Equal((null, "failed", null), (PipelineStepDataKeeping.Stored(""), PipelineStepDataKeeping.Stored("Failed"), PipelineStepDataKeeping.Stored("sometimes")));
        }

        // n8n's time saved per run is a structured field - absent while not said - and a file carries it.
        [Fact]
        public void The_minutes_a_run_saves_round_trip_as_a_structured_field()
        {
            var graph = Sample();
            graph.MinutesSavedPerRun = 15;
            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("15", fields["pipeline/minutes-saved"]);
            Assert.Equal(15, PipelineGraphFields.ToGraph(fields, graph).MinutesSavedPerRun);
            Assert.DoesNotContain("pipeline/minutes-saved", PipelineGraphFields.ToFields(Sample()).Keys);
            Assert.Null(PipelineGraphFields.ToGraph(PipelineGraphFields.ToFields(Sample()), Sample()).MinutesSavedPerRun);

            var change = Assert.Single(PipelineGraphFields.Changes(Sample(), graph));
            Assert.Equal(("pipeline/minutes-saved", FieldKind.Structured, "15"), (change.Path, change.Kind, change.ProposedValue));
            Assert.Equal("the minutes a run of the pipeline saves", PipelineGraphFields.Describe(change.Path, graph));
            Assert.Equal(15, PipelineExchange.Read(PipelineExchange.Write("Worth it", null, graph)).Graph.MinutesSavedPerRun); // a file carries it
        }

        [Fact]
        public void An_inputs_expression_round_trips_as_a_text_field()
        {
            var graph = Sample();
            graph.Steps[2].Inputs.Remove("Message");
            graph.Steps[2].InputExpressions["Message"] = "Read {{ rows[0].name }}";

            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("Read {{ rows[0].name }}", fields["step/log/input-expression/Message"]);
            var back = PipelineGraphFields.ToGraph(fields, graph);
            Assert.Equal("Read {{ rows[0].name }}", back.Steps[2].InputExpressions["Message"]);
            Assert.Empty(back.Steps[2].Inputs);
            Assert.Equal(PipelineGraphFields.Version(fields), PipelineGraphFields.Version(PipelineGraphFields.ToFields(back)));

            var edited = Sample();
            edited.Steps[2].Inputs.Remove("Message");
            edited.Steps[2].InputExpressions["Message"] = "Read {{ rows[1].name }}";
            var change = Assert.Single(PipelineGraphFields.Changes(graph, edited), change => change.Path == "step/log/input-expression/Message");
            Assert.Equal(FieldKind.Text, change.Kind);
            Assert.EndsWith("the expression of input 'Message'", PipelineGraphFields.Describe(change.Path, graph));
        }

        [Fact]
        public void Variables_and_the_steps_bound_to_them_round_trip_and_a_removed_variable_still_bound_is_a_conflict()
        {
            // The pipeline editor walk (2026-10-02): an output reaches a later input through a variable, and a collaborating
            // save must carry the variables and every binding - or a merge would drop them.
            var graph = Sample();
            graph.Variables.Add(new PipelineGraphVariable { Id = "v-rows", Name = "rows", TypeName = "String", Value = "" });
            graph.Steps[0].OutputVariables["Result"] = "v-rows";
            graph.Steps[2].InputVariables["Message"] = "v-rows";
            graph.Steps[2].Inputs.Remove("Message");

            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("rows", fields["variable/v-rows"]);
            Assert.Equal("String", fields["variable/v-rows/type"]);
            Assert.Equal("v-rows", fields["step/extract/output/Result"]);
            Assert.Equal("v-rows", fields["step/log/input-variable/Message"]);

            var back = PipelineGraphFields.ToGraph(fields, graph);
            var variable = Assert.Single(back.Variables);
            Assert.Equal(("v-rows", "rows", "String", ""), (variable.Id, variable.Name, variable.TypeName, variable.Value));
            Assert.Equal("v-rows", back.Steps[0].OutputVariables["Result"]);
            Assert.Equal("v-rows", back.Steps[2].InputVariables["Message"]);
            Assert.Equal(PipelineGraphFields.Version(fields), PipelineGraphFields.Version(PipelineGraphFields.ToFields(back)));
            Assert.Equal("variable 'rows', its starting value", PipelineGraphFields.Describe("variable/v-rows/value", graph));

            // One editor removed the variable while the other bound a step to it: the merge would leave the binding dangling.
            var merged = new Dictionary<string, string?>(fields);
            merged.Remove("variable/v-rows");
            var conflicts = PipelineGraphFields.Dangling(merged, fields);
            Assert.Contains(conflicts, conflict => conflict.Path == "step/log/input-variable/Message");
            Assert.Contains(conflicts, conflict => conflict.Path == "step/extract/output/Result");
            Assert.Contains(conflicts, conflict => conflict.Path == "variable/v-rows/type");
            Assert.Empty(PipelineGraphFields.Dangling(fields, fields));
        }

        [Fact]
        public void An_id_or_a_name_holding_a_slash_cannot_fake_a_path()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a/name", Type = "T", Inputs = { ["x/input/y"] = "1" } },
                    new PipelineGraphStep { Id = "b", Type = "T" },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a/name", SourcePort = "P/Q", TargetId = "b" } },
            };

            var fields = PipelineGraphFields.ToFields(graph);

            Assert.DoesNotContain("step/a/name", fields.Keys);
            Assert.Contains("step/a%2Fname/input/x%2Finput%2Fy", fields.Keys);
            var back = PipelineGraphFields.ToGraph(fields, graph);
            Assert.Equal(new[] { "a/name", "b" }, back.Steps.Select(step => step.Id));
            Assert.Equal("1", back.Steps[0].Inputs["x/input/y"]);
            Assert.Equal(("a/name", "P/Q", "b"), (back.Connections[0].SourceId, back.Connections[0].SourcePort, back.Connections[0].TargetId));
        }

        [Fact]
        public void Equal_graphs_carry_equal_version_tokens_and_any_change_moves_it()
        {
            var one = PipelineGraphFields.Version(PipelineGraphFields.ToFields(Sample()));
            Assert.Equal(one, PipelineGraphFields.Version(PipelineGraphFields.ToFields(Sample())));

            var moved = Sample();
            moved.Steps[2].Position.X = 1;
            Assert.NotEqual(one, PipelineGraphFields.Version(PipelineGraphFields.ToFields(moved)));

            // An absent field and a null one are the same field set.
            var withNull = PipelineGraphFields.ToFields(Sample());
            withNull["step/ghost/name"] = null;
            Assert.Equal(one, PipelineGraphFields.Version(withNull));
        }

        [Fact]
        public void The_changes_are_exactly_the_fields_the_editor_changed_each_with_its_kind()
        {
            var loaded = Sample();
            var saved = Sample();
            saved.Steps[1].Name = "Check rows";                                      // structured
            saved.Steps[1].Position = new GraphPosition { X = 300, Y = 40 };         // text: layout, last-writer-wins
            saved.Steps[2].Inputs["Message"] = "all done";                           // text: a multi-line input
            saved.Steps[1].Inputs["OnFailure"] = "Fault";                            // structured: a dropdown
            saved.Connections.RemoveAt(1);                                          // a connection removed

            var changes = PipelineGraphFields.Changes(loaded, saved, (type, input) => type == "Stomme.LogMessage" && input == "Message");

            Assert.Equal(
                new[]
                {
                    ("connection/check//log", (string?)null, FieldKind.Structured),
                    ("step/check/input/OnFailure", "Fault", FieldKind.Structured),
                    ("step/check/name", "Check rows", FieldKind.Structured),
                    ("step/check/position", "300,40", FieldKind.Text),
                    ("step/log/input/Message", "all done", FieldKind.Text),
                },
                changes.Select(change => (change.Path, change.ProposedValue, change.Kind)));
            Assert.Empty(PipelineGraphFields.Changes(loaded, Sample()));
        }

        [Fact]
        public void Two_editors_changing_different_steps_are_both_kept_by_the_merge()
        {
            var loaded = Sample();
            var theirs = Sample();
            theirs.Steps[0].Name = "Extract rows";
            var ours = Sample();
            ours.Steps[1].Inputs["Rules"] = "k required\nv required";
            ours.Steps.Add(new PipelineGraphStep { Id = "more", Type = "Stomme.Finish", Name = "End" });
            ours.Connections.Add(new PipelineGraphConnection { SourceId = "log", TargetId = "more" });

            var resolution = Resolve(loaded, theirs, ours);

            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            Assert.Empty(PipelineGraphFields.Dangling(resolution.MergedFields, PipelineGraphFields.ToFields(theirs)));
            var merged = PipelineGraphFields.ToGraph(resolution.MergedFields, ours, theirs);
            Assert.Equal(new[] { "extract", "check", "log", "more" }, merged.Steps.Select(step => step.Id));
            Assert.Equal("Extract rows", merged.Steps[0].Name);
            Assert.Equal("k required\nv required", merged.Steps[1].Inputs["Rules"]);
            Assert.Contains(merged.Connections, c => c.SourceId == "log" && c.TargetId == "more");
        }

        [Fact]
        public void A_step_one_editor_removed_that_the_other_still_used_is_a_conflict()
        {
            var loaded = Sample();
            var theirs = Sample();
            theirs.Steps.RemoveAt(2);                  // the log step removed, and its connection with it
            theirs.Connections.RemoveAt(1);
            var ours = Sample();
            ours.Steps.Add(new PipelineGraphStep { Id = "after", Type = "Stomme.Finish", Name = "After" });
            ours.Connections.Add(new PipelineGraphConnection { SourceId = "log", TargetId = "after" }); // a new use of the removed step

            var resolution = Resolve(loaded, theirs, ours);
            var dangling = PipelineGraphFields.Dangling(resolution.MergedFields, PipelineGraphFields.ToFields(theirs));

            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            var conflict = Assert.Single(dangling);
            Assert.Equal(("connection/log//after", PipelineGraphFields.Present, (string?)null), (conflict.Path, conflict.ProposedValue, conflict.CurrentValue));

            // Editing an input of the removed step is the resolver's own conflict: the field was removed underneath.
            var editsIt = Sample();
            editsIt.Steps[2].Inputs["Message"] = "changed";
            Assert.Equal("step/log/input/Message", Assert.Single(Resolve(loaded, theirs, editsIt).Conflicts).Path);
        }

        [Fact]
        public void A_path_reads_as_a_person_would_say_it()
        {
            var graph = Sample();

            Assert.Equal("step 'Check', its name", PipelineGraphFields.Describe("step/check/name", graph));
            Assert.Equal("step 'Log', input 'Message'", PipelineGraphFields.Describe("step/log/input/Message", graph));
            Assert.Equal("the connection from step 'Extract' (Done) to step 'Check'", PipelineGraphFields.Describe("connection/extract/Done/check", graph));
            Assert.Equal("step gone", PipelineGraphFields.Describe("step/gone", graph));
            Assert.Equal("something/else", PipelineGraphFields.Describe("something/else", graph));
        }

        // BL-95: a step in a body - a loop's Body, an If's branch - carries the step it sits in as ONE more field,
        // so the merge treats a body's steps exactly as the pipeline's own: field by field.
        private static PipelineGraph Looping() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "each", Type = "Stomme.ForEach", Name = "Each row" },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Parent = "each", ParentPort = "Body", Inputs = { ["Text"] = "a" } },
                new PipelineGraphStep { Id = "also", Type = "Stomme.WriteLine", Name = "Also", Parent = "each", ParentPort = "Body", Inputs = { ["Text"] = "b" } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "say", TargetId = "also" } },
        };

        [Fact]
        public void A_bodys_steps_carry_their_holder_round_trip_and_merge_field_by_field()
        {
            var fields = PipelineGraphFields.ToFields(Looping());
            Assert.Equal("each/Body", fields["step/say/parent"]);
            // A step of the pipeline itself has no such field - a graph with no bodies has exactly the fields it always had.
            Assert.DoesNotContain("step/each/parent", fields.Keys);
            Assert.DoesNotContain(PipelineGraphFields.ToFields(Sample()).Keys, path => path.EndsWith("/parent", StringComparison.Ordinal));

            var back = PipelineGraphFields.ToGraph(fields, Looping());
            Assert.Equal(new[] { ("each", (string?)null, (string?)null), ("say", "each", "Body"), ("also", "each", "Body") }, back.Steps.Select(step => (step.Id, step.Parent, step.ParentPort)));
            Assert.Equal(PipelineGraphFields.Version(fields), PipelineGraphFields.Version(PipelineGraphFields.ToFields(back)));
            Assert.Equal("step 'Also', the step it sits in", PipelineGraphFields.Describe("step/also/parent", Looping()));

            // Two editors change different steps inside one body: both kept, both still inside it.
            var theirs = Looping();
            theirs.Steps[1].Inputs["Text"] = "a!";
            var ours = Looping();
            ours.Steps[2].Inputs["Text"] = "b!";
            var resolution = Resolve(Looping(), theirs, ours);
            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            var merged = PipelineGraphFields.ToGraph(resolution.MergedFields, ours, theirs);
            Assert.Equal(("a!", "b!"), (merged.Steps[1].Inputs["Text"], merged.Steps[2].Inputs["Text"]));
            Assert.All(merged.Steps.Skip(1), step => Assert.Equal(("each", "Body"), (step.Parent, step.ParentPort)));

            // Moving a step into another body is a change of its one field.
            var moved = Looping();
            moved.Steps[2].ParentPort = "Then";
            Assert.Equal(("step/also/parent", "each/Then"), Assert.Single(PipelineGraphFields.Changes(Looping(), moved)) is var change ? (change.Path, change.ProposedValue) : default);
        }

        [Fact]
        public void A_loop_one_editor_removed_that_the_other_added_a_step_into_is_a_conflict()
        {
            var removed = Looping();
            removed.Steps.Clear();                    // the loop removed, and what it held with it
            removed.Connections.Clear();
            var adding = Looping();
            adding.Steps.Add(new PipelineGraphStep { Id = "late", Type = "Stomme.WriteLine", Parent = "each", ParentPort = "Body" });

            var resolution = Resolve(Looping(), removed, adding);
            var conflicts = resolution.Status == ConflictResolutionStatus.Rejected
                ? resolution.Conflicts
                : PipelineGraphFields.Dangling(resolution.MergedFields, PipelineGraphFields.ToFields(removed));

            Assert.Contains(conflicts, conflict => conflict.Path == "step/late/parent");
        }

        private static ConflictResolution Resolve(PipelineGraph loaded, PipelineGraph stored, PipelineGraph saved)
        {
            var baseFields = PipelineGraphFields.ToFields(loaded);
            var currentFields = PipelineGraphFields.ToFields(stored);
            return new ConflictResolver(TimeProvider.System).Resolve(
                ResourceSnapshot.Of(PipelineGraphFields.Version(baseFields), baseFields),
                ResourceSnapshot.Of(PipelineGraphFields.Version(currentFields), currentFields),
                PipelineGraphFields.Changes(loaded, saved),
                1);
        }
    }
}
