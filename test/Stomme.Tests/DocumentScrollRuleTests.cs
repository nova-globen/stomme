using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The document scrolls: app.css takes <c>body</c> back from FluentUI 5's reset.
    /// </summary>
    /// <remarks>
    /// FluentUI 5 adopts a stylesheet whose <c>body</c> rule is the reset of an app laid out with FluentLayout, which scrolls
    /// inside: <c>height: 100dvh; overflow: hidden</c>. Adopted sheets come after the page's own, so on 10.5.0 and 10.5.1 every
    /// Stomme page was locked at the window's height - no visitor of a deployed site could scroll (measured in a browser:
    /// body height 420px over 1,570px of content, the wheel and PageDown both inert). A browser fixes
    /// that only through a rule that outranks FluentUI's bare <c>body</c>, so this pins it in both stylesheets the hosts serve.
    /// </remarks>
    public sealed class DocumentScrollRuleTests
    {
        private static readonly Regex Rule = new(@"(?m)^html body\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);

        [Theory]
        [InlineData("src", "Stomme.Server", "wwwroot", "css", "app.css")]
        [InlineData("src", "Stomme.Maui", "wwwroot", "css", "app.css")]
        public void The_page_scrolls_whatever_FluentUI_resets(params string[] path)
        {
            var css = File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(path).ToArray()));
            var match = Rule.Match(css);
            Assert.True(match.Success, "app.css carries no `html body` rule: FluentUI 5's `body { height: 100dvh; overflow: hidden }` locks the page.");
            Assert.Matches(@"height:\s*auto", match.Groups["body"].Value);
            Assert.Matches(@"overflow:\s*visible", match.Groups["body"].Value);
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
        }
    }
}
