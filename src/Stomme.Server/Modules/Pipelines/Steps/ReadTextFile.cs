#nullable enable
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>Read a text file from the site's pipeline folder (per-site).</summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Files", "Read a text file from the site's pipeline folder.", DisplayName = "Read text file")]
    public class ReadTextFile : DataStep
    {
        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "The file's path, relative to the site's pipeline folder.", Order = 1)]
        public Input<string> Path { get; set; } = null!;

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var relative = Required(context, Path, "Path");
            var full = PipelineFileSandbox.Resolve(context, relative, out var refusal);
            if (full is null)
            {
                await FailAsync(context, "PATH_REFUSED", refusal!).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(full))
            {
                await FailAsync(context, "FILE_NOT_FOUND", $"No file at '{relative}'.").ConfigureAwait(false);
                return;
            }

            var max = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new FileStepOptions().MaxFileBytes;
            if (new FileInfo(full).Length > max)
            {
                await FailAsync(context, "FILE_TOO_LARGE", $"'{relative}' is larger than Pipelines:Files:MaxFileBytes ({max}).").ConfigureAwait(false);
                return;
            }

            var text = await File.ReadAllTextAsync(full, Encoding.UTF8, context.CancellationToken).ConfigureAwait(false);
            await SucceedAsync(context, text).ConfigureAwait(false);
        }
    }
}
