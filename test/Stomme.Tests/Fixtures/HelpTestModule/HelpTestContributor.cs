#nullable enable
using System.Collections.Generic;
using Stomme.Help;

namespace Stomme.Tests.Fixtures.HelpTestModule
{
    /// <summary>
    /// A contributor the help suites drive (BL-84). It has a public parameterless constructor and lives in an assembly
    /// whose name contains <c>Stomme</c>, so it is found by the CONVENTION - nothing registers it.
    /// </summary>
    /// <remarks>
    /// <b>It names nothing but <c>Stomme.Help</c> from <c>Stomme.Shared</c></b> - the bar a packaged module is held
    /// to (D103 item 2), asserted by <c>HelpContributorConflictTests</c>. It ships one anchor in TWO locales, so the
    /// locale chain has something to fall back from and to.
    /// </remarks>
    public sealed class HelpTestContributor : IHelpTopicContributor
    {
        public const string Id = "helptest";
        public const string Anchor = "helptest:widget";

        public string ContributorId => Id;

        public IReadOnlyCollection<HelpTopicContribution> Topics { get; } = new[]
        {
            new HelpTopicContribution(Anchor, "en", "The widget", "The **widget** does one thing."),
            new HelpTopicContribution(Anchor, "sv-SE", "Widgeten", "**Widgeten** gör en sak."),
        };
    }
}
