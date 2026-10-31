using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// A module message is rendered as markup, so what reaches it must not be able to inject any (BL-251, BL-239's C4: admin
    /// pages put exception messages in it). The framework's own messages keep their links and formatting.
    /// </summary>
    public sealed class SafeMessageMarkupTests
    {
        [Theory]
        [InlineData("<script>alert(1)</script>", "&lt;script&gt;alert(1)&lt;/script&gt;")]
        [InlineData("<img src=x onerror=alert(1)>", "&lt;img src=x onerror=alert(1)&gt;")]
        [InlineData("<b onclick=\"x()\">bold</b>", "&lt;b onclick=&quot;x()&quot;&gt;bold</b>")]
        [InlineData("<a href=javascript:alert(1)>x</a>", "&lt;a href=javascript:alert(1)&gt;x&lt;/a&gt;")]
        [InlineData("<a href=//evil.example/x>x</a>", "&lt;a href=//evil.example/x&gt;x&lt;/a&gt;")]
        [InlineData("Tom & Jerry's \"show\"", "Tom &amp; Jerry&#39;s &quot;show&quot;")]
        public void Markup_that_could_run_or_leave_the_site_is_shown_as_text(string message, string expected) =>
            Assert.Equal(expected, Utilities.SafeMessageMarkup(message));

        [Theory]
        [InlineData("You Must <a href=/admin/system>Restart</a> To Complete.", "You Must <a href=\"/admin/system\">Restart</a> To Complete.")]
        [InlineData("See <a href=\"https://example.com/docs\">the docs</a>.", "See <a href=\"https://example.com/docs\">the docs</a>.")]
        [InlineData("One<br />two <strong>three</strong> <CODE>x</CODE>", "One<br>two <strong>three</strong> <code>x</code>")]
        public void The_frameworks_own_links_and_formatting_still_render(string message, string expected) =>
            Assert.Equal(expected, Utilities.SafeMessageMarkup(message));

        [Fact]
        public void Nothing_renders_nothing()
        {
            Assert.Equal(string.Empty, Utilities.SafeMessageMarkup(null));
            Assert.Equal(string.Empty, Utilities.SafeMessageMarkup(string.Empty));
        }
    }
}
