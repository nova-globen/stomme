#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The stimulus a "File added" trigger is indexed by: the folder it watches, the names it takes, and whether it looks into
    /// subfolders and takes changed files - so pipelines watching the same folder the same way share one look at it.
    /// </summary>
    public sealed record FileAddedStimulus(string Folder, string Pattern, bool Subfolders, bool Changed);

    /// <summary>
    /// n8n's Local File Trigger, as an engine TRIGGER: a published pipeline that starts with it is indexed under the folder
    /// of the site's pipeline folder it watches, and <c>Triggers/WatchedFoldersTask.cs</c> (a recurring task the engine runs per
    /// site tenant) looks at that folder and starts a run for each file put there since it last looked - one run a file, the file
    /// as the trigger's result (<see cref="PipelineFileEvent"/>). Files already there when the pipeline is published start
    /// nothing (n8n's "ignore existing files", its default). A folder that is not one, or a pattern that is no file name, is
    /// refused at publish.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline when a file is put in a folder of the site's pipeline folder - n8n's Local File Trigger.", DisplayName = "File added")]
    public class FileAdded : Trigger<string>
    {
        public const string FileInputKey = "__FileAdded";

        [Input(Description = "The folder of the site's pipeline folder to watch - 'inbox', or 'forms/invoices' for a form's files. Empty is the whole pipeline folder.",
            UIHint = PipelineInputHints.PipelineFile, Order = 1)]
        public Input<string?> Folder { get; set; } = null!;

        [Input(DisplayName = "Files matching", Description = "Only files whose name matches start a run: '*.pdf', or several - '*.csv, *.xlsx'. * is any characters, ? is one. Empty is every file.", Order = 2)]
        public Input<string?> Pattern { get; set; } = null!;

        [Input(DisplayName = "Include subfolders", Description = "A file put in a folder inside it starts a run too.", Order = 3)]
        public Input<bool> Subfolders { get; set; } = new(new Literal<bool>(false));

        [Input(DisplayName = "Changed files too", Description = "A file written again - its size or its time not what they were - starts a run too, not only a new file.", Order = 4)]
        public Input<bool> Changed { get; set; } = new(new Literal<bool>(false));

        /// <summary>The stimulus for a folder, its patterns and its two switches - as it is indexed at publish - or the refusal, thrown.</summary>
        public static FileAddedStimulus Stimulus(string? folder, string? pattern, bool subfolders, bool changed)
        {
            if ((PipelineFileEvent.FolderProblem(folder) ?? PipelineFileEvent.PatternProblem(pattern)) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            return new FileAddedStimulus(PipelineFilePaths.Normalize(folder ?? ""), PipelineFileEvent.NormalizePattern(pattern), subfolders, changed);
        }

        protected override object GetTriggerPayload(TriggerIndexingContext context) =>
            Stimulus(context.Get(Folder), context.Get(Pattern), context.Get(Subfolders), context.Get(Changed));

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself (measured - the run sat Suspended).
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            context.SetResult(context.TryGetPipelineInput<string>(FileInputKey, out var file) ? file : "");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}
