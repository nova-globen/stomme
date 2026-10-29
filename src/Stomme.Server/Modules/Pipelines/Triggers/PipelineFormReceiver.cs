#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// n8n's Form trigger, the site's side: the form a published pipeline offers under a name (read from its Form step as
    /// the engine indexed it - the version that is PUBLISHED, so a draft's changes show once published), and a submission sent on
    /// as the trigger's stimulus, which starts every published pipeline with a form of that name.
    /// </summary>
    public sealed class PipelineFormReceiver(ITriggerStore triggers, IStimulusHasher hasher, IPipelineDefinitionStore definitions, IStimulusDispatcher dispatcher)
    {
        /// <summary>
        /// The form under the name, as its Form step says it literally; null when no published pipeline on the site has one. Several
        /// pipelines may start on one form: its page is the first one's, and a next page can follow when ANY of them has one.
        /// </summary>
        public async Task<PipelineFormSpec?> FindAsync(string name, CancellationToken cancellationToken)
        {
            var stored = (await triggers.FindManyAsync(new TriggerFilter { Name = Form.StepTypeName, Hash = hasher.Hash(Form.StepTypeName, new FormStimulus(name)) }, cancellationToken).ConfigureAwait(false)).ToList();
            PipelineGraphStep? step = null;
            var leadsOn = false;
            foreach (var trigger in stored)
            {
                var definition = await definitions.FindAsync(new PipelineDefinitionFilter { Id = trigger.PipelineDefinitionVersionId }, cancellationToken).ConfigureAwait(false);
                if (definition is null)
                {
                    continue;
                }

                var graph = PipelineGraphCodec.ToGraph(definition.StringData);
                step ??= graph.Steps.FirstOrDefault(each => each.Type == Form.StepTypeName && each.Id == trigger.StepId);
                leadsOn |= graph.Steps.Any(each => PipelineForms.IsNextPage(each) && !each.Disabled);
            }

            if (step is null)
            {
                return null;
            }

            string? Literal(string input) => step.Inputs.TryGetValue(input, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
            var fields = PipelineForms.Parse(Literal(nameof(Form.Fields)), out _) ?? new List<PipelineFormField>();
            return new PipelineFormSpec(name, Literal(nameof(Form.Title)) ?? name, Literal(nameof(Form.Description)), fields,
                Literal(nameof(Form.SubmitLabel)) ?? "Submit", Literal(nameof(Form.Answer)))
            {
                LeadsOn = leadsOn,
                Redirect = Form.IsRedirect(Literal(nameof(Form.Redirect))) ? Literal(nameof(Form.Redirect))!.Trim() : null,
            };
        }

        /// <summary>
        /// Starts every published pipeline with the form, the answers (a JSON object) as its result. Dispatched, never awaited; with
        /// <paramref name="continueTo"/>, the submission's id rides with the run, for a next page to take the person on.
        /// </summary>
        public Task SubmitAsync(string name, string answers, string? continueTo, CancellationToken cancellationToken)
        {
            var input = new Dictionary<string, object> { [Form.SubmissionInputKey] = answers, [Form.NameInputKey] = name };
            if (continueTo is not null)
            {
                input[PipelineFormContinuations.InputKey] = continueTo;
            }

            return dispatcher.SendAsync(new DispatchStimulusRequest
            {
                StepTypeName = Form.StepTypeName,
                Stimulus = new FormStimulus(name),
                Metadata = new StimulusMetadata { Input = input },
            }, cancellationToken);
        }
    }
}
