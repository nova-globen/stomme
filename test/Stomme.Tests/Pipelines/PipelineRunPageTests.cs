using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Services;
using Stomme.Pipelines;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ModuleModel = Stomme.Models.Module;
using RunPage = Stomme.Modules.Pipelines.Run;
using RunsPage = Stomme.Modules.Pipelines.Runs;
using PipelinesPage = Stomme.Modules.Pipelines.Index;
using TablesPage = Stomme.Modules.Pipelines.Tables;
using FilesPage = Stomme.Modules.Pipelines.Files;
using EvaluationsPage = Stomme.Modules.Pipelines.Evaluations;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The run page a Run button opens waits for a run the engine has not stored yet (found walking the editor in a browser).
    /// </summary>
    /// <remarks>
    /// A run is DISPATCHED: Run returns the run's id and the page opens at once, before the engine's worker has
    /// stored the run. The page polled only a run it had found, so on a Server site and a WebAssembly site alike every new
    /// run read "There is no such run on this site" until Refresh was pressed (2026-10-02). It now reads a just-started run
    /// again, shown as starting, for <see cref="RunPage.StartingPolls"/> reads before it calls it missing; a link opened any
    /// other way is answered at once.
    /// </remarks>
    public sealed class PipelineRunPageTests : BunitContext
    {
        private readonly FakePipelinesService _service = new();

        public PipelineRunPageTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(_service);
            Services.AddLocalization(); // the runs list's delete confirmations
        }

        // Each poll is FIRED by the test on a manual clock: the page polls through a timer, and on a wall clock its callbacks
        // waited for a thread under a loaded run - the first case failed now and then in the gate's busy rest part, on 2026-10-02
        // under memory pressure and again on 2026-10-03 with the machine idle but a thousand tests starting at once.
        [Fact]
        public void A_run_started_from_its_button_is_shown_once_the_engine_has_stored_it()
        {
            _service.MissingReads = 2;
            var clock = new ManualClock();

            var cut = Render<RunPage>(parameters => parameters
                .Add(page => page.Clock, clock)
                .AddCascadingValue(Page(started: true))
                .AddCascadingValue(Module()));

            Assert.NotEmpty(cut.FindAll("#pipelines-run-starting"));
            Assert.Empty(cut.FindAll("#pipelines-run-missing"));
            var poll = Assert.Single(clock.Timers);

            poll.Fire();
            cut.WaitForAssertion(() => Assert.Equal(2, _service.RunReads));
            Assert.NotEmpty(cut.FindAll("#pipelines-run-starting"));
            Assert.True(poll.Armed, "a run still starting is read again");

            poll.Fire();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-run-status")));
            Assert.Equal(3, _service.RunReads);
            Assert.True(poll.Disposed, "a finished run is not read again");
        }

        [Fact]
        public void A_started_run_that_never_appears_is_called_missing_after_the_window_and_no_longer_read()
        {
            _service.MissingReads = int.MaxValue;
            var clock = new ManualClock();

            var cut = Render<RunPage>(parameters => parameters
                .Add(page => page.Clock, clock)
                .AddCascadingValue(Page(started: true))
                .AddCascadingValue(Module()));

            var poll = Assert.Single(clock.Timers);
            for (var fired = 0; fired < RunPage.StartingPolls && !poll.Disposed; fired++)
            {
                poll.Fire();
            }

            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-run-missing")));
            Assert.Equal("There is no such run on this site.", cut.Find("#pipelines-run-missing").TextContent.Trim());
            Assert.Equal(RunPage.StartingPolls + 1, _service.RunReads);
            Assert.True(poll.Disposed, "a run called missing is not read again");
            Assert.Single(clock.Timers);
        }

        [Fact]
        public void A_run_opened_from_a_link_is_answered_at_once()
        {
            _service.MissingReads = int.MaxValue;
            var clock = new ManualClock();

            var cut = Render<RunPage>(parameters => parameters
                .Add(page => page.Clock, clock)
                .AddCascadingValue(Page(started: false))
                .AddCascadingValue(Module()));

            Assert.Equal("There is no such run on this site.", cut.Find("#pipelines-run-missing").TextContent.Trim());
            Assert.Equal(1, _service.RunReads);
            Assert.Empty(clock.Timers);
        }

        // n8n's execution view - whose run it is, the way to it on the canvas, what it was started with, the run whose
        // failure started it; and Retry, which starts the published version again and opens the new run's page.
        [Fact]
        public void A_failed_run_says_whose_it_is_what_started_it_and_is_retried()
        {
            _service.Shown = new PipelineRunView
            {
                Id = "run-1", DefinitionId = "def-1", PipelineName = "Alert on failures", Version = 2, Status = "Finished", SubStatus = "Faulted",
                CreatedOnUtc = DateTime.UtcNow, FinishedOnUtc = DateTime.UtcNow, InputJson = "{\n  \"who\": \"Ada\"\n}",
                FailedRunId = "run-0", FailedPipelineName = "Nightly report",
            };

            var cut = Render<RunPage>(parameters => parameters.AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            Assert.Equal("Run of Alert on failures", cut.Find("#pipelines-run-title").TextContent.Trim());
            Assert.EndsWith("/Editor?definition=def-1&run=run-1", cut.Find("#pipelines-run-open-editor").GetAttribute("href"));
            Assert.Contains("\"who\": \"Ada\"", cut.Find("#pipelines-run-input pre").TextContent);
            Assert.Equal("a run of Nightly report failed", cut.Find("#pipelines-run-failed-run").TextContent.Trim());
            Assert.EndsWith("/Run?id=run-0", cut.Find("#pipelines-run-failed-run").GetAttribute("href"));

            Assert.Equal("Retry from the start", cut.Find("#pipelines-run-retry").TextContent.Trim());
            cut.Find("#pipelines-run-retry").Click();
            Assert.Equal(new[] { "run-1" }, _service.Retried);
            Assert.EndsWith("/Run?id=run-2&started=1", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);

            // n8n's retry from the node with the error: the new run's page opens; it says which run it goes on from.
            Assert.Empty(_service.RetriedFromFailure);
            cut.Find("#pipelines-run-retry-failed").Click();
            Assert.Equal(new[] { "run-1" }, _service.RetriedFromFailure);
            Assert.EndsWith("/Run?id=run-3&started=1", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
            Assert.Empty(cut.FindAll("#pipelines-run-retry-of"));
            _service.Shown.RetryOfRunId = "run-0";
            cut.Find("#pipelines-run-refresh").Click();
            cut.WaitForAssertion(() => Assert.EndsWith("/Run?id=run-0", cut.Find("#pipelines-run-retry-of").GetAttribute("href")));

            // A run that finished well is run again, and has no failed step to go on from; a run of no pipeline (a step test's,
            // before 10.12) offers neither.
            _service.Shown.SubStatus = "Finished";
            cut.Find("#pipelines-run-refresh").Click();
            cut.WaitForAssertion(() => Assert.Equal("Run again", cut.Find("#pipelines-run-retry").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-run-retry-failed"));
            _service.Shown.PipelineName = null;
            cut.Find("#pipelines-run-refresh").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-run-retry")));
            Assert.Empty(cut.FindAll("#pipelines-run-open-editor"));
            Assert.Equal("Run", cut.Find("#pipelines-run-title").TextContent.Trim());
        }

        // n8n's "retry with original workflow": a run of a version the pipeline has moved past is also offered again on
        // that version; a run of the published version, only once.
        [Fact]
        public void A_run_of_an_older_version_is_run_again_on_it_or_on_the_published_one()
        {
            _service.Shown = new PipelineRunView
            {
                Id = "run-1", DefinitionId = "def-1", PipelineName = "Orders", Version = 2, PublishedVersion = 3, Status = "Finished", SubStatus = "Finished",
                CreatedOnUtc = DateTime.UtcNow, FinishedOnUtc = DateTime.UtcNow,
            };

            var cut = Render<RunPage>(parameters => parameters.AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            Assert.Equal("Run again on version 2", cut.Find("#pipelines-run-retry-its-version").TextContent.Trim());
            Assert.Equal("Run version 2 - the one this run ran - again with what it was started with; version 3 is the published one",
                cut.Find("#pipelines-run-retry-its-version").GetAttribute("title"));
            cut.Find("#pipelines-run-retry-its-version").Click();
            Assert.Equal(new[] { "run-1 on its version" }, _service.Retried);

            _service.Shown.PublishedVersion = 2;
            cut.Find("#pipelines-run-refresh").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-run-retry-its-version")));
            Assert.Equal("Run again", cut.Find("#pipelines-run-retry").TextContent.Trim());
        }

        // n8n Enterprise's execution data redaction: a run whose pipeline did not keep what it was started with says so
        // where that would be, and offers neither retry - the server refuses both, from here or over MCP.
        [Fact]
        public void A_run_whose_input_was_not_kept_says_so_and_offers_no_retry()
        {
            _service.Shown = new PipelineRunView
            {
                Id = "run-1", DefinitionId = "def-1", PipelineName = "Card payments", Version = 1, Status = "Finished", SubStatus = "Faulted",
                CreatedOnUtc = DateTime.UtcNow, FinishedOnUtc = DateTime.UtcNow, InputNotKept = true,
            };

            var cut = Render<RunPage>(parameters => parameters.AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            Assert.StartsWith("What it was started with was not kept", cut.Find("#pipelines-run-input-not-kept").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-run-input"));
            Assert.Empty(cut.FindAll("#pipelines-run-retry"));
            Assert.Empty(cut.FindAll("#pipelines-run-retry-failed"));

            _service.Shown.InputNotKept = false;
            cut.Find("#pipelines-run-refresh").Click();
            cut.WaitForAssertion(() => Assert.Equal("Retry from the start", cut.Find("#pipelines-run-retry").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-run-input-not-kept"));
        }

        // One at a time, the rest in turn: a run waiting its turn is Queued - on its page, which says why and how to take it
        // out of the line, and on the runs list, whose state filter offers it; one that has started is not.
        [Fact]
        public void A_run_waiting_its_turn_is_queued_on_its_page_and_on_the_runs_list()
        {
            _service.Shown = new PipelineRunView
            {
                Id = "run-2", DefinitionId = "def-1", PipelineName = "Orders", Version = 1, Status = "Running", SubStatus = "Pending", CreatedOnUtc = DateTime.UtcNow,
            };

            var cut = Render<RunPage>(parameters => parameters.AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            Assert.Equal("Queued", cut.Find("#pipelines-run-status").TextContent.Trim());
            Assert.StartsWith("It waits its turn: its pipeline runs one at a time", cut.Find("#pipelines-run-queued").TextContent.Trim());
            Assert.EndsWith("Cancel it to take it out of the line.", cut.Find("#pipelines-run-queued").TextContent.Trim()); // the button is Cancel
            _service.Shown.SubStatus = "Suspended";
            cut.Find("#pipelines-run-refresh").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-run-queued")));

            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Orders" });
            _service.Runs.Add(new PipelineRunView { Id = "r1", DefinitionId = "def-1", PipelineName = "Orders", Status = "Running", SubStatus = "Pending", CreatedOnUtc = DateTime.UtcNow });
            var list = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Equal("Queued", list.Find(".db-pipeline-runs-state").TextContent.Trim());
            Assert.Equal("queued", list.Find(".db-pipeline-runs-took").TextContent.Trim());
            Assert.Contains(list.FindAll("#pipelines-runs-state option"), option => option.GetAttribute("value") == PipelineRunStates.Queued && option.TextContent.Trim() == "Queued");
        }

        // n8n's execution filters on the runs list - every pipeline's runs named, narrowed to one pipeline and to a state,
        // a state in the address honoured, and an empty answer said as such.
        [Fact]
        public void The_runs_list_is_filtered_by_pipeline_and_state()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report" });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Alert on failures" });
            DateTime At(int minute) => new DateTime(2026, 10, 6, 7, minute, 0, DateTimeKind.Utc);
            _service.Runs.Add(new PipelineRunView { Id = "r1", DefinitionId = "def-1", PipelineName = "Nightly report", Status = "Finished", SubStatus = "Faulted", CreatedOnUtc = At(3), FinishedOnUtc = At(3) });
            _service.Runs.Add(new PipelineRunView { Id = "r2", DefinitionId = "def-2", PipelineName = "Alert on failures", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = At(2), FinishedOnUtc = At(2) });
            _service.Runs.Add(new PipelineRunView { Id = "r3", DefinitionId = "def-1", PipelineName = "Nightly report", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = At(1), FinishedOnUtc = At(1) });
            string[] Shown(IRenderedComponent<RunsPage> page) => page.FindAll(".db-pipeline-runs-pipeline").Select(cell => cell.TextContent.Trim()).ToArray();

            var cut = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));

            Assert.Equal(new[] { "Nightly report", "Alert on failures", "Nightly report" }, Shown(cut));
            Assert.All(cut.FindAll(".db-pipeline-runs-took"), took => Assert.Equal("0 ms", took.TextContent.Trim()));
            // The state in a word - the engine's two in the title - and the start to the minute, in full in the title (found looking at
            // UAT with fresh eyes: "Finished / Finished" and the full date wrapped over three lines each).
            var state = cut.FindAll(".db-pipeline-runs-state")[0];
            Assert.Equal(("Failed", "Finished / Faulted"), (state.TextContent.Trim(), state.GetAttribute("title")));
            var started = state.ParentElement!.QuerySelector("td[title]")!;
            Assert.Equal(("2026-10-06 07:03", "2026-10-06 07:03:00Z"), (started.TextContent.Trim(), started.GetAttribute("title")));
            Assert.Equal("Runs", cut.Find("#pipelines-runs-title").TextContent.Trim());
            Assert.Equal(new[] { "(every pipeline)", "Alert on failures", "Nightly report" }, cut.FindAll("#pipelines-runs-pipeline option").Select(option => option.TextContent.Trim()));

            cut.Find("#pipelines-runs-state").Change(PipelineRunStates.Failed);
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Nightly report" }, Shown(cut)));
            Assert.Equal((null, PipelineRunStates.Failed), _service.RunQueries[^1]);

            cut.Find("#pipelines-runs-pipeline").Change("def-2");
            cut.WaitForAssertion(() => Assert.Equal("No runs match.", cut.Find("#pipelines-runs-empty").TextContent.Trim()));
            Assert.Equal("Runs of Alert on failures", cut.Find("#pipelines-runs-title").TextContent.Trim());
            Assert.Equal(("def-2", PipelineRunStates.Failed), _service.RunQueries[^1]);

            cut.Find("#pipelines-runs-state").Change("");
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Alert on failures" }, Shown(cut)));

            // A state in the address is the list's to start with; one it does not know is no filter.
            var fromLink = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState("finished")).AddCascadingValue(Module()));
            Assert.Equal(new[] { "Alert on failures", "Nightly report" }, Shown(fromLink));
            Assert.Equal("finished", fromLink.Find("#pipelines-runs-state").GetAttribute("value"));
            var unknown = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState("sideways")).AddCascadingValue(Module()));
            Assert.Equal(3, Shown(unknown).Length);
        }

        // A run beside another of the same pipeline, step by step - the other runs offered (not itself), the steps that went
        // another way said and marked, a step that ran in one only, and a link to the other run.
        [Fact]
        public void A_run_is_compared_with_another_of_its_pipeline_step_by_step()
        {
            var at = new DateTime(2026, 10, 9, 2, 0, 0, DateTimeKind.Utc);
            PipelineStepRunView Pass(string id, string name, string status, int ms, string outputs, string? error = null) =>
                new() { StepId = id, StepName = name, Status = status, StartedAtUtc = at, CompletedAtUtc = at.AddMilliseconds(ms), OutputsJson = outputs, Error = error };
            _service.Shown = new PipelineRunView { Id = "run-1", DefinitionId = "def-1", PipelineName = "Orders", Version = 2, Status = "Finished", SubStatus = "Finished", CreatedOnUtc = at, FinishedOnUtc = at.AddSeconds(1) };
            _service.Steps = new List<PipelineStepRunView> { Pass("a", "Fetch", "Completed", 120, "{\"n\":1}"), Pass("b", "Send", "Completed", 30, "{}") };
            _service.Runs.Add(new PipelineRunView { Id = "run-1", DefinitionId = "def-1", Version = 2, SubStatus = "Finished", CreatedOnUtc = at });
            _service.Runs.Add(new PipelineRunView { Id = "run-0", DefinitionId = "def-1", Version = 1, SubStatus = "Faulted", CreatedOnUtc = at.AddHours(-1) });
            _service.StepsByRun["run-0"] = new List<PipelineStepRunView> { Pass("a", "Fetch", "Completed", 2000, "{\"n\":1}"), Pass("b", "Send", "Faulted", 10, null!, "boom"), Pass("c", "Retry", "Completed", 5, "{}") };

            var cut = Render<RunPage>(parameters => parameters.Add(run => run.Clock, new ManualClock()).AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-run-compare-open")));
            cut.Find("#pipelines-run-compare-open").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "", "run-0" }, cut.FindAll("#pipelines-run-compare-with option").Select(option => option.GetAttribute("value")).ToArray()));
            cut.Find("#pipelines-run-compare-with").Change("run-0");
            cut.WaitForAssertion(() => Assert.StartsWith("2 of 3 steps went another way.", cut.Find("#pipelines-run-compare-summary").TextContent.Trim()));
            Assert.EndsWith("/Run?id=run-0", cut.Find("#pipelines-run-compare-summary a").GetAttribute("href"));

            string[] Row(int index) => cut.FindAll("#pipelines-run-compare-table tbody tr")[index].QuerySelectorAll("td").Select(cell => cell.TextContent.Trim()).ToArray();
            Assert.Equal(new[] { "Fetch", "Completed · 120 ms", "Completed · 2.0 s", "-" }, Row(0)); // time is said, not a difference
            Assert.Equal(new[] { "Send", "Completed · 30 ms", "Faulted · 10 ms · boom", "Completed here, Faulted there" }, Row(1)); // an output not known is not compared
            Assert.Equal(new[] { "Retry", "did not run", "Completed · 5 ms", "ran only in the other run" }, Row(2));
            Assert.Equal(2, cut.FindAll("#pipelines-run-compare-table tr.is-different").Count);
        }

        [Fact]
        public void Two_runs_are_compared_by_state_passes_and_what_each_step_produced()
        {
            var at = new DateTime(2026, 10, 9, 2, 0, 0, DateTimeKind.Utc);
            PipelineStepRunView Pass(string id, string status, int offset, string? outputs) => new() { StepId = id, StepName = id.ToUpperInvariant(), Status = status, StartedAtUtc = at.AddSeconds(offset), CompletedAtUtc = at.AddSeconds(offset + 1), OutputsJson = outputs };

            var mine = new[] { Pass("loop", "Completed", 0, "[]"), Pass("each", "Completed", 1, "1"), Pass("each", "Completed", 2, "2"), Pass("end", "Completed", 3, "\"ok\"") };
            var theirs = new[] { Pass("loop", "Completed", 0, "[]"), Pass("each", "Completed", 1, "1"), Pass("end", "Completed", 3, "\"late\"") };
            var rows = PipelineRunComparison.Compare(mine, theirs);

            Assert.Equal(new[] { "loop", "each", "end" }, rows.Select(row => row.StepId)); // in the order they began
            Assert.Empty(rows[0].Differences);
            Assert.Equal(new[] { "2 times here, once there", "produced something else" }, rows[1].Differences); // its last pass '2' against '1'
            Assert.Equal((2, TimeSpan.FromSeconds(2)), (rows[1].This!.Passes, rows[1].This!.Took));
            Assert.Equal(new[] { "produced something else" }, rows[2].Differences);
            Assert.Equal("2 of 3 steps went another way.", PipelineRunComparison.Summary(rows));
            Assert.Equal("The two runs went the same way, step by step.", PipelineRunComparison.Summary(PipelineRunComparison.Compare(mine, mine)));

            // A viewer is sent no outputs: nothing to compare there, so nothing said.
            var unseen = PipelineRunComparison.Compare(new[] { Pass("end", "Completed", 0, null) }, new[] { Pass("end", "Completed", 0, "\"x\"") });
            Assert.Empty(unseen.Single().Differences);

            // A pass that never ended - it faulted - has no time to say (found driving: it read "0 ms").
            var faulted = new PipelineStepRunView { StepId = "f", Status = "Faulted", StartedAtUtc = at, CompletedAtUtc = null };
            Assert.Null(PipelineRunComparison.Compare(new[] { faulted }, Array.Empty<PipelineStepRunView>()).Single().This!.Took);
        }

        // A sub-pipeline's run links the run that started it, and a run lists the runs it started.
        [Fact]
        public void A_run_links_the_run_that_started_it_and_the_runs_it_started()
        {
            var at = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
            _service.Shown = new PipelineRunView
            {
                Id = "run-1", DefinitionId = "def-1", PipelineName = "Send every report", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = at, FinishedOnUtc = at.AddSeconds(2),
                ParentRunId = "run-0", ParentPipelineName = "Nightly",
                ChildRuns = { new PipelineRunView { Id = "run-2", DefinitionId = "def-2", PipelineName = "Send one report", Status = "Finished", SubStatus = "Faulted", CreatedOnUtc = at, FinishedOnUtc = at.AddMilliseconds(40) } },
            };

            var cut = Render<RunPage>(parameters => parameters.AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            Assert.Equal("a run of Nightly", cut.Find("#pipelines-run-parent").TextContent.Trim());
            Assert.EndsWith("/Run?id=run-0", cut.Find("#pipelines-run-parent").GetAttribute("href"));
            var child = cut.Find("#pipelines-run-children li");
            Assert.Equal("Send one report - Failed, 40 ms", child.TextContent.Trim());
            Assert.EndsWith("/Run?id=run-2", child.QuerySelector("a")!.GetAttribute("href"));
        }

        // n8n's execution data: what a run saved about itself is on its page, and with it in the runs list - where a click
        // on one finds the runs that saved the same.
        [Fact]
        public void A_runs_saved_data_is_shown_and_finds_the_runs_that_saved_the_same()
        {
            var at = new DateTime(2026, 10, 7, 4, 0, 0, DateTimeKind.Utc);
            _service.Shown = new PipelineRunView { Id = "run-1", Version = 1, Status = "Finished", SubStatus = "Finished", CreatedOnUtc = at, FinishedOnUtc = at, Data = { ["order"] = "42", ["customer"] = "Ada" } };
            var page = Render<RunPage>(parameters => parameters.Add(run => run.Clock, new ManualClock()).AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));
            page.WaitForAssertion(() => Assert.Equal(new[] { "order", "customer" }, page.FindAll(".db-pipeline-run-data-key").Select(key => key.TextContent.Trim())));
            Assert.Equal(new[] { "42", "Ada" }, page.FindAll(".db-pipeline-run-data-value").Select(value => value.TextContent.Trim()));

            _service.Runs.Add(new PipelineRunView { Id = "r1", DefinitionId = "def-1", PipelineName = "Orders", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = at, FinishedOnUtc = at, Data = { ["order"] = "42", ["customer"] = "Ada" } });
            _service.Runs.Add(new PipelineRunView { Id = "r2", DefinitionId = "def-1", PipelineName = "Invoices", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = at, FinishedOnUtc = at, Data = { ["order"] = "43" } });
            _service.Runs.Add(new PipelineRunView { Id = "r3", DefinitionId = "def-1", PipelineName = "Plain", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = at, FinishedOnUtc = at });
            string[] Shown(IRenderedComponent<RunsPage> list) => list.FindAll(".db-pipeline-runs-pipeline").Select(cell => cell.TextContent.Trim()).ToArray();
            var runs = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Equal(new[] { "order: 42", "customer: Ada", "order: 43" }, runs.FindAll(".db-pipeline-runs-data button").Select(chip => chip.TextContent.Trim()));
            runs.FindAll(".db-pipeline-runs-data button")[0].Click();
            runs.WaitForAssertion(() => Assert.Equal(new[] { "Orders" }, Shown(runs)));
            Assert.Equal("order: 42", _service.Searches[^1]);
            Assert.Equal("order: 42", runs.Find("#pipelines-runs-search").GetAttribute("value"));
        }

        // n8n's execution annotations: an author says on the run's page how it went - a rating taken back when pressed
        // again, tags, a note - and the review shows there and in the runs list, where a click on a tag finds the runs with it.
        [Fact]
        public void A_run_is_reviewed_on_its_page_and_its_review_shown_in_the_runs_list()
        {
            var at = new DateTime(2026, 10, 7, 4, 0, 0, DateTimeKind.Utc);
            _service.Shown = new PipelineRunView { Id = "run-1", Version = 1, Status = "Finished", SubStatus = "Faulted", CreatedOnUtc = at, FinishedOnUtc = at };
            var page = Render<RunPage>(parameters => parameters.Add(run => run.Clock, new ManualClock()).AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));
            page.WaitForAssertion(() => Assert.Equal("Review the run", page.Find("#pipelines-run-review-open").TextContent.Trim()));
            Assert.Empty(page.FindAll("#pipelines-run-review-said"));

            page.Find("#pipelines-run-review-open").Click();
            page.Find("#pipelines-run-review-good").Click();
            Assert.Equal("true", page.Find("#pipelines-run-review-good").GetAttribute("aria-pressed"));
            page.Find("#pipelines-run-review-bad").Click();
            page.Find("#pipelines-run-review-bad").Click();
            Assert.Equal("false", page.Find("#pipelines-run-review-bad").GetAttribute("aria-pressed")); // taken back
            page.Find("#pipelines-run-review-bad").Click();
            page.Find("#pipelines-run-review-tags").Input("late, customer, Late");
            page.Find("#pipelines-run-review-note").Input("The invoice came twice.");
            page.Find("#pipelines-run-review-save").Click();
            page.WaitForAssertion(() => Assert.Equal("Went badly · late, customer · The invoice came twice. - user7, 2026-10-07 12:00:00Z", page.Find("#pipelines-run-review-said").TextContent.Trim()));
            var (runId, said) = _service.Reviewed.Single();
            Assert.Equal(("run-1", "bad"), (runId, said.Rating));
            Assert.Equal(new[] { "late", "customer" }, said.Tags);
            Assert.Equal("Change the review", page.Find("#pipelines-run-review-open").TextContent.Trim());

            // Opened again on what was said; past the rules the reason is shown and the form stays.
            page.Find("#pipelines-run-review-open").Click();
            Assert.Equal("late, customer", page.Find("#pipelines-run-review-tags").GetAttribute("value"));
            page.Find("#pipelines-run-review-tags").Input(string.Join(",", Enumerable.Range(1, PipelineRunReviews.MaxTags + 1).Select(i => "t" + i)));
            page.Find("#pipelines-run-review-save").Click();
            page.WaitForAssertion(() => Assert.Equal("The review was refused: A run takes at most 10 tags.", page.Find("#pipelines-run-error").TextContent.Trim()));
            Assert.NotNull(page.Find("#pipelines-run-review"));

            _service.Runs.Add(new PipelineRunView { Id = "r1", DefinitionId = "def-1", PipelineName = "Orders", Status = "Finished", SubStatus = "Faulted", CreatedOnUtc = at, FinishedOnUtc = at, Review = new PipelineRunReview { Rating = "bad", Tags = { "late" }, Note = "Twice." } });
            _service.Runs.Add(new PipelineRunView { Id = "r2", DefinitionId = "def-1", PipelineName = "Plain", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = at, FinishedOnUtc = at });
            var runs = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Equal(new[] { "Went badly" }, runs.FindAll(".db-pipeline-runs-rating").Select(mark => mark.TextContent.Trim()));
            Assert.Equal("Twice.", runs.Find(".db-pipeline-runs-rating").GetAttribute("title"));
            runs.Find(".db-pipeline-runs-review button").Click();
            runs.WaitForAssertion(() => Assert.Equal("late", _service.Searches[^1]));
        }

        // n8n's Evaluations: an author starts one over a data table's rows (asked how many), sees it running with its test
        // cases, a second start refused while it runs; the page follows it to the end - the averages, each row's metrics, outputs and
        // run - stops polling, and the evaluation is deleted, asked first.
        [Fact]
        public void An_evaluation_is_started_over_a_data_table_followed_to_its_end_and_deleted()
        {
            _service.SiteTables.Add(new PipelineSiteTableView { Id = "t1", Name = "Questions" });
            var clock = new ManualClock();
            var state = RunsPageState();
            state.QueryString["definition"] = "def-1";
            state.Action = "Evaluations";
            var cut = Render<EvaluationsPage>(parameters => parameters.Add(page => page.Clock, clock).AddCascadingValue(state).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-evaluations-none")));
            Assert.True(cut.Find("#pipelines-evaluations-start").HasAttribute("disabled")); // no table chosen

            cut.Find("#pipelines-evaluations-table").Change("t1");
            cut.Find("#pipelines-evaluations-rows").Change("2");
            cut.Find("#pipelines-evaluations-start").Click();
            cut.WaitForAssertion(() => Assert.Equal("Questions - Running - 0 of 2 rows done", cut.Find("#pipelines-evaluation h4").TextContent.Trim()));
            Assert.Equal(("def-1", "t1", 2), (_service.EvaluationsStarted.Single().DefinitionId, _service.EvaluationsStarted.Single().Request.TableId, _service.EvaluationsStarted.Single().Request.MaxRows));
            Assert.Equal(new[] { "question: 2+2, expected: 4", "question: 3+3, expected: 6" }, cut.FindAll(".db-pipeline-evaluation-case").Select(cell => cell.TextContent.Trim()));

            cut.Find("#pipelines-evaluations-start").Click();
            cut.WaitForAssertion(() => Assert.Equal("The evaluation could not start: An evaluation of this pipeline is still running - wait for it to end, or cancel it.", cut.Find("#pipelines-error").TextContent.Trim()));

            // The rows run; the next poll shows the end.
            var evaluation = _service.Evaluations.Single();
            evaluation.State = PipelineEvaluations.Finished;
            evaluation.RowsDone = 2;
            evaluation.RowsFailed = 1;
            evaluation.Averages = new Dictionary<string, decimal> { ["match"] = 0.5m };
            evaluation.Rows[0].State = PipelineEvaluations.RowFinished;
            evaluation.Rows[0].RunId = "run-a";
            evaluation.Rows[0].Metrics = new Dictionary<string, decimal> { ["match"] = 1m };
            evaluation.Rows[0].Outputs = new Dictionary<string, string> { ["answer"] = "4" };
            evaluation.Rows[1].State = PipelineEvaluations.RowFailed;
            evaluation.Rows[1].RunId = "run-b";
            evaluation.Rows[1].Error = "The run failed.";
            evaluation.Rows[1].Metrics = new Dictionary<string, decimal> { ["match"] = 0m };
            cut.InvokeAsync(() => clock.Timers.Last(timer => timer.Armed).Fire());
            cut.WaitForAssertion(() => Assert.Equal("Questions - Finished - 2 rows, 1 did not finish", cut.Find("#pipelines-evaluation h4").TextContent.Trim()));
            Assert.Equal("Averages: match 0.5", cut.Find("#pipelines-evaluation-averages").TextContent.Trim());
            Assert.Equal(new[] { "match 1", "match 0" }, cut.FindAll(".db-pipeline-evaluation-metrics").Select(cell => cell.TextContent.Trim()));
            Assert.Equal(new[] { "Finished", "Failed - The run failed." }, cut.FindAll(".db-pipeline-evaluation-state").Select(cell => cell.TextContent.Trim()));
            Assert.Equal("answer: 4", cut.Find(".db-pipeline-evaluation-outputs div").TextContent.Trim());
            Assert.Equal(2, cut.FindAll("#pipelines-evaluation a.db-btn").Count);
            Assert.DoesNotContain(clock.Timers, timer => timer.Armed && !timer.Disposed); // nothing runs: no more polling

            cut.Find(".pipelines-evaluation-delete").Click(); // asked first
            Assert.Single(_service.Evaluations);
            cut.Find(".pipelines-evaluation-delete-confirm").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-evaluations-none")));
            Assert.Empty(cut.FindAll("#pipelines-evaluation"));
        }

        // A run's timeline - each step's pass a bar placed where in the run it began, as wide as it took, coloured by how it
        // went, with its time beside it.
        [Fact]
        public void A_runs_timeline_shows_where_each_step_began_and_how_long_it_took()
        {
            var start = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
            _service.Shown = new PipelineRunView { Id = "run-1", Version = 1, Status = "Finished", SubStatus = "Faulted", CreatedOnUtc = start, FinishedOnUtc = start.AddMilliseconds(3250) };
            _service.Steps = new List<PipelineStepRunView>
            {
                new() { StepId = "c", StepName = "Send", Status = "Faulted", StartedAtUtc = start.AddSeconds(3), CompletedAtUtc = start.AddMilliseconds(3250) },
                new() { StepId = "a", StepName = "Read", Status = "Completed", StartedAtUtc = start, CompletedAtUtc = start.AddSeconds(1) },
                new() { StepId = "b", StepName = "Wait a bit", Status = "Completed", StartedAtUtc = start.AddSeconds(1), CompletedAtUtc = start.AddSeconds(3) },
            };
            var page = Render<RunPage>(parameters => parameters.Add(run => run.Clock, new ManualClock()).AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".db-pipeline-run-timeline__row").Count));
            Assert.Equal(new[] { "Read", "Wait a bit", "Send" }, page.FindAll(".db-pipeline-run-timeline__name").Select(name => name.TextContent));
            Assert.Equal(new[] { "1.0 s", "2.0 s", "250 ms" }, page.FindAll(".db-pipeline-run-timeline__took").Select(took => took.TextContent));
            var bars = page.FindAll(".db-pipeline-run-timeline__bar");
            Assert.Equal(new[] { "left:0%;width:30.77%;", "left:30.77%;width:61.54%;", "left:92.31%;width:7.69%;" }, bars.Select(bar => bar.GetAttribute("style")));
            Assert.Equal(new[] { true, true, false }, bars.Select(bar => bar.ClassList.Contains("is-completed")));
            Assert.Contains("is-faulted", bars[2].ClassList);
        }

        // Found driving the retry from a failed step: a quick pass at the very end of a long run (after a wait) has a bar of half a
        // percent that does not fit after it - the page threw while drawing it. It moves left to fit.
        [Fact]
        public void A_quick_step_at_the_end_of_a_long_run_is_drawn_at_the_end_of_the_timeline()
        {
            var start = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
            _service.Shown = new PipelineRunView { Id = "run-1", Version = 1, Status = "Finished", SubStatus = "Finished", CreatedOnUtc = start, FinishedOnUtc = start.AddHours(2) };
            _service.Steps = new List<PipelineStepRunView>
            {
                new() { StepId = "a", StepName = "Wait", Status = "Completed", StartedAtUtc = start, CompletedAtUtc = start.AddHours(2).AddMilliseconds(-3) },
                new() { StepId = "b", StepName = "Say", Status = "Completed", StartedAtUtc = start.AddHours(2).AddMilliseconds(-2), CompletedAtUtc = start.AddHours(2) },
            };
            var page = Render<RunPage>(parameters => parameters.Add(run => run.Clock, new ManualClock()).AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".db-pipeline-run-timeline__row").Count));
            Assert.Equal(new[] { "left:0%;width:100%;", "left:99.5%;width:0.5%;" }, page.FindAll(".db-pipeline-run-timeline__bar").Select(bar => bar.GetAttribute("style")));
        }

        // Found driving Stop run: the engine leaves the record of a step its run abandoned as it was - "Running", and waiting - so
        // a cancelled run's approval step read "waiting". A pass that never ended, in a run that has, ended with it: canceled.
        [Fact]
        public void A_step_its_cancelled_run_abandoned_is_drawn_canceled_not_waiting()
        {
            var start = new DateTime(2026, 10, 8, 4, 27, 57, DateTimeKind.Utc);
            _service.Shown = new PipelineRunView { Id = "run-1", Version = 1, Status = "Finished", SubStatus = "Cancelled", CreatedOnUtc = start, FinishedOnUtc = start.AddSeconds(16) };
            _service.Steps = new List<PipelineStepRunView>
            {
                new() { StepId = "a", StepName = "Greet", Status = "Completed", StartedAtUtc = start, CompletedAtUtc = start.AddMilliseconds(1) },
                new() { StepId = "b", StepName = "Ask for approval", Status = "Running", Waiting = true, StartedAtUtc = start.AddMilliseconds(1) },
            };
            var page = Render<RunPage>(parameters => parameters.Add(run => run.Clock, new ManualClock()).AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".db-pipeline-run-timeline__row").Count));
            Assert.Contains("is-canceled", page.FindAll(".db-pipeline-run-timeline__bar")[1].ClassList);
            Assert.Equal("16.0 s", page.FindAll(".db-pipeline-run-timeline__took")[1].TextContent); // ended with the run
        }

        // The one rule the canvas, the inspector and the timeline share: a waiting pass in a run still going waits; in a run that has
        // finished it was ended with the run; a test that stopped at a wait (shown as a finished run, Suspended) still waits there.
        [Fact]
        public void A_pass_is_named_by_how_it_went_and_by_how_its_run_ended()
        {
            var waiting = new PipelineStepRunView { Status = "Running", Waiting = true };
            var running = new PipelineStepRunView { Status = "Running" };
            var going = new PipelineRunView { Status = "Running", SubStatus = "Suspended" };
            var cancelled = new PipelineRunView { Status = "Finished", SubStatus = "Cancelled" };
            var testAtAWait = new PipelineRunView { Status = "Finished", SubStatus = "Suspended" };

            Assert.Equal(("waiting", "running"), (PipelineStepStates.Of(waiting, going), PipelineStepStates.Of(running, going)));
            Assert.Equal(("canceled", "canceled"), (PipelineStepStates.Of(waiting, cancelled), PipelineStepStates.Of(running, cancelled)));
            Assert.Equal("waiting", PipelineStepStates.Of(waiting, testAtAWait));
            Assert.Equal("waiting", PipelineStepStates.Of(waiting, null)); // the run not read yet
            Assert.Equal(("completed", "handled", "faulted", "canceled"), (
                PipelineStepStates.Of(new PipelineStepRunView { Status = "Completed" }, cancelled),
                PipelineStepStates.Of(new PipelineStepRunView { Status = "Completed", HandledFailure = PipelineStepRunView.TookFailedPath }, cancelled),
                PipelineStepStates.Of(new PipelineStepRunView { Status = "Faulted" }, cancelled),
                PipelineStepStates.Of(new PipelineStepRunView { Status = "Canceled" }, going)));
            Assert.Equal(("Canceled", "Running", "Completed"), (
                PipelineStepStates.StatusOf(waiting, cancelled), PipelineStepStates.StatusOf(waiting, going), PipelineStepStates.StatusOf(new PipelineStepRunView { Status = "Completed" }, cancelled)));
        }

        // n8n's comparison of evaluation runs: an evaluation opens beside the one before it - each average and each row's
        // metric with the earlier value and the change, a gain marked - and is compared with nothing on request; the oldest has
        // nothing before it.
        [Fact]
        public void An_evaluation_is_compared_with_the_one_before_it()
        {
            var at = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
            PipelineEvaluationView Finished(string id, DateTime started, decimal first, decimal second) => new()
            {
                Id = id, DefinitionId = "def-1", TableName = "Quiz", State = PipelineEvaluations.Finished, StartedOnUtc = started, RowCount = 2, RowsDone = 2,
                Averages = new Dictionary<string, decimal> { ["correct"] = (first + second) / 2 },
                Rows =
                {
                    new PipelineEvaluationRowView { RowId = "r1", State = PipelineEvaluations.RowFinished, Values = { ["q"] = "2+2" }, Metrics = { ["correct"] = first } },
                    new PipelineEvaluationRowView { RowId = "r2", State = PipelineEvaluations.RowFinished, Values = { ["q"] = "3+3" }, Metrics = { ["correct"] = second } },
                },
            };
            _service.Evaluations.Add(Finished("eval-2", at, 1m, 1m));
            _service.Evaluations.Add(Finished("eval-1", at.AddHours(-1), 0m, 1m));
            var state = RunsPageState();
            state.QueryString["definition"] = "def-1";
            state.Action = "Evaluations";
            static string Text(AngleSharp.Dom.IElement element) => System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();
            var cut = Render<EvaluationsPage>(parameters => parameters.Add(page => page.Clock, new ManualClock()).AddCascadingValue(state).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-pipeline-evaluations-table tbody tr").Count));

            cut.FindAll(".db-pipeline-evaluations-table tbody tr")[0].QuerySelector("button")!.Click(); // the newer one
            cut.WaitForAssertion(() => Assert.Equal("Averages: correct 1 (was 0.5, +0.5)", Text(cut.Find("#pipelines-evaluation-averages"))));
            Assert.Equal(new[] { "correct 1 (was 0, +1)", "correct 1 (was 1, ±0)" }, cut.FindAll(".db-pipeline-evaluation-metrics").Select(Text));
            Assert.Contains("is-better", cut.Find("#pipelines-evaluation-averages .db-pipeline-evaluation-change").ClassList);
            Assert.Equal("eval-1", cut.Find("#pipelines-evaluation-compare").GetAttribute("value"));

            cut.Find("#pipelines-evaluation-compare").Change("");
            cut.WaitForAssertion(() => Assert.Equal("Averages: correct 1", Text(cut.Find("#pipelines-evaluation-averages"))));

            cut.FindAll(".db-pipeline-evaluations-table tbody tr")[1].QuerySelector("button")!.Click(); // the older one: nothing before it
            cut.WaitForAssertion(() => Assert.Equal("Averages: correct 0.5", Text(cut.Find("#pipelines-evaluation-averages"))));
        }

        // n8n's Data tables: an author makes a table with typed columns, adds a row, changes a cell (a value its column cannot
        // hold refused saying why, the kept value shown again), deletes the row, renames the table (a kept column's type fixed) and
        // deletes it, asked first.
        [Fact]
        public void Data_tables_are_made_and_their_rows_added_changed_and_deleted_by_an_author()
        {
            var state = RunsPageState();
            state.Action = "Tables"; // the page's action, as the router sets it - what the module's log names
            var cut = Render<TablesPage>(parameters => parameters.AddCascadingValue(state).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("No data tables yet.", cut.Find("#pipelines-tables-none").TextContent.Trim()));

            cut.Find("#pipelines-tables-new-name").Input("Subscribers");
            cut.Find("#pipelines-tables-new-column-name-0").Input("email");
            cut.Find("#pipelines-tables-new-column-add").Click();
            cut.Find("#pipelines-tables-new-column-name-1").Input("visits");
            cut.Find("#pipelines-tables-new-column-type-1").Change("Number");
            cut.Find("#pipelines-tables-create").Click();

            cut.WaitForAssertion(() => Assert.Equal("Subscribers", cut.Find("#pipelines-table-name").TextContent.Trim()));
            Assert.Equal("'Subscribers' is made - a pipeline reads and writes it with the Data table step.", cut.Find("#pipelines-message").TextContent.Trim());
            Assert.Equal(new[] { "email", "visits" }, _service.SiteTables.Single().Columns.Select(column => column.Name));
            Assert.Equal(PipelineTableColumnType.Number, _service.SiteTables.Single().Columns[1].Type);
            Assert.Equal("0 rows", cut.Find("#pipelines-table-count").TextContent.Trim());

            cut.Find("#pipelines-table-add-email").Input("ada@example.com");
            cut.Find("#pipelines-table-add-visits").Input("3");
            cut.Find("#pipelines-table-add").Click();
            cut.WaitForAssertion(() => Assert.Equal("1 row", cut.Find("#pipelines-table-count").TextContent.Trim()));
            Assert.Equal("3", cut.Find("#pipelines-table-cell-0-visits").GetAttribute("value"));

            cut.Find("#pipelines-table-cell-0-visits").Change("many");
            cut.WaitForAssertion(() => Assert.Equal("The value could not be saved: visits: 'many' is not a number.", cut.Find("#pipelines-error").TextContent.Trim()));
            Assert.Equal("3", _service.TableRows.Single().Value.Single()["visits"]);            cut.Find("#pipelines-table-cell-0-visits").Change("4");
            cut.WaitForAssertion(() => Assert.Equal("4", _service.TableRows.Single().Value.Single()["visits"]));
            Assert.Empty(cut.FindAll("#pipelines-error"));

            cut.Find(".pipelines-table-row-delete-0").Click(); // asked first
            Assert.Single(_service.TableRows.Single().Value);
            cut.Find(".pipelines-table-row-delete-confirm-0").Click();
            cut.WaitForAssertion(() => Assert.Equal("0 rows", cut.Find("#pipelines-table-count").TextContent.Trim()));
            Assert.Empty(_service.TableRows.Single().Value);

            // A CSV's rows added (its header naming the columns), then the table downloaded through the editor's helper as CSV.
            cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("email,visits\nbob@example.com,2\ncy@example.com,5\n", "subscribers.csv"));
            cut.WaitForAssertion(() => Assert.Equal("Imported 2 row(s) from 'subscribers.csv'.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal("2 rows", cut.Find("#pipelines-table-count").TextContent.Trim());
            var download = JSInterop.SetupVoid("stommePipelineEditorDownload", _ => true);
            download.SetVoidResult();
            cut.Find("#pipelines-table-download").Click();
            cut.WaitForAssertion(() => Assert.Equal("Downloaded as Subscribers.csv.", cut.Find("#pipelines-message").TextContent.Trim()));
            var saved = Assert.Single(download.Invocations).Arguments;
            Assert.Equal(("Subscribers.csv", "text/csv"), (saved[0], saved[2]));
            Assert.Contains("bob@example.com,2", (string)saved[1]!);

            // Rows found by any value, and sorted by a column - a second click descending, a third back to oldest first.
            cut.Find("#pipelines-table-search").Change("cy@");
            cut.WaitForAssertion(() => Assert.Equal("1 row", cut.Find("#pipelines-table-count").TextContent.Trim()));
            Assert.Equal("cy@", _service.LastRowsRead.Search);
            cut.Find("#pipelines-table-clear").Click();
            cut.WaitForAssertion(() => Assert.Equal("2 rows", cut.Find("#pipelines-table-count").TextContent.Trim()));
            cut.Find("#pipelines-table-sort-visits").Click();
            cut.WaitForAssertion(() => Assert.Equal(("visits", false), (_service.LastRowsRead.Sort, _service.LastRowsRead.Descending)));
            Assert.EndsWith("\u25B2", cut.Find("#pipelines-table-sort-visits").TextContent.Trim());
            cut.Find("#pipelines-table-sort-visits").Click();
            cut.WaitForAssertion(() => Assert.Equal(("visits", true), (_service.LastRowsRead.Sort, _service.LastRowsRead.Descending)));
            Assert.Equal("cy@example.com", cut.Find("#pipelines-table-cell-0-email").GetAttribute("value"));
            cut.Find("#pipelines-table-sort-visits").Click();
            cut.WaitForAssertion(() => Assert.Null(_service.LastRowsRead.Sort));

            cut.Find("#pipelines-table-edit").Click();
            Assert.True(cut.Find("#pipelines-table-edit-column-type-0").HasAttribute("disabled")); // a kept column keeps its type
            cut.Find("#pipelines-table-edit-name").Input("Members");
            cut.Find("#pipelines-table-edit-column-name-0").Input("address"); // a kept column renamed: sent with the name it had
            cut.Find("#pipelines-table-save").Click();
            cut.WaitForAssertion(() => Assert.Equal("'Members' is saved.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal(new[] { ("address", "email"), ("visits", "visits") }, _service.LastTableRequest.Columns.Select(column => (column.Name, column.Was)));
            Assert.Equal("Members", cut.Find("#pipelines-table-0").TextContent.Trim());

            cut.Find(".pipelines-table-delete").Click();
            Assert.Contains("Delete 'Members' and its 2 rows?", cut.Find("#pipelines-table").TextContent);
            cut.Find(".pipelines-table-delete-confirm").Click();
            cut.WaitForAssertion(() => Assert.Equal("'Members' is deleted.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Empty(_service.SiteTables);
            Assert.Equal("No data tables yet.", cut.Find("#pipelines-tables-none").TextContent.Trim());
        }

        // The site's pipeline folder: an author makes a folder and goes into it, uploads files (one too large refused before
        // it is sent, a name already there refused by the server saying why), copies a path as a file step reads it, follows the
        // trail back, downloads a file by its link, and deletes a folder with what is in it - asked first.
        [Fact]
        public void The_pipeline_folder_is_browsed_filled_and_emptied_by_an_author()
        {
            var state = RunsPageState();
            state.Action = "Files";
            _service.Files["orders.csv"] = System.Text.Encoding.UTF8.GetBytes("a,b");
            var cut = Render<FilesPage>(parameters => parameters.AddCascadingValue(state).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("orders.csv", cut.Find("#pipelines-file-0 .pipelines-file-name").TextContent.Trim()));
            Assert.Equal("3 bytes", cut.Find("#pipelines-file-0").QuerySelectorAll("td")[1].TextContent.Trim());
            Assert.EndsWith("/api/Pipelines/files/content?path=orders.csv", cut.Find("#pipelines-file-download-0").GetAttribute("href"));

            cut.Find("#pipelines-files-folder-make").Click(); // never disabled: an empty name is said
            cut.WaitForAssertion(() => Assert.Equal("A name is needed.", cut.Find("#pipelines-error").TextContent.Trim()));
            cut.Find("#pipelines-files-folder-name").Input("reports");
            cut.Find("#pipelines-files-folder-make").Click();
            cut.WaitForAssertion(() => Assert.Equal("Made the folder 'reports'.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal("reports/", cut.Find("#pipelines-file-open-0").TextContent.Trim()); // folders first

            Assert.Equal("Open the folder reports", cut.Find("#pipelines-file-open-0").GetAttribute("aria-label")); // each row named for a screen reader
            Assert.Equal("Copy the path of reports", cut.Find("#pipelines-file-copy-0").GetAttribute("aria-label"));
            Assert.Equal("Download orders.csv", cut.Find("#pipelines-file-download-1").GetAttribute("aria-label"));
            cut.Find("#pipelines-file-open-0").Click();
            cut.WaitForAssertion(() => Assert.Equal("Nothing here yet.", cut.Find("#pipelines-files-none").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-message")); // what was said of the folder left goes with it
            Assert.Equal(new[] { "Files", "reports" }, cut.FindAll("#pipelines-files-trail button").Select(button => button.TextContent.Trim()));

            cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(
                InputFileContent.CreateFromText("q,3", "q3.csv"), InputFileContent.CreateFromText(new string('x', 2000), "huge.bin"));
            cut.WaitForAssertion(() => Assert.Equal("Uploaded 'q3.csv'.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal("'huge.bin' was not uploaded: it is larger than the 1 KB a pipeline file may be.", cut.Find("#pipelines-error").TextContent.Trim());
            Assert.Equal("q,3", System.Text.Encoding.UTF8.GetString(_service.Files["reports/q3.csv"]));
            Assert.False(_service.Files.ContainsKey("reports/huge.bin"));

            cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("again", "q3.csv"));
            cut.WaitForAssertion(() => Assert.Equal("'q3.csv' was not uploaded: 'q3.csv' is already in this folder - delete it first, or upload the file under another name.",
                cut.Find("#pipelines-error").TextContent.Trim()));
            Assert.Equal("q,3", System.Text.Encoding.UTF8.GetString(_service.Files["reports/q3.csv"]));

            var copied = JSInterop.SetupVoid("stommePipelineEditorWriteClipboard", _ => true);
            copied.SetVoidResult();
            cut.Find("#pipelines-file-copy-0").Click();
            Assert.Equal("reports/q3.csv", Assert.Single(copied.Invocations).Arguments[0]);

            cut.Find("#pipelines-files-root").Click();
            cut.WaitForAssertion(() => Assert.Equal("reports/", cut.Find("#pipelines-file-open-0").TextContent.Trim()));
            cut.Find(".pipelines-file-delete-0").Click(); // asked first
            Assert.True(_service.Files.ContainsKey("reports"));
            Assert.Contains("Delete the folder 'reports' and everything in it?", cut.Markup);
            cut.Find(".pipelines-file-delete-confirm-0").Click();
            cut.WaitForAssertion(() => Assert.Equal("Deleted 'reports'.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal(new[] { "orders.csv" }, _service.Files.Keys);
        }

        // What a run wrote is run data, shown to the pipelines' authors only: someone who may only view them is told, and nothing is asked for.
        [Fact]
        public void The_pipeline_folder_is_not_shown_to_someone_who_cannot_change_pipelines()
        {
            var state = RunsPageState();
            state.Action = "Files";
            _service.Files["orders.csv"] = System.Text.Encoding.UTF8.GetBytes("a,b");
            var cut = Render<FilesPage>(parameters => parameters.AddCascadingValue(state).AddCascadingValue(Module(without: PipelinesPermissions.Manage)));

            Assert.Equal("The pipelines' files are shown to the people who may change the site's pipelines.", cut.Find(".db-pipelines-denied").TextContent.Trim());
            Assert.DoesNotContain("orders.csv", cut.Markup);
        }

        // n8n's Variables: the site's variables are listed on the pipelines page; an author adds one (a name that cannot be
        // read refused here, before anything is sent), changes a value, removes one, and saves them all.
        [Fact]
        public void Site_variables_are_listed_and_saved_by_an_author()
        {
            _service.SiteVariables = new Dictionary<string, string> { ["team"] = "ops", ["apiBase"] = "https://a.example" };
            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));

            Assert.Equal(new[] { "apiBase", "team" }, cut.FindAll("[id^=pipelines-variable-name-]").Select(cell => cell.TextContent.Trim()));
            cut.Find("#pipelines-variables-add-name").Input("not a name");
            cut.Find("#pipelines-variables-add").Click();
            Assert.StartsWith("'not a name' cannot be a variable's name", cut.Find("#pipelines-error").TextContent.Trim());
            cut.Find("#pipelines-variables-add-name").Input("limit");
            cut.Find("#pipelines-variables-add-value").Input("10");
            cut.Find("#pipelines-variables-add").Click();
            cut.Find("#pipelines-variable-value-0").Change("https://b.example");
            cut.Find("#pipelines-variable-remove-1").Click();
            cut.Find("#pipelines-variables-save").Click();

            cut.WaitForAssertion(() => Assert.Equal("2 site variables saved.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal(new Dictionary<string, string> { ["apiBase"] = "https://b.example", ["limit"] = "10" }, _service.SiteVariables);
            Assert.Equal(new[] { "apiBase", "limit" }, cut.FindAll("[id^=pipelines-variable-name-]").Select(cell => cell.TextContent.Trim()));
        }

        // n8n's workflow list - found by any words of its name or description, each with how its last run went (a link
        // to it), "never" for one that has not run.
        [Fact]
        public void The_pipelines_list_is_searched_and_says_how_each_last_run_went()
        {
            var at = new DateTime(2026, 10, 6, 5, 38, 0, DateTimeKind.Utc);
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report", Description = "Reads the figures", IsPublished = true, Version = 1,
                LastRun = new PipelineRunView { Id = "r1", DefinitionId = "def-1", Status = "Finished", SubStatus = "Faulted", CreatedOnUtc = at }, Queued = 2 });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Alert on failures", Description = "Says what failed", IsPublished = true, Version = 1 });
            string[] Names(IRenderedComponent<PipelinesPage> page) => page.FindAll(".db-pipelines-table tbody strong").Select(name => name.TextContent.Trim()).ToArray();

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));

            Assert.True(_service.AskedForLastRuns);
            Assert.Equal(new[] { "Nightly report", "Alert on failures" }, Names(cut));
            var last = cut.Find(".db-pipelines-last-run a");
            Assert.Equal("Failed", last.TextContent.Trim());
            Assert.Contains("is-failed", last.ClassName);
            Assert.EndsWith("/Run?id=r1", last.GetAttribute("href"));
            Assert.Contains("never", cut.FindAll(".db-pipelines-last-run")[1].TextContent);
            // Its runs waiting their turn, a link to them - none said for a pipeline without.
            var queued = Assert.Single(cut.FindAll(".db-pipelines-queued"));
            Assert.Equal("2 queued", queued.TextContent.Trim());
            Assert.EndsWith("/Runs?definition=def-1&state=" + PipelineRunStates.Queued, queued.GetAttribute("href"));

            cut.Find("#pipelines-search").Input("figures night");
            Assert.Equal(new[] { "Nightly report" }, Names(cut));
            cut.Find("#pipelines-search").Input("ALERT");
            Assert.Equal(new[] { "Alert on failures" }, Names(cut));
            cut.Find("#pipelines-search").Input("nothing like it");
            Assert.Empty(Names(cut));
            Assert.Equal("No pipeline matches 'nothing like it'.", cut.Find("#pipelines-none-match").TextContent.Trim());
        }

        // n8n's human in the loop - a run waiting for approval says what is asked, the details and by when; a person with
        // the approve key decides it with a comment, and the page reads the run again; a decision someone made first is said so.
        [Fact]
        public void A_run_waiting_for_approval_is_decided_on_its_page()
        {
            var at = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
            _service.Shown = new PipelineRunView { Id = "run-1", DefinitionId = "def-1", PipelineName = "Pay invoices", Version = 1, Status = "Running", SubStatus = "Suspended", CreatedOnUtc = at };
            _service.Approvals.Add(Waiting("bm-1", "run-1", at));
            _service.Approvals.Add(Waiting("bm-9", "run-9", at)); // another run's: not this page's
            var clock = new ManualClock();

            var cut = Render<RunPage>(parameters => parameters.Add(page => page.Clock, clock).AddCascadingValue(Page(started: false)).AddCascadingValue(Module()));

            var panel = Assert.Single(cut.FindAll(".db-pipeline-approval"));
            Assert.Equal("pipelines-approval-bm-1", panel.Id);
            Assert.Equal("Waiting for approval - Manager OK", panel.QuerySelector("h4")!.TextContent.Trim());
            Assert.Equal("Pay invoice 42?", cut.Find(".db-pipeline-approval__question").TextContent.Trim());
            Assert.Equal("Amount: 1200 EUR", cut.Find(".db-pipeline-approval__details").TextContent.Trim());
            Assert.Contains("to be decided by 2026-10-08 09:00:00Z", cut.Find(".db-pipeline-approval__when").TextContent);

            cut.Find("#pipelines-approval-comment-bm-1").Input("Within budget");
            cut.Find("#pipelines-approval-approve-bm-1").Click();

            cut.WaitForAssertion(() => Assert.Equal("Approved - the run goes on down Approved.", cut.Find("#pipelines-approval-decided").TextContent.Trim()));
            Assert.Equal(("bm-1", true, "Within budget"), Assert.Single(_service.Decisions));
            Assert.Empty(cut.FindAll(".db-pipeline-approval"));

            // Decided first by someone else: said so, and nothing else happens.
            _service.Approvals.Insert(0, Waiting("bm-2", "run-1", at));
            cut.Find("#pipelines-run-refresh").Click();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-approval-reject-bm-2")));
            _service.DecidedElsewhere = true;
            cut.Find("#pipelines-approval-reject-bm-2").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("It was decided already", cut.Find("#pipelines-approval-decided").TextContent.Trim()));
            Assert.Equal(("bm-2", false, (string)null), _service.Decisions[^1]);
        }

        // Without the approve key the question is shown and no decision offered; a finished run asks nothing.
        [Fact]
        public void A_run_waiting_for_approval_offers_no_decision_without_the_approve_key()
        {
            var at = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
            _service.Shown = new PipelineRunView { Id = "run-1", DefinitionId = "def-1", PipelineName = "Pay invoices", Version = 1, Status = "Running", SubStatus = "Suspended", CreatedOnUtc = at };
            _service.Approvals.Add(Waiting("bm-1", "run-1", at));

            var cut = Render<RunPage>(parameters => parameters.Add(page => page.Clock, new ManualClock()).AddCascadingValue(Page(started: false)).AddCascadingValue(Module(without: PipelinesPermissions.Approve)));

            Assert.Equal("Pay invoice 42?", cut.Find(".db-pipeline-approval__question").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-approval-approve-bm-1"));
            Assert.Empty(cut.FindAll("#pipelines-approval-comment-bm-1"));
            Assert.Contains("pipelines:approve", cut.Find(".db-pipeline-approval__denied").TextContent);

            _service.Shown = new PipelineRunView { Id = "run-1", DefinitionId = "def-1", PipelineName = "Pay invoices", Version = 1, Status = "Finished", SubStatus = "Finished", CreatedOnUtc = at, FinishedOnUtc = at };
            cut.Find("#pipelines-run-refresh").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".db-pipeline-approval")));
        }

        // The runs waiting for a decision head the runs list (one pipeline's, when filtered), and the pipelines list counts them.
        [Fact]
        public void The_runs_and_pipelines_lists_say_which_runs_wait_for_approval()
        {
            var at = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Pay invoices" });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Ship orders" });
            _service.Approvals.Add(Waiting("bm-1", "run-1", at));
            var other = Waiting("bm-2", "run-2", at.AddMinutes(5));
            other.DefinitionId = "def-2";
            other.PipelineName = "Ship orders";
            other.Question = "Ship order 7?";
            other.ExpiresOnUtc = null;
            _service.Approvals.Add(other);

            var runs = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Equal(new[] { "Pay invoice 42?", "Ship order 7?" }, runs.FindAll(".db-pipeline-approvals-question").Select(cell => cell.TextContent.Trim()));
            var decide = runs.FindAll("#pipelines-runs-approvals a");
            Assert.Equal("Decide", decide[0].TextContent.Trim());
            Assert.EndsWith("/Run?id=run-1", decide[0].GetAttribute("href"));
            runs.Find("#pipelines-runs-pipeline").Change("def-2");
            runs.WaitForAssertion(() => Assert.Equal(new[] { "Ship order 7?" }, runs.FindAll(".db-pipeline-approvals-question").Select(cell => cell.TextContent.Trim())));

            var pipelines = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Equal("2 runs wait for approval", pipelines.Find("#pipelines-approvals-link").TextContent.Trim());

            _service.Approvals.Clear();
            var none = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Empty(none.FindAll("#pipelines-approvals-link"));
            var noRuns = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Empty(noRuns.FindAll("#pipelines-runs-approvals"));
        }

        // n8n's Insights on the pipelines list - the window's runs, failures (to the failed runs), failure rate and median
        // run, a window chosen; each pipeline's own figures in its row, its failures linked to its failed runs.
        [Fact]
        public void The_pipelines_list_says_how_the_pipelines_have_been_running()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report", IsPublished = true, Version = 1 });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Alert on failures", IsPublished = true, Version = 1 });
            _service.Insights = days => new PipelineInsightsView
            {
                Days = days,
                Total = new PipelineInsight { Runs = days == 30 ? 40 : 6, Failed = 2, Unfinished = 1, MedianMilliseconds = 3000, FailureRate = 0.4, MinutesSaved = 125 },
                Pipelines = { new PipelineInsight { DefinitionId = "def-1", PipelineName = "Nightly report", Runs = 6, Failed = 2, MedianMilliseconds = 3000, FailureRate = 0.4, MinutesSaved = 60, MinutesSavedPerRun = 15 } },
            };

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));

            Assert.Equal(7, _service.InsightWindows[^1]);
            Assert.Equal("6 runs", cut.Find("#pipelines-insights-runs").TextContent.Trim());
            var failed = cut.Find("#pipelines-insights-failed a");
            Assert.Equal("2 failed", failed.TextContent.Trim());
            Assert.EndsWith("/Runs?state=failed", failed.GetAttribute("href"));
            Assert.Equal("failure rate 40 %", cut.Find("#pipelines-insights-rate").TextContent.Trim());
            Assert.Equal("median run 3.0 s", cut.Find("#pipelines-insights-median").TextContent.Trim());
            Assert.Equal("1 not ended", cut.Find("#pipelines-insights-unfinished").TextContent.Trim());
            Assert.Equal("2 h 5 min saved", cut.Find("#pipelines-insights-saved").TextContent.Trim()); // n8n's time saved
            Assert.Empty(cut.FindAll("#pipelines-insights-capped"));

            var rows = cut.FindAll(".db-pipelines-insight");
            Assert.Equal("6 runs · 2 failed · 3.0 s · 1 h saved", string.Join(" ", rows[0].TextContent.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)));
            Assert.Equal("15 min a run that finishes well", rows[0].QuerySelector(".db-pipelines-insight__saved")!.GetAttribute("title"));
            Assert.EndsWith("/Runs?definition=def-1&state=failed", rows[0].QuerySelector("a")!.GetAttribute("href"));
            Assert.Equal("no runs", rows[1].TextContent.Trim());
            Assert.Contains("Last 7 days", cut.Find(".db-pipelines-table thead").TextContent);

            cut.Find("#pipelines-insights-days").Change("30");
            cut.WaitForAssertion(() => Assert.Equal("40 runs", cut.Find("#pipelines-insights-runs").TextContent.Trim()));
            Assert.Equal(30, _service.InsightWindows[^1]);
            Assert.Contains("Last 30 days", cut.Find(".db-pipelines-table thead").TextContent);
        }

        // What runs next: the published pipelines' schedules, the soonest first - when, which pipeline (to its editor), its
        // cron and zone, how many runs in the window; the next week chosen; the site's schedules being off said.
        [Fact]
        public void The_pipelines_list_says_what_runs_next()
        {
            var soon = DateTime.UtcNow.AddMinutes(90);
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report", IsPublished = true, Version = 1 });
            _service.Upcoming = hours => new PipelineUpcomingView
            {
                Hours = hours,
                Enabled = hours == 24,
                Schedules = { new PipelineUpcomingSchedule { DefinitionId = "def-1", PipelineName = "Nightly report", StepId = "s", StepName = "Every morning", Cron = "0 6 * * *", TimeZone = "Europe/Stockholm", NextRunsUtc = { soon }, RunsInWindow = hours == 24 ? 1 : 7 } },
            };

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-pipelines-upcoming__row")));
            Assert.Equal("Coming up - 1 schedule in the next day", cut.Find("#pipelines-upcoming summary").TextContent.Trim());
            var cells = cut.FindAll(".db-pipelines-upcoming__row td").Select(cell => cell.TextContent.Trim()).ToList();
            Assert.StartsWith(soon.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC · in 1 h", cells[0]);
            Assert.Equal(("Nightly report", "0 6 * * * Europe/Stockholm · Every morning", "1"), (cells[1], cells[2], cells[3]));
            Assert.EndsWith("/Editor?definition=def-1", cut.Find(".db-pipelines-upcoming__row a").GetAttribute("href"));
            Assert.Empty(cut.FindAll("#pipelines-upcoming-off"));

            cut.Find("#pipelines-upcoming-hours").Change("168");
            cut.WaitForAssertion(() => Assert.Equal("7", cut.FindAll(".db-pipelines-upcoming__row td")[3].TextContent.Trim()));
            Assert.Equal(168, _service.UpcomingWindows[^1]);
            Assert.Contains("runs no schedules", cut.Find("#pipelines-upcoming-off").TextContent); // the site's schedules are off
        }

        // n8n's archive: More's Archive sets a pipeline aside - out of the list, said - and "Archived (1)" shows it alone,
        // opened read-only, with Restore (back as a draft) and Delete for good, asked first; the archive emptied, the list is back.
        [Fact]
        public void A_pipeline_archived_leaves_the_list_and_is_restored_or_deleted_from_the_archive()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report", IsPublished = true, Version = 1 });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Old import", IsPublished = false, Version = 3 });
            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            string[] Names() => cut.FindAll(".db-pipelines-table tbody tr strong").Select(name => name.TextContent.Trim()).ToArray();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Nightly report", "Old import" }, Names()));
            Assert.Empty(cut.FindAll("#pipelines-archived-toggle")); // nothing archived, nothing to show

            // Archived: gone from the list, said, and a button away.
            cut.FindAll(".db-pipelines-archive")[1].Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Nightly report" }, Names()));
            Assert.Equal(new[] { "archive def-2" }, _service.ArchiveCalls);
            Assert.StartsWith("Archived 'Old import'", cut.Find("#pipelines-message").TextContent.Trim());
            Assert.Equal("Archived (1)", cut.Find("#pipelines-archived-toggle").TextContent.Trim());

            // The archive: it alone, opened read-only, with Restore and Delete - no Run, no More.
            cut.Find("#pipelines-archived-toggle").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Old import" }, Names()));
            var row = cut.Find(".db-pipelines-table tbody tr");
            Assert.Contains("Archived 2026-10-08", row.TextContent);
            Assert.Empty(row.QuerySelectorAll(".db-pipelines-more"));
            Assert.Equal("Open", row.QuerySelector("a.db-btn")!.TextContent.Trim());
            Assert.NotNull(cut.Find("#pipelines-archived-note"));

            // Restored: in the list again, the archive empty - so the list is what is shown.
            cut.Find(".db-pipelines-restore").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Nightly report", "Old import" }, Names()));
            Assert.Empty(cut.FindAll("#pipelines-archived-toggle"));
            Assert.StartsWith("Restored 'Old import' as a draft", cut.Find("#pipelines-message").TextContent.Trim());

            // Archived again and deleted for good from the archive, asked first.
            cut.FindAll(".db-pipelines-archive")[1].Click();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-archived-toggle")));
            cut.Find("#pipelines-archived-toggle").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Old import" }, Names()));
            cut.Find(".db-pipelines-table tbody tr .db-btn-danger").Click();
            cut.Find(".app-actiondialog .db-dialog-foot button").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "archive def-2", "restore def-2", "archive def-2", "delete def-2" }, _service.ArchiveCalls));
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Nightly report" }, Names())); // the archive emptied: back to the list
        }

        // n8n's execution search and bulk delete - a run found by its id or correlation id (and from the address); finished
        // runs chosen, deleted together once confirmed; a run still going cannot be chosen.
        [Fact]
        public void Runs_are_found_by_id_or_correlation_and_finished_ones_deleted_together()
        {
            DateTime At(int minute) => new DateTime(2026, 10, 6, 7, minute, 0, DateTimeKind.Utc);
            _service.Runs.Add(new PipelineRunView { Id = "r1", DefinitionId = "def-1", PipelineName = "Orders", Status = "Finished", SubStatus = "Finished", CorrelationId = "order-7", CreatedOnUtc = At(3), FinishedOnUtc = At(3) });
            _service.Runs.Add(new PipelineRunView { Id = "r2", DefinitionId = "def-1", PipelineName = "Orders", Status = "Finished", SubStatus = "Faulted", CorrelationId = "order-8", CreatedOnUtc = At(2), FinishedOnUtc = At(2) });
            _service.Runs.Add(new PipelineRunView { Id = "r3", DefinitionId = "def-1", PipelineName = "Orders", Status = "Running", SubStatus = "Suspended", CorrelationId = "order-7", CreatedOnUtc = At(1) });

            var cut = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Equal(3, cut.FindAll(".db-pipeline-runs-table tbody tr").Count);
            Assert.Equal(2, cut.FindAll(".db-pipeline-runs-choose").Count); // the run still waiting cannot be chosen

            cut.Find("#pipelines-runs-search").Change("order-7");
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-pipeline-runs-table tbody tr").Count));
            Assert.Equal("order-7", _service.Searches[^1]);
            cut.Find("#pipelines-runs-search").Change("  ");
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-pipeline-runs-table tbody tr").Count));
            Assert.Null(_service.Searches[^1]);

            // Two chosen, the delete confirmed: both gone, nothing chosen any more.
            var boxes = cut.FindAll(".db-pipeline-runs-choose");
            boxes[0].Change(true);
            cut.FindAll(".db-pipeline-runs-choose")[1].Change(true);
            Assert.Equal("2 runs chosen", cut.Find("#pipelines-runs-chosen span").TextContent.Trim());
            // Asked first (the ActionDialog, as every delete): cancelled, nothing is deleted and both stay chosen.
            cut.Find(".pipelines-runs-delete-chosen").Click();
            Assert.Contains("Delete the 2 chosen runs", cut.Find("#pipelines-runs-chosen").TextContent);
            cut.FindAll("#pipelines-runs-chosen button").Single(button => button.TextContent.Trim() == "Cancel").Click();
            Assert.Empty(_service.Deleted);
            Assert.Equal("2 runs chosen", cut.Find("#pipelines-runs-chosen span").TextContent.Trim());
            cut.Find(".pipelines-runs-delete-chosen").Click();
            cut.Find(".pipelines-runs-delete-confirm").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-pipeline-runs-table tbody tr")));
            Assert.Equal(new[] { "r1", "r2" }, _service.Deleted.OrderBy(id => id));
            Assert.Empty(cut.FindAll("#pipelines-runs-chosen"));

            // A search in the address is the list's to start with.
            var fromLink = Render<RunsPage>(parameters => parameters.AddCascadingValue(new PageState
            {
                Alias = Alias(), Site = new Site { SiteId = 1, Name = "Site" }, Page = new Page { PageId = 1, SiteId = 1, Path = "admin/pipelines", Name = "Pipelines" },
                User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Admin + ";" },
                QueryString = new Dictionary<string, string> { ["search"] = "order-7" }, UrlParameters = "",
            }).AddCascadingValue(Module()));
            Assert.Equal("order-7", fromLink.Find("#pipelines-runs-search").GetAttribute("value"));
            Assert.Equal("order-7", _service.Searches[^1]);
        }

        // n8n's start-date filter - the runs started from one day to another (UTC days, both kept), either bound alone;
        // two days the wrong way round said, nothing asked; and the days read from the address.
        [Fact]
        public void Runs_are_found_by_the_days_they_started()
        {
            _service.Runs.Add(new PipelineRunView { Id = "r1", DefinitionId = "def-1", PipelineName = "Orders", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = new DateTime(2026, 10, 6, 23, 59, 0, DateTimeKind.Utc) });
            _service.Runs.Add(new PipelineRunView { Id = "r2", DefinitionId = "def-1", PipelineName = "Orders", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc) });
            _service.Runs.Add(new PipelineRunView { Id = "r3", DefinitionId = "def-1", PipelineName = "Orders", Status = "Finished", SubStatus = "Finished", CreatedOnUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc) });
            int Rows(IRenderedComponent<RunsPage> page) => page.FindAll(".db-pipeline-runs-table tbody tr").Count;

            var cut = Render<RunsPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Equal(3, Rows(cut));
            cut.Find("#pipelines-runs-from").Change("2026-10-07");
            cut.WaitForAssertion(() => Assert.Equal(2, Rows(cut)));
            Assert.Equal((new DateOnly(2026, 10, 7), (DateOnly?)null), _service.Days[^1]);
            cut.Find("#pipelines-runs-to").Change("2026-10-08");
            cut.WaitForAssertion(() => Assert.Equal(1, Rows(cut)));
            Assert.Equal((new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8)), _service.Days[^1]);

            // The wrong way round: said, and nothing asked.
            var asked = _service.Days.Count;
            cut.Find("#pipelines-runs-from").Change("2026-10-09");
            cut.WaitForAssertion(() => Assert.Equal("No run could have started from 2026-10-09 to 2026-10-08: the second day comes first.", cut.Find("#pipelines-runs-error").TextContent.Trim()));
            Assert.Equal(asked, _service.Days.Count);

            // Cleared: every run again.
            cut.Find("#pipelines-runs-from").Change("");
            cut.Find("#pipelines-runs-to").Change("");
            cut.WaitForAssertion(() => Assert.Equal(3, Rows(cut)));
            Assert.Equal(((DateOnly?)null, (DateOnly?)null), _service.Days[^1]);

            // From the address: the days a link names are the list's to start with.
            var fromLink = Render<RunsPage>(parameters => parameters.AddCascadingValue(new PageState
            {
                Alias = Alias(), Site = new Site { SiteId = 1, Name = "Site" }, Page = new Page { PageId = 1, SiteId = 1, Path = "admin/pipelines", Name = "Pipelines" },
                User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Admin + ";" },
                QueryString = new Dictionary<string, string> { ["from"] = "2026-10-09", ["to"] = "2026-10-09" }, UrlParameters = "",
            }).AddCascadingValue(Module()));
            Assert.Equal(("2026-10-09", "2026-10-09"), (fromLink.Find("#pipelines-runs-from").GetAttribute("value"), fromLink.Find("#pipelines-runs-to").GetAttribute("value")));
            Assert.Equal(1, Rows(fromLink));
        }

        // n8n's Duplicate - the pipeline's graph imported under a new name and opened in the editor.
        [Fact]
        public void A_pipeline_is_duplicated_as_a_new_draft_and_opened()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report", Description = "Reads the figures", IsPublished = true, Version = 3 });
            _service.Graphs["def-1"] = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "Say" } }, RunsAtOnce = PipelineRunsAtOnce.One };

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            cut.Find(".db-pipelines-duplicate").Click();

            var copy = Assert.Single(_service.Imported);
            Assert.Equal(("Nightly report (copy)", "Reads the figures"), (copy.Name, copy.Description));
            Assert.Equal(("a", PipelineRunsAtOnce.One), (Assert.Single(copy.Graph.Steps).Id, copy.Graph.RunsAtOnce));
            Assert.EndsWith("/Editor?definition=def-copy", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);

            Assert.Equal(200, PipelinesPage.CopyName(new string('x', 200)).Length);
            Assert.EndsWith(" (copy)", PipelinesPage.CopyName(new string('x', 200)));
        }

        // n8n's workflow tags - chips on each pipeline, a filter of every tag in use (once whatever its case; a pipeline
        // shown carries EVERY tag chosen), the search reading tags too; an author changes a pipeline's tags in its row, a tag
        // that cannot be one refused before anything is sent; a duplicate keeps them.
        [Fact]
        public void Pipelines_are_tagged_filtered_by_their_tags_and_keep_them_when_duplicated()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report", IsPublished = true, Version = 1, Tags = { "billing", "nightly" } });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Invoice sync", IsPublished = true, Version = 1, Tags = { "Billing" } });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-3", Name = "Alert on failures", IsPublished = true, Version = 1 });
            _service.Graphs["def-1"] = new PipelineGraph();
            string[] Names(IRenderedComponent<PipelinesPage> page) => page.FindAll(".db-pipelines-table tbody strong").Select(name => name.TextContent.Trim()).ToArray();
            AngleSharp.Dom.IElement Filter(IRenderedComponent<PipelinesPage> page, string tag) =>
                page.FindAll("#pipelines-tag-filter .db-pipelines-tag").Single(chip => chip.TextContent.Trim() == tag);
            AngleSharp.Dom.IElement Row(IRenderedComponent<PipelinesPage> page, string name) =>
                page.FindAll(".db-pipelines-table tbody tr").Single(row => row.QuerySelector("strong")!.TextContent.Trim() == name);

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));

            Assert.Equal(new[] { "billing", "nightly" }, cut.FindAll("#pipelines-tag-filter .db-pipelines-tag").Select(chip => chip.TextContent.Trim()));
            Assert.Equal(new[] { "billing", "nightly" }, Row(cut, "Nightly report").QuerySelectorAll(".db-pipelines-tag:not(.db-pipelines-tags-change)").Select(chip => chip.TextContent.Trim()));
            Filter(cut, "billing").Click();
            Assert.Equal(new[] { "Nightly report", "Invoice sync" }, Names(cut));
            Assert.Equal("true", Filter(cut, "billing").GetAttribute("aria-pressed"));
            Filter(cut, "nightly").Click();
            Assert.Equal(new[] { "Nightly report" }, Names(cut));
            cut.Find("#pipelines-tag-filter-clear").Click();
            Assert.Equal(3, Names(cut).Length);
            // A chip on a row filters too; the search reads tags.
            Row(cut, "Invoice sync").QuerySelector(".db-pipelines-tag")!.Click();
            Assert.Equal(new[] { "Nightly report", "Invoice sync" }, Names(cut));
            cut.Find("#pipelines-search").Input("nightly");
            Assert.Equal(new[] { "Nightly report" }, Names(cut));
            cut.Find("#pipelines-search").Input("alert");
            Assert.Equal("No pipeline matches 'alert' tagged Billing.", cut.Find("#pipelines-none-match").TextContent.Trim());
            cut.Find("#pipelines-tag-filter-clear").Click();
            cut.Find("#pipelines-search").Input("BILLING");
            Assert.Equal(new[] { "Nightly report", "Invoice sync" }, Names(cut));
            cut.Find("#pipelines-search").Input("");

            // Tagging a pipeline: tidied, the same tag once, saved for that pipeline only.
            Row(cut, "Alert on failures").QuerySelector(".db-pipelines-tags-change")!.Click();
            Assert.Equal("", cut.Find("#pipelines-tags-input").GetAttribute("value"));
            cut.Find("#pipelines-tags-input").Input("ops,  on   call , OPS");
            cut.Find("#pipelines-tags-save").Click();
            var tagged = Assert.Single(_service.Tagged);
            Assert.Equal("def-3", tagged.DefinitionId);
            Assert.Equal(new[] { "ops", "on call" }, tagged.Tags);
            Assert.Equal(new[] { "ops", "on call" }, Row(cut, "Alert on failures").QuerySelectorAll(".db-pipelines-tag:not(.db-pipelines-tags-change)").Select(chip => chip.TextContent.Trim()));
            Assert.Equal("'Alert on failures' is tagged ops, on call.", cut.Find("#pipelines-message").TextContent.Trim());
            Assert.Contains("on call", cut.FindAll("#pipelines-tag-filter .db-pipelines-tag").Select(chip => chip.TextContent.Trim()));

            // A tag that cannot be one is refused here, with the reason; Escape leaves the row as it was.
            Row(cut, "Alert on failures").QuerySelector(".db-pipelines-tags-change")!.Click();
            Assert.Equal("ops, on call", cut.Find("#pipelines-tags-input").GetAttribute("value"));
            cut.Find("#pipelines-tags-input").Input("ops, a/b");
            cut.Find("#pipelines-tags-save").Click();
            Assert.Single(_service.Tagged);
            Assert.StartsWith("'a/b' cannot be a tag", cut.Find("#pipelines-error").TextContent.Trim());
            cut.Find("#pipelines-tags-input").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
            Assert.Empty(cut.FindAll("#pipelines-tags-input"));

            // A duplicate keeps the tags.
            Row(cut, "Nightly report").QuerySelector(".db-pipelines-duplicate")!.Click();
            Assert.Equal("def-copy", _service.Tagged[^1].DefinitionId);
            Assert.Equal(new[] { "billing", "nightly" }, _service.Tagged[^1].Tags);
        }

        // n8n's folders: the list shows one folder at a time - the folders inside it, counted, then its pipelines - with the
        // way back above; a search finds in every folder, each pipeline saying where it is; an author moves a pipeline from its
        // row's More (a folder on the list keeps its spelling, one that cannot be a folder refused before anything is sent); a new
        // folder is opened before anything is in it, and a pipeline created there goes in it; a duplicate goes where its original is.
        [Fact]
        public void Pipelines_are_filed_in_folders_found_moved_and_made_where_the_list_is()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report", IsPublished = true, Version = 1, Folder = "Finance" });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Invoice sync", IsPublished = true, Version = 1, Folder = "Finance/Invoices" });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-3", Name = "Alert on failures", IsPublished = true, Version = 1 });
            _service.Graphs["def-2"] = new PipelineGraph();
            string[] Names(IRenderedComponent<PipelinesPage> page) => page.FindAll(".db-pipelines-table tbody strong").Select(name => name.TextContent.Trim()).ToArray();
            string Text(AngleSharp.Dom.IElement element) => System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();
            string[] Folders(IRenderedComponent<PipelinesPage> page) => page.FindAll("#pipelines-folders li").Select(Text).ToArray();
            AngleSharp.Dom.IElement Row(IRenderedComponent<PipelinesPage> page, string name) =>
                page.FindAll(".db-pipelines-table tbody tr").Single(row => row.QuerySelector("strong")!.TextContent.Trim() == name);

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));

            // At the top: Finance - two pipelines in it, one a folder deeper - and the pipeline at the top. In and back out.
            Assert.Equal(new[] { "Alert on failures" }, Names(cut));
            Assert.Equal(new[] { "Finance 2 pipelines" }, Folders(cut));
            cut.Find("#pipelines-folders .db-pipelines-folder").Click();
            Assert.Equal(new[] { "Nightly report" }, Names(cut));
            Assert.Equal("All pipelines / Finance", Text(cut.Find("#pipelines-folder-path")));
            Assert.Equal(new[] { "Invoices 1 pipeline" }, Folders(cut));
            cut.Find("#pipelines-folders .db-pipelines-folder").Click();
            Assert.Equal(new[] { "Invoice sync" }, Names(cut));
            cut.Find("#pipelines-folder-top").Click();
            Assert.Equal(new[] { "Alert on failures" }, Names(cut));

            // A search finds in every folder, each pipeline saying where it is - and going there.
            cut.Find("#pipelines-search").Input("sync");
            Assert.Equal(new[] { "Invoice sync" }, Names(cut));
            Assert.Equal("in Finance/Invoices", Text(Row(cut, "Invoice sync").QuerySelector(".db-pipelines-in-folder")!));
            Row(cut, "Invoice sync").QuerySelector(".db-pipelines-in-folder button")!.Click();
            Assert.Equal("", cut.Find("#pipelines-search").GetAttribute("value") ?? "");
            Assert.Equal("All pipelines / Finance / Invoices", Text(cut.Find("#pipelines-folder-path")));

            // A duplicate goes where its original is.
            Row(cut, "Invoice sync").QuerySelector(".db-pipelines-duplicate")!.Click();
            Assert.Equal(("def-copy", "Finance/Invoices"), _service.Moved[^1]);

            // Moved from its row's More: one that cannot be a folder is refused here; a folder on the list keeps its spelling.
            cut.Find("#pipelines-folder-top").Click();
            Row(cut, "Alert on failures").QuerySelector(".db-pipelines-move")!.Click();
            Assert.Equal("", cut.Find("#pipelines-folder-input").GetAttribute("value") ?? "");
            var moves = _service.Moved.Count;
            cut.Find("#pipelines-folder-input").Input("finance/Q1|Q2");
            cut.Find("#pipelines-folder-move").Click();
            Assert.StartsWith("'Q1|Q2' cannot be a folder's name", cut.Find("#pipelines-error").TextContent.Trim());
            Assert.Equal(moves, _service.Moved.Count);
            cut.Find("#pipelines-folder-input").Input("FINANCE");
            cut.Find("#pipelines-folder-input").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
            Assert.Equal(("def-3", "Finance"), _service.Moved[^1]);
            Assert.Equal("'Alert on failures' is in Finance now.", cut.Find("#pipelines-message").TextContent.Trim());
            Assert.Empty(Names(cut));
            Assert.Equal(new[] { "Finance 3 pipelines" }, Folders(cut));

            // A new folder, opened before anything is in it; a pipeline created there goes in it.
            cut.Find("#pipelines-folder-new").Input("On call");
            cut.Find("#pipelines-folder-new-open").Click();
            Assert.Equal("All pipelines / On call", Text(cut.Find("#pipelines-folder-path")));
            Assert.NotNull(cut.Find("#pipelines-folder-empty"));
            cut.Find("#pipelines-new-open").Click();
            cut.Find("#pipelines-new-name").Change("Page the team");
            cut.Find("#pipelines-create").Click();
            cut.WaitForAssertion(() => Assert.Equal(("def-new", "On call"), _service.Moved[^1]));
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Page the team" }, Names(cut)));
            Assert.Equal("Created 'Page the team' as a draft, in On call.", cut.Find("#pipelines-message").TextContent.Trim());
        }

        // A folder as one file and back: the pipelines in it and in the folders inside it exported with their ids and their
        // folders below it; imported into a folder, each goes below it, and once all are made, what they name of each other names
        // the new pipelines - a pipeline the file does not hold left as named.
        [Fact]
        public void A_folder_is_exported_as_one_file_and_imported_with_its_folders_and_its_calls_among_them()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Charge", Description = "Charges a card", IsPublished = true, Version = 1, Folder = "Billing" });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Notify", IsPublished = true, Version = 1, Folder = "Billing/Mail" });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-3", Name = "Elsewhere", IsPublished = true, Version = 1 });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-4", Name = "Set aside", IsPublished = false, Version = 1, Folder = "Billing", ArchivedOnUtc = new DateTime(2026, 10, 8) });
            _service.Graphs["def-1"] = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.ExecutePipeline", Inputs = { ["PipelineDefinitionId"] = "def-2" } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.ExecutePipeline", Inputs = { ["PipelineDefinitionId"] = "def-3" } },
                },
            };
            _service.Graphs["def-2"] = new PipelineGraph { CalledBy = "def-1" };
            var download = JSInterop.SetupVoid("stommePipelineEditorDownload", _ => true);
            download.SetVoidResult();

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Equal("Export all", cut.Find("#pipelines-folder-export").TextContent.Trim());
            cut.FindAll("#pipelines-folders .db-pipelines-folder").Single(folder => folder.TextContent.Trim() == "Billing").Click();
            Assert.Equal("Export folder", cut.Find("#pipelines-folder-export").TextContent.Trim());
            cut.Find("#pipelines-folder-export").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("Exported 2 pipelines as billing.pipelines.json", cut.Find("#pipelines-message").TextContent.Trim()));
            var saved = Assert.Single(download.Invocations).Arguments;
            Assert.Equal("billing.pipelines.json", saved[0]);
            var file = (string)saved[1]!;
            var written = PipelineExchange.ReadMany(file);
            Assert.Equal(new[] { ("def-1", "Charge", (string?)null), ("def-2", "Notify", "Mail") }, written.Select(document => (document.Id!, document.Name!, document.Folder)).ToArray()); // the archived one stays

            // Imported into a folder of its own: below it, each where it was below Billing; then the calls among them repointed.
            cut.Find("#pipelines-folder-new").Input("Copied");
            cut.Find("#pipelines-folder-new-open").Click();
            cut.Find("#pipelines-new-open").Click();
            cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText(file, "billing.pipelines.json"));
            cut.WaitForAssertion(() => Assert.Equal("Imported 2 of 2 pipelines as drafts - what they named of each other now names the new pipelines (2 places).", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal(new[] { ("def-copy", "Billing/Copied"), ("def-copy-2", "Billing/Copied/Mail") }, _service.Moved.TakeLast(2).ToArray()); // opened inside Billing
            var charge = _service.SavedGraphs.Single(saved => saved.DefinitionId == "def-copy").Graph;
            Assert.Equal(new[] { "def-copy-2", "def-3" }, charge.Steps.Select(step => step.Inputs["PipelineDefinitionId"]));
            Assert.Equal("def-copy", _service.SavedGraphs.Single(saved => saved.DefinitionId == "def-copy-2").Graph.CalledBy);
        }

        // The map: the pipelines that run or are run by another drawn, each a link to its editor, each line its kind; the
        // ones linked to none counted, not drawn; nothing linked, said so; closed.
        [Fact]
        public void The_map_draws_which_pipeline_runs_which()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "a", Name = "Orders", IsPublished = true, Version = 1 });
            _service.Map = new PipelineMapView
            {
                Nodes =
                {
                    new PipelineMapNode { DefinitionId = "a", Name = "Orders", IsPublished = true },
                    new PipelineMapNode { DefinitionId = "b", Name = "Bill", Folder = "Money", IsPublished = true },
                    new PipelineMapNode { DefinitionId = "d", Name = "On bill failure", IsPublished = false },
                    new PipelineMapNode { DefinitionId = "z", Name = "Alone", IsPublished = true },
                    new PipelineMapNode { DefinitionId = "e", Name = "On any failure", IsPublished = true, WatchesEvery = true },
                },
                Links = { new PipelineMapLink { From = "a", To = "b", Kind = PipelineLinks.Calls }, new PipelineMapLink { From = "b", To = "d", Kind = PipelineLinks.OnFailure } },
            };

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            cut.Find("#pipelines-map-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-map-drawing")));
            var drawn = cut.FindAll(".db-pipelines-map__node").Select(node => node.GetAttribute("data-pipeline")).ToArray();
            Assert.Equal(new[] { "b", "e", "d", "a" }.OrderBy(id => id), drawn.OrderBy(id => id)); // "Alone" is not drawn
            Assert.Equal("1 pipeline runs no other and is run by none - not drawn.", cut.Find("#pipelines-map-alone").TextContent.Trim().TrimStart('·').Trim());
            Assert.Equal(new[] { "db-pipelines-map__link is-calls", "db-pipelines-map__link is-on-failure" }, cut.FindAll(".db-pipelines-map__link").Select(line => line.GetAttribute("class")).ToArray());
            Assert.Equal("Orders calls Bill", cut.FindAll(".db-pipelines-map__link title")[0].TextContent);
            Assert.Equal("draft", cut.FindAll(".db-pipelines-map__node").Single(node => node.GetAttribute("data-pipeline") == "d").QuerySelector(".db-pipelines-map__note")!.TextContent);
            Assert.Equal("on any failure", cut.FindAll(".db-pipelines-map__node").Single(node => node.GetAttribute("data-pipeline") == "e").QuerySelector(".db-pipelines-map__note")!.TextContent);
            Assert.EndsWith("definition=b", cut.FindAll("#pipelines-map-drawing a").Select(link => link.GetAttribute("href")).Single(href => href!.EndsWith("=b")));
            // The one called is to the right of the one calling it.
            double X(string id) => double.Parse(cut.FindAll(".db-pipelines-map__node").Single(node => node.GetAttribute("data-pipeline") == id).QuerySelector("rect")!.GetAttribute("x")!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(X("b") > X("a") && X("d") > X("b"));

            cut.Find("#pipelines-map-toggle").Click();
            Assert.Empty(cut.FindAll("#pipelines-map"));
            _service.Map = new PipelineMapView { Nodes = { new PipelineMapNode { DefinitionId = "z", Name = "Alone" } } };
            cut.Find("#pipelines-map-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-map-empty")));
        }

        // Find in every pipeline: offered once the search has two letters, the places listed with their pipeline, folder,
        // where and what, each a link to the editor at its step (a note or a variable: the pipeline alone); closed.
        [Fact]
        public void A_text_is_found_inside_the_pipelines_and_each_place_links_to_its_step()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Charge", IsPublished = true, Version = 1 });
            _service.Found.Add(new PipelineFoundView { DefinitionId = "def-1", PipelineName = "Charge", Folder = "Billing", StepId = "fetch", Where = "step 'Fetch', input 'Url'", Snippet = "https://shop/api/orders" });
            _service.Found.Add(new PipelineFoundView { DefinitionId = "def-1", PipelineName = "Charge", StepId = null, Where = "variable 'orders'", Snippet = "orders" });

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            cut.Find("#pipelines-search").Input("o");
            Assert.Empty(cut.FindAll("#pipelines-search-inside"));
            cut.Find("#pipelines-search").Input("orders");
            cut.Find("#pipelines-search-inside").Click();
            cut.WaitForAssertion(() => Assert.Equal("'orders' is used in 2 places:", cut.Find("#pipelines-found-count").TextContent.Trim()));
            Assert.Equal(new[] { "orders" }, _service.FindQueries);
            var places = cut.FindAll("#pipelines-found li");
            Assert.Equal("Charge (in Billing) - step 'Fetch', input 'Url': https://shop/api/orders", System.Text.RegularExpressions.Regex.Replace(places[0].TextContent, @"\s+", " ").Trim());
            Assert.EndsWith("definition=def-1&step=fetch", places[0].QuerySelector("a")!.GetAttribute("href"));
            Assert.EndsWith("definition=def-1", places[1].QuerySelector("a")!.GetAttribute("href"));

            cut.Find("#pipelines-found-close").Click();
            Assert.Empty(cut.FindAll("#pipelines-found"));
            _service.Found.Clear();
            cut.Find("#pipelines-search-inside").Click();
            cut.WaitForAssertion(() => Assert.Equal("'orders' is not used in any pipeline's steps, notes or variables.", cut.Find("#pipelines-found-count").TextContent.Trim()));
        }

        // Pipelines chosen on the list and done to at once: moved to a folder, tagged (one already full refused, the rest
        // done), exported as one file, archived after being asked - each one's own call, the list read again after.
        [Fact]
        public void Pipelines_chosen_on_the_list_are_moved_tagged_exported_and_archived_at_once()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Charge", IsPublished = true, Version = 1 });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Notify", IsPublished = true, Version = 1, Tags = Enumerable.Range(1, PipelineTags.MaxTags).Select(n => "t" + n).ToList() });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-3", Name = "Report", IsPublished = true, Version = 1 });
            _service.Graphs["def-1"] = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.ExecutePipeline", Inputs = { ["PipelineDefinitionId"] = "def-2" } } } };
            var download = JSInterop.SetupVoid("stommePipelineEditorDownload", _ => true);
            download.SetVoidResult();
            AngleSharp.Dom.IElement Box(IRenderedComponent<PipelinesPage> page, string name) =>
                page.FindAll(".db-pipelines-table tbody tr").Single(row => row.QuerySelector("strong")!.TextContent.Trim() == name).QuerySelector(".db-pipelines-choose")!;

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Empty(cut.FindAll("#pipelines-chosen")); // nothing chosen, nothing offered
            Box(cut, "Charge").Change(true);
            Box(cut, "Notify").Change(true);
            Assert.Equal("2 pipelines chosen", cut.Find("#pipelines-chosen-count").TextContent.Trim());

            // Exported as one file, with their ids.
            cut.Find("#pipelines-chosen-export").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("Exported 2 pipelines as chosen.pipelines.json", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal(new[] { "def-1", "def-2" }, PipelineExchange.ReadMany((string)download.Invocations.Single().Arguments[1]!).Select(document => document.Id));

            // Tagged: the one with every tag it may have is refused, saying why; the other is tagged and no longer chosen.
            cut.Find("#pipelines-chosen-tag-open").Click();
            cut.Find("#pipelines-chosen-tag").Input("nightly");
            cut.Find("#pipelines-chosen-tag-add").Click();
            cut.WaitForAssertion(() => Assert.Equal("Tagged 1 pipeline nightly.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal($"Not done: 'Notify' (A pipeline takes at most {PipelineTags.MaxTags} tags.).", cut.Find("#pipelines-error").TextContent.Trim());
            Assert.Equal("def-1", _service.Tagged.Single().DefinitionId);
            Assert.Equal(new[] { "nightly" }, _service.Tagged.Single().Tags);
            Assert.Equal("1 pipeline chosen", cut.Find("#pipelines-chosen-count").TextContent.Trim()); // Notify, still to do

            // Every one shown chosen, moved to a folder.
            cut.Find("#pipelines-choose-all").Change(true);
            Assert.Equal("3 pipelines chosen", cut.Find("#pipelines-chosen-count").TextContent.Trim());
            cut.Find("#pipelines-chosen-move-open").Click();
            cut.Find("#pipelines-chosen-folder").Input("Billing");
            cut.Find("#pipelines-chosen-move").Click();
            cut.WaitForAssertion(() => Assert.Equal("Moved 3 pipelines to Billing.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal(new[] { ("def-1", "Billing"), ("def-2", "Billing"), ("def-3", "Billing") }, _service.Moved.ToArray());
            Assert.Empty(cut.FindAll("#pipelines-chosen"));

            // In the folder, two archived - asked first.
            cut.FindAll("#pipelines-folders .db-pipelines-folder").Single().Click();
            Box(cut, "Charge").Change(true);
            Box(cut, "Report").Change(true);
            cut.Find("#pipelines-chosen-archive").Click();
            Assert.StartsWith("Archive these 2?", cut.Find("#pipelines-chosen-archive-what").TextContent.Trim());
            Assert.Empty(_service.ArchiveCalls);
            cut.Find("#pipelines-chosen-archive-confirm").Click();
            cut.WaitForAssertion(() => Assert.Equal("Archived 2 pipelines: they do not run. Restore, or delete for good, under Archived.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal(new[] { "archive def-1", "archive def-3" }, _service.ArchiveCalls);
            Assert.Equal(new[] { "Notify" }, cut.FindAll(".db-pipelines-table tbody strong").Select(name => name.TextContent.Trim()).ToArray());
        }

        // The site's pipeline activity on the list: who did what to which pipeline, and when - each pipeline and run a link,
        // the site's own lines (its variables) with no pipeline; nothing recorded, said so; closed.
        [Fact]
        public void The_activity_says_who_did_what_to_which_pipeline_and_when()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Orders", IsPublished = true, Version = 1 });
            _service.Audit.Add(new PipelineAuditEntryView { OccurredOnUtc = new DateTime(2026, 10, 9, 3, 24, 9, DateTimeKind.Utc), UserName = "ada", Action = "started a run", DefinitionId = "def-1", PipelineName = "Orders", RunId = "run-9" });
            _service.Audit.Add(new PipelineAuditEntryView { OccurredOnUtc = new DateTime(2026, 10, 9, 3, 20, 0, DateTimeKind.Utc), UserName = "bo", Action = "changed the site's variables", Detail = "added region" });
            static string Text(AngleSharp.Dom.IElement element) => System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-activity-toggle")));
            cut.Find("#pipelines-activity-toggle").Click();
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#pipelines-activity-table tbody tr").Count));
            var rows = cut.FindAll("#pipelines-activity-table tbody tr");
            Assert.Equal("2026-10-09 03:24:09 ada started a run Orders The run", Text(rows[0]));
            Assert.EndsWith("definition=def-1", rows[0].QuerySelectorAll("a")[0].GetAttribute("href"));
            Assert.EndsWith("id=run-9", rows[0].QuerySelectorAll("a")[1].GetAttribute("href"));
            Assert.Equal("2026-10-09 03:20:00 bo changed the site's variables - added region", Text(rows[1]));
            Assert.Empty(rows[1].QuerySelectorAll("a")); // the site's own: no pipeline, no run

            cut.Find("#pipelines-activity-toggle").Click();
            Assert.Empty(cut.FindAll("#pipelines-activity"));
            _service.Audit.Clear();
            cut.Find("#pipelines-activity-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-activity-none")));
        }

        // Before a pipeline others run stops running: retracted or archived, what stops working is said first and done only
        // when confirmed; one nothing runs is done at once; chosen ones are told as the choice changes - those leaving together
        // breaking nothing among themselves.
        [Fact]
        public void Archiving_or_retracting_a_pipeline_others_run_says_first_what_stops_working()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Bill", IsPublished = true, Version = 1 });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Orders", IsPublished = true, Version = 1 });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-3", Name = "Lookup", IsPublished = true, Version = 1 });
            _service.Map = new PipelineMapView
            {
                Nodes = { new PipelineMapNode { DefinitionId = "def-1", Name = "Bill" }, new PipelineMapNode { DefinitionId = "def-2", Name = "Orders" }, new PipelineMapNode { DefinitionId = "def-3", Name = "Lookup", IsTool = true } },
                Links = { new PipelineMapLink { From = "def-2", To = "def-1", Kind = PipelineLinks.Calls }, new PipelineMapLink { From = "def-2", To = "def-3", Kind = PipelineLinks.AsTool } },
            };
            AngleSharp.Dom.IElement Row(IRenderedComponent<PipelinesPage> page, string name) =>
                page.FindAll(".db-pipelines-table tbody tr").Single(row => row.QuerySelector("strong")!.TextContent.Trim() == name);
            string[] Said(IRenderedComponent<PipelinesPage> page, string list) => page.FindAll(list + " li").Select(line => line.TextContent.Trim()).ToArray();

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-pipelines-table tbody tr").Count));

            // Retracting one another pipeline calls: said, not done; cancelled, nothing done.
            Row(cut, "Bill").QuerySelector(".db-pipelines-retract")!.Click();
            cut.WaitForAssertion(() => Assert.Equal("Retract 'Bill'? It will not run, and:", cut.Find("#pipelines-leaving-what").TextContent.Trim()));
            Assert.Equal(new[] { "'Orders' calls 'Bill' - that call will fail." }, Said(cut, "#pipelines-leaving-list"));
            Assert.Equal("Retract anyway", cut.Find("#pipelines-leaving-confirm").TextContent.Trim());
            Assert.Empty(_service.ArchiveCalls);
            cut.Find("#pipelines-leaving-cancel").Click();
            Assert.Empty(cut.FindAll("#pipelines-leaving"));
            Assert.Empty(_service.ArchiveCalls);

            // Asked again and confirmed: done, said, the question gone.
            Row(cut, "Bill").QuerySelector(".db-pipelines-retract")!.Click();
            cut.WaitForAssertion(() => cut.Find("#pipelines-leaving-confirm").Click());
            cut.WaitForAssertion(() => Assert.Equal(new[] { "retract def-1" }, _service.ArchiveCalls));
            cut.WaitForAssertion(() => Assert.Equal("Retracted 'Bill'.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-leaving"));

            // Chosen to archive: what stops working said as the choice changes - all three leaving together break nothing.
            Row(cut, "Bill").QuerySelector(".db-pipelines-choose")!.Change(true);
            Row(cut, "Lookup").QuerySelector(".db-pipelines-choose")!.Change(true);
            cut.Find("#pipelines-chosen-archive").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "'Orders' calls 'Bill' - that call will fail.", "'Orders' may use 'Lookup' as an AI tool - it will not be offered." }, Said(cut, "#pipelines-chosen-archive-left")));
            Assert.Equal("Archive anyway", cut.Find("#pipelines-chosen-archive-confirm").TextContent.Trim());
            Row(cut, "Orders").QuerySelector(".db-pipelines-choose")!.Change(true);
            Assert.Empty(cut.FindAll("#pipelines-chosen-archive-left"));
            Assert.Equal("Archive", cut.Find("#pipelines-chosen-archive-confirm").TextContent.Trim());
            cut.FindAll("#pipelines-chosen button").Single(button => button.TextContent.Trim() == "Cancel").Click();
            cut.Find("#pipelines-chosen-clear").Click();
            Assert.Empty(_service.ArchiveCalls.Where(call => call.StartsWith("archive", StringComparison.Ordinal)));

            // One nothing runs is archived at once, as before.
            Row(cut, "Orders").QuerySelector(".db-pipelines-archive")!.Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "retract def-1", "archive def-2" }, _service.ArchiveCalls));
            Assert.Empty(cut.FindAll("#pipelines-leaving"));
        }

        // n8n's folder rename, move and delete, on the folder shown: renamed or moved as a path, what is in it going along;
        // one that cannot be refused before anything is sent; removed, what was in it moves up - the list showing where it went.
        [Fact]
        public void A_folder_is_renamed_moved_and_removed_from_the_list()
        {
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "Nightly report", IsPublished = true, Version = 1, Folder = "Finance" });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-2", Name = "Invoice sync", IsPublished = true, Version = 1, Folder = "Finance/Invoices" });
            _service.Pipelines.Add(new PipelineView { DefinitionId = "def-3", Name = "Alert on failures", IsPublished = true, Version = 1, Folder = "Ops" });
            string[] Names(IRenderedComponent<PipelinesPage> page) => page.FindAll(".db-pipelines-table tbody strong").Select(name => name.TextContent.Trim()).ToArray();
            string Text(AngleSharp.Dom.IElement element) => System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();

            var cut = Render<PipelinesPage>(parameters => parameters.AddCascadingValue(RunsPageState()).AddCascadingValue(Module()));
            Assert.Empty(cut.FindAll("#pipelines-folder-actions")); // the top of the list is no folder to rename
            cut.FindAll("#pipelines-folders .db-pipelines-folder").Single(folder => folder.TextContent.Trim() == "Finance").Click();

            // Renamed: what is in it - its folder inside too - goes along, and the list shows it where it went.
            cut.Find("#pipelines-folder-rename").Click();
            Assert.Equal("Finance", cut.Find("#pipelines-folder-rename-input").GetAttribute("value"));
            cut.Find("#pipelines-folder-rename-input").Input("Ops");
            cut.Find("#pipelines-folder-rename-apply").Click();
            Assert.Equal("There is a folder 'Ops' already.", cut.Find("#pipelines-error").TextContent.Trim());
            Assert.Empty(_service.FolderMoves); // refused before anything was sent
            cut.Find("#pipelines-folder-rename-input").Input("Money");
            cut.Find("#pipelines-folder-rename-input").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("'Finance' is Money now, with its 2 pipelines.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Equal(("Finance", "Money", false), (_service.FolderMoves[^1].Folder, _service.FolderMoves[^1].NewFolder, _service.FolderMoves[^1].Remove));
            Assert.Equal("All pipelines / Money", Text(cut.Find("#pipelines-folder-path")));
            Assert.Equal(new[] { "Nightly report" }, Names(cut));
            Assert.Equal(new[] { "Invoices 1 pipeline" }, cut.FindAll("#pipelines-folders li").Select(Text).ToArray());

            // Moved as a path, into another folder.
            cut.Find("#pipelines-folder-rename").Click();
            cut.Find("#pipelines-folder-rename-input").Input("Ops/Money");
            cut.Find("#pipelines-folder-rename-apply").Click();
            cut.WaitForAssertion(() => Assert.Equal("All pipelines / Ops / Money", Text(cut.Find("#pipelines-folder-path"))));
            Assert.Equal("Ops/Money/Invoices", _service.Pipelines.Single(pipeline => pipeline.DefinitionId == "def-2").Folder);

            // Removed - said first, then confirmed: what was in it moves up to Ops; nothing is deleted.
            cut.Find("#pipelines-folder-remove").Click();
            Assert.Equal("Remove 'Money'? Its 2 pipelines move to Ops - none is deleted.", Text(cut.Find("#pipelines-folder-remove-what")));
            cut.Find("#pipelines-folder-remove-cancel").Click();
            Assert.Equal(2, _service.FolderMoves.Count);
            cut.Find("#pipelines-folder-remove").Click();
            cut.Find("#pipelines-folder-remove-confirm").Click();
            cut.WaitForAssertion(() => Assert.Equal("'Money' is removed; its 2 pipelines are in Ops now.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.True(_service.FolderMoves[^1].Remove);
            Assert.Equal("All pipelines / Ops", Text(cut.Find("#pipelines-folder-path")));
            Assert.Equal(new[] { "Alert on failures", "Nightly report" }, Names(cut).OrderBy(name => name).ToArray());
            Assert.Equal(3, _service.Pipelines.Count); // none deleted
        }

        private static PipelineApprovalView Waiting(string id, string runId, DateTime at) => new()
        {
            Id = id, RunId = runId, DefinitionId = "def-1", PipelineName = "Pay invoices", StepId = "ask", StepName = "Manager OK",
            Question = "Pay invoice 42?", Details = "Amount: 1200 EUR", RequestedOnUtc = at, ExpiresOnUtc = at.AddDays(2),
        };

        // How long a run took, as the list says it - and an unfinished one is running.
        [Theory]
        [InlineData(17, "17 ms")]
        [InlineData(2_340, "2.3 s")]
        [InlineData(252_000, "4 min 12 s")]
        [InlineData(3_780_000, "1 h 3 min")]
        public void A_runs_time_is_said_at_its_scale(int milliseconds, string expected)
        {
            var start = new DateTime(2026, 10, 6, 7, 0, 0, DateTimeKind.Utc);
            Assert.Equal(expected, PipelineRunDurations.Of(new PipelineRunView { CreatedOnUtc = start, FinishedOnUtc = start.AddMilliseconds(milliseconds) }));
            Assert.Equal("running", PipelineRunDurations.Of(new PipelineRunView { CreatedOnUtc = start }));
        }

        private static PageState RunsPageState(string state = null) => new()
        {
            RenderMode = RenderModes.Interactive, // as the admin page renders - an ActionDialog asks in place
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 1, SiteId = 1, Path = "admin/pipelines", Name = "Pipelines" },
            User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Admin + ";" },
            QueryString = state is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["state"] = state },
            UrlParameters = "",
        };

        /// <summary>A clock whose timers fire only when the test says so.</summary>
        private sealed class ManualClock : TimeProvider
        {
            public List<ManualTimer> Timers { get; } = new();

            public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
            {
                var timer = new ManualTimer(callback, state, dueTime);
                Timers.Add(timer);
                return timer;
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object state, TimeSpan dueTime) : ITimer
        {
            public bool Armed { get; private set; } = dueTime != Timeout.InfiniteTimeSpan;

            public bool Disposed { get; private set; }

            /// <summary>What the timer would do when its time came: run the callback once (the page re-arms it).</summary>
            public void Fire()
            {
                Assert.True(Armed && !Disposed, "the page's timer is not armed");
                Armed = false;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Armed = !Disposed && dueTime != Timeout.InfiniteTimeSpan;
                return !Disposed;
            }

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Page(bool started)
        {
            var query = new Dictionary<string, string> { ["id"] = "run-1" };
            if (started)
            {
                query["started"] = "1";
            }

            return new PageState
            {
                Alias = Alias(),
                Site = new Site { SiteId = 1, Name = "Site" },
                Page = new Page { PageId = 1, SiteId = 1, Path = "admin/pipelines", Name = "Pipelines" },
                User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Admin + ";" },
                QueryString = query,
                UrlParameters = "",
            };
        }

        /// <summary>The key rows as the module definition seeds them - every key to Administrators (but one, when a test says).</summary>
        private static ModuleModel Module(string without = null) => new()
        {
            ModuleId = 32,
            SiteId = 1,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(PipelinesPermissions.Declarations))
                .Where(key => key.Key != without)
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Registered, true) })
                .ToList(),
        };

        private sealed class FakePipelinesService : IPipelinesService
        {
            private int _runReads;

            /// <summary>How many reads find no run before the engine "stores" it.</summary>
            public int MissingReads { get; set; }

            public int RunReads => Volatile.Read(ref _runReads);

            /// <summary>The run as the server answers it, when a test says; else a bare finished run.</summary>
            public PipelineRunView Shown { get; set; }

            public List<string> Retried { get; } = new();

            public Task<PipelineRunView> GetRunAsync(string runId)
            {
                var read = Interlocked.Increment(ref _runReads);
                return Task.FromResult(read <= MissingReads
                    ? null
                    : Shown ?? new PipelineRunView { Id = runId, Version = 1, Status = "Finished", SubStatus = "Finished", CreatedOnUtc = DateTime.UtcNow, FinishedOnUtc = DateTime.UtcNow });
            }

            public Task<PipelineRunStarted> RetryRunAsync(string runId, bool onItsVersion = false)
            {
                Retried.Add(onItsVersion ? runId + " on its version" : runId);
                return Task.FromResult(new PipelineRunStarted { RunId = "run-2" });
            }

            public List<string> RetriedFromFailure { get; } = new();

            public Task<List<PipelineCallerView>> GetCallersAsync(string definitionId) => Task.FromResult(Callers.ToList());
            public List<PipelineCallerView> Callers { get; } = new();
            public Task<PipelineRunStarted> RetryFromFailedStepAsync(string runId)
            {
                RetriedFromFailure.Add(runId);
                return Task.FromResult(new PipelineRunStarted { RunId = "run-3" });
            }

            public Task<List<PipelineRunLogLine>> GetRunLogAsync(string runId) =>
                Task.FromResult(new List<PipelineRunLogLine> { new() { Sequence = 0, TimestampUtc = DateTime.UtcNow, StepType = "Stomme.Pipeline", EventName = "Started" } });

            public bool AskedForLastRuns { get; private set; }

            public Task<List<PipelineView>> GetPipelinesAsync(bool withLastRuns = false)
            {
                AskedForLastRuns |= withLastRuns;
                return Task.FromResult(Pipelines.ToList());
            }
            public Task<PipelineDetail> GetPipelineAsync(string definitionId) => Task.FromResult<PipelineDetail>(null);

            public List<PipelineEvaluationView> Evaluations { get; } = new();

            public List<(string DefinitionId, PipelineEvaluationRequest Request)> EvaluationsStarted { get; } = new();

            public Task<List<PipelineEvaluationView>> GetEvaluationsAsync(string definitionId) =>
                Task.FromResult(Evaluations.Where(evaluation => evaluation.DefinitionId == definitionId).ToList());

            public Task<PipelineEvaluationView> GetEvaluationAsync(string evaluationId) =>
                Task.FromResult(Evaluations.FirstOrDefault(evaluation => evaluation.Id == evaluationId));

            public Task<PipelineEvaluationView> StartEvaluationAsync(string definitionId, PipelineEvaluationRequest request)
            {
                if (Evaluations.Any(evaluation => evaluation.DefinitionId == definitionId && evaluation.State == PipelineEvaluations.Running))
                {
                    throw new InvalidOperationException("An evaluation of this pipeline is still running - wait for it to end, or cancel it.");
                }

                EvaluationsStarted.Add((definitionId, request));
                var started = new PipelineEvaluationView
                {
                    Id = "eval-" + (Evaluations.Count + 1),
                    DefinitionId = definitionId,
                    TableId = request.TableId,
                    TableName = SiteTables.FirstOrDefault(table => table.Id == request.TableId)?.Name,
                    Version = 1,
                    StartedBy = "user7",
                    StartedOnUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
                    RowCount = 2,
                    Rows =
                    {
                        new PipelineEvaluationRowView { RowId = "r1", Values = { ["question"] = "2+2", ["expected"] = "4" } },
                        new PipelineEvaluationRowView { RowId = "r2", Values = { ["question"] = "3+3", ["expected"] = "6" } },
                    },
                };
                Evaluations.Insert(0, started);
                return Task.FromResult(started);
            }

            public Task<PipelineEvaluationView> CancelEvaluationAsync(string evaluationId)
            {
                var evaluation = Evaluations.First(each => each.Id == evaluationId);
                evaluation.State = PipelineEvaluations.Cancelled;
                return Task.FromResult(evaluation);
            }

            public Task DeleteEvaluationAsync(string evaluationId)
            {
                Evaluations.RemoveAll(evaluation => evaluation.Id == evaluationId);
                return Task.CompletedTask;
            }
            public Task<PipelineView> CreatePipelineAsync(CreatePipelineRequest request)
            {
                var created = new PipelineView { Id = "v-new", DefinitionId = "def-new", Name = request.Name, Description = request.Description, Version = 1, IsLatest = true };
                Pipelines.Add(created);
                return Task.FromResult(created);
            }

            /// <summary>What was imported, in order - a duplicate is an import of the graph under a new name.</summary>
            public List<ImportPipelineRequest> Imported { get; } = new();

            /// <summary>The graph each pipeline answers with, when a test says.</summary>
            public Dictionary<string, PipelineGraph> Graphs { get; } = new();

            public Task<PipelineView> ImportPipelineAsync(ImportPipelineRequest request)
            {
                Imported.Add(request);
                var id = Imported.Count == 1 ? "def-copy" : "def-copy-" + Imported.Count; // each import a pipeline of its own
                return Task.FromResult(new PipelineView { DefinitionId = id, Name = request.Name, Description = request.Description });
            }

            /// <summary>Each graph saved, in order.</summary>
            public List<(string DefinitionId, PipelineGraph Graph)> SavedGraphs { get; } = new();

            public Task<TriggerInfoView> GetTriggerInfoAsync(string type, string value, string zone = null) => throw new NotSupportedException();

            public Task<WebhookCallView> ListenForWebhookAsync(string name, int seconds) => throw new NotSupportedException();
            public Task<PipelineTestEventView> FetchNewestEmailAsync(string connection, string mailbox) => throw new NotSupportedException();

            public Task<PipelineWebhookSecretView> RenewWebhookSecretAsync(string name) => throw new NotSupportedException();

            public Task RemoveWebhookSecretAsync(string name) => throw new NotSupportedException();

            public Task<PipelineCapabilitiesView> GetCapabilitiesAsync() => Task.FromResult(new PipelineCapabilitiesView());

            public Task<ScriptPreviewView> PreviewScriptAsync(ScriptPreviewRequest request) => throw new NotSupportedException();

            public Task<PipelineScriptView> WriteScriptAsync(PipelineScriptRequest request) => throw new NotSupportedException();

            public Task<PipelineScriptCheckView> CheckScriptsAsync(PipelineScriptCheckRequest request) =>
                Task.FromResult(new PipelineScriptCheckView { Problems = request.Scripts.Select(_ => (string)null!).ToList() });

            public Task<List<PipelineStepCommentView>> GetStepCommentsAsync(string definitionId) => Task.FromResult(new List<PipelineStepCommentView>());
            public Task<List<PipelineMentionView>> GetMentionablePeopleAsync() => Task.FromResult(new List<PipelineMentionView>());
            public Task<Dictionary<string, int>> GetOpenCommentCountsAsync() => Task.FromResult(new Dictionary<string, int>());
            public Task<PipelineStepCommentView> AddStepCommentAsync(string definitionId, PipelineStepCommentRequest request) => throw new NotSupportedException();
            public Task<PipelineStepCommentView> SetStepCommentResolvedAsync(string definitionId, System.Guid id, bool resolved) => throw new NotSupportedException();
            public Task<bool> DeleteStepCommentAsync(string definitionId, System.Guid id) => throw new NotSupportedException();
            public Task<PipelineGraphDesignView> DesignGraphAsync(PipelineGraphDesignRequest request) => throw new NotSupportedException();
            public Task<PipelineView> UpdatePipelineAsync(string definitionId, UpdatePipelineRequest request) => throw new NotSupportedException();
            public Task<PublishResult> PublishPipelineAsync(string definitionId, string note = null) => throw new NotSupportedException();
            public Task RetractPipelineAsync(string definitionId)
            {
                ArchiveCalls.Add("retract " + definitionId);
                Pipelines.Single(candidate => candidate.DefinitionId == definitionId).IsPublished = false;
                return Task.CompletedTask;
            }

            /// <summary>The archive's calls, in order ("archive def-1", "restore def-1", "delete def-1", "retract def-1"); each changes the list it answers.</summary>
            public List<string> ArchiveCalls { get; } = new();

            public Task ArchivePipelineAsync(string definitionId)
            {
                ArchiveCalls.Add("archive " + definitionId);
                var pipeline = Pipelines.Single(candidate => candidate.DefinitionId == definitionId);
                (pipeline.ArchivedOnUtc, pipeline.ArchivedBy, pipeline.IsPublished) = (new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc), "host", false);
                return Task.CompletedTask;
            }

            public Task RestorePipelineAsync(string definitionId)
            {
                ArchiveCalls.Add("restore " + definitionId);
                var pipeline = Pipelines.Single(candidate => candidate.DefinitionId == definitionId);
                (pipeline.ArchivedOnUtc, pipeline.ArchivedBy) = (null, null);
                return Task.CompletedTask;
            }

            public Task DeletePipelineAsync(string definitionId)
            {
                ArchiveCalls.Add("delete " + definitionId);
                Pipelines.RemoveAll(candidate => candidate.DefinitionId == definitionId);
                return Task.CompletedTask;
            }

            /// <summary>The tags each pipeline was given, in order of the calls.</summary>
            public List<(string DefinitionId, List<string> Tags)> Tagged { get; } = new();

            public Task<List<AgentToolView>> GetAgentToolsAsync() => Task.FromResult(new List<AgentToolView>());

            /// <summary>The site's variables, as the list page is told; what a save sets.</summary>
            public Dictionary<string, string> SiteVariables { get; set; } = new();

            public Task<Dictionary<string, string>> GetSiteVariablesAsync() => Task.FromResult(new Dictionary<string, string>(SiteVariables));

            public Task<Dictionary<string, string>> SetSiteVariablesAsync(Dictionary<string, string> variables)
            {
                if (variables.Keys.FirstOrDefault(name => !PipelineSiteVariables.IsName(name)) is { } bad)
                {
                    throw new Stomme.Services.ServiceRefusalException($"'{bad}' cannot be a variable's name.", System.Net.HttpStatusCode.BadRequest);
                }

                SiteVariables = new Dictionary<string, string>(variables);
                return Task.FromResult(new Dictionary<string, string>(SiteVariables));
            }

            public List<PipelineSiteTableView> SiteTables { get; set; } = new();

            public Dictionary<string, List<Dictionary<string, string>>> TableRows { get; } = new();

            private int _nextId;

            public Task<List<PipelineSiteTableView>> GetSiteTablesAsync() => Task.FromResult(SiteTables.OrderBy(table => table.Name).ToList());

            public Task<PipelineSiteTableView> CreateSiteTableAsync(PipelineSiteTableRequest request)
            {
                if (!PipelineSiteTables.Check(request.Name, request.Columns, out var error))
                {
                    throw new ArgumentException(error);
                }

                var table = new PipelineSiteTableView { Id = "t" + ++_nextId, Name = request.Name.Trim(), Columns = request.Columns.ToList() };
                SiteTables.Add(table);
                TableRows[table.Id] = new List<Dictionary<string, string>>();
                return Task.FromResult(table);
            }

            public PipelineSiteTableRequest LastTableRequest { get; private set; }

            public Task<PipelineSiteTableView> UpdateSiteTableAsync(string tableId, PipelineSiteTableRequest request)
            {
                LastTableRequest = request;
                if (!PipelineSiteTables.Check(request.Name, request.Columns, out var error))
                {
                    throw new ArgumentException(error);
                }

                var table = SiteTables.Single(table => table.Id == tableId);
                table.Name = request.Name.Trim();
                table.Columns = request.Columns.ToList();
                return Task.FromResult(table);
            }

            public Task DeleteSiteTableAsync(string tableId)
            {
                SiteTables.RemoveAll(table => table.Id == tableId);
                TableRows.Remove(tableId);
                return Task.CompletedTask;
            }

            public (string Search, string Sort, bool Descending) LastRowsRead { get; private set; }

            // The server's search, and a plain sort by text - the server's typed order is PipelineSiteTablesTests'.
            public Task<PipelineTableRowsView> GetTableRowsAsync(string tableId, int skip, int take, string search = null, string sort = null, bool descending = false)
            {
                LastRowsRead = (search, sort, descending);
                if (SiteTables.FirstOrDefault(table => table.Id == tableId) is not { } table)
                {
                    return Task.FromResult<PipelineTableRowsView>(null);
                }

                var rows = TableRows[tableId].Where(row => string.IsNullOrEmpty(search) || row.Values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
                if (!string.IsNullOrEmpty(sort))
                {
                    rows = (descending ? rows.OrderByDescending(row => row.GetValueOrDefault(sort)) : rows.OrderBy(row => row.GetValueOrDefault(sort))).ToList();
                }

                return Task.FromResult(new PipelineTableRowsView { Table = table, Total = rows.Count, Skip = skip, Rows = rows.Skip(skip).Take(take).Select(row => new Dictionary<string, string>(row)).ToList() });
            }

            public Task<Dictionary<string, string>> AddTableRowAsync(string tableId, PipelineTableRowRequest request)
            {
                var row = new Dictionary<string, string> { ["id"] = "r" + ++_nextId };
                Fill(tableId, row, request);
                TableRows[tableId].Add(row);
                return Task.FromResult(new Dictionary<string, string>(row));
            }

            public Task<Dictionary<string, string>> UpdateTableRowAsync(string tableId, string rowId, PipelineTableRowRequest request)
            {
                var row = TableRows[tableId].Single(row => row["id"] == rowId);
                Fill(tableId, row, request);
                return Task.FromResult(new Dictionary<string, string>(row));
            }

            public Task DeleteTableRowAsync(string tableId, string rowId)
            {
                TableRows[tableId].RemoveAll(row => row["id"] == rowId);
                return Task.CompletedTask;
            }

            public List<(string RunId, PipelineRunReview Review)> Reviewed { get; } = new();

            public Task<PipelineRunReview> SetRunReviewAsync(string runId, PipelineRunReview review)
            {
                if (!PipelineRunReviews.Check(review, out var error))
                {
                    throw new ArgumentException(error);
                }

                Reviewed.Add((runId, review));
                return Task.FromResult(new PipelineRunReview { Rating = review.Rating, Tags = review.Tags.ToList(), Note = review.Note, By = "user7", AtUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc) });
            }

            public Task<PipelineTableCsv> ExportTableCsvAsync(string tableId) =>
                Task.FromResult(new PipelineTableCsv
                {
                    FileName = SiteTables.Single(table => table.Id == tableId).Name + ".csv",
                    Csv = "id," + string.Join(",", SiteTables.Single(table => table.Id == tableId).Columns.Select(column => column.Name)) + "\n"
                        + string.Concat(TableRows[tableId].Select(row => row["id"] + "," + string.Join(",", SiteTables.Single(table => table.Id == tableId).Columns.Select(column => row.TryGetValue(column.Name, out var value) ? value : "")) + "\n")),
                });

            /// <summary>The site's pipeline folder, by path: a folder holds null, a file its bytes.</summary>
            public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

            public long MaxFileBytes { get; set; } = 1024;

            public Task<PipelineFolderView> GetPipelineFilesAsync(string folder)
            {
                folder = PipelineFilePaths.Normalize(folder);
                if (folder.Length > 0 && !(Files.TryGetValue(folder, out var held) && held is null))
                {
                    return Task.FromResult<PipelineFolderView>(null);
                }

                var inside = Files.Where(pair => PipelineFilePaths.Parent(pair.Key) == folder).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
                var view = new PipelineFolderView { Folder = folder, MaxFileBytes = MaxFileBytes };
                view.Entries.AddRange(inside.Where(pair => pair.Value is null).Select(pair => FileEntry(pair.Key, null)));
                view.Entries.AddRange(inside.Where(pair => pair.Value is not null).Select(pair => FileEntry(pair.Key, pair.Value)));
                return Task.FromResult(view);
            }

            public async Task<PipelineFileEntryView> UploadPipelineFileAsync(string folder, string name, System.IO.Stream content)
            {
                var path = PipelineFilePaths.Combine(folder, name);
                if (Files.ContainsKey(path))
                {
                    throw new Stomme.Services.ServiceRefusalException($"'{name}' is already in this folder - delete it first, or upload the file under another name.", System.Net.HttpStatusCode.BadRequest);
                }

                using var copy = new System.IO.MemoryStream();
                await content.CopyToAsync(copy);
                Files[path] = copy.ToArray();
                return FileEntry(path, Files[path]);
            }

            public Task<PipelineFileEntryView> CreatePipelineFolderAsync(string path)
            {
                path = PipelineFilePaths.Normalize(path);
                Files[path] = null;
                return Task.FromResult(FileEntry(path, null));
            }

            public Task<bool> DeletePipelineFileAsync(string path)
            {
                var gone = Files.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
                gone.ForEach(key => Files.Remove(key));
                return Task.FromResult(gone.Count > 0);
            }

            private static PipelineFileEntryView FileEntry(string path, byte[] bytes) => new()
            {
                Name = PipelineFilePaths.Name(path),
                Path = path,
                IsFolder = bytes is null,
                Length = bytes?.Length ?? 0,
                ModifiedOnUtc = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc),
            };

            public Task<PipelineTableImportResult> ImportTableCsvAsync(string tableId, string csv)
            {
                var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var header = lines[0].Split(',');
                var rows = lines.Skip(1).Select(line => header.Zip(line.Split(','), (name, value) => (name, value)).ToDictionary(pair => pair.name, pair => pair.value)).ToList();
                foreach (var values in rows)
                {
                    var row = new Dictionary<string, string> { ["id"] = "r" + ++_nextId };
                    Fill(tableId, row, new PipelineTableRowRequest { Values = values });
                    TableRows[tableId].Add(row);
                }

                return Task.FromResult(new PipelineTableImportResult { Imported = rows.Count });
            }

            // The server's conversion, so a value its column cannot hold is refused here as it is there.
            private void Fill(string tableId, Dictionary<string, string> row, PipelineTableRowRequest request)
            {
                var table = SiteTables.Single(table => table.Id == tableId);
                foreach (var (name, text) in request.Values)
                {
                    var column = table.Columns.Single(column => column.Name == name);
                    if (!PipelineSiteTables.TryConvert(text, column.Type, out var converted, out var why))
                    {
                        throw new ArgumentException(column.Name + ": " + why + ".");
                    }

                    row[name] = PipelineSiteTables.Text(converted);
                }
            }

            public Task<List<string>> SetTagsAsync(string definitionId, List<string> tags)
            {
                Tagged.Add((definitionId, tags));
                return Task.FromResult(PipelineTags.Normalize(tags, out _));
            }

            /// <summary>BL-229 - n8n's folders: each move asked for, in order; a folder that cannot be one throws its reason.</summary>
            public List<(string DefinitionId, string Folder)> Moved { get; } = new();

            public Task<string> SetFolderAsync(string definitionId, string folder)
            {
                var kept = PipelineFolders.Normalize(folder, out var error);
                if (error is not null)
                {
                    throw new ArgumentException(error);
                }

                Moved.Add((definitionId, kept));
                if (Pipelines.FirstOrDefault(pipeline => pipeline.DefinitionId == definitionId) is { } moved)
                {
                    moved.Folder = kept; // as the next list reads it
                }

                return Task.FromResult(kept);
            }

            /// <summary>BL-229 - find in every pipeline: what each search asked for, and what it answers.</summary>
            public List<string> FindQueries { get; } = new();

            public List<PipelineFoundView> Found { get; } = new();

            /// <summary>BL-229 - the map the list is answered with.</summary>
            public PipelineMapView Map { get; set; } = new();

            public Task<PipelineMapView> GetMapAsync() => Task.FromResult(Map);

            /// <summary>BL-229: the site's pipeline activity the list is told.</summary>
            public List<PipelineAuditEntryView> Audit { get; } = new();

            public Task<List<PipelineAuditEntryView>> GetAuditAsync(string definitionId = null, int take = PipelineAuditLimits.DefaultTake) =>
                Task.FromResult(Audit.Where(entry => definitionId is null || entry.DefinitionId == definitionId).Take(take).ToList());

            public Task<List<PipelineFoundView>> FindInPipelinesAsync(string text)
            {
                FindQueries.Add(text);
                return Task.FromResult(Found.ToList());
            }

            /// <summary>BL-229 - a folder renamed, moved or removed: each asked for, in order; the pipelines in it move as the server moves them.</summary>
            public List<PipelineFolderMoveRequest> FolderMoves { get; } = new();

            public Task<PipelineFolderMoveResult> MoveFolderAsync(PipelineFolderMoveRequest request)
            {
                FolderMoves.Add(request);
                var inIt = Pipelines.Where(pipeline => PipelineFolders.IsWithin(pipeline.Folder, request.Folder) && !string.IsNullOrEmpty(request.Folder)).ToList();
                if (inIt.Count == 0)
                {
                    return Task.FromResult<PipelineFolderMoveResult>(null);
                }

                var to = request.Remove ? PipelineFolders.Parent(request.Folder) : PipelineFolders.Normalize(request.NewFolder, out _);
                if (!request.Remove && PipelineFolders.MoveProblem(Pipelines.Select(pipeline => pipeline.Folder), request.Folder, to) is { } problem)
                {
                    throw new ArgumentException(problem);
                }

                inIt.ForEach(pipeline => pipeline.Folder = PipelineFolders.Moved(pipeline.Folder, request.Folder, to));
                return Task.FromResult(new PipelineFolderMoveResult { Folder = to, Moved = inIt.Count });
            }

            public Task<PipelineRunStarted> StartPipelineAsync(string definitionId, StartPipelineRequest request) => throw new NotSupportedException();
            /// <summary>What the runs list asked for, in order: the pipeline and the state.</summary>
            public List<(string DefinitionId, string State)> RunQueries { get; } = new();

            public List<PipelineRunView> Runs { get; } = new();

            public List<PipelineView> Pipelines { get; } = new();

            /// <summary>The searches the runs list asked for, in order.</summary>
            public List<string> Searches { get; } = new();

            public List<string> Deleted { get; } = new();

            /// <summary>BL-229: the days the runs list asked for, in order (from, to).</summary>
            public List<(DateOnly? From, DateOnly? To)> Days { get; } = new();

            public Task<List<PipelineRunView>> GetRunsAsync(string definitionId = null, int take = 100, string state = null, string search = null, DateOnly? from = null, DateOnly? to = null)
            {
                RunQueries.Add((definitionId, state));
                Searches.Add(search);
                Days.Add((from, to));
                return Task.FromResult(Runs.Where(run => (definitionId is null || run.DefinitionId == definitionId)
                    && (state is null || PipelineRunStates.SubStatusesOf(state).Contains(run.SubStatus))
                    && (string.IsNullOrEmpty(search) || run.Id.Contains(search, StringComparison.Ordinal) || (run.CorrelationId ?? "").Contains(search, StringComparison.Ordinal)
                        || PipelineRunData.Matches(run.Data, search))
                    && (from is null || DateOnly.FromDateTime(run.CreatedOnUtc) >= from) && (to is null || DateOnly.FromDateTime(run.CreatedOnUtc) <= to)).ToList());
            }
            public List<PipelineStepRunView> Steps { get; set; } = new();

            /// <summary>Another run's steps, by its id - the shown run's are <see cref="Steps"/>.</summary>
            public Dictionary<string, List<PipelineStepRunView>> StepsByRun { get; } = new();

            public Task<List<PipelineStepRunView>> GetRunStepsAsync(string runId) => Task.FromResult((StepsByRun.TryGetValue(runId, out var theirs) ? theirs : Steps).ToList());
            public Task<StepTestResult> TestStepAsync(string definitionId, StepTestRequest request) => throw new NotSupportedException();
            public Task<List<PipelineVersionView>> GetVersionsAsync(string definitionId) => Task.FromResult(new List<PipelineVersionView>());
            public Task<PipelineGraph> GetVersionGraphAsync(string definitionId, int version) => throw new NotSupportedException();
            public Task CancelRunAsync(string runId) => throw new NotSupportedException();
            public Task DeleteRunAsync(string runId)
            {
                Deleted.Add(runId);
                Runs.RemoveAll(run => run.Id == runId);
                return Task.CompletedTask;
            }

            /// <summary>The runs waiting for a decision, as the server answers; a decision takes the approval off the list.</summary>
            public List<PipelineApprovalView> Approvals { get; } = new();

            public List<(string Id, bool Approve, string Comment)> Decisions { get; } = new();

            /// <summary>When set, the next decision finds the approval decided already (by someone else).</summary>
            public bool DecidedElsewhere { get; set; }

            public Task<List<PipelineApprovalView>> GetApprovalsAsync(string runId = null) =>
                Task.FromResult(Approvals.Where(approval => runId is null || approval.RunId == runId).ToList());

            public Task<PipelineApprovalDecided> DecideApprovalAsync(string approvalId, ApprovalDecisionRequest request)
            {
                Decisions.Add((approvalId, request.Approve, request.Comment));
                var approval = Approvals.FirstOrDefault(entry => entry.Id == approvalId);
                Approvals.RemoveAll(entry => entry.Id == approvalId);
                return Task.FromResult(DecidedElsewhere || approval is null
                    ? null
                    : new PipelineApprovalDecided { RunId = approval.RunId, Outcome = request.Approve ? PipelineApprovals.Approved : PipelineApprovals.Rejected });
            }

            /// <summary>The insights as the server answers them for a window; none (the list without them) unless a test says.</summary>
            public Func<int, PipelineInsightsView> Insights { get; set; }

            public List<int> InsightWindows { get; } = new();

            public Task<PipelineInsightsView> GetInsightsAsync(int days)
            {
                InsightWindows.Add(days);
                return Task.FromResult(Insights?.Invoke(days));
            }

            public Task<PipelineStepInsightsView> GetStepInsightsAsync(string definitionId, int days) => Task.FromResult(new PipelineStepInsightsView { Days = days });

            public Func<int, PipelineUpcomingView> Upcoming { get; set; } = hours => new PipelineUpcomingView { Hours = hours, Enabled = true };

            public List<int> UpcomingWindows { get; } = new();

            public Task<PipelineUpcomingView> GetUpcomingRunsAsync(int hours)
            {
                UpcomingWindows.Add(hours);
                return Task.FromResult(Upcoming(hours));
            }

            public Task<List<StepCatalogEntry>> GetStepsAsync() => Task.FromResult(new List<StepCatalogEntry>());
            public Task<PipelineGraph> GetGraphAsync(string definitionId) => Task.FromResult(Graphs.TryGetValue(definitionId, out var graph) ? graph : new PipelineGraph());
            public Task<PipelineView> SaveGraphAsync(string definitionId, PipelineGraph graph)
            {
                SavedGraphs.Add((definitionId, graph));
                return Task.FromResult(new PipelineView { DefinitionId = definitionId });
            }
            public Task<GraphSaveResult> SaveGraphMergedAsync(string definitionId, PipelineGraph graph, PipelineGraph loaded) => throw new NotSupportedException();
        }

        private sealed class NoOpLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => Task.FromResult(new List<Log>());
            public Task<Log> GetLogAsync(int logId) => Task.FromResult<Log>(null!);
            public Task DeleteLogsAsync(int siteId) => Task.CompletedTask;
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
        }
    }
}
