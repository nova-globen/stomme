#nullable enable
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Markdown (n8n's Markdown node), run by the engine as the host composes it: Markdown into HTML through the help
    /// module's pipeline and allowlist - formatting kept, a script and an event handler gone - and HTML into plain text with its
    /// blocks on lines of their own.
    /// </summary>
    public sealed class ConvertMarkdownTests : IAsyncLifetime
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

        private async Task<string> ResultOf(ConvertMarkdown step)
        {
            using var scope = _services.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
            return (string)run.Journal.StepExecutionContexts.Single(c => c.Step is ConvertMarkdown).GetOutputs()["Result"];
        }

        [Fact]
        public async Task Markdown_becomes_safe_html()
        {
            var html = await ResultOf(new ConvertMarkdown { Value = new("# Orders\n\nShip **three** to <https://example.org>.\n\n<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>") });
            Assert.Contains("<h1>Orders</h1>", html);
            Assert.Contains("<strong>three</strong>", html);
            Assert.Contains("href=\"https://example.org\"", html);
            // Raw HTML is shown as the text it is, never made an element: no script, no image carrying a handler.
            Assert.DoesNotContain("<script", html);
            Assert.DoesNotContain("<img", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("&lt;img", html);
        }

        [Fact]
        public async Task Html_becomes_plain_text_with_its_blocks_on_lines()
        {
            var text = await ResultOf(new ConvertMarkdown
            {
                Operation = new(MarkdownOperation.HtmlToText),
                Value = new("<html><head><style>p{}</style></head><body><h1>Orders</h1><p>Ship   <b>three</b><br>today.</p><ul><li>tea</li><li>cake</li></ul><script>x()</script></body></html>"),
            });
            Assert.Equal("Orders\n\nShip three\ntoday.\n\n- tea\n- cake", text);
        }
    }
}
