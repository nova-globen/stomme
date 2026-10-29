#nullable enable
using Stomme.Modules.Pipelines.Triggers;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>The site's chats: a visitor's session is one the site made (random, never guessable from another), and the page is text.</summary>
    public sealed class PipelineChatPageTests
    {
        [Fact]
        public void A_session_is_random_and_only_a_session_the_site_made_is_taken()
        {
            var one = PipelineChatReceiver.NewSession();
            Assert.Equal(32, one.Length);
            Assert.NotEqual(one, PipelineChatReceiver.NewSession());
            Assert.True(PipelineChatReceiver.IsSession(one));
            Assert.False(PipelineChatReceiver.IsSession(null));
            Assert.False(PipelineChatReceiver.IsSession("not-a-session"));
            Assert.False(PipelineChatReceiver.IsSession(one.ToUpperInvariant()));
            Assert.False(PipelineChatReceiver.IsSession(one + "0"));
        }

        [Fact]
        public void The_page_shows_the_welcome_then_the_conversation_every_text_encoded()
        {
            var page = PipelineChatPage.Render(new PipelineChatSpec("help", "Help <desk>", "Hi <there>"),
                [new PipelineChatLine("user", "<script>alert(1)</script>"), new PipelineChatLine("assistant", "Line one\nline two")],
                "Too long.", "draft <text>");
            Assert.DoesNotContain("<script", page);
            Assert.Contains("<title>Help &lt;desk&gt;</title>", page);
            Assert.Contains("<li class=\"assistant\"><span class=\"who\">Reply</span>Hi &lt;there&gt;</li><li class=\"user\"><span class=\"who\">You</span>&lt;script&gt;alert(1)&lt;/script&gt;</li>", page);
            Assert.Contains("Line one&#xA;line two", page);
            Assert.Contains("<p class=\"error\" role=\"alert\" id=\"message-error\">Too long.</p>", page);
            Assert.Contains("aria-invalid=\"true\" aria-describedby=\"message-error\">draft &lt;text&gt;</textarea>", page);
            Assert.Contains("name=\"" + PipelineChatPage.NewConversationField + "\" value=\"1\" formnovalidate>New conversation</button>", page);
        }
    }
}
