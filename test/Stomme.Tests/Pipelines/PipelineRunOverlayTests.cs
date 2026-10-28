using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Stomme.Collaboration;
using Stomme.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.Collaboration.Services;
using Stomme.Modules.Pipelines.Services;
using Stomme.Pipelines;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EditorPage = Stomme.Modules.Pipelines.Editor;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 increment 2 - a run on the canvas: Run stays in the editor and follows the run, each step marked with what the run
    /// did with it as the reads come in, and the poll ends with the run. Each poll is fired by the test on a manual clock.
    /// </summary>
    public sealed class PipelineRunOverlayTests : BunitContext
    {
        private readonly FakePipelinesService _service = new();
        private readonly ManualClock _clock = new();

        public PipelineRunOverlayTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(_service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
        }

        private IRenderedComponent<EditorPage> RenderEditor(string run = null, bool mayRun = true) =>
            Render<EditorPage>(parameters => parameters.Add(page => page.Clock, _clock).AddCascadingValue(Page(run)).AddCascadingValue(Module(mayRun)));

        private static string RunMark(IRenderedComponent<EditorPage> cut, string title) =>
            cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == title).GetAttribute("data-run");

        [Fact]
        public void Run_stays_in_the_editor_and_marks_each_step_as_the_run_goes()
        {
            var cut = RenderEditor();
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            Assert.Null(RunMark(cut, "Read"));

            // Run: the editor stays (no navigation) and the dispatched run is not stored yet.
            cut.Find("#pipelines-editor-run").Click();
            cut.WaitForAssertion(() => Assert.Equal("none", RunMark(cut, "Read")));
            Assert.Equal("starting…", cut.Find("#pipelines-editor-run-state").TextContent.Trim());
            Assert.Contains("run-1", cut.Find("#pipelines-editor-run-page").GetAttribute("href"));
            var poll = RunPoll();

            // The engine has run the first step.
            _service.Phase = 1;
            poll.Fire();
            cut.WaitForAssertion(() => Assert.Equal("completed", RunMark(cut, "Read")));
            Assert.Equal("none", RunMark(cut, "Say"));
            // Not reached yet - not "waiting", which is a step parked on a decision or a time.
            Assert.Equal("not yet", cut.FindAll(".db-step-node__run").Last().TextContent.Trim());
            Assert.True(poll.Armed, "an unfinished run is read again");

            // It has finished: the second step faulted, and the poll ends.
            _service.Phase = 2;
            poll.Fire();
            cut.WaitForAssertion(() => Assert.Equal("faulted", RunMark(cut, "Say")));
            Assert.Equal("completed", RunMark(cut, "Read"));
            Assert.True(poll.Disposed, "a finished run is not read again");
            // A step that failed has no completion time of its own (the engine's record has none): its time ends with the run's.
            Assert.Equal("failed · 30 ms", cut.FindAll(".db-step-node__run").Last().TextContent.Trim());

            // Showing no run takes the marks away.
            cut.Find("#pipelines-editor-run-select").Change("");
            cut.WaitForAssertion(() => Assert.Null(RunMark(cut, "Read")));
        }

        // BL-229 - beyond n8n's Insights: each step's figures over a window, on the canvas where a run would be - amber with some
        // passes failed, "did not run" for a step no run reached - the window chosen, and gone again; a run chosen takes their place.
        [Fact]
        public void Step_insights_show_each_steps_figures_where_a_run_would_be()
        {
            _service.StepInsights = days => new PipelineStepInsightsView
            {
                Days = days,
                DefinitionId = "def-1",
                Runs = days == 30 ? 90 : 24,
                Steps = { new PipelineStepInsight { StepId = "read", Passes = 24, Failed = 2, MedianMilliseconds = 120 } },
            };
            var cut = RenderEditor();
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            Assert.Equal("false", cut.Find("#pipelines-editor-step-insights").GetAttribute("aria-pressed"));

            cut.Find("#pipelines-editor-step-insights").Click();
            cut.WaitForAssertion(() => Assert.Equal("handled", RunMark(cut, "Read")));
            Assert.Equal("24× · 2 failed · 120 ms", cut.FindAll(".db-step-node__run").First().TextContent.Trim());
            Assert.Equal(("none", "did not run"), (RunMark(cut, "Say"), cut.FindAll(".db-step-node__run").Last().TextContent.Trim()));
            Assert.Equal(("24 runs", "true"), (cut.Find("#pipelines-editor-step-insights-runs").TextContent.Trim(), cut.Find("#pipelines-editor-step-insights").GetAttribute("aria-pressed")));

            cut.Find("#pipelines-editor-step-insights-days").Change("30");
            cut.WaitForAssertion(() => Assert.Equal("90 runs", cut.Find("#pipelines-editor-step-insights-runs").TextContent.Trim()));
            Assert.Equal(new[] { 7, 30 }, _service.StepInsightWindows);

            // Off again: the marks go.
            cut.Find("#pipelines-editor-step-insights").Click();
            cut.WaitForAssertion(() => Assert.Null(RunMark(cut, "Read")));
            Assert.Empty(cut.FindAll("#pipelines-editor-step-insights-runs"));

            // On, then a run: the run's marks take their place.
            cut.Find("#pipelines-editor-step-insights").Click();
            cut.WaitForAssertion(() => Assert.Equal("handled", RunMark(cut, "Read")));
            cut.Find("#pipelines-editor-run").Click();
            cut.WaitForAssertion(() => Assert.Equal("none", RunMark(cut, "Read")));
            Assert.Empty(cut.FindAll("#pipelines-editor-step-insights-runs"));
        }

        // BL-229 - n8n's "Stop execution": the run followed on the canvas stopped from the editor (the Run page's Cancel) - offered
        // while it goes on, while it is starting too (a run busy in its first burst is stored only when the burst ends, and is
        // stopped before its next step all the same), and only to whoever may run the pipeline; read again at once, so the canvas
        // says where it stopped, and gone with the run.
        [Fact]
        public void Stop_run_ends_the_run_on_the_canvas_where_it_is()
        {
            var cut = RenderEditor();
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            Assert.Empty(cut.FindAll("#pipelines-editor-run-stop")); // no run shown

            cut.Find("#pipelines-editor-run").Click();
            cut.WaitForAssertion(() => Assert.Equal("starting…", cut.Find("#pipelines-editor-run-state").TextContent.Trim()));
            Assert.NotEmpty(cut.FindAll("#pipelines-editor-run-stop")); // not stored yet, and stoppable all the same

            _service.Phase = 1;
            RunPoll().Fire();
            cut.WaitForAssertion(() => Assert.Equal("completed", RunMark(cut, "Read")));
            cut.Find("#pipelines-editor-run-stop").Click();

            cut.WaitForAssertion(() => Assert.Equal("canceled", RunMark(cut, "Say"))); // ended with the run, not "waiting" on
            Assert.Equal("canceled · 10 ms", cut.FindAll(".db-step-node__run").Last().TextContent.Trim());
            Assert.Equal(new[] { "run-1" }, _service.Cancelled);
            Assert.Equal("cancelled", cut.Find("#pipelines-editor-run-state").TextContent.Trim());
            Assert.StartsWith("Stopped the run", cut.Find("#pipelines-editor-message").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-run-stop")); // over: nothing to stop

            // Without pipelines:run, the run goes on before them and they cannot stop it.
            _service.Phase = 1;
            var viewer = RenderEditor(run: "run-1", mayRun: false);
            viewer.WaitForAssertion(() => Assert.Equal("completed", RunMark(viewer, "Read")));
            Assert.Equal("running", viewer.Find("#pipelines-editor-run-state").TextContent.Trim());
            Assert.Empty(viewer.FindAll("#pipelines-editor-run-stop"));
        }

        // BL-229 - n8n's logs: the run shown, pass by pass in the order the passes began - each one's state, time and records, the
        // step that failed named - and a pass pressed is its step on the canvas, with that very pass in the inspector.
        [Fact]
        public void The_runs_passes_are_listed_in_order_and_each_leads_to_its_step()
        {
            _service.Phase = 2;
            var cut = RenderEditor(run: "run-1");
            cut.WaitForAssertion(() => Assert.Equal("faulted", RunMark(cut, "Say")));
            Assert.Equal("What ran, in order: 2 steps - Say failed", cut.Find("#pipelines-editor-passes-summary").TextContent.Trim());
            Assert.Equal(new[] { "Read completed · 40 ms", "Say faulted · 30 ms" }, cut.FindAll(".db-pipeline-editor__pass").Select(pass => pass.TextContent.Trim()));
            cut.FindAll(".db-pipeline-editor__pass")[1].Click();
            cut.WaitForAssertion(() => Assert.Equal("Say", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));

            // A loop: a step's every pass, each leading to that pass.
            _service.Phase = PipelineRunOverlayTests.FakePipelinesService.Looped;
            var looped = RenderEditor(run: "run-1");
            looped.WaitForAssertion(() => Assert.Equal("What ran, in order: 3 passes of 2 steps", looped.Find("#pipelines-editor-passes-summary").TextContent.Trim()));
            Assert.Equal(new[] { "Read completed · 40 ms · 2 records", "Read completed · 10 ms · 1 record", "Say completed · 10 ms" },
                looped.FindAll(".db-pipeline-editor__pass").Select(pass => pass.TextContent.Trim()));
            looped.FindAll(".db-pipeline-editor__pass")[1].Click();
            looped.WaitForAssertion(() => Assert.Equal("1", looped.Find("#pipelines-editor-run-execution").GetAttribute("value")));
            Assert.Equal("Read", looped.Find("#pipelines-editor-step-name").GetAttribute("value"));

            // No run shown, no list.
            looped.Find("#pipelines-editor-run-select").Change("");
            looped.WaitForAssertion(() => Assert.Empty(looped.FindAll("#pipelines-editor-passes")));

            // A step taken out of the pipeline since the run: listed under its type's name, and said to be gone when pressed.
            _service.Phase = 2;
            _service.WithoutSay = true;
            var changed = RenderEditor(run: "run-1");
            changed.WaitForAssertion(() => Assert.Equal(2, changed.FindAll(".db-pipeline-editor__pass").Count));
            Assert.Equal("Write Line faulted · 30 ms", changed.FindAll(".db-pipeline-editor__pass")[1].TextContent.Trim());
            changed.FindAll(".db-pipeline-editor__pass")[1].Click();
            changed.WaitForAssertion(() => Assert.Equal("Write Line is not in the pipeline any more - it was in the version that ran.", changed.Find("#pipelines-editor-message").TextContent.Trim()));
        }

        // BL-229 - n8n's view and download of a node's binary data: a pass of a file step links the file it was given, to download or
        // to see in its folder on the Files page; a step with no such input links nothing.
        [Fact]
        public void A_file_steps_pass_links_the_file_it_was_given()
        {
            _service.Phase = 2;
            _service.WithFile = true;
            var cut = RenderEditor(run: "run-1");
            cut.WaitForAssertion(() => Assert.Equal("faulted", RunMark(cut, "Say")));
            cut.FindAll(".db-pipeline-editor__pass")[0].Click();
            cut.WaitForAssertion(() => Assert.Equal("reports/q3.csv", cut.Find("#pipelines-editor-run-file-0").TextContent.Trim()));
            Assert.EndsWith("/api/Pipelines/files/content?path=reports%2Fq3.csv", cut.Find("#pipelines-editor-run-file-0").GetAttribute("href"));
            Assert.Equal("q3.csv", cut.Find("#pipelines-editor-run-file-0").GetAttribute("download"));
            Assert.EndsWith("Files?folder=reports", cut.Find("#pipelines-editor-run-file-folder-0").GetAttribute("href"));

            cut.FindAll(".db-pipeline-editor__pass")[1].Click();
            cut.WaitForAssertion(() => Assert.Equal("Say", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            Assert.Empty(cut.FindAll("#pipelines-editor-run-files"));
        }

        // BL-229 - n8n Enterprise's execution data redaction: a step whose data the pipeline's setting emptied says so where its input
        // and output would be, and offers no empty data to read or pin.
        [Fact]
        public void A_step_whose_data_was_not_kept_says_so_in_place_of_its_data()
        {
            _service.Phase = 2;
            _service.DataNotKept = true;
            var cut = RenderEditor(run: "run-1");
            cut.WaitForAssertion(() => Assert.Equal("faulted", RunMark(cut, "Say")));
            cut.FindAll(".db-pipeline-editor__pass")[0].Click();
            cut.WaitForAssertion(() => Assert.Equal("Read", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));

            Assert.StartsWith("Not kept: the pipeline is set not to keep", cut.Find("#pipelines-editor-run-not-kept").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-run-input"));
            Assert.Empty(cut.FindAll("#pipelines-editor-pin-run"));

            // Kept, its data is shown as ever and nothing says otherwise.
            _service.DataNotKept = false;
            var kept = RenderEditor(run: "run-1");
            kept.WaitForAssertion(() => Assert.Equal("faulted", RunMark(kept, "Say")));
            kept.FindAll(".db-pipeline-editor__pass")[0].Click();
            kept.WaitForAssertion(() => Assert.NotEmpty(kept.FindAll("#pipelines-editor-run-input")));
            Assert.Empty(kept.FindAll("#pipelines-editor-run-not-kept"));
        }

        // BL-229 - n8n's "fix with AI" on a node's error: a step a run failed asks the builder what it failed with - the error the
        // inspector shows and nothing else of the run's data - and the change comes back marked on the canvas.
        [Fact]
        public void A_failed_step_asks_the_builder_to_fix_what_it_failed_with()
        {
            _service.Phase = 2;
            _service.WithVariable = true; // the run left data the AI is not sent
            _service.Capabilities = new PipelineCapabilitiesView { Ai = true };
            var cut = RenderEditor(run: "run-1");
            cut.WaitForAssertion(() => Assert.Equal("faulted", RunMark(cut, "Say")));
            cut.FindAll(".db-pipeline-editor__pass")[1].Click();
            cut.WaitForAssertion(() => Assert.Equal("boom", cut.Find("#pipelines-editor-run-error").TextContent.Trim()));

            cut.Find("#pipelines-editor-run-error-ai").Click();

            cut.WaitForAssertion(() => Assert.Equal("The AI's change: 1 changed step - marked on the canvas.", cut.Find("#pipelines-editor-comparing-summary").TextContent.Trim()));
            var asked = Assert.Single(_service.Designs);
            Assert.Equal("The step 'Say' failed in a run with: boom\nChange the pipeline so it does not fail this way.", asked.Instruction);
            Assert.DoesNotContain("Ada", System.Text.Json.JsonSerializer.Serialize(asked)); // the run's records stay where they are
            Assert.Equal("The AI says: Say is tried twice more now.", cut.Find("#pipelines-editor-ai-summary").TextContent.Trim());
        }

        // BL-229 increment 3: Ctrl+Enter is the Run button - the keyboard script hands the editor "run".
        [Fact]
        public async Task Ctrl_Enter_runs_the_published_pipeline_like_the_Run_button()
        {
            var cut = RenderEditor();
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));

            await cut.InvokeAsync(() => cut.Instance.OnShortcut("run"));

            cut.WaitForAssertion(() => Assert.Equal("none", RunMark(cut, "Read")));
            Assert.Contains("run-1", cut.Find("#pipelines-editor-run-page").GetAttribute("href"));
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        // BL-229: a run opened from its page ("Open in the editor", ?run=) is on the canvas as the editor opens - and a run of
        // another pipeline is not.
        [Fact]
        public void A_run_named_in_the_address_is_shown_as_the_editor_opens()
        {
            _service.Phase = 2;
            var cut = RenderEditor(run: "run-1");
            cut.WaitForAssertion(() => Assert.Equal("faulted", RunMark(cut, "Say")));
            Assert.Equal("completed", RunMark(cut, "Read"));

            _service.OfDefinition = "def-other";
            var other = RenderEditor(run: "run-1");
            other.WaitForAssertion(() => Assert.Equal(2, other.FindAll(".db-step-node").Count));
            Assert.Null(RunMark(other, "Say"));
        }

        // BL-229 - n8n's "Debug in editor": what the run on the canvas left in the variables is kept as the test values on request,
        // marked kept and used by the next test; with nothing new to keep, the button is gone.
        [Fact]
        public void The_run_on_the_canvas_has_its_values_kept_as_the_test_values_on_request()
        {
            _service.Phase = 2;
            _service.WithVariable = true;
            var cut = RenderEditor(run: "run-1");
            cut.WaitForAssertion(() => Assert.Equal("faulted", RunMark(cut, "Say")));
            cut.Find("#pipelines-editor-find").Input("say");
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".db-pipeline-editor__find-result")));
            cut.FindAll(".db-pipeline-editor__find-result")[0].Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-test-keep-run")));
            Assert.Equal("rows", cut.Find("label[for=pipelines-editor-test-value-v-rows]").TextContent.Trim());

            cut.Find("#pipelines-editor-test-keep-run").Click();
            cut.WaitForAssertion(() => Assert.Equal("rows (kept)", cut.Find("label[for=pipelines-editor-test-value-v-rows]").TextContent.Trim()));
            Assert.Equal("[{\"name\":\"Ada\"}]", cut.Find("#pipelines-editor-test-value-v-rows").GetAttribute("value"));
            Assert.Empty(cut.FindAll("#pipelines-editor-test-keep-run"));
        }

        // BL-229 - n8n's "Debug in editor" with pinned data: at a step of the run on the canvas, what each step leading to it
        // produced in that run is pinned in one click - so a test up to here runs the step on the run's own data and calls
        // nothing before it again. A first step has nothing before it to pin.
        [Fact]
        public void The_results_that_led_to_a_step_in_the_run_shown_are_pinned_in_one_click()
        {
            _service.Phase = 2;
            _service.WithVariable = true;
            var cut = RenderEditor(run: "run-1");
            cut.WaitForAssertion(() => Assert.Equal("faulted", RunMark(cut, "Say")));
            void Select(string title)
            {
                cut.Find("#pipelines-editor-find").Input(title.ToLowerInvariant());
                cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".db-pipeline-editor__find-result")));
                cut.FindAll(".db-pipeline-editor__find-result")[0].Click();
                cut.WaitForAssertion(() => Assert.Equal(title, cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            }

            Select("Read");
            Assert.Empty(cut.FindAll("#pipelines-editor-pin-before")); // nothing leads to the first step

            Select("Say");
            cut.WaitForAssertion(() => Assert.Equal("Pin the results that led here (1)", cut.Find("#pipelines-editor-pin-before").TextContent.Trim()));
            cut.Find("#pipelines-editor-pin-before").Click();
            cut.WaitForAssertion(() => Assert.Equal("Pinned 1 earlier step's result from this run: Test up to here runs this step on the run's data.", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            Assert.Equal("pinned", cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == "Read").QuerySelector(".db-step-node__pinned")!.TextContent.Trim());

            Select("Read");
            Assert.Equal("[{\"name\":\"Ada\"}]", cut.Find("#pipelines-editor-pinned-result").GetAttribute("value"));
        }

        // BL-229 - n8n's node details view: Wide view puts the step's settings over the canvas, between what it received and what it
        // produced in the run shown - the narrow panel's data moved beside them, not shown twice; Back to the canvas undoes it.
        [Fact]
        public void The_wide_view_shows_a_steps_settings_between_what_it_received_and_produced()
        {
            _service.Phase = 2;
            var cut = RenderEditor(run: "run-1");
            cut.WaitForAssertion(() => Assert.Equal("completed", RunMark(cut, "Read")));
            cut.Find("#pipelines-editor-find").Input("read");
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".db-pipeline-editor__find-result")));
            cut.FindAll(".db-pipeline-editor__find-result")[0].Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-run-input")));
            Assert.Empty(cut.FindAll("#pipelines-editor-wide-in"));

            cut.Find("#pipelines-editor-wide").Click();
            cut.WaitForAssertion(() => Assert.Contains("is-wide", cut.Find(".db-pipeline-editor__body").ClassName));
            Assert.Equal(("true", "Back to the canvas"), (cut.Find("#pipelines-editor-wide").GetAttribute("aria-pressed"), cut.Find("#pipelines-editor-wide").TextContent.Trim()));
            Assert.Contains("\"a\"", cut.Find("#pipelines-editor-wide-in").TextContent);
            Assert.Contains("Completed · 40 ms", cut.Find("#pipelines-editor-wide-in").TextContent);
            Assert.Contains("[]", cut.Find("#pipelines-editor-wide-out").TextContent);
            Assert.Empty(cut.FindAll("#pipelines-editor-run-input")); // not shown twice
            Assert.NotNull(cut.Find("#pipelines-editor-step-name")); // the settings in the middle

            // A step that failed: what went wrong, on the produced side.
            cut.Find("#pipelines-editor-find").Input("say");
            cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".db-pipeline-editor__find-result"), result => result.TextContent.Contains("Say")));
            cut.FindAll(".db-pipeline-editor__find-result").First(result => result.TextContent.Contains("Say")).Click();
            cut.WaitForAssertion(() => Assert.Contains("boom", cut.Find("#pipelines-editor-wide-out").TextContent));

            cut.Find("#pipelines-editor-wide").Click();
            cut.WaitForAssertion(() => Assert.DoesNotContain("is-wide", cut.Find(".db-pipeline-editor__body").ClassName));
            Assert.Empty(cut.FindAll("#pipelines-editor-wide-in"));

            // A step double-clicked opens its wide view, as n8n opens a node's details.
            cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == "Read").DoubleClick();
            cut.WaitForAssertion(() => Assert.Contains("is-wide", cut.Find(".db-pipeline-editor__body").ClassName));
            Assert.Equal("Read", cut.Find("#pipelines-editor-step-name").GetAttribute("value"));
        }

        private static PageState Page(string run = null) => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site", Settings = new Dictionary<string, string> { [HostSwitches.SettingName(HostSwitches.Collaboration)] = "false" } },
            Page = new Page { PageId = 1, SiteId = 1, Path = "admin/pipelines", Name = "Pipelines" },
            User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Admin + ";" },
            QueryString = run is null ? new Dictionary<string, string> { ["definition"] = "def-1" } : new Dictionary<string, string> { ["definition"] = "def-1", ["run"] = run },
            UrlParameters = "",
            Action = "",
        };

        private static ModuleModel Module(bool mayRun = true) => new()
        {
            ModuleId = 32,
            SiteId = 1,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(PipelinesPermissions.Declarations.Where(declared => mayRun || declared.Key != PipelinesPermissions.Run)))
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Registered, true) })
                .ToList(),
        };

        /// <summary>A two-step published pipeline whose one run moves through phases the test sets.</summary>
        private sealed class FakePipelinesService : IPipelinesService
        {
            private static readonly DateTime Start = new(2026, 10, 5, 22, 0, 0, DateTimeKind.Utc);

            public int Phase { get; set; }

            /// <summary>Whether Read saves its result to a variable, rows.</summary>
            public bool WithVariable { get; set; }

            /// <summary>Whose run it is.</summary>
            public string OfDefinition { get; set; } = "def-1";

            /// <summary>Whether Say has been taken out of the pipeline since its run.</summary>
            public bool WithoutSay { get; set; }

            /// <summary>Whether the pipeline's setting has emptied what Read received and produced.</summary>
            public bool DataNotKept { get; set; }

            /// <summary>Whether Read takes a path in the site's pipeline folder, and its pass was given one.</summary>
            public bool WithFile { get; set; }

            public Task<List<StepCatalogEntry>> GetStepsAsync() => Task.FromResult(new List<StepCatalogEntry>
            {
                new()
                {
                    Type = "Stomme.ParseCsv", Version = 1, DisplayName = "Parse CSV", Category = "Data",
                    Inputs = WithFile ? new List<StepInputView> { new() { Name = "Path", DisplayName = "Path", TypeName = "String", UIHint = PipelineInputHints.PipelineFile } } : new List<StepInputView>(),
                },
                new() { Type = "Stomme.WriteLine", Version = 1, DisplayName = "Write Line", Category = "Primitives" },
            });

            public Task<PipelineDetail> GetPipelineAsync(string definitionId) => Task.FromResult(new PipelineDetail { Id = "v-1", DefinitionId = definitionId, Name = "Overlay", Version = 1, IsLatest = true, IsPublished = true });
            public Task<List<PipelineView>> GetPipelinesAsync(bool withLastRuns = false) => Task.FromResult(new List<PipelineView>());

            public Task<PipelineGraph> GetGraphAsync(string definitionId)
            {
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ParseCsv", Name = "Read", Position = new GraphPosition { X = 30, Y = 40 }, OutputVariables = WithVariable ? new Dictionary<string, string> { ["Result"] = "v-rows" } : new Dictionary<string, string>() },
                        new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 290, Y = 40 } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "read", TargetId = "say" } },
                    Variables = WithVariable ? new List<PipelineGraphVariable> { new() { Id = "v-rows", Name = "rows", TypeName = "Object" } } : new List<PipelineGraphVariable>(),
                };
                if (WithoutSay)
                {
                    graph.Steps.RemoveAll(step => step.Id == "say");
                    graph.Connections.Clear();
                }

                return Task.FromResult(graph);
            }

            public Task<PipelineRunStarted> StartPipelineAsync(string definitionId, StartPipelineRequest request) => Task.FromResult(new PipelineRunStarted { RunId = "run-1" });

            public Task<PipelineRunView> GetRunAsync(string runId) => Task.FromResult(Phase switch
            {
                0 => null!,
                1 => new PipelineRunView { Id = runId, DefinitionId = OfDefinition, Status = "Running", SubStatus = "Executing", CreatedOnUtc = Start },
                Stopped => new PipelineRunView { Id = runId, DefinitionId = OfDefinition, Status = "Finished", SubStatus = "Cancelled", CreatedOnUtc = Start, FinishedOnUtc = Start.AddMilliseconds(60) },
                Looped => new PipelineRunView { Id = runId, DefinitionId = OfDefinition, Status = "Finished", SubStatus = "Finished", CreatedOnUtc = Start, FinishedOnUtc = Start.AddMilliseconds(80) },
                _ => new PipelineRunView { Id = runId, DefinitionId = OfDefinition, Status = "Finished", SubStatus = "Faulted", CreatedOnUtc = Start, FinishedOnUtc = Start.AddMilliseconds(80) },
            });

            /// <summary>The phase a cancelled run is in: stopped while its second step ran.</summary>
            public const int Stopped = 3;

            /// <summary>The phase of a run that went through Read twice (a loop), then Say, and finished.</summary>
            public const int Looped = 4;

            /// <summary>The runs cancelled, in order.</summary>
            public List<string> Cancelled { get; } = new();

            public Task<List<PipelineStepRunView>> GetRunStepsAsync(string runId)
            {
                if (Phase == Looped)
                {
                    return Task.FromResult(new List<PipelineStepRunView>
                    {
                        new() { StepId = "read", StepType = "Stomme.ParseCsv", Status = "Completed", StartedAtUtc = Start, CompletedAtUtc = Start.AddMilliseconds(40), ResultCount = 2 },
                        new() { StepId = "read", StepType = "Stomme.ParseCsv", Status = "Completed", StartedAtUtc = Start.AddMilliseconds(50), CompletedAtUtc = Start.AddMilliseconds(60), ResultCount = 1 },
                        new() { StepId = "say", StepType = "Stomme.WriteLine", Status = "Completed", StartedAtUtc = Start.AddMilliseconds(70), CompletedAtUtc = Start.AddMilliseconds(80) },
                    });
                }

                var steps = new List<PipelineStepRunView>();
                if (Phase >= 1)
                {
                    steps.Add(new PipelineStepRunView { StepId = "read", StepType = "Stomme.ParseCsv", Status = "Completed", StartedAtUtc = Start, CompletedAtUtc = Start.AddMilliseconds(40), InputsJson = DataNotKept ? null : WithFile ? "{ \"Text\": \"a\", \"Path\": \"reports/q3.csv\" }" : "{ \"Text\": \"a\" }", OutputsJson = DataNotKept ? null : WithVariable ? "{ \"Result\": \"[{\\\"name\\\":\\\"Ada\\\"}]\" }" : "{ \"Result\": \"[]\" }", DataNotKept = DataNotKept });
                }

                if (Phase == Stopped)
                {
                    // As the engine leaves a step its run abandoned (measured after a Stop): running, waiting, never ended.
                    steps.Add(new PipelineStepRunView { StepId = "say", StepType = "Stomme.WriteLine", Status = "Running", Waiting = true, StartedAtUtc = Start.AddMilliseconds(50), CompletedAtUtc = null });
                }
                else if (Phase >= 2)
                {
                    steps.Add(new PipelineStepRunView { StepId = "say", StepType = "Stomme.WriteLine", Status = "Faulted", StartedAtUtc = Start.AddMilliseconds(50), CompletedAtUtc = null, Error = "boom" });
                }

                return Task.FromResult(steps);
            }

            public Task<StepTestResult> TestStepAsync(string definitionId, StepTestRequest request) => throw new NotSupportedException();
            public Task<List<PipelineVersionView>> GetVersionsAsync(string definitionId) => Task.FromResult(new List<PipelineVersionView>());
            public Task<PipelineGraph> GetVersionGraphAsync(string definitionId, int version) => throw new NotSupportedException();
            public Task<List<PipelineRunView>> GetRunsAsync(string definitionId = null, int take = 100, string state = null, string search = null, DateOnly? from = null, DateOnly? to = null) => Task.FromResult(new List<PipelineRunView>());
            public Task<List<PipelineRunLogLine>> GetRunLogAsync(string runId) => Task.FromResult(new List<PipelineRunLogLine>());
            public Task<PipelineView> SaveGraphAsync(string definitionId, PipelineGraph graph) => throw new NotSupportedException();
            public Task<GraphSaveResult> SaveGraphMergedAsync(string definitionId, PipelineGraph graph, PipelineGraph loaded) => throw new NotSupportedException();
            public Task<PipelineView> CreatePipelineAsync(CreatePipelineRequest request) => throw new NotSupportedException();

            public Task<PipelineView> ImportPipelineAsync(ImportPipelineRequest request) => throw new NotSupportedException();

            public Task<TriggerInfoView> GetTriggerInfoAsync(string type, string value, string zone = null) => throw new NotSupportedException();

            public Task<WebhookCallView> ListenForWebhookAsync(string name, int seconds) => throw new NotSupportedException();
            public Task<PipelineTestEventView> FetchNewestEmailAsync(string connection, string mailbox) => throw new NotSupportedException();

            public Task<PipelineWebhookSecretView> RenewWebhookSecretAsync(string name) => throw new NotSupportedException();

            public Task RemoveWebhookSecretAsync(string name) => throw new NotSupportedException();

            public PipelineCapabilitiesView Capabilities { get; set; } = new();

            public Task<PipelineCapabilitiesView> GetCapabilitiesAsync() => Task.FromResult(Capabilities);

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
            /// <summary>What the editor asked the AI; the AI answers by having Say tried twice more, on a copy, as a server's would be.</summary>
            public List<PipelineGraphDesignRequest> Designs { get; } = new();

            public Task<PipelineGraphDesignView> DesignGraphAsync(PipelineGraphDesignRequest request)
            {
                Designs.Add(request);
                var copy = System.Text.Json.JsonSerializer.Deserialize<PipelineGraph>(System.Text.Json.JsonSerializer.Serialize(request.Graph))!;
                copy.Steps.Single(step => step.Id == "say").Retries = 2;
                return Task.FromResult(new PipelineGraphDesignView { Graph = copy, Summary = "Say is tried twice more now." });
            }
            public Task<PipelineView> UpdatePipelineAsync(string definitionId, UpdatePipelineRequest request) => throw new NotSupportedException();
            public Task<PublishResult> PublishPipelineAsync(string definitionId, string note = null) => throw new NotSupportedException();
            public Task RetractPipelineAsync(string definitionId) => throw new NotSupportedException();
            public Task ArchivePipelineAsync(string definitionId) => throw new NotSupportedException();
            public Task RestorePipelineAsync(string definitionId) => throw new NotSupportedException();
            public Task DeletePipelineAsync(string definitionId) => throw new NotSupportedException();

            public Task<List<string>> SetTagsAsync(string definitionId, List<string> tags) => throw new NotSupportedException();
            public Task<string> SetFolderAsync(string definitionId, string folder) => throw new NotSupportedException();

            public Task<PipelineFolderMoveResult> MoveFolderAsync(PipelineFolderMoveRequest request) => throw new NotSupportedException();

            public Task<List<PipelineFoundView>> FindInPipelinesAsync(string text) => throw new NotSupportedException();

            public Task<PipelineMapView> GetMapAsync() => Task.FromResult(new PipelineMapView());

            public Task<List<PipelineAuditEntryView>> GetAuditAsync(string definitionId = null, int take = PipelineAuditLimits.DefaultTake) => Task.FromResult(new List<PipelineAuditEntryView>());

            public Task<List<AgentToolView>> GetAgentToolsAsync() => Task.FromResult(AgentTools.ToList());

            /// <summary>The site's variables, as the editor is told; what a save sets.</summary>
            public Dictionary<string, string> SiteVariables { get; set; } = new();

            public Task<Dictionary<string, string>> GetSiteVariablesAsync() => Task.FromResult(new Dictionary<string, string>(SiteVariables));

            public Task<Dictionary<string, string>> SetSiteVariablesAsync(Dictionary<string, string> variables)
            {
                SiteVariables = new Dictionary<string, string>(variables);
                return Task.FromResult(new Dictionary<string, string>(SiteVariables));
            }

            public List<PipelineSiteTableView> SiteTables { get; set; } = new();

            public Task<List<PipelineSiteTableView>> GetSiteTablesAsync() => Task.FromResult(SiteTables.ToList());

            public Task<PipelineSiteTableView> CreateSiteTableAsync(PipelineSiteTableRequest request) => throw new NotSupportedException();

            public Task<PipelineSiteTableView> UpdateSiteTableAsync(string tableId, PipelineSiteTableRequest request) => throw new NotSupportedException();

            public Task DeleteSiteTableAsync(string tableId) => throw new NotSupportedException();

            public Task<PipelineTableRowsView> GetTableRowsAsync(string tableId, int skip, int take, string search = null, string sort = null, bool descending = false) => throw new NotSupportedException();

            public Task<Dictionary<string, string>> AddTableRowAsync(string tableId, PipelineTableRowRequest request) => throw new NotSupportedException();

            public Task<Dictionary<string, string>> UpdateTableRowAsync(string tableId, string rowId, PipelineTableRowRequest request) => throw new NotSupportedException();

            public Task DeleteTableRowAsync(string tableId, string rowId) => throw new NotSupportedException();

            public Task<PipelineTableCsv> ExportTableCsvAsync(string tableId) => throw new NotSupportedException();

            public Task<PipelineRunReview> SetRunReviewAsync(string runId, PipelineRunReview review) => throw new NotSupportedException();

            public Task<List<PipelineEvaluationView>> GetEvaluationsAsync(string definitionId) => throw new NotSupportedException();

            public Task<PipelineEvaluationView> GetEvaluationAsync(string evaluationId) => throw new NotSupportedException();

            public Task<PipelineEvaluationView> StartEvaluationAsync(string definitionId, PipelineEvaluationRequest request) => throw new NotSupportedException();

            public Task<PipelineEvaluationView> CancelEvaluationAsync(string evaluationId) => throw new NotSupportedException();

            public Task DeleteEvaluationAsync(string evaluationId) => throw new NotSupportedException();

            public Task<PipelineTableImportResult> ImportTableCsvAsync(string tableId, string csv) => throw new NotSupportedException();
            public Task<PipelineFolderView> GetPipelineFilesAsync(string folder) => throw new NotSupportedException();
            public Task<PipelineFileEntryView> UploadPipelineFileAsync(string folder, string name, System.IO.Stream content) => throw new NotSupportedException();
            public Task<PipelineFileEntryView> CreatePipelineFolderAsync(string path) => throw new NotSupportedException();
            public Task<bool> DeletePipelineFileAsync(string path) => throw new NotSupportedException();

            /// <summary>The site's AI agent tools, as the editor is told.</summary>
            public List<AgentToolView> AgentTools { get; } = new();
            public Task CancelRunAsync(string runId)
            {
                Cancelled.Add(runId);
                Phase = Stopped;
                return Task.CompletedTask;
            }

            public Task<PipelineRunStarted> RetryRunAsync(string runId, bool onItsVersion = false) => throw new NotSupportedException();
            public Task<List<PipelineCallerView>> GetCallersAsync(string definitionId) => Task.FromResult(Callers.ToList());
            public List<PipelineCallerView> Callers { get; } = new();
            public Task<PipelineRunStarted> RetryFromFailedStepAsync(string runId) => throw new NotSupportedException();
            public Task DeleteRunAsync(string runId) => throw new NotSupportedException();
            public Task<List<PipelineApprovalView>> GetApprovalsAsync(string runId = null) => Task.FromResult(new List<PipelineApprovalView>());
            public Task<PipelineApprovalDecided> DecideApprovalAsync(string approvalId, ApprovalDecisionRequest request) => throw new NotSupportedException();
            public Task<PipelineInsightsView> GetInsightsAsync(int days) => Task.FromResult(new PipelineInsightsView { Days = days });

            public Func<int, PipelineStepInsightsView> StepInsights { get; set; } = days => new PipelineStepInsightsView { Days = days };

            public List<int> StepInsightWindows { get; } = new();

            public Task<PipelineStepInsightsView> GetStepInsightsAsync(string definitionId, int days)
            {
                StepInsightWindows.Add(days);
                return Task.FromResult(StepInsights(days));
            }

            public Task<PipelineUpcomingView> GetUpcomingRunsAsync(int hours) => Task.FromResult(new PipelineUpcomingView { Hours = hours, Enabled = true });
        }

        // The editor's run poll among its timers (the discussion has one of its own, armed at load).
        private ManualTimer RunPoll() => Assert.Single(_clock.Timers, timer => timer.DueTime == EditorPage.RunPollInterval);

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
            /// <summary>When it was first due: the run poll's second, the discussion's half minute.</summary>
            public TimeSpan DueTime { get; } = dueTime;

            public bool Armed { get; private set; } = dueTime != Timeout.InfiniteTimeSpan;

            public bool Disposed { get; private set; }

            public void Fire()
            {
                Assert.True(Armed && !Disposed, "the editor's run poll is not armed");
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

        private sealed class UnusedCollaborationService : ICollaborationService
        {
            public Task<CollaborationOutcome<CollaborationOverviewView>> OverviewAsync() => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> TakeOverAsync(LockRequest request) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> CloseSessionAsync(Guid sessionId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<EditSessionView>> JoinAsync(string resourceType, string resourceId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<EditSessionView>> SessionAsync(Guid sessionId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> PresenceAsync(Guid sessionId, string activeRegion) => throw new NotSupportedException();
            public Task<CollaborationOutcome<EditSessionView>> AnnounceAsync(Guid sessionId, string descriptor) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> LeaveAsync(Guid sessionId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> HeartbeatAsync(Guid sessionId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<ResourceLockResult>> AcquireAsync(LockRequest request) => throw new NotSupportedException();
            public Task<CollaborationOutcome<ResourceLockResult>> RenewAsync(LockRequest request) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> ReleaseAsync(LockRequest request) => throw new NotSupportedException();
            public Task<CollaborationOutcome<ResourceLockResult>> LockAsync(string resourceType, string resourceId) => throw new NotSupportedException();
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
