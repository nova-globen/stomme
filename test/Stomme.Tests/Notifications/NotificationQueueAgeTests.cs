using System;
using System.Linq;
using Stomme.Infrastructure;
using Stomme.Models;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The mail queue kept every undelivered row for as long as SMTP was off, so switching SMTP on sent everything
    /// since the install (one deployment held 120 rows, every one to a placeholder address). The Notification Job now sends
    /// only mail due within the last day and closes the rest: a system notice deleted (the purge takes it), a person's message
    /// left in its inbox with its e-mail given up.
    /// </summary>
    public sealed class NotificationQueueAgeTests
    {
        private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Only_mail_due_more_than_a_day_ago_is_too_old_to_send()
        {
            var fresh = Row("fresh", sendOn: Now.AddHours(-23));
            var stale = Row("stale", sendOn: Now.AddDays(-70));
            var boundary = Row("boundary", sendOn: Now - NotificationQueueAge.MaxAge); // exactly a day: still sent
            var scheduled = Row("scheduled", created: Now.AddDays(-5), sendOn: Now.AddHours(-1)); // judged from when it was due
            var noSendOn = Row("no-send-on", created: Now.AddDays(-2), sendOn: null); // a row from before SendOn was set

            var tooOld = NotificationQueueAge.TooOld(new[] { fresh, stale, boundary, scheduled, noSendOn }, Now);

            Assert.Equal(new[] { "stale", "no-send-on" }, tooOld.Select(row => row.Subject));
            Assert.Equal(TimeSpan.FromHours(24), NotificationQueueAge.MaxAge);
        }

        [Fact]
        public void A_system_notice_is_deleted_and_a_persons_message_keeps_its_inbox_place()
        {
            var notice = Row("notice", sendOn: Now.AddDays(-3));
            var message = Row("message", sendOn: Now.AddDays(-3), fromUserId: 4);

            NotificationQueueAge.Close(notice);
            NotificationQueueAge.Close(message);

            Assert.Equal((true, false), (notice.IsDeleted, notice.IsDelivered));
            Assert.Equal((false, true, (DateTime?)null), (message.IsDeleted, message.IsDelivered, message.DeliveredOn));
        }

        // The first mail through the relay showed the Message-ID naming the container ("...@deee53a6f9d6"); it now
        // names the sender's own domain, and is new for every mail.
        [Fact]
        public void A_mail_is_identified_at_its_senders_domain()
        {
            var sender = new MimeKit.MailboxAddress("Default Site", "no-reply@notify.example.com");

            var first = NotificationMail.MessageIdFor(sender);
            var second = NotificationMail.MessageIdFor(sender);

            Assert.EndsWith("@notify.example.com", first, StringComparison.Ordinal);
            Assert.NotEqual(first, second);
        }

        private static Notification Row(string subject, DateTime? sendOn, DateTime? created = null, int? fromUserId = null) => new()
        {
            SiteId = 1,
            FromUserId = fromUserId,
            Subject = subject,
            Body = "body",
            ToEmail = "someone@example.test",
            CreatedOn = created ?? sendOn ?? Now,
            SendOn = sendOn,
            IsDelivered = false,
            IsDeleted = false,
        };
    }
}
