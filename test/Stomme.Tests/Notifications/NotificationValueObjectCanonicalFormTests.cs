using System;
using System.Collections.Generic;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// BL-78 (D100 point 7) - NOT a ported case: the eight notification value objects are
    /// <see cref="IStringValueObject"/>s, so every store persists them as their canonical <c>Value</c> and rebuilds
    /// them through <c>Create(string)</c>. These facts pin the documented canonical strings and that
    /// <c>Create(x.Value)</c> equals <c>x</c> for each, plus the invariants the string form adds (minute-precision
    /// quiet hours, a comma-free placeholder name, a well-formed secret reference key, UTC-only instants).
    /// </summary>
    public sealed class NotificationValueObjectCanonicalFormTests
    {
        public static TheoryData<IStringValueObject, string> CanonicalForms() => new()
        {
            { new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.FromHours(2)), "22:00-07:00+02:00" },
            { new QuietHours(new TimeOnly(1, 30), new TimeOnly(6, 15), TimeSpan.FromMinutes(-210)), "01:30-06:15-03:30" },
            { new RateLimit(10, TimeSpan.FromMinutes(5)), "10/00:05:00" },
            { new RateLimit(100, TimeSpan.FromDays(1)), "100/1.00:00:00" },
            { new Locale(" en-US "), "en-US" },
            { new PlaceholderSchema([" a", "b ", "c"]), "a,b,c" },
            { new PlaceholderSchema([]), "" },
            { new Recipient(RecipientKind.User, "42"), "User:42" },
            { new Recipient(RecipientKind.Email, "ops@example.com"), "Email:ops@example.com" },
            { new Recipient(RecipientKind.Endpoint, "https://hooks.example.com/a:b"), "Endpoint:https://hooks.example.com/a:b" },
            { new Content("Hi {{ name }}", "<b>Body</b>"), "{\"subject\":\"Hi {{ name }}\",\"body\":\"\\u003Cb\\u003EBody\\u003C/b\\u003E\"}" },
            { new Payload("", "Body"), "{\"subject\":\"\",\"body\":\"Body\"}" },
            {
                new TransportSettings(ChannelKind.Webhook, "https://hooks.example.com/x", "notifications.channel.abc.credential",
                    [new TransportOption("header", "X-Ops")]),
                "{\"kind\":\"Webhook\",\"endpoint\":\"https://hooks.example.com/x\",\"secretReference\":\"notifications.channel.abc.credential\",\"options\":[{\"key\":\"header\",\"value\":\"X-Ops\"}]}"
            },
            { new TransportSettings(ChannelKind.InApp, null, null, []), "{\"kind\":\"InApp\",\"options\":[]}" },
        };

        [Theory]
        [MemberData(nameof(CanonicalForms))]
        public void Value_is_the_documented_canonical_string_and_Create_rebuilds_an_equal_value(IStringValueObject valueObject, string canonical)
        {
            Assert.Equal(canonical, valueObject.Value);

            var create = valueObject.GetType().GetMethod("Create", [typeof(string)]);
            Assert.NotNull(create);
            var rebuilt = (IStringValueObject)create!.Invoke(null, [canonical])!;

            Assert.Equal(valueObject, rebuilt);
            Assert.Equal(canonical, rebuilt.Value);
        }

        [Fact]
        public void Bounded_value_objects_declare_MaxLength_and_their_canonical_form_fits()
        {
            Assert.True(new QuietHours(new TimeOnly(23, 59), new TimeOnly(0, 0), TimeSpan.FromHours(-14)).Value.Length <= QuietHours.MaxLength);
            Assert.True(new RateLimit(int.MaxValue, TimeSpan.MaxValue).Value.Length <= RateLimit.MaxLength);
            Assert.Throws<ArgumentException>(() => new Locale(new string('a', Locale.MaxLength + 1)));
        }

        [Fact]
        public void QuietHours_rejects_sub_minute_precision_that_the_canonical_string_cannot_carry()
        {
            Assert.Throws<ArgumentException>(() => new QuietHours(new TimeOnly(22, 0, 30), new TimeOnly(7, 0), TimeSpan.Zero));
            Assert.Throws<ArgumentException>(() => new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.FromSeconds(30)));
        }

        [Fact]
        public void QuietHours_rejects_a_non_utc_instant()
        {
            var quiet = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.Zero);

            Assert.Throws<ArgumentException>(() => quiet.IsQuietAt(new DateTime(2026, 6, 25, 23, 0, 0, DateTimeKind.Local)));
            Assert.Throws<ArgumentException>(() => quiet.NextEnd(new DateTime(2026, 6, 25, 23, 0, 0, DateTimeKind.Unspecified)));
        }

        [Fact]
        public void PlaceholderSchema_rejects_a_name_containing_the_separator()
        {
            var ex = Assert.Throws<ArgumentException>(() => new PlaceholderSchema(["a,b"]));
            Assert.Equal("placeholders", ex.ParamName);
        }

        [Fact]
        public void A_user_recipient_must_be_a_positive_int_user_id()
        {
            Assert.Throws<ArgumentException>(() => new Recipient(RecipientKind.User, "00000000-0000-0000-0000-0000000000aa"));
            Assert.Throws<ArgumentException>(() => new Recipient(RecipientKind.User, "0"));
            Assert.Equal("7", new Recipient(RecipientKind.User, " 7 ").Address);
        }

        // EXTENDED (D100 point 7 / D101): the transport stores a secret REFERENCE KEY, never the secret - so a value
        // in the reference slot that is not a well-formed ISiteSecretStore key (a pasted token, a URI) is rejected.
        [Theory]
        [InlineData("sk-live-123")]
        [InlineData("vault://smtp/primary")]
        [InlineData("Notifications.Channel.Credential")]
        public void Transport_rejects_a_secret_reference_that_is_not_a_reference_key(string reference)
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                new TransportSettings(ChannelKind.Webhook, "https://hooks.example.com/x", reference, []));
            Assert.Equal("secretReference", ex.ParamName);
        }

        [Fact]
        public void Every_aggregate_timestamp_input_must_be_utc()
        {
            var request = NotificationTestData.Request(ChannelKind.Email);

            Assert.Throws<ArgumentException>(() =>
                request.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Local)));
            Assert.Throws<ArgumentException>(() => request.ScheduleRetry(new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Unspecified)));
            Assert.Throws<ArgumentException>(() => NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, 1, "k", [NotificationTestData.Email()], [ChannelKind.Email],
                createdOnUtc: new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Local)));
        }

        [Fact]
        public void The_domain_enums_keep_the_numeric_wire_values_of_the_published_Shared_mirrors()
        {
            Assert.Equal(
                (IEnumerable<int>)[(int)NotificationChannelKind.Email, (int)NotificationChannelKind.InApp, (int)NotificationChannelKind.Webhook, (int)NotificationChannelKind.Sms],
                [(int)ChannelKind.Email, (int)ChannelKind.InApp, (int)ChannelKind.Webhook, (int)ChannelKind.Sms]);
            Assert.Equal(Enum.GetNames<NotificationChannelKind>(), Enum.GetNames<ChannelKind>());
            Assert.Equal(Enum.GetNames<NotificationRecipientKind>(), Enum.GetNames<RecipientKind>());
            Assert.Equal(
                (IEnumerable<int>)[(int)NotificationRecipientKind.User, (int)NotificationRecipientKind.Email, (int)NotificationRecipientKind.Endpoint],
                [(int)RecipientKind.User, (int)RecipientKind.Email, (int)RecipientKind.Endpoint]);
        }
    }
}
