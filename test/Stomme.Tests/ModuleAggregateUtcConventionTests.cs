#nullable enable
using System;
using Stomme.Persistence.EfCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-117: the module-aggregate store's UTC convention, both directions. The READ half is driven on five stores by
    /// <c>HealthAggregateConformanceTests.Every_instant_reads_back_with_KIND_Utc_not_merely_the_right_ticks</c> (SQLite and
    /// SQL Server returned <c>Unspecified</c> before it); this pins the WRITE half, which no module factory can reach
    /// because every one of them already stamps UTC.
    /// </summary>
    public sealed class ModuleAggregateUtcConventionTests
    {
        private static readonly ValueConverter Converter = (ValueConverter)Activator.CreateInstance(
            typeof(ModuleAggregatesDbContext).Assembly.GetType("Stomme.Persistence.EfCore.UtcDateTimeConverter", throwOnError: true)!)!;

        private static readonly DateTime Instant = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_LOCAL_value_is_written_as_the_same_instant_in_UTC()
        {
            var written = (DateTime)Converter.ConvertToProvider(Instant.ToLocalTime())!;
            Assert.Equal(DateTimeKind.Utc, written.Kind);
            Assert.Equal(Instant, written);
        }

        [Fact]
        public void A_UTC_value_is_written_unchanged_and_an_UNSPECIFIED_one_is_NOT_guessed_at()
        {
            Assert.Equal(Instant, (DateTime)Converter.ConvertToProvider(Instant)!);

            // Unspecified passes through as it is: PostgreSQL then refuses it LOUDLY, which is the behaviour to keep -
            // silently declaring it UTC would hide a caller that never decided what its clock meant.
            var unspecified = DateTime.SpecifyKind(Instant, DateTimeKind.Unspecified);
            var written = (DateTime)Converter.ConvertToProvider(unspecified)!;
            Assert.Equal(DateTimeKind.Unspecified, written.Kind);
            Assert.Equal(unspecified.Ticks, written.Ticks);
        }

        [Fact]
        public void A_stored_value_reads_back_as_UTC_with_the_same_ticks()
        {
            var read = (DateTime)Converter.ConvertFromProvider(DateTime.SpecifyKind(Instant, DateTimeKind.Unspecified))!;
            Assert.Equal(DateTimeKind.Utc, read.Kind);
            Assert.Equal(Instant.Ticks, read.Ticks);
        }
    }
}
