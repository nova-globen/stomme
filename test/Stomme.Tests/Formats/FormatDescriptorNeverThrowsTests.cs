#nullable enable
using System;
using System.Globalization;
using Stomme.Formats;
using Xunit;

namespace Stomme.Tests.Formats
{
    /// <summary>
    /// BL-94 increment 1 (D110): <see cref="FormatDescriptor.Format"/> and <see cref="FormatDescriptor.TryParse"/> never
    /// throw. the predecessor platform's threw on two inputs a consumer cannot prevent - an unknown culture name on a descriptor built from
    /// stored data (the <c>CultureInfo</c> getter) and a Local <see cref="DateTime"/> (the (value, Zero) constructor) -
    /// both pinned here beside the time-zone cases the predecessor platform already had.
    /// </summary>
    public sealed class FormatDescriptorNeverThrowsTests
    {
        private static FormatDescriptor Date(string culture, string timeZone = "") =>
            new(Guid.NewGuid(), "date", FormatKind.Date, "yyyy-MM-dd HH:mm", culture, FormatScope.Tenant, timeZone);

        [Fact]
        public void Unknown_culture_falls_back_to_invariant()
        {
            var descriptor = new FormatDescriptor(Guid.NewGuid(), "n", FormatKind.Number, "N2", "zz-ZZ-not-real", FormatScope.Tenant);

            Assert.Equal(CultureInfo.InvariantCulture, descriptor.CultureInfo);
            Assert.Equal("1,234.50", descriptor.Format(1234.5m));
            Assert.True(descriptor.TryParse("1,234.50", out var parsed));
            Assert.Equal(1234.5m, parsed);
        }

        [Fact]
        public void A_local_DateTime_renders_at_its_own_offset()
        {
            var local = new DateTime(2026, 6, 23, 14, 0, 0, DateTimeKind.Local);

            var rendered = Date("en-US").Format(local);

            Assert.Equal(local.ToString("yyyy-MM-dd HH:mm", CultureInfo.GetCultureInfo("en-US")), rendered);
        }

        [Fact]
        public void An_unspecified_DateTime_is_read_as_utc()
        {
            var rendered = Date("en-US", "America/New_York").Format(new DateTime(2026, 6, 23, 14, 0, 0, DateTimeKind.Unspecified));

            Assert.Equal("2026-06-23 10:00", rendered);
        }

        [Fact]
        public void Format_ConvertsUtcToTargetTimeZone() =>
            Assert.Equal("2026-06-23 10:00", Date("en-US", "America/New_York").Format(new DateTimeOffset(2026, 6, 23, 14, 0, 0, TimeSpan.Zero)));

        [Fact]
        public void Format_NoTimeZone_RendersAsSupplied() =>
            Assert.Equal("2026-06-23 14:00", Date("en-US").Format(new DateTimeOffset(2026, 6, 23, 14, 0, 0, TimeSpan.Zero)));

        [Fact]
        public void Format_BadTimeZone_FallsBackInsteadOfThrowing() =>
            Assert.False(string.IsNullOrEmpty(Date("en-US", "Not/AZone").Format(new DateTimeOffset(2026, 6, 23, 14, 0, 0, TimeSpan.Zero))));

        [Theory]
        [InlineData(FormatKind.Date, "yyyy-MM-dd", "not a date")]
        [InlineData(FormatKind.Number, "N2", "not a number")]
        [InlineData(FormatKind.Currency, "C2", "still not a number")]
        [InlineData(FormatKind.Date, "yyyy-MM-dd", 12345)]
        public void A_value_the_kind_cannot_convert_renders_as_text_and_does_not_parse(FormatKind kind, string pattern, object value)
        {
            var descriptor = new FormatDescriptor(Guid.NewGuid(), "x", kind, pattern, "en-US", FormatScope.Tenant);

            var rendered = descriptor.Format(value);

            Assert.False(string.IsNullOrEmpty(rendered));
            if (value is string text)
            {
                Assert.False(descriptor.TryParse(text, out _));
            }
        }

        [Fact]
        public void Null_and_blank_are_empty_and_unparsed()
        {
            var descriptor = Date("en-US");

            Assert.Equal(string.Empty, descriptor.Format(null));
            Assert.False(descriptor.TryParse(null, out _));
            Assert.False(descriptor.TryParse("   ", out _));
        }

        [Fact]
        public void A_locale_parses_anything_as_text()
        {
            var descriptor = new FormatDescriptor(Guid.NewGuid(), "de", FormatKind.Locale, "", "de-DE", FormatScope.System);

            Assert.True(descriptor.TryParse("hallo", out var value));
            Assert.Equal("hallo", value);
            Assert.Equal("1234,5", descriptor.Format(1234.5m));
        }
    }
}
