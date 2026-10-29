#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Landing.Application;
using Stomme.Modules.Landing.Infrastructure;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// The Landing Page Designer: a site's landing page as static HTML from an administrator's brief, and one section of it rewritten from an
    /// instruction. The model is the SITE's (quota-gated and metered like every agent); it SUBMITS through one tool, and what
    /// the tool accepts is only what the landing sanitizer leaves unchanged - anything it had to take out is reported back
    /// for the model to put right, within <see cref="MaxAttempts"/> submissions. It PROPOSES: nothing is stored here.
    /// </summary>
    public sealed class LandingPageDesignerAgent(IAssistantModelAccess models, LandingHtmlSanitizer sanitizer) : ILandingPageDesigner
    {
        public const string AgentName = "landing-page-designer";

        public const int MaxAttempts = 3;

        private const string Rules =
            "Rules for the HTML: a fragment for inside <body> - no <html>, <head> or <body>; no <script>, <style>, forms, frames, " +
            "SVG or event-handler attributes; style only with inline style attributes using layout, typography, colour, spacing and " +
            "border properties - no url(), no position; links only to site paths such as /about or to https addresses; images only " +
            "from https addresses the brief gives. Every top-level block is a <section> with a data-section attribute naming it in " +
            "lowercase with hyphens (hero, features, contact...), each name used once. Write in the language of the brief.";

        private const string GenerateSystemPrompt =
            "You are the Landing Page Designer. Design the landing page a site's visitors see first, from the administrator's " +
            "brief: a clear headline, what the site offers, and where to go next. " + Rules +
            " Call SubmitLandingPage with the complete HTML; if it reports problems, fix them and submit again.";

        private const string EditSystemPrompt =
            "You are the Landing Page Designer, revising ONE section of an existing landing page. Rewrite only that section as the " +
            "administrator asks, keeping its data-section name. " + Rules +
            " Call SubmitSection with the section's complete HTML - one <section> element; if it reports problems, fix them and submit again.";

        public async Task<LandingDesignResult> GenerateAsync(string brief, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(brief);
            var tool = new Submission(sanitizer, section: null);
            await RunAsync(GenerateSystemPrompt, "The brief:\n" + brief.Trim(), tool, "SubmitLandingPage", "Submits the complete landing page HTML.", cancellationToken);
            return tool.Result();
        }

        public async Task<LandingDesignResult> EditSectionAsync(string sectionHtml, string section, string instruction, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sectionHtml);
            ArgumentException.ThrowIfNullOrWhiteSpace(section);
            ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
            var tool = new Submission(sanitizer, section);
            var prompt = $"The section '{section}' as it is now:\n{sectionHtml}\n\nThe administrator asks:\n{instruction.Trim()}";
            await RunAsync(EditSystemPrompt, prompt, tool, "SubmitSection", "Submits the rewritten section's HTML - one <section> element.", cancellationToken);
            return tool.Result();
        }

        private async Task RunAsync(string systemPrompt, string userPrompt, Submission tool, string toolName, string toolDescription, CancellationToken cancellationToken)
        {
            using var model = await models.OpenAsync(AgentName, cancellationToken);
            // Bounded to the submissions it may make and one past them; the tool also ends the turn on acceptance and on the
            // refused extra one. Every round is a metered call.
            var client = AssistantTurn.WithTools(model.Client, MaxAttempts + 1);
            var options = new ChatOptions
            {
                ModelId = model.Model,
                Temperature = model.Temperature,
                Tools = [AIFunctionFactory.Create((Func<string, string>)tool.Submit, toolName, toolDescription)],
            };
            await client.GetResponseAsync([new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, userPrompt)], options, cancellationToken);
        }

        /// <summary>The one tool: a submission is accepted only when the sanitizer leaves it as it is.</summary>
        private sealed class Submission(LandingHtmlSanitizer sanitizer, string? section)
        {
            private int _attempts;
            private string? _accepted;
            private IReadOnlyList<string> _sections = Array.Empty<string>();
            private IReadOnlyList<string> _problems = new[] { "The model submitted nothing." };

            [Description("Submits the HTML. Returns 'accepted', or the problems to fix before submitting again.")]
            public string Submit([Description("The complete HTML.")] string html)
            {
                if (_accepted is not null)
                {
                    Stop();
                    return "accepted";
                }

                if (++_attempts > MaxAttempts)
                {
                    Stop();
                    return "No more submissions are accepted.";
                }

                var result = sanitizer.Sanitize(html);
                var problems = result.Removed.Select(removed => "removed: " + removed).ToList();
                if (result.Html.Length == 0)
                {
                    problems.Add("the HTML is empty after sanitising");
                }

                if (section is null && result.Sections.Count == 0)
                {
                    problems.Add("no top-level <section> carries a data-section name");
                }

                if (section is not null && !(result.Sections.Count == 1 && result.Sections[0] == section))
                {
                    problems.Add($"the answer must be ONE <section data-section=\"{section}\"> element");
                }

                if (problems.Count > 0)
                {
                    _problems = problems;
                    return "Not accepted. Fix these and submit again:\n- " + string.Join("\n- ", problems);
                }

                _accepted = result.Html;
                _sections = result.Sections;
                Stop();
                return "accepted";
            }

            // Accepted is the end of the turn: no further round - a closing text would be a metered call that changes nothing.
            private static void Stop()
            {
                if (FunctionInvokingChatClient.CurrentContext is { } context)
                {
                    context.Terminate = true;
                }
            }

            public LandingDesignResult Result() =>
                _accepted is { } html ? LandingDesignResult.Ok(html, _sections) : LandingDesignResult.Failed(_problems);
        }
    }
}
