#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The step families, run by the engine as the host composes it (the
    /// module's steps registered from the marker, the options as the host binds them). Each family is driven through a
    /// real run, the records convention (a step's text input falls back to the previous step's result) through a chain,
    /// the failure policy both ways (a fault, and the Failed port of a flowchart), the file sandbox's refusals, and the
    /// HTTP step against a local endpoint with the egress guard refusing it until private networks are allowed.
    /// </summary>
    public sealed class PipelineStepsTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly PipelineStepOptions _options = new() { MaxDelaySeconds = 2 };
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-pipeline-steps-" + Guid.NewGuid().ToString("N"));

        public async Task InitializeAsync()
        {
            _options.Files.Root = _root;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ResultSink>();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(_options));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<CaptureLastResult>();
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        /// <summary>Copies the last result into the sink: the instrument for the records convention.</summary>
        [Step("Stomme.Tests", "Tests", "Captures the last result.")]
        public sealed class CaptureLastResult : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<ResultSink>().Value = context.GetLastResult()?.ToString();
        }

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private string Captured => _services.GetRequiredService<ResultSink>().Value ?? "";

        /// <summary>The recorded outputs of the one step of type T in the run (a lone step runs inside the engine's root wrapper).</summary>
        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        private static void AssertFinished(RunPipelineResult run)
        {
            Assert.True(run.PipelineState.SubStatus == PipelineSubStatus.Finished,
                $"{run.PipelineState.SubStatus}: {string.Join(" | ", run.PipelineState.Incidents.Select(i => i.StepType + ": " + i.Message))}");
        }

        private const string Csv = "name,age,email\r\nAda,36,ada@example.org\r\n\"Byron, George\",\"3\"\"6\",byron@example.org\r\nCarl,,carl@example.org\r\n";

        [Fact]
        public async Task A_chain_flows_through_the_records_convention_parse_map_validate_render()
        {
            var sequence = new Sequence
            {
                Steps =
                {
                    new ParseCsv { Text = new(Csv) },
                    new MapFields
                    {
                        Mappings = new("name => Name\nage => Age : Number = 0\nemail => Email"),
                        Computed = new("Greeting = Hello {{ name }}!"),
                        ErrorThreshold = new(2),
                    },
                    new ValidateRecords { Rules = new("Name required\nAge type Number\nwarn: Age range 18 99\nEmail regex ^\\S+@\\S+$") },
                    new ToCsv { Columns = new("Name,Age,Greeting") },
                    new CaptureLastResult(),
                },
            };

            var result = await RunAsync(sequence);

            AssertFinished(result);
            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Equal("Name,Age,Greeting\r\nAda,36,Hello Ada!\r\n\"Byron, George\",0,\"Hello Byron, George!\"\r\nCarl,0,Hello Carl!\r\n", Captured);
        }

        [Fact]
        public async Task Parse_csv_reads_quotes_embedded_delimiters_and_a_header_or_names_the_columns()
        {
            await RunAsync(new Sequence { Steps = { new ParseCsv { Text = new(Csv) }, new CaptureLastResult() } });
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(Captured)!;
            Assert.Equal(3, rows.Count);
            Assert.Equal(("Byron, George", "3\"6"), (rows[1]["name"], rows[1]["age"]));
            Assert.Equal("", rows[2]["age"]);

            await RunAsync(new Sequence { Steps = { new ParseCsv { Text = new("1;2\n3;4\n"), Delimiter = new(";"), HasHeader = new(false) }, new CaptureLastResult() } });
            Assert.Equal("[{\"column1\":\"1\",\"column2\":\"2\"},{\"column1\":\"3\",\"column2\":\"4\"}]", Captured);
        }

        [Fact]
        public async Task Map_fields_converts_types_tolerates_errors_under_the_threshold_and_faults_above_it()
        {
            var records = "[{\"n\":\"12.5\",\"ok\":\"true\",\"when\":\"2026-09-26\"},{\"n\":\"not a number\",\"ok\":\"maybe\"}]";
            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new MapFields { Records = new(records), Mappings = new("n => Amount : Number = -1\nok => Flag : Boolean\nwhen => When : Date"), ErrorThreshold = new(2) },
                    new CaptureLastResult(),
                },
            });
            AssertFinished(run);
            var rows = JsonNode.Parse(Captured)!.AsArray();
            Assert.Equal(12.5m, rows[0]!["Amount"]!.GetValue<decimal>());
            Assert.True(rows[0]!["Flag"]!.GetValue<bool>());
            Assert.StartsWith("2026-09-26T00:00:00", rows[0]!["When"]!.GetValue<string>());
            Assert.Equal("-1", rows[1]!["Amount"]!.GetValue<string>());
            Assert.Null(rows[1]!["Flag"]);

            // The same two errors above a threshold of 1: the default policy faults the run with the step's code.
            var faulted = await RunAsync(new MapFields { Records = new(records), Mappings = new("n => Amount : Number\nok => Flag : Boolean"), ErrorThreshold = new(1) });
            Assert.Equal(PipelineSubStatus.Faulted, faulted.PipelineState.SubStatus);
            Assert.Contains(faulted.PipelineState.Incidents, incident => incident.Message.Contains("2 error(s) exceed the threshold of 1"));

            // A mapping line the step cannot read is a failure too, named.
            var bad = await RunAsync(new MapFields { Records = new(records), Mappings = new("n => Amount : Money") });
            Assert.Contains(bad.PipelineState.Incidents, incident => incident.Message.Contains("unknown type 'Money'"));
        }

        // Any moves a value as it is (a number stays a number, a list a list, a text a text however it reads), and Leave
        // out keeps fields from passing through: with a mapping from the old name, n8n's Rename Keys.
        [Fact]
        public async Task Map_fields_moves_a_value_as_it_is_and_leaves_fields_out()
        {
            var records = "[{\"id\":7,\"tags\":[\"a\"],\"zip\":\"01234\",\"n\":\"123\",\"secret\":\"x\",\"keep\":true}]";
            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new MapFields
                    {
                        Records = new(records),
                        Mappings = new("id => orderId : Any\ntags => labels : Any\nn => count : Any\nmissing => none : Any = fallback"),
                        PassThroughUnmapped = new(true),
                        LeaveOut = new("id, tags,secret"),
                    },
                    new CaptureLastResult(),
                },
            });
            AssertFinished(run);
            Assert.Equal("[{\"zip\":\"01234\",\"n\":\"123\",\"keep\":true,\"orderId\":7,\"labels\":[\"a\"],\"count\":\"123\",\"none\":\"fallback\"}]", Captured);

            // A validation rule cannot ask for Any - it says nothing about a value.
            var rule = await RunAsync(new ValidateRecords { Records = new(records), Rules = new("id type Any") });
            Assert.Contains(rule.PipelineState.Incidents, incident => incident.Message.Contains("unknown type 'Any'"));
        }

        // A computed field names a type as a mapping does (n8n's Edit Fields types every field it sets): its result
        // converted, an empty result no value, one that does not convert an error the threshold counts; no type is text, as before.
        [Fact]
        public async Task Map_fields_types_a_computed_field_that_names_one()
        {
            var records = "[{\"n\":\"12.5\",\"ok\":\"true\",\"word\":\"many\"}]";
            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new MapFields
                    {
                        Records = new(records),
                        Computed = new("Amount : Number = {{ n }}\nFlag : boolean = {{ ok }}\nTags : Json = [1,2]\nLabel = {{ n }}\nNone : Number = {{ missing }}\nBad : Number = {{ word }}"),
                        ErrorThreshold = new(1),
                    },
                    new CaptureLastResult(),
                },
            });
            AssertFinished(run);
            var row = JsonNode.Parse(Captured)!.AsArray()[0]!;
            Assert.Equal(12.5m, row["Amount"]!.GetValue<decimal>());
            Assert.True(row["Flag"]!.GetValue<bool>());
            Assert.Equal("[1,2]", row["Tags"]!.ToJsonString());
            Assert.Equal("12.5", row["Label"]!.GetValue<string>());
            Assert.Null(row["None"]);
            Assert.Null(row["Bad"]);

            // The one that did not convert, over a threshold of 0, faults the run; a type the step does not know is named.
            var faulted = await RunAsync(new MapFields { Records = new(records), Computed = new("Bad : Number = {{ word }}") });
            Assert.Contains(faulted.PipelineState.Incidents, incident => incident.Message.Contains("'many' could not be converted to Number for field 'Bad'"));
            var unknown = await RunAsync(new MapFields { Records = new(records), Computed = new("x : Money = 1") });
            Assert.Contains(unknown.PipelineState.Incidents, incident => incident.Message.Contains("unknown type 'Money'"));
        }

        [Fact]
        public async Task Validate_records_routes_the_flowchart_through_passed_or_failed_and_scores_the_data()
        {
            var records = "[{\"id\":\"1\",\"age\":\"30\"},{\"id\":\"2\",\"age\":\"-4\"},{\"id\":\"2\",\"age\":\"x\"}]";
            var validate = new ValidateRecords
            {
                Id = "validate",
                Records = new(records),
                Rules = new("id required\nage range 0 120\nwarn: age type Number"),
                UniqueFields = new("id"),
                OnFailure = new(FailurePolicy.Continue),
            };
            var passed = new WriteLine("passed") { Id = "passed" };
            var failed = new WriteLine("failed") { Id = "failed" };
            var flowchart = new Flowchart
            {
                Steps = { validate, passed, failed },
                Connections =
                {
                    new Connection(new Endpoint(validate, ValidateRecords.PassedOutcome), new Endpoint(passed)),
                    new Connection(new Endpoint(validate, DataStep.FailedOutcome), new Endpoint(failed)),
                },
            };

            var run = await RunAsync(flowchart);

            AssertFinished(run);
            Assert.Contains("failed", _console.ToString());
            Assert.DoesNotContain("passed", _console.ToString());
            var outputs = OutputsOf<ValidateRecords>(run);
            Assert.Equal(false, outputs["Passed"]);
            Assert.Equal(3, outputs["ViolationCount"]);
            Assert.Equal(1, outputs["WarningCount"]);
            Assert.Equal(4.0 / 6.0, (double)outputs["QualityScore"]!, 6);
            Assert.StartsWith("VALIDATION_FAILED: 3 violation(s)", (string)outputs["Error"]!);

            // Fail-fast stops at the first error; a clean set passes through unchanged as the result.
            var fast = await RunAsync(new ValidateRecords { Records = new(records), Rules = new("age range 0 120"), FailureMode = new(ValidationFailureMode.FailFast), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal(1, OutputsOf<ValidateRecords>(fast)["ViolationCount"]);
            await RunAsync(new Sequence { Steps = { new ValidateRecords { Records = new("[{\"id\":\"1\"}]"), Rules = new("id required"), MinRows = new(1), MaxRows = new(1) }, new CaptureLastResult() } });
            Assert.Equal("[{\"id\":\"1\"}]", Captured);
        }

        [Fact]
        public async Task File_steps_round_trip_under_the_site_folder_and_refuse_to_leave_it()
        {
            var write = await RunAsync(new Sequence { Steps = { new WriteTextFile { Path = new("out/report.csv"), Content = new("a,b\r\n1,2\r\n") }, new ReadTextFile { Path = new("out/report.csv") }, new CaptureLastResult() } });
            AssertFinished(write);
            Assert.Equal("a,b\r\n1,2\r\n", Captured);
            Assert.True(File.Exists(Path.Combine(_root, "default", "out", "report.csv")));

            await RunAsync(new WriteTextFile { Path = new("out/report.csv"), Content = new("3,4\r\n"), Append = new(true) });
            Assert.Equal("a,b\r\n1,2\r\n3,4\r\n", await File.ReadAllTextAsync(Path.Combine(_root, "default", "out", "report.csv")));
            // UTF-8 with no byte-order mark - a JSON file written here is one any JSON reader takes - and letters as written.
            await RunAsync(new WriteTextFile { Path = new("out/kund.json"), Content = new("{\"namn\":\"Åsa\"}") });
            Assert.Equal(new byte[] { (byte)'{', (byte)'"', (byte)'n' }, (await File.ReadAllBytesAsync(Path.Combine(_root, "default", "out", "kund.json")))[..3]);
            await RunAsync(new WriteTextFile { Path = new("out/new.txt"), Content = new("first"), Append = new(true) });
            Assert.Equal((byte)'f', (await File.ReadAllBytesAsync(Path.Combine(_root, "default", "out", "new.txt")))[0]);

            foreach (var escape in new[] { "../outside.txt", "out/../../outside.txt", "C:\\Windows\\x.txt", "\\\\server\\share\\x.txt" })
            {
                var refused = await RunAsync(new WriteTextFile { Path = new(escape), Content = new("no") });
                Assert.Equal(PipelineSubStatus.Faulted, refused.PipelineState.SubStatus);
                Assert.Contains(refused.PipelineState.Incidents, incident => incident.Message.Contains("outside the site's folder") || incident.Message.Contains("not a relative path"));
            }

            Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
            var missing = await RunAsync(new ReadTextFile { Path = new("nowhere.txt"), OnFailure = new(FailurePolicy.Continue) });
            AssertFinished(missing);
            Assert.Equal("FILE_NOT_FOUND: No file at 'nowhere.txt'.", OutputsOf<ReadTextFile>(missing)["Error"]);
        }

        [Fact]
        public async Task Scripts_are_refused_until_enabled_then_bounded_and_see_the_input_and_the_log()
        {
            _options.Scripts.Enabled = false;
            var refused = await RunAsync(new RunScript { Script = new("1 + 1"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("SCRIPTS_DISABLED: Script execution is disabled (Pipelines:Scripts:Enabled=false).", OutputsOf<RunScript>(refused)["Error"]);

            _options.Scripts.Enabled = true;
            try
            {
                var run = await RunAsync(new Sequence
                {
                    Steps =
                    {
                        new ParseCsv { Text = new("n\r\n2\r\n3\r\n") },
                        new RunScript { Script = new("log('rows: ' + JSON.parse(input).length); var t = 0; for (var r of JSON.parse(input)) t += Number(r.n) * factor; t"), Variables = new("factor = 10") },
                        new CaptureLastResult(),
                    },
                });
                AssertFinished(run);
                Assert.Equal("rows: 2\n50", Captured);
                Assert.Equal("[\"rows: 2\"]", OutputsOf<RunScript>(run)["Log"]);

                // The sandbox has no host: a CLR type is not there, and a runaway loop hits the statement limit.
                var noClr = await RunAsync(new RunScript { Script = new("System.IO.File.ReadAllText('x')"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("SCRIPT_ERROR:", (string)OutputsOf<RunScript>(noClr)["Error"]!);
                _options.Scripts.StatementLimit = 1_000;
                var runaway = await RunAsync(new RunScript { Script = new("while (true) {}"), OnFailure = new(FailurePolicy.Continue) });
                Assert.Contains("statement", ((string)OutputsOf<RunScript>(runaway)["Error"]!).ToLowerInvariant());

                // A js: computed field in the map step rides the same sandbox - its result text, or the type it names.
                await RunAsync(new Sequence { Steps = { new MapFields { Records = new("[{\"a\":\"2\",\"b\":\"3\"}]"), Computed = new("product = js: Number(a) * Number(b)\ntotal : Number = js: a * b\nbig : Boolean = js: a * b > 5"), PassThroughUnmapped = new(true) }, new CaptureLastResult() } });
                Assert.Equal("[{\"a\":\"2\",\"b\":\"3\",\"product\":\"6\",\"total\":6,\"big\":true}]", Captured);
            }
            finally
            {
                _options.Scripts.Enabled = false;
                _options.Scripts.StatementLimit = new ScriptStepOptions().StatementLimit;
            }

            // And with scripts off, a js: computed field is an error the threshold counts, not a silent null.
            var off = await RunAsync(new MapFields { Records = new("[{\"a\":\"2\"}]"), Computed = new("x = js: 1") });
            Assert.Contains(off.PipelineState.Incidents, incident => incident.Message.Contains("needs Pipelines:Scripts:Enabled"));
        }

        // n8n's Code node run as it was written: the input's records as n8n's items, once for all of them or once for each,
        // what it returns the records; console.log in the step's log; an n8n helper this sandbox does not have fails, named.
        [Fact]
        public async Task A_script_written_for_n8ns_code_node_runs_as_n8n_ran_it()
        {
            _options.Scripts.Enabled = true;
            try
            {
                const string records = "[{\"name\":\"Ada\",\"price\":\"2\"},{\"name\":\"Bo\",\"price\":\"3\"}]";

                // Once for all items: $input.all(), items returned as { json }.
                await RunAsync(new Sequence { Steps = { new RunScript { Input = new(records), Style = new(ScriptStyle.N8nAllItems), Script = new("return $input.all().map(i => ({ json: { name: i.json.name.toUpperCase(), total: Number(i.json.price) * 2 } }));") }, new CaptureLastResult() } });
                Assert.Equal("[{\"name\":\"ADA\",\"total\":4},{\"name\":\"BO\",\"total\":6}]", Captured);

                // The old Function node's items, filtered; $json is the first item's.
                await RunAsync(new Sequence { Steps = { new RunScript { Input = new(records), Style = new(ScriptStyle.N8nAllItems), Script = new("log($json.name); return items.filter(i => Number(i.json.price) > 2);") }, new CaptureLastResult() } });
                Assert.Equal("[{\"name\":\"Bo\",\"price\":\"3\"}]", Captured);

                // Once for each item: its $json changed and returned, or changed and nothing returned - kept as it was left.
                var each = await RunAsync(new Sequence { Steps = { new RunScript { Input = new(records), Style = new(ScriptStyle.N8nEachItem), Script = new("$json.total = Number($json.price) * 10; console.log('row', $itemIndex, { n: $json.name }); if ($itemIndex === 0) { return $input.item; }") }, new CaptureLastResult() } });
                Assert.Equal("[{\"name\":\"Ada\",\"price\":\"2\",\"total\":20},{\"name\":\"Bo\",\"price\":\"3\",\"total\":30}]", Captured);
                Assert.Equal("[\"row 0 {\\u0022n\\u0022:\\u0022Ada\\u0022}\",\"row 1 {\\u0022n\\u0022:\\u0022Bo\\u0022}\"]", OutputsOf<RunScript>(each)["Log"]);

                // A plain value comes in as { value }; n8n's $('a node') is not here, and the script fails saying so.
                await RunAsync(new Sequence { Steps = { new RunScript { Input = new("7"), Style = new(ScriptStyle.N8nAllItems), Script = new("return [{ json: { doubled: items[0].json.value * 2 } }];") }, new CaptureLastResult() } });
                Assert.Equal("[{\"doubled\":14}]", Captured);
                var helper = await RunAsync(new RunScript { Input = new(records), Style = new(ScriptStyle.N8nAllItems), Script = new("return $('Webhook').all();"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("SCRIPT_ERROR:", (string)OutputsOf<RunScript>(helper)["Error"]!);
                Assert.Contains("$", (string)OutputsOf<RunScript>(helper)["Error"]!);
            }
            finally
            {
                _options.Scripts.Enabled = false;
            }
        }

        [Fact]
        public async Task Processes_are_refused_until_enabled_and_rooted_then_run_with_stdin_and_a_bounded_output()
        {
            var (root, executable, arguments) = OperatingSystem.IsWindows()
                ? (Environment.GetFolderPath(Environment.SpecialFolder.System), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "findstr.exe"), "v")
                : ("/bin", "/bin/grep", "v");

            _options.Processes.Enabled = false;
            var refused = await RunAsync(new RunProcess { Executable = new(executable), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("PROCESSES_DISABLED:", (string)OutputsOf<RunProcess>(refused)["Error"]!);

            _options.Processes.Enabled = true;
            try
            {
                var noRoots = await RunAsync(new RunProcess { Executable = new(executable), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("PROCESS_ROOTS_EMPTY:", (string)OutputsOf<RunProcess>(noRoots)["Error"]!);

                _options.Processes.AllowedRoots.Add(Path.Combine(_root, "approved"));
                var outside = await RunAsync(new RunProcess { Executable = new(executable), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("EXECUTABLE_OUTSIDE_ROOTS:", (string)OutputsOf<RunProcess>(outside)["Error"]!);

                _options.Processes.AllowedRoots.Add(root);
                var run = await RunAsync(new Sequence
                {
                    Steps =
                    {
                        new ParseCsv { Text = new("k\r\nv\r\n") },
                        new ToCsv(),
                        new RunProcess { Executable = new(executable), Arguments = new(arguments) },
                        new CaptureLastResult(),
                    },
                });
                AssertFinished(run);
                Assert.Equal("v", Captured.Trim());
                Assert.Equal(0, OutputsOf<RunProcess>(run)["ExitCode"]);

                // No match: a non-zero exit is a failure that carries the code.
                var failed = await RunAsync(new RunProcess { Executable = new(executable), Arguments = new("zzz"), Input = new("nothing here"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("PROCESS_ERROR:", (string)OutputsOf<RunProcess>(failed)["Error"]!);
                Assert.NotEqual(0, OutputsOf<RunProcess>(failed)["ExitCode"]);
            }
            finally
            {
                _options.Processes.Enabled = false;
                _options.Processes.AllowedRoots.Clear();
            }
        }

        [Fact]
        public async Task Extract_html_reads_records_by_selector_attribute_and_pattern()
        {
            const string html = "<html><body><h1 id=\"t\">Prices</h1><ul><li class=\"item\" data-sku=\"A1\"><span class=\"name\">Apple</span> <b>1.50</b></li><li class=\"item\" data-sku=\"B2\"><span class=\"name\">Pear</span> <b>2.25</b></li></ul></body></html>";
            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new ExtractHtml { Html = new(html), RowSelector = new("li.item"), Fields = new("name = span.name\nsku = li.item @data-sku\nprice = regex: <b>([^<]+)</b>") },
                    new CaptureLastResult(),
                },
            });
            AssertFinished(run);
            Assert.Equal("[{\"name\":\"Apple\",\"sku\":\"A1\",\"price\":\"1.50\"},{\"name\":\"Pear\",\"sku\":\"B2\",\"price\":\"2.25\"}]", Captured);
            Assert.Equal(2, OutputsOf<ExtractHtml>(run)["RowCount"]);

            await RunAsync(new Sequence { Steps = { new ExtractHtml { Html = new(html), Fields = new("title = #t") }, new CaptureLastResult() } });
            Assert.Equal("[{\"title\":\"Prices\"}]", Captured);
            var bad = await RunAsync(new ExtractHtml { Html = new(html), Fields = new("x = li["), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("SELECTOR_INVALID:", (string)OutputsOf<ExtractHtml>(bad)["Error"]!);
        }

        [Fact]
        public async Task Send_notification_needs_a_site_and_says_so_outside_one()
        {
            var run = await RunAsync(new SendNotification { TemplateKey = new("pipelines.test"), Recipients = new("user:1"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("SITE_UNAVAILABLE: The run has no site to notify from.", OutputsOf<SendNotification>(run)["Error"]);
            var badRecipient = await RunAsync(new SendNotification { TemplateKey = new("pipelines.test"), Recipients = new("someone"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("RECIPIENT_INVALID:", (string)OutputsOf<SendNotification>(badRecipient)["Error"]!);
        }

        [Fact]
        public async Task Delay_and_log_are_bounded_and_recorded()
        {
            var run = await RunAsync(new Sequence { Steps = { new Delay { Seconds = new(0) }, new LogMessage { Message = new("hello log"), Level = new(LogMessageLevel.Warning) } } });
            AssertFinished(run);
            Assert.Contains(run.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Warning" && entry.Message == "hello log" && entry.StepType == "Stomme.LogMessage");

            var tooLong = await RunAsync(new Delay { Seconds = new(3) });
            Assert.Equal(PipelineSubStatus.Faulted, tooLong.PipelineState.SubStatus);
            Assert.Contains(tooLong.PipelineState.Incidents, incident => incident.Message.Contains("Pipelines:MaxDelaySeconds (2)"));
        }

        private const string Rss = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel>
                <title>Release notes</title>
                <item>
                  <title>10.18.0</title>
                  <link>https://example.com/10.18.0</link>
                  <guid isPermaLink="false">release-10.18.0</guid>
                  <pubDate>Wed, 07 Oct 2026 04:00:00 GMT</pubDate>
                  <dc:creator>Nova Globen</dc:creator>
                  <description>Forms and tags.</description>
                  <category>pipelines</category>
                  <category>editor</category>
                </item>
                <item>
                  <title>10.17.0</title>
                  <link>https://example.com/10.17.0</link>
                  <pubDate>Tue, 06 Oct 2026 22:00:00 PDT</pubDate>
                </item>
              </channel>
            </rss>
            """;

        // n8n's RSS Read: a feed's items as records, RSS 2.0, Atom and RSS 1.0 alike, dates in UTC; fetched through the
        // HTTP request step's guard - refused on the private network until allowed, a redirect judged again.
        [Fact]
        public async Task Read_rss_feed_reads_rss_atom_and_rdf_items_and_fetches_through_the_guard()
        {
            Assert.True(ReadFeed.TryRead(Rss, out var title, out var items, out _));
            Assert.Equal("Release notes", title);
            Assert.Equal("""{"title":"10.18.0","link":"https://example.com/10.18.0","id":"release-10.18.0","published":"2026-10-07T04:00:00Z","author":"Nova Globen","summary":"Forms and tags.","categories":["pipelines","editor"]}""", items[0].ToJsonString());
            Assert.Equal("https://example.com/10.17.0", (string)items[1]["id"]!); // no guid: the link
            Assert.Equal("2026-10-07T05:00:00Z", (string)items[1]["published"]!);

            Assert.True(ReadFeed.TryRead("""
                <feed xmlns="http://www.w3.org/2005/Atom">
                  <title>Changes</title>
                  <entry>
                    <title>Data tables</title>
                    <link rel="self" href="https://example.com/self"/>
                    <link rel="alternate" href="https://example.com/data-tables"/>
                    <id>urn:change:1</id>
                    <updated>2026-10-07T07:00:00+02:00</updated>
                    <author><name>Ada</name></author>
                    <summary>Rows between runs.</summary>
                    <category term="pipelines"/>
                  </entry>
                </feed>
                """, out title, out items, out _));
            Assert.Equal("Changes", title);
            Assert.Equal("""{"title":"Data tables","link":"https://example.com/data-tables","id":"urn:change:1","published":"2026-10-07T05:00:00Z","author":"Ada","summary":"Rows between runs.","categories":["pipelines"]}""", Assert.Single(items).ToJsonString());

            Assert.True(ReadFeed.TryRead("""
                <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
                  <channel><title>Old style</title></channel>
                  <item><title>One</title><link>https://example.com/1</link><dc:date>2026-10-01</dc:date></item>
                  <item><title>Two</title><link>https://example.com/2</link></item>
                </rdf:RDF>
                """, out title, out items, out _));
            Assert.Equal(("Old style", 2, "2026-10-01T00:00:00Z"), (title, items.Count, (string)items[0]["published"]!));

            Assert.False(ReadFeed.TryRead("<html><body/></html>", out _, out _, out var error));
            Assert.Equal("The XML is not an RSS or Atom feed: its root is <html>.", error);
            Assert.False(ReadFeed.TryRead("""<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY x SYSTEM "file:///etc/passwd">]><rss><channel><title>&x;</title></channel></rss>""", out _, out _, out error));
            Assert.StartsWith("The feed is not XML this step reads", error);

            // From the previous step's result, at most one item.
            var fromText = await RunAsync(new Sequence { Steps = { new WriteLine("ignored"), new ReadFeed { Feed = new(Rss), MaxItems = new(1) }, new CaptureLastResult() } });
            AssertFinished(fromText);
            Assert.Equal("10.18.0", (string)JsonNode.Parse(Captured)!.AsArray().Single()!["title"]!);
            Assert.Equal("Release notes", OutputsOf<ReadFeed>(fromText)["Title"]);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.MapGet("/feed", () => Results.Text(Rss, "application/rss+xml"));
            app.MapGet("/old", () => Results.Redirect("/feed"));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            try
            {
                var refused = await RunAsync(new ReadFeed { Url = new(address + "/feed"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("EGRESS_REFUSED: The destination '127.0.0.1' is not permitted", (string)OutputsOf<ReadFeed>(refused)["Error"]!);

                _options.Http.AllowedHosts.Add("127.0.0.1");
                var fetched = await RunAsync(new Sequence { Steps = { new ReadFeed { Url = new(address + "/old") }, new CaptureLastResult() } });
                AssertFinished(fetched);
                Assert.Equal(new[] { "10.18.0", "10.17.0" }, JsonNode.Parse(Captured)!.AsArray().Select(item => (string)item!["title"]!));
                Assert.Equal(2, OutputsOf<ReadFeed>(fetched)["Count"]);
            }
            finally
            {
                _options.Http.AllowedHosts.Clear();
                await app.StopAsync();
            }
        }

        [Fact]
        public async Task Http_request_is_refused_on_the_private_network_until_allowed_then_captures_the_response()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.MapGet("/ok", (HttpRequest request) => Results.Text("{\"got\":\"" + request.Headers["X-Probe"] + "|" + request.Headers.Authorization + "\"}", "application/json"));
            app.MapPost("/echo", async (HttpRequest request) => Results.Text(await new StreamReader(request.Body).ReadToEndAsync(), "text/plain"));
            app.MapGet("/fail", () => Results.Problem("nope", statusCode: 500));
            app.MapGet("/hop", () => Results.Redirect("/ok"));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            try
            {
                // Fail-closed: loopback is refused by the framework's ONE outbound guard before any connection is made.
                Assert.Empty(_options.Http.AllowedHosts);
                var refused = await RunAsync(new SendHttpRequest { Url = new(address + "/ok"), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("EGRESS_REFUSED: The destination '127.0.0.1' is not permitted: it resolves to a loopback, private, link-local or otherwise internal address.", OutputsOf<SendHttpRequest>(refused)["Error"]);
                var refusedName = await RunAsync(new SendHttpRequest { Url = new("http://localhost:1/ok"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("EGRESS_REFUSED: The destination 'localhost' is not permitted", (string)OutputsOf<SendHttpRequest>(refusedName)["Error"]!);

                // The guard's allowlist (Pipelines:Http:AllowedHosts) names the hosts that may reach a denied address.
                _options.Http.AllowedHosts.Add("127.0.0.1");
                var ok = await RunAsync(new Sequence
                {
                    Steps =
                    {
                        new SendHttpRequest { Url = new(address + "/ok"), Headers = new("X-Probe: yes"), Authorization = new("Bearer secret-token") },
                        new CaptureLastResult(),
                    },
                });
                AssertFinished(ok);
                Assert.Equal("{\"got\":\"yes|Bearer secret-token\"}", Captured);
                Assert.Equal(200, OutputsOf<SendHttpRequest>(ok)["StatusCode"]);

                // A body from the previous step's result, posted; a redirect followed; a 500 as a failure with the body kept.
                await RunAsync(new Sequence { Steps = { new WriteLine("ignored"), new ParseCsv { Text = new("k\r\nv\r\n") }, new SendHttpRequest { Url = new(address + "/echo"), Method = new(HttpRequestMethod.POST) }, new CaptureLastResult() } });
                Assert.Equal("[{\"k\":\"v\"}]", Captured);
                await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/hop") }, new CaptureLastResult() } });
                Assert.StartsWith("{\"got\":", Captured);
                var failed = await RunAsync(new SendHttpRequest { Url = new(address + "/fail"), OnFailure = new(FailurePolicy.Continue) });
                var outputs = OutputsOf<SendHttpRequest>(failed);
                Assert.Equal(500, outputs["StatusCode"]);
                Assert.StartsWith("HTTP_ERROR: HTTP 500", (string)outputs["Error"]!);
                Assert.Contains("nope", (string)outputs["Result"]!);

                // The allowlist is by host NAME: the same server through 'localhost' is still refused.
                var stillRefused = await RunAsync(new SendHttpRequest { Url = new(address.Replace("127.0.0.1", "localhost") + "/ok"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("EGRESS_REFUSED: The destination 'localhost' is not permitted", (string)OutputsOf<SendHttpRequest>(stillRefused)["Error"]!);
            }
            finally
            {
                _options.Http.AllowedHosts.Clear();
                await app.StopAsync();
            }
        }

        // n8n's binary data on its HTTP Request node: a file from the site's folder sent as the body - its bytes, typed by its
        // extension - or as a multipart form's file; a response saved to a file as it comes, bytes exactly (a PDF is not text), the
        // path the result; an error answer never saved; one over Pipelines:Files:MaxFileBytes refused with nothing left behind; a
        // file outside the folder or missing refused before anything is sent.
        [Fact]
        public async Task Http_request_sends_a_file_and_saves_a_response_to_one()
        {
            var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0xFE, 0x0A, 0x80, 0x81 };
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.MapMethods("/raw", new[] { "POST", "PUT" }, async (HttpRequest request) =>
            {
                using var buffer = new MemoryStream();
                await request.Body.CopyToAsync(buffer);
                return Results.Text($"{buffer.Length} bytes of {request.ContentType}: {Convert.ToHexString(buffer.ToArray())}");
            });
            app.MapPost("/form", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var file = form.Files.Single();
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer);
                return Results.Text($"{file.Name} {file.FileName} {file.ContentType} {Convert.ToHexString(buffer.ToArray())}");
            });
            app.MapGet("/report.pdf", () => Results.Bytes(pdf, "application/pdf"));
            app.MapGet("/fail", () => Results.Problem("nope", statusCode: 500));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var folder = Path.Combine(_root, "default");
            Directory.CreateDirectory(Path.Combine(folder, "in"));
            await File.WriteAllBytesAsync(Path.Combine(folder, "in", "scan.pdf"), pdf);
            _options.Http.AllowedHosts.Add("127.0.0.1");

            try
            {
                await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/raw"), Method = new(HttpRequestMethod.POST), SendFile = new("in/scan.pdf") }, new CaptureLastResult() } });
                Assert.Equal("10 bytes of application/pdf: " + Convert.ToHexString(pdf), Captured);
                await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/raw"), Method = new(HttpRequestMethod.PUT), SendFile = new("in/scan.pdf"), ContentType = new("application/octet-stream") }, new CaptureLastResult() } });
                Assert.StartsWith("10 bytes of application/octet-stream", Captured);
                await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/form"), Method = new(HttpRequestMethod.POST), SendFile = new("in/scan.pdf"), FileField = new("document") }, new CaptureLastResult() } });
                Assert.Equal("document scan.pdf application/pdf " + Convert.ToHexString(pdf), Captured);

                var saved = await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/report.pdf"), SaveTo = new("downloads/report.pdf") }, new CaptureLastResult() } });
                AssertFinished(saved);
                Assert.Equal("downloads/report.pdf", Captured);
                Assert.Equal(pdf, await File.ReadAllBytesAsync(Path.Combine(folder, "downloads", "report.pdf")));
                Assert.Equal(10L, OutputsOf<SendHttpRequest>(saved)["SavedBytes"]);
                Assert.Equal("application/pdf", OutputsOf<SendHttpRequest>(saved)["ResponseContentType"]);
                Assert.Contains(saved.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Saved" && entry.Message == "Saved 10 bytes to 'downloads/report.pdf'.");

                // An error answer is a failure with its body as the result, and nothing is saved.
                var failed = await RunAsync(new SendHttpRequest { Url = new(address + "/fail"), SaveTo = new("downloads/fail.json"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("HTTP_ERROR: HTTP 500", (string)OutputsOf<SendHttpRequest>(failed)["Error"]!);
                Assert.False(File.Exists(Path.Combine(folder, "downloads", "fail.json")));

                // Over the file limit: refused, and neither the file nor its part is left.
                _options.Files.MaxFileBytes = 5;
                var tooBig = await RunAsync(new SendHttpRequest { Url = new(address + "/report.pdf"), SaveTo = new("downloads/big.pdf"), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("FILE_TOO_LARGE: The response is over Pipelines:Files:MaxFileBytes (5); nothing was saved to 'downloads/big.pdf'.", OutputsOf<SendHttpRequest>(tooBig)["Error"]);
                Assert.Empty(Directory.GetFiles(Path.Combine(folder, "downloads"), "big.pdf*"));
                var tooBigToSend = await RunAsync(new SendHttpRequest { Url = new(address + "/raw"), Method = new(HttpRequestMethod.POST), SendFile = new("in/scan.pdf"), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("FILE_TOO_LARGE: 'in/scan.pdf' is over Pipelines:Files:MaxFileBytes (5).", OutputsOf<SendHttpRequest>(tooBigToSend)["Error"]);
                _options.Files.MaxFileBytes = new FileStepOptions().MaxFileBytes;

                // Outside the folder, or not there: refused before anything is sent.
                var outside = await RunAsync(new SendHttpRequest { Url = new(address + "/raw"), Method = new(HttpRequestMethod.POST), SendFile = new("../secrets.txt"), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("PATH_REFUSED: '../secrets.txt' resolves outside the site's folder.", OutputsOf<SendHttpRequest>(outside)["Error"]);
                var saveOutside = await RunAsync(new SendHttpRequest { Url = new(address + "/report.pdf"), SaveTo = new("C:\\temp\\x.pdf"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("PATH_REFUSED:", (string)OutputsOf<SendHttpRequest>(saveOutside)["Error"]!);
                var missing = await RunAsync(new SendHttpRequest { Url = new(address + "/raw"), Method = new(HttpRequestMethod.POST), SendFile = new("in/none.pdf"), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("FILE_NOT_FOUND: 'in/none.pdf' is not a file in the site's pipeline folder.", OutputsOf<SendHttpRequest>(missing)["Error"]);
            }
            finally
            {
                _options.Http.AllowedHosts.Clear();
                _options.Files.MaxFileBytes = new FileStepOptions().MaxFileBytes;
                await app.StopAsync();
            }
        }

        // n8n's HTTP Request pagination: a GET that follows the next page - by the Link header, a URL in the body, or a page
        // number until a page has no records - and gathers every page's records into one list; at most so many pages (said when
        // there were more); a page answering an error fails the step with its number; a next page on an address the guard denies is
        // refused; a page that repeats ends it; a body without the list, and a POST, are refused.
        [Fact]
        public async Task Http_request_follows_the_pages_and_gathers_their_records()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.MapGet("/numbered", (int page, string? kind) => Results.Json(new { data = page <= 3 ? new[] { new { id = page * 10 + 1, kind }, new { id = page * 10 + 2, kind } } : Array.Empty<object>() }));
            app.MapGet("/linked", (HttpResponse response, int? p) =>
            {
                var at = p ?? 1;
                if (at < 3)
                {
                    response.Headers.Append("Link", $"</linked?p={at + 1}>; rel=\"next\", </linked?p=1>; rel=\"first\"");
                }

                return Results.Json(new[] { new { page = at } });
            });
            app.MapGet("/cursor", (string? c) => Results.Json(new { result = new { items = new[] { new { c = c ?? "start" } } }, links = new { next = c switch { null => "/cursor?c=b", "b" => "/cursor?c=c", _ => null } } }));
            app.MapGet("/loop", () => Results.Json(new { items = new[] { 1 }, next = "/loop" }));
            app.MapGet("/escape", () => Results.Json(new { items = new[] { 1 }, next = "http://localhost:1/elsewhere" }));
            app.MapGet("/broken", (int page) => page < 2 ? Results.Json(new { data = new[] { 1 } }) : Results.Problem("down", statusCode: 503));
            app.MapGet("/flat", () => Results.Json(new { count = 2 }));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            _options.Http.AllowedHosts.Add("127.0.0.1");

            try
            {
                // A page number, from its value in the URL (the other parameters kept), until a page has none.
                var numbered = await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/numbered?kind=x&page=2"), Pagination = new(HttpPagination.PageNumber), RecordsField = new("data"), NextField = new("page") }, new CaptureLastResult() } });
                AssertFinished(numbered);
                Assert.Equal("[{\"id\":21,\"kind\":\"x\"},{\"id\":22,\"kind\":\"x\"},{\"id\":31,\"kind\":\"x\"},{\"id\":32,\"kind\":\"x\"}]", Captured);
                Assert.Equal(3, OutputsOf<SendHttpRequest>(numbered)["Pages"]);

                // The Link header's next, the list being the body.
                await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/linked"), Pagination = new(HttpPagination.NextLinkHeader) }, new CaptureLastResult() } });
                Assert.Equal("[{\"page\":1},{\"page\":2},{\"page\":3}]", Captured);

                // A URL in the body, relative to the page, the records deeper in.
                await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/cursor"), Pagination = new(HttpPagination.NextUrlInBody), RecordsField = new("result.items"), NextField = new("links.next") }, new CaptureLastResult() } });
                Assert.Equal("[{\"c\":\"start\"},{\"c\":\"b\"},{\"c\":\"c\"}]", Captured);

                // At most so many pages - and the run says there were more.
                var capped = await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/numbered?page=1"), Pagination = new(HttpPagination.PageNumber), RecordsField = new("data"), NextField = new("page"), MaxPages = new(2) }, new CaptureLastResult() } });
                Assert.Equal("[{\"id\":11,\"kind\":null},{\"id\":12,\"kind\":null},{\"id\":21,\"kind\":null},{\"id\":22,\"kind\":null}]", Captured);
                Assert.Contains(capped.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Truncated" && entry.Message == "Stopped after 2 pages ('Most pages'); there were more.");

                // A page that repeats ends it; a next page the guard denies is refused; an error page fails with its number.
                await RunAsync(new Sequence { Steps = { new SendHttpRequest { Url = new(address + "/loop"), Pagination = new(HttpPagination.NextUrlInBody), RecordsField = new("items"), NextField = new("next") }, new CaptureLastResult() } });
                Assert.Equal("[1]", Captured);
                var escaped = await RunAsync(new SendHttpRequest { Url = new(address + "/escape"), Pagination = new(HttpPagination.NextUrlInBody), RecordsField = new("items"), NextField = new("next"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("EGRESS_REFUSED: Page 2 at 'http://localhost:1/elsewhere' refused:", (string)OutputsOf<SendHttpRequest>(escaped)["Error"]!);
                var broken = await RunAsync(new SendHttpRequest { Url = new(address + "/broken"), Pagination = new(HttpPagination.PageNumber), RecordsField = new("data"), NextField = new("page"), OnFailure = new(FailurePolicy.Continue) });
                Assert.StartsWith("HTTP_ERROR: Page 2: HTTP 503", (string)OutputsOf<SendHttpRequest>(broken)["Error"]!);
                Assert.Equal(503, OutputsOf<SendHttpRequest>(broken)["StatusCode"]);

                // Refused before anything is sent, or as soon as a page is not what was said.
                var flat = await RunAsync(new SendHttpRequest { Url = new(address + "/flat"), Pagination = new(HttpPagination.NextLinkHeader), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("PAGE_NOT_RECORDS: Page 1's body is not a list - name the field holding its records in 'Records field'.", OutputsOf<SendHttpRequest>(flat)["Error"]);
                var noList = await RunAsync(new SendHttpRequest { Url = new(address + "/flat"), Pagination = new(HttpPagination.NextLinkHeader), RecordsField = new("data"), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("PAGE_NOT_RECORDS: Page 1's body has no list at 'data'.", OutputsOf<SendHttpRequest>(noList)["Error"]);
                var post = await RunAsync(new SendHttpRequest { Url = new(address + "/linked"), Method = new(HttpRequestMethod.POST), Pagination = new(HttpPagination.NextLinkHeader), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("PAGINATION_INVALID: A paginated request is a GET.", OutputsOf<SendHttpRequest>(post)["Error"]);
                var noNext = await RunAsync(new SendHttpRequest { Url = new(address + "/numbered"), Pagination = new(HttpPagination.PageNumber), OnFailure = new(FailurePolicy.Continue) });
                Assert.Equal("PAGINATION_INVALID: PageNumber needs 'Next': the query parameter counting the pages.", OutputsOf<SendHttpRequest>(noNext)["Error"]);
            }
            finally
            {
                _options.Http.AllowedHosts.Clear();
                await app.StopAsync();
            }
        }
    }
}
