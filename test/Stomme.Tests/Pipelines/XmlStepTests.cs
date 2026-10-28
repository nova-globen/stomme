#nullable enable
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - XML (n8n's XML node), run by the engine as the host composes it: XML into JSON (attributes, repeated elements as
    /// lists, text, mixed text), JSON into XML and back, names XML cannot hold encoded, and a DTD refused so no entity reaches a
    /// file or the network.
    /// </summary>
    public sealed class XmlStepTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new Silent());
                module.AddStepsFrom<PipelineStepsMarker>();
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class Silent : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => TextWriter.Null;
        }

        private async Task<(RunPipelineResult Run, string Result)> RunAsync(Xml step)
        {
            using var scope = _services.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
            var outputs = run.Journal.StepExecutionContexts.Single(c => c.Step is Xml).GetOutputs();
            return (run, outputs.TryGetValue("Result", out var result) ? (string)result : "");
        }

        private const string Orders = "<orders shop=\"north\"><order id=\"1\"><item>tea</item><qty>3</qty></order><order id=\"2\"><item>cake</item><qty>1</qty></order><note>Late <b>today</b></note><empty/></orders>";

        [Fact]
        public async Task Xml_becomes_json_with_attributes_lists_and_text()
        {
            var (_, result) = await RunAsync(new Xml { Value = new(Orders) });
            var orders = JsonNode.Parse(result)!["orders"]!;
            Assert.Equal("north", orders["@shop"]!.GetValue<string>());
            var list = orders["order"]!.AsArray();
            Assert.Equal(2, list.Count);
            Assert.Equal(("1", "tea", "3"), (list[0]!["@id"]!.GetValue<string>(), list[0]!["item"]!.GetValue<string>(), list[0]!["qty"]!.GetValue<string>()));
            Assert.Equal("Late", orders["note"]!["#text"]!.GetValue<string>());
            Assert.Equal("today", orders["note"]!["b"]!.GetValue<string>());
            Assert.Null(orders["empty"]);
        }

        [Fact]
        public async Task Json_becomes_xml_and_back_with_names_xml_cannot_hold_encoded()
        {
            var (_, xml) = await RunAsync(new Xml
            {
                Operation = new(XmlOperation.ToXml), RootName = new("order"),
                Value = new("{\"@id\":\"7\",\"item\":\"tea\",\"lines\":[{\"sku\":\"A\"},{\"sku\":\"B\"}],\"unit price\":9.5}"),
            });
            Assert.Equal("<order id=\"7\"><item>tea</item><lines><sku>A</sku></lines><lines><sku>B</sku></lines><unit_x0020_price>9.5</unit_x0020_price></order>", xml);

            var (_, back) = await RunAsync(new Xml { Value = new(xml) });
            Assert.Equal("{\"order\":{\"@id\":\"7\",\"item\":\"tea\",\"lines\":[{\"sku\":\"A\"},{\"sku\":\"B\"}],\"unit_x0020_price\":\"9.5\"}}", back);

            var (_, list) = await RunAsync(new Xml { Operation = new(XmlOperation.ToXml), Value = new("[1,2]") });
            Assert.Equal("<root><item>1</item><item>2</item></root>", list);
        }

        [Fact]
        public async Task A_dtd_is_refused_so_no_entity_reaches_a_file_and_broken_input_says_why()
        {
            var (run, _) = await RunAsync(new Xml { Value = new("<!DOCTYPE x [<!ENTITY secret SYSTEM \"file:///c:/windows/win.ini\">]><x>&secret;</x>") });
            Assert.Equal(PipelineSubStatus.Faulted, run.PipelineState.SubStatus);
            Assert.Contains(run.PipelineState.Incidents, incident => incident.Message.Contains("DTD", System.StringComparison.OrdinalIgnoreCase));

            var (broken, _) = await RunAsync(new Xml { Value = new("<open>") });
            Assert.Contains(broken.PipelineState.Incidents, incident => incident.Message.Contains("not XML this step reads"));
            var (notJson, _) = await RunAsync(new Xml { Operation = new(XmlOperation.ToXml), Value = new("{nope") });
            Assert.Contains(notJson.PipelineState.Incidents, incident => incident.Message.Contains("not JSON"));
        }
    }
}
