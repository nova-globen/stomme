#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// A run waiting for a form (BL-229, <see cref="WaitForForm"/>), the site's side: the form a run's form address shows - read
    /// from the waiting run's bookmark, found by the hash of the address's token (the request's site's, as every bookmark read is) -
    /// and a submission DISPATCHED to it through the engine's stimulus path, never awaited (D111 item 3). No waiting bookmark, no
    /// form: the page says it is closed.
    /// </summary>
    public sealed class PipelineFormWaits(IBookmarkStore bookmarks, IStimulusHasher hasher, IStimulusDispatcher dispatcher)
    {
        /// <summary>The form the run with this token waits on; null when none waits (answered, timed out, or never asked).</summary>
        public async Task<PipelineFormSpec?> FindAsync(string token, CancellationToken cancellationToken)
        {
            if (!PipelineCalls.IsToken(token))
            {
                return null;
            }

            var hash = hasher.Hash(WaitForForm.StepTypeName, new FormWaitStimulus(PipelineCalls.Hash(token)));
            var waiting = await bookmarks.FindManyAsync(new BookmarkFilter { Name = WaitForForm.StepTypeName, Hash = hash }, cancellationToken).ConfigureAwait(false);
            return waiting.Select(bookmark => WaitForForm.FormOf(bookmark.Metadata)).FirstOrDefault(form => form is not null);
        }

        /// <summary>Resumes the waiting run with the answers (a JSON object); with <paramref name="continueTo"/>, the id a next page takes the person on under.</summary>
        public Task SubmitAsync(string token, string answers, string? continueTo, CancellationToken cancellationToken) =>
            dispatcher.SendAsync(new DispatchStimulusRequest
            {
                StepTypeName = WaitForForm.StepTypeName,
                Stimulus = new FormWaitStimulus(PipelineCalls.Hash(token)),
                Metadata = new StimulusMetadata { Input = WaitForForm.SubmissionInput(answers, continueTo) },
            }, cancellationToken);
    }
}
