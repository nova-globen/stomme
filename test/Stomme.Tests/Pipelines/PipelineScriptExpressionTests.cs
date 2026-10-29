#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// An input computed by JavaScript, in the ENGINE: the module's <c>JavaScript</c> expression kind runs the
    /// expression in the one bounded sandbox with the pipeline's variables in scope (records as JavaScript values, never a .NET
    /// object), only where <c>Pipelines:Scripts:Enabled</c>; the sandbox's limits fault the step with their reason; the preview
    /// runs the same sandbox for an author; the graph comes back as written.
    /// </summary>
    public sealed class PipelineScriptExpressionTests
    {
        private static async Task<(ServiceProvider Services, StringWriter Console)> EngineAsync(bool scripts)
        {
            var console = new StringWriter();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions { Scripts = { Enabled = scripts, StatementLimit = 10_000, TimeoutSeconds = 2 } }));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
            });
            services.AddExpressionDescriptorProvider<ScriptExpressionDescriptorProvider>();
            var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
            return (provider, console);
        }

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private static async Task<(string Body, Stomme.Engine.Pipelines.Models.RunPipelineResult Result)> RunAsync(IServiceProvider services, PipelineGraph graph)
        {
            using var scope = services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var serializer = scope.ServiceProvider.GetRequiredService<IStepSerializer>();
            var body = PipelineGraphJudge.Body(graph, null, registry, scope.ServiceProvider.GetRequiredService<IIdentityGenerator>(), serializer);
            return (body, await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(serializer.Deserialize(body)));
        }

        private static PipelineGraph Say(string script) => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "world" },
                new PipelineGraphVariable { Id = "v-rows", Name = "rows" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Inputs = { ["Text"] = "name,total\nAda,120\nGrace,80\n" }, OutputVariables = { ["Result"] = "v-rows" } },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputScripts = { ["Text"] = script } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "say" } },
        };

        [Theory]
        [InlineData("'Hello ' + who.toUpperCase()", "Hello WORLD")]
        [InlineData("rows.length + ' rows, the first ' + rows[0].name", "2 rows, the first Ada")]
        [InlineData("rows.filter(r => Number(r.total) > 100).map(r => r.name).join(',')", "Ada")]
        [InlineData("({ count: rows.length })", "{\"count\":2}")]
        [InlineData("6 * 7", "42")]
        public async Task A_script_input_runs_in_the_sandbox_with_the_variables_as_javascript_values(string script, string expected)
        {
            var (services, console) = await EngineAsync(scripts: true);
            await using var _ = services;

            var (body, result) = await RunAsync(services, Say(script));

            Assert.True(result.PipelineState.Status == PipelineStatus.Finished, $"{result.PipelineState.Status}: {string.Join("; ", result.PipelineState.Incidents.Select(incident => incident.Message))}");
            Assert.Contains(expected, console.ToString());
            Assert.Equal(script, PipelineGraphCodec.ToGraph(body).Steps.Single(step => step.Id == "say").InputScripts["Text"]);
        }

        [Fact]
        public async Task Where_scripts_are_off_the_step_faults_saying_so_and_a_script_past_its_limits_faults_with_the_limit()
        {
            var (off, _) = await EngineAsync(scripts: false);
            await using (off)
            {
                var (_, refused) = await RunAsync(off, Say("'x'"));
                Assert.Equal(PipelineSubStatus.Faulted, refused.PipelineState.SubStatus);
                Assert.Contains("switched off", Assert.Single(refused.PipelineState.Incidents).Message);
            }

            var (on, _) = await EngineAsync(scripts: true);
            await using (on)
            {
                var (_, endless) = await RunAsync(on, Say("while (true) {}"));
                Assert.Equal(PipelineSubStatus.Faulted, endless.PipelineState.SubStatus);
                Assert.Contains("sandbox limits", Assert.Single(endless.PipelineState.Incidents).Message);
            }
        }

        [Fact]
        public void No_dotnet_object_reaches_a_script_and_its_value_comes_back_plain()
        {
            var options = new ScriptStepOptions { Enabled = true };
            var values = new Dictionary<string, object?>
            {
                ["record"] = new Dictionary<string, object?> { ["name"] = "Ada", ["total"] = 120 },
                ["when"] = new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc),
                ["count"] = 3,
                ["flag"] = true,
                ["text"] = "[not json",
                // Objects whose members a wrapper would hand the script: a list's Add, a builder's Clear.
                ["list"] = new List<string> { "a", "b" },
                ["builder"] = new System.Text.StringBuilder("x"),
            };

            var plain = JavaScriptSandbox.EvaluateValue("typeof record.GetType + ',' + record.name + ',' + (record.total + count) + ',' + when + ',' + flag + ',' + text", values, options);

            Assert.True(plain.Succeeded, plain.Error);
            Assert.Equal("undefined,Ada,123,2026-10-06T03:00:00.0000000Z,true,[not json", plain.Value);
            var members = JavaScriptSandbox.EvaluateValue("typeof list.Add + ',' + typeof builder.Clear + ',' + Array.isArray(list) + ',' + list.join('')", values, options);
            Assert.True(members.Succeeded, members.Error);
            Assert.Equal("undefined,undefined,true,ab", members.Value);
            Assert.Equal(42L, JavaScriptSandbox.EvaluateValue("40 + 2", values, options).Value);
            Assert.Equal(2.5, JavaScriptSandbox.EvaluateValue("5 / 2", values, options).Value);
            Assert.Equal(true, JavaScriptSandbox.EvaluateValue("count > 2", values, options).Value);
            Assert.Null(JavaScriptSandbox.EvaluateValue("undefined", values, options).Value);
            Assert.Equal("[1,2]", JavaScriptSandbox.EvaluateValue("[1, 2]", values, options).Value);
        }

        [Fact]
        public async Task The_preview_runs_the_same_sandbox_for_an_author_and_refuses_where_scripts_are_off()
        {
            var on = new PreviewScriptCommandHandler(Options.Create(new PipelineStepOptions { Scripts = { Enabled = true } }));
            var preview = await on.Handle(new PreviewScriptCommand("rows[1].name + ' ' + who", new Dictionary<string, string?> { ["rows"] = "[{\"name\":\"Ada\"},{\"name\":\"Grace\"}]", ["who"] = "x" }), CancellationToken.None);
            Assert.Equal(("Grace x", (string?)null), (preview.Value, preview.Error));
            Assert.Contains("is not defined", (await on.Handle(new PreviewScriptCommand("nobody + 1", new Dictionary<string, string?>()), CancellationToken.None)).Error);

            var off = new PreviewScriptCommandHandler(Options.Create(new PipelineStepOptions()));
            Assert.Contains("switched off", (await off.Handle(new PreviewScriptCommand("1", new Dictionary<string, string?>()), CancellationToken.None)).Error);
        }
    }
}
