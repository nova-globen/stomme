#nullable enable
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// Write (or append) text to a file in the site's pipeline folder (the predecessor platform's file <c>write</c>/<c>append</c>,
    /// per-site). The content passes through as the result, so a chain can keep going after storing.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Files", "Write text to a file in the site's pipeline folder.", DisplayName = "Write text file")]
    public class WriteTextFile : DataStep
    {
        // UTF-8 with no byte-order mark (found driving the Form trigger: a JSON file written here began with one, which
        // many JSON readers refuse). Reading still accepts a file that has one.
        private static readonly UTF8Encoding NoByteOrderMark = new(encoderShouldEmitUTF8Identifier: false);

        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "The file's path, relative to the site's pipeline folder; folders are created.", Order = 1)]
        public Input<string> Path { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "The text to write. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Content { get; set; }

        [Input(Description = "Append to the file instead of replacing it.", DefaultValue = false, Order = 3)]
        public Input<bool> Append { get; set; } = new(new Literal<bool>(false));

        [Output(DisplayName = "Full path", Description = "Where the file was written.")]
        public Output<string>? FullPath { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var relative = Required(context, Path, "Path");
            var full = PipelineFileSandbox.Resolve(context, relative, out var refusal);
            if (full is null)
            {
                await FailAsync(context, "PATH_REFUSED", refusal!).ConfigureAwait(false);
                return;
            }

            var content = TextOrLastResult(context, Content) ?? "";
            var max = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new FileStepOptions().MaxFileBytes;
            var bytes = Encoding.UTF8.GetByteCount(content);
            if (bytes > max || (Append.GetOrDefault(context) && File.Exists(full) && new FileInfo(full).Length + bytes > max))
            {
                await FailAsync(context, "FILE_TOO_LARGE", $"Writing '{relative}' would exceed Pipelines:Files:MaxFileBytes ({max}).").ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            if (Append.GetOrDefault(context))
            {
                await File.AppendAllTextAsync(full, content, NoByteOrderMark, context.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(full, content, NoByteOrderMark, context.CancellationToken).ConfigureAwait(false);
            }

            context.Set(FullPath, full);
            await SucceedAsync(context, content).ConfigureAwait(false);
        }
    }
}
