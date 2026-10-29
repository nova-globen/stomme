#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's HTML node, its Convert to HTML Table and Generate HTML Template, as Make HTML run by the real engine: records
    /// as a table - a header of their fields, a row each, every header and value ENCODED, a nested value as its JSON, columns chosen,
    /// a caption, headers in words - and a template as written; and the template language's html filter that encodes a value.
    /// </summary>
    public sealed class MakeHtmlTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private async Task<IDictionary<string, object>> OutputsAsync(MakeHtml step)
        {
            using var scope = _services.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
            return run.Journal.StepExecutionContexts.Single(context => context.Step is MakeHtml).GetOutputs();
        }

        [Fact]
        public async Task Records_become_a_table_with_every_value_encoded()
        {
            var outputs = await OutputsAsync(new MakeHtml
            {
                Records = new("""[{"orderId": 7, "customer": "<script>alert(1)</script>", "lines": [{"sku": "A"}]}, {"orderId": 8, "customer": "Grace & Co", "note": "\"rush\""}]"""),
                Caption = new("Orders <today>"),
            });

            Assert.Equal(2, outputs["RowCount"]);
            Assert.Equal(
                "<table>\n  <caption>Orders &lt;today&gt;</caption>\n  <thead>\n    <tr><th>orderId</th><th>customer</th><th>lines</th><th>note</th></tr>\n  </thead>\n  <tbody>\n"
                + "    <tr><td>7</td><td>&lt;script&gt;alert(1)&lt;/script&gt;</td><td>[{&quot;sku&quot;:&quot;A&quot;}]</td><td></td></tr>\n"
                + "    <tr><td>8</td><td>Grace &amp; Co</td><td></td><td>&quot;rush&quot;</td></tr>\n  </tbody>\n</table>",
                outputs["Result"]);
        }

        [Fact]
        public async Task Columns_are_chosen_and_headers_written_in_words()
        {
            var outputs = await OutputsAsync(new MakeHtml { Records = new("""[{"order_id": 7, "customerName": "Ada", "VAT": 25}]"""), Columns = new("customerName, order_id, VAT"), CapitalizeHeaders = new(true) });
            Assert.Contains("<tr><th>Customer Name</th><th>Order Id</th><th>VAT</th></tr>", (string)outputs["Result"]);
            Assert.Contains("<tr><td>Ada</td><td>7</td><td>25</td></tr>", (string)outputs["Result"]);
            Assert.Equal(("Order Id", "Customer Name", "VAT", "Html Body"), (MakeHtml.Header("orderId"), MakeHtml.Header("customer-name"), MakeHtml.Header("VAT"), MakeHtml.Header("html body")));

            // A field's name is a record's too: encoded as its values are.
            var named = MakeHtml.Table(new[] { "<i>x</i>" }, new List<System.Text.Json.Nodes.JsonObject> { new() { ["<i>x</i>"] = 1 } }, null, false);
            Assert.Contains("<th>&lt;i&gt;x&lt;/i&gt;</th>", named);
        }

        [Fact]
        public async Task A_template_is_the_result_as_written_and_records_that_are_none_fail_saying_why()
        {
            var template = await OutputsAsync(new MakeHtml { Make = new(HtmlMake.Template), Html = new("<h1>Orders</h1>") });
            Assert.Equal("<h1>Orders</h1>", template["Result"]);

            var none = await OutputsAsync(new MakeHtml { Records = new("not records"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("RECORDS_INVALID:", (string)none["Error"]);
        }

        [Fact]
        public void The_html_filter_encodes_a_value()
        {
            (bool, object?) Lookup(string name) => name == "who" ? (true, "<b>Ada</b> & \"Grace\"") : (false, null);
            Assert.Equal("<p>&lt;b&gt;Ada&lt;/b&gt; &amp; &quot;Grace&quot;</p>", PipelineTemplate.Render("<p>{{ who | html }}</p>", Lookup));
            Assert.Equal("<p><b>Ada</b> & \"Grace\"</p>", PipelineTemplate.Render("<p>{{ who }}</p>", Lookup)); // without it, as it is
            Assert.Equal("<p><b>Ada</b> & \"Grace\"</p>", PipelineTemplate.Render("<p>{{ who | raw }}</p>", Lookup)); // raw: on purpose
            Assert.Contains(PipelineTemplateFilters.All, filter => filter.Name == "html");
            Assert.Contains(PipelineTemplateFilters.All, filter => filter.Name == "raw");
        }
    }
}
