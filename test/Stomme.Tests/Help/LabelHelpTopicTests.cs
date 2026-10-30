using System;
using System.Threading.Tasks;
using Bunit;
using Stomme.Help;
using Stomme.Models.Help;
using Stomme.Modules.ContextualHelp.Services;
using Stomme.Modules.Controls;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// <c>Label HelpTopic</c> - and the promise that a label WITHOUT it is untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The unset markup is pinned to strings RECORDED from the label as it was before this parameter existed</b>
    /// (the pre-change <c>Label.razor</c> was checked out, rendered by this fixture's own probe, and the output
    /// copied here; the new label then rendered identically). <c>Label</c> has hundreds of uses, so "unchanged" is a
    /// measurement, not an intention.
    /// </para>
    /// <para>
    /// <b>No <c>[Inject]</c> of the help service</b>: a label that does not ask for help must render where help is not
    /// composed at all - this fixture registers no <see cref="IHelpService"/> for exactly that reason.
    /// </para>
    /// </remarks>
    public sealed class LabelHelpTopicTests : BunitContext
    {
        // Recorded from the pre-change Label (see the remarks). Do not "fix" these to match a new rendering.
        // CORRECTION 2026-10-30 (BL-268, a deliberate change unrelated to help topics): the tooltip's help icon carries its help
        // text as alt, which axe required; the recording takes that attribute and the self-closed form a dynamic attribute renders in.
        private const string RecordedPlain = "<label for=\"name\" class=\"db-field-label\">Name: </label>";
        private const string RecordedTooltip = "<span class=\"app-tooltip db-field-label\" data-tip=\"The name\"><label for=\"name\" class=\"db-field-label\">Name: </label> <img src=\"images/help.png\" alt=\"The name\" /></span>";
        private const string RecordedClassed = "<label for=\"name\" class=\"db-field-label extra\">Name: </label>";

        public LabelHelpTopicTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddLocalization();
            Services.AddSingleton(HelpControlClickTests.Inert<Stomme.Services.ILogService>.Create());
            Services.AddSingleton(new Stomme.Shared.SiteState());
        }

        [Fact]
        public void Without_HelpTopic_the_markup_is_BYTE_IDENTICAL_to_the_label_before_it_existed()
        {
            Assert.Equal(RecordedPlain, Render<Label>(p => p.Add(l => l.For, "name").AddChildContent("Name: ")).Markup);
            Assert.Equal(RecordedTooltip, Render<Label>(p => p.Add(l => l.For, "name").Add(l => l.HelpText, "The name").AddChildContent("Name: ")).Markup);
            Assert.Equal(RecordedClassed, Render<Label>(p => p.Add(l => l.For, "name").Add(l => l.Class, "extra").AddChildContent("Name: ")).Markup);
        }

        [Fact]
        public void With_HelpTopic_and_NO_help_service_composed_the_label_is_the_plain_label()
        {
            Assert.Equal(RecordedTooltip, Render<Label>(p => p.Add(l => l.For, "name").Add(l => l.HelpText, "The name")
                .Add(l => l.HelpTopic, "stomme:site.rendermode").AddChildContent("Name: ")).Markup);
        }

        [Fact]
        public void With_HelpTopic_and_no_topic_or_a_FAILING_service_the_label_is_the_plain_label()
        {
            var service = new OneTopicService(null);
            Services.AddSingleton<IHelpService>(service);
            Assert.Equal(RecordedPlain, Render<Label>(p => p.Add(l => l.For, "name").Add(l => l.HelpTopic, "stomme:site.rendermode").AddChildContent("Name: ")).Markup);

            service.Throw = true;
            Assert.Equal(RecordedPlain, Render<Label>(p => p.Add(l => l.For, "name").Add(l => l.HelpTopic, "stomme:site.rendermode").AddChildContent("Name: ")).Markup);
        }

        [Fact]
        public void With_a_resolved_topic_the_label_carries_it_as_a_disclosure_and_the_tooltip_gives_way()
        {
            var service = new OneTopicService(new HelpTopicView("stomme:site.rendermode", "en", "Render mode", "<p><strong>Static</strong> is lightest.</p>"));
            Services.AddSingleton<IHelpService>(service);

            var cut = Render<Label>(p => p.Add(l => l.For, "rendermode").Add(l => l.HelpText, "The default render mode")
                .Add(l => l.HelpTopic, "stomme:site.rendermode").AddChildContent("Render Mode: "));

            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("details.db-help-inline")));
            Assert.Equal("stomme:site.rendermode", service.Asked);
            Assert.Equal("rendermode", cut.Find("label").GetAttribute("for"));        // the label still labels its field
            Assert.Equal("Render mode", cut.Find(".db-help-title").TextContent);
            Assert.NotNull(cut.Find(".db-help-topic strong"));
            Assert.Empty(cut.FindAll("[data-tip]"));                                  // the tooltip gave way to the topic
            Assert.Empty(cut.FindAll("button"));                                      // a disclosure: nothing needs a circuit
        }

        [Fact]
        public void A_labels_panel_opens_RIGHTWARDS_and_never_wider_than_the_viewport()
        {
            // Found by driving: the label's disclosure inherited the header launcher's `right: 0`, and a
            // label sits near the LEFT edge - the panel opened at x = -273, off-screen. bUnit and HTTP render the right
            // markup either way; only a browser measured it. This pins the stylesheet rule that fixed it.
            var css = System.IO.File.ReadAllText(System.IO.Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "wwwroot", "css", "app.css"));
            var rule = System.Text.RegularExpressions.Regex.Match(css, @"\.db-help-inline\s+\.db-help-panel\s*\{([^}]*)\}");

            Assert.True(rule.Success, "app.css has no rule for a label's help panel, so it inherits the header launcher's right-anchoring");
            Assert.Matches(@"left:\s*0", rule.Groups[1].Value);
            Assert.Matches(@"right:\s*auto", rule.Groups[1].Value);
            Assert.Contains("100vw", rule.Groups[1].Value, StringComparison.Ordinal);
        }

        private static string RepositoryRoot()
        {
            var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }

        private sealed class OneTopicService(HelpTopicView topic) : IHelpService
        {
            public string Asked;
            public bool Throw;

            public Task<HelpTopicView> GetTopicAsync(string anchor, string locale = null)
            {
                Asked = anchor;
                return Throw ? throw new InvalidOperationException("unreachable") : Task.FromResult(topic);
            }

            public Task<System.Collections.Generic.List<HelpTopicSummary>> SearchAsync(string query) => throw new NotSupportedException();
            public Task<System.Collections.Generic.List<HelpTopicSummary>> GetTopicsAsync() => throw new NotSupportedException();
            public Task<System.Collections.Generic.List<HelpConflictView>> GetConflictsAsync() => throw new NotSupportedException();
            public Task<HelpTopicEditView> GetTopicForEditAsync(Guid id) => throw new NotSupportedException();
            public Task<Guid> SaveTopicAsync(HelpTopicSaveInput input) => throw new NotSupportedException();
            public Task SetPublishedAsync(Guid id, bool published) => throw new NotSupportedException();
            public Task<string> PreviewAsync(string markdown) => throw new NotSupportedException();
            public Task DeleteTopicAsync(Guid id) => throw new NotSupportedException();
            public Task ResetTopicAsync(Guid id) => throw new NotSupportedException();
        }
    }
}
