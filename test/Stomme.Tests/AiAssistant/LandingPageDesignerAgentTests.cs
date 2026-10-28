#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.Landing.Infrastructure;
using Xunit;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// BL-98 increments 3 and 4 - the Landing Page Designer over the site's metered model (a scripted one here) and the real
    /// landing sanitizer: a submission is accepted only as the sanitizer leaves it; anything it had to take out goes back to
    /// the model to put right, within <see cref="LandingPageDesignerAgent.MaxAttempts"/> submissions; a revised section must be
    /// ONE section of the same name; the model off for the site is the refusal the Landing request surfaces.
    /// </summary>
    public sealed class LandingPageDesignerAgentTests
    {
        private const string Clean = "<section data-section=\"hero\" style=\"padding: 2rem\"><h1>Fresh bread</h1><a href=\"/order\">Order</a></section><section data-section=\"contact\"><p>Call us.</p></section>";

        private static (LandingPageDesignerAgent Agent, TurnScriptChatClient Model, ScriptedModelAccess Access) Rig(params Microsoft.Extensions.AI.ChatMessage[] turns)
        {
            var model = new TurnScriptChatClient(turns);
            var access = new ScriptedModelAccess(model);
            return (new LandingPageDesignerAgent(access, new LandingHtmlSanitizer()), model, access);
        }

        [Fact]
        public async Task A_clean_page_is_accepted_on_its_first_submission_and_the_call_is_the_designers()
        {
            var (agent, model, access) = Rig(
                TurnScriptChatClient.Call("s1", "SubmitLandingPage", "html", Clean),
                TurnScriptChatClient.Answer("Done."));

            var result = await agent.GenerateAsync("A bakery in the old town");

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { "hero", "contact" }, result.Sections);
            Assert.Contains("<h1>Fresh bread</h1>", result.Html);
            Assert.Contains("target=\"_top\"", result.Html);
            Assert.Equal(new[] { LandingPageDesignerAgent.AgentName }, access.Opened);
            Assert.Equal(1, model.Turns);
            Assert.Contains("A bakery in the old town", model.Received[0].Last().Text);
            Assert.NotEmpty(access.Usage.Events);
            Assert.All(access.Usage.Events, usage => Assert.Equal(LandingPageDesignerAgent.AgentName, usage.Agent));
        }

        [Fact]
        public async Task What_the_sanitizer_takes_out_goes_back_to_the_model_and_the_corrected_page_is_accepted()
        {
            var (agent, model, _) = Rig(
                TurnScriptChatClient.Call("s1", "SubmitLandingPage", "html", "<section data-section=\"hero\" onclick=\"x()\"><script>alert(1)</script><h1>Hi</h1></section>"),
                TurnScriptChatClient.Call("s2", "SubmitLandingPage", "html", Clean),
                TurnScriptChatClient.Answer("Fixed."));

            var result = await agent.GenerateAsync("A bakery");

            Assert.True(result.Succeeded);
            Assert.Contains("Fresh bread", result.Html);
            var feedback = model.ToolResults();
            Assert.Contains(feedback, text => text.Contains("<script>", StringComparison.Ordinal) && text.Contains("onclick", StringComparison.Ordinal));
            Assert.Equal(2, model.Turns); // accepted ends the turn: no further metered round for a closing text
        }

        [Fact]
        public async Task Past_its_attempts_the_designer_fails_with_what_is_still_wrong()
        {
            var bad = "<p>No section here.</p>";
            var (agent, model, _) = Rig(
                TurnScriptChatClient.Call("s1", "SubmitLandingPage", "html", bad),
                TurnScriptChatClient.Call("s2", "SubmitLandingPage", "html", bad),
                TurnScriptChatClient.Call("s3", "SubmitLandingPage", "html", bad),
                TurnScriptChatClient.Call("s4", "SubmitLandingPage", "html", Clean),
                TurnScriptChatClient.Answer("Gave up."));

            var result = await agent.GenerateAsync("A bakery");

            Assert.False(result.Succeeded);
            Assert.Null(result.Html);
            Assert.Equal(LandingPageDesignerAgent.MaxAttempts + 1, model.Turns); // bounded: the fourth submission is refused and ends it
            Assert.Contains(result.Problems, problem => problem.Contains("data-section", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_revised_section_must_be_one_section_of_the_same_name()
        {
            var (agent, model, _) = Rig(
                TurnScriptChatClient.Call("s1", "SubmitSection", "html", "<section data-section=\"banner\"><h1>New</h1></section>"),
                TurnScriptChatClient.Call("s2", "SubmitSection", "html", "<section data-section=\"hero\"><h1>Baked daily</h1></section>"),
                TurnScriptChatClient.Answer("Revised."));

            var result = await agent.EditSectionAsync("<section data-section=\"hero\"><h1>Fresh bread</h1></section>", "hero", "Say we bake daily");

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { "hero" }, result.Sections);
            Assert.Contains("Baked daily", result.Html);
            Assert.Contains(model.ToolResults(), text => text.Contains("ONE <section data-section=\"hero\">", StringComparison.Ordinal));
            Assert.Contains("Say we bake daily", model.Received[0].Last().Text);
        }

        [Fact]
        public async Task The_model_off_for_the_site_is_the_refusal_and_no_call_is_made()
        {
            var (agent, model, access) = Rig(TurnScriptChatClient.Answer("unused"));
            access.Enabled = false;

            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => agent.GenerateAsync("A bakery"));

            Assert.Equal(AssistantUnavailableReason.Disabled, refused.Reason);
            Assert.Equal(0, model.Turns);
        }
    }
}
