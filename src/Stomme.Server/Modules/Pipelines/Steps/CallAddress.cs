#nullable enable
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Shared;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The run's call address (BL-229, n8n's resume URL): the address another system POSTs to so that this run goes on past its
    /// <see cref="WaitForCall"/> - send it to them (an HTTP request, a notification), then wait. One address per run: a second
    /// "Call address" in the run gives the same one. The address is the result; the run's log never shows it, as whoever holds
    /// it can resume the run. Its Form address (BL-229) is the same address with <c>/form</c> after it: where a person fills in
    /// the form a <see cref="WaitForForm"/> of this run waits on.
    /// </summary>
    [Step("Stomme", "Control Flow", "The address another system calls to go on with this run past 'Wait for a call' - send it to them, then wait.", DisplayName = "Call address")]
    public class CallAddress : Step<string>
    {
        /// <summary>Where the run keeps its token (the run's properties, stored with it).</summary>
        public const string TokenProperty = "stomme:call:token";

        [Output(DisplayName = "Form address", Description = "Where a person fills in the form 'Wait for a form' waits on - send it to them, then wait.")]
        public Output<string>? FormAddress { get; set; }

        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var token = TokenOf(context) ?? PipelineCalls.NewToken();
            context.PipelineExecutionContext.Properties[TokenProperty] = token;

            var origin = string.Empty;
            if (await StommeSiteScope.EnsureSiteAsync(context).ConfigureAwait(false) && context.GetService<SiteState>()?.Alias is { } alias)
            {
                origin = alias.Protocol + alias.Name;
            }

            context.Set(Result, origin + "/" + PipelineCalls.Path + token);
            context.Set(FormAddress, origin + "/" + PipelineCalls.Path + token + "/form");
            context.AddExecutionLogEntry("Call address", "The run has an address another system can call, and a form address a person fills in; Wait for a call and Wait for a form wait for them.");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }

        /// <summary>The run's token, if a Call address has made one.</summary>
        public static string? TokenOf(StepExecutionContext context) =>
            context.PipelineExecutionContext.Properties.TryGetValue(TokenProperty, out var kept) ? kept switch
            {
                string text when PipelineCalls.IsToken(text) => text,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element when PipelineCalls.IsToken(element.GetString()) => element.GetString(),
                _ => null,
            } : null;
    }
}
