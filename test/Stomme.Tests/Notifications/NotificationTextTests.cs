using System.Linq;
using Stomme.Models.Notifications;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// A notice's body as the inbox shows it (<see cref="NotificationText"/>): plain text out of what the channels stored,
    /// and links only to the site's own origin - scheme, host and port all - so an inbox never sends a person elsewhere.
    /// </summary>
    public sealed class NotificationTextTests
    {
        [Theory]
        [InlineData("Line one&#xA;Line two", "Line one\nLine two")]
        [InlineData("Error<br />See the log<BR>now", "Error\nSee the log\nnow")]
        [InlineData("a &amp; b &lt;i&gt;", "a & b <i>")]
        [InlineData("<p>kept text</p><img src=x onerror=alert(1)>", "kept text")]
        [InlineData("windows\r\nline", "windows\nline")]
        [InlineData(null, "")]
        public void A_stored_body_reads_as_plain_text(string body, string expected) =>
            Assert.Equal(expected, NotificationText.Plain(body));

        [Fact]
        public void Only_the_sites_own_addresses_are_links()
        {
            const string text = "Decide: https://example.test/admin/pipelines/*/59/Run?id=r1. Or https://example.test:8443/x, http://example.test/y and https://evil.test/z!";

            var links = NotificationText.Runs(text, "https://example.test").Where(run => run.Href is not null).Select(run => run.Href).ToArray();

            // Not the other port, not plain http, not another host; the full stop ends the sentence, not the address.
            Assert.Equal(new[] { "https://example.test/admin/pipelines/*/59/Run?id=r1" }, links);
            Assert.Equal(text, string.Concat(NotificationText.Runs(text, "https://example.test").Select(run => run.Text)));
            Assert.All(NotificationText.Runs(text, null), run => Assert.Null(run.Href));
            Assert.Empty(NotificationText.Runs("", "https://example.test"));
        }
    }
}
