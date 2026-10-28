#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// What this site lets the editor offer (BL-229 increment 5b): JavaScript expressions where <c>Pipelines:Scripts:Enabled</c>,
    /// whether it receives webhooks, and whether its model can build in the editor - read once when the editor opens, so it offers
    /// nothing the run (or the model) would refuse.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetCapabilitiesQuery : IQuery<PipelineCapabilitiesView>;

    public sealed class GetCapabilitiesQueryHandler(IOptions<PipelineStepOptions> steps, IOptions<PipelineTriggerOptions> triggers, IPipelineGraphDesigner designer, IAccessGuard accessGuard)
        : IQueryHandler<GetCapabilitiesQuery, PipelineCapabilitiesView>
    {
        public async Task<PipelineCapabilitiesView> Handle(GetCapabilitiesQuery request, CancellationToken ct) =>
            new PipelineCapabilitiesView
            {
                Scripts = steps.Value.Scripts.Enabled,
                Webhooks = triggers.Value.Webhooks.Enabled,
                // The person's key first: a person without it costs the site no read of its assistant settings.
                Ai = await accessGuard.IsAllowedAsync(null, Stomme.AiAssistant.AssistantPermissions.Use, ct) && await designer.IsAvailableAsync(ct),
            };
    }

    /// <summary>
    /// A JavaScript expression's value for the editor's preview (BL-229 increment 5b): run in the same bounded sandbox the engine
    /// uses, with the variables' values the editor previews with, and nothing else - no step, no store. Authoring rights, and only
    /// where scripts are on.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record PreviewScriptCommand(string Script, IReadOnlyDictionary<string, string?> Values) : ICommand<ScriptPreviewView>;

    public sealed class PreviewScriptCommandHandler(IOptions<PipelineStepOptions> options) : ICommandHandler<PreviewScriptCommand, ScriptPreviewView>
    {
        public Task<ScriptPreviewView> Handle(PreviewScriptCommand request, CancellationToken ct)
        {
            var scripts = options.Value.Scripts;
            if (!scripts.Enabled)
            {
                return Task.FromResult(new ScriptPreviewView { Error = "JavaScript expressions are switched off on this site (Pipelines:Scripts:Enabled)." });
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in request.Values ?? new Dictionary<string, string?>())
            {
                values[name] = value;
            }

            var evaluation = JavaScriptSandbox.EvaluateValue(request.Script ?? string.Empty, values, scripts);
            return Task.FromResult(evaluation.Succeeded
                ? new ScriptPreviewView { Value = PipelineTemplate.Text(evaluation.Value) }
                : new ScriptPreviewView { Error = evaluation.Error });
        }
    }
}
