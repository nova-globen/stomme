#nullable enable
using System;
using System.Linq;
using Stomme.Formats;
using Stomme.Modules.Formats.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Formats
{
    /// <summary>
    /// The format definition invariants, checked on the aggregate: the
    /// invariants (a valid pattern for the kind under the culture, a known culture, a non-empty name, a known time zone),
    /// the saved event, the descriptor projection - plus the house rules the port added (a site, a normalised name, a
    /// bounded pattern and name, UTC instants).
    /// </summary>
    public sealed class FormatDefinitionTests
    {
        private static readonly DateTime Now = new(2026, 9, 27, 1, 0, 0, DateTimeKind.Utc);

        private static FormatDefinitionAggregate NewDate() =>
            FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "Audit date", FormatKind.Date, "yyyy-MM-dd", "en-US", null, Now);

        [Fact]
        public void Create_ValidDateFormat_RaisesSavedEvent()
        {
            var format = NewDate();

            Assert.Equal(FormatKind.Date, format.Kind);
            Assert.Equal("yyyy-MM-dd", format.Pattern.Value);
            Assert.Equal("en-US", format.CultureName.Value);
            Assert.Equal(("Audit date", "AUDIT DATE", 1), (format.Name, format.NameNormalized, format.SiteId));
            Assert.Equal((Now, Now), (format.CreatedUtc, format.ModifiedUtc));
            var saved = Assert.Single(format.DomainEvents.OfType<FormatDefinitionSavedEvent>());
            Assert.Equal("Audit date", saved.Name);
        }

        [Fact]
        public void Create_BlankName_Throws() =>
            Assert.ThrowsAny<ArgumentException>(() =>
                FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "  ", FormatKind.Date, "yyyy-MM-dd", "en-US", null, Now));

        [Fact]
        public void Create_NoSite_Throws() =>
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 0, "Audit date", FormatKind.Date, "yyyy-MM-dd", "en-US", null, Now));

        [Fact]
        public void Create_UnknownCulture_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "Bad", FormatKind.Number, "N2", "zz-ZZ-not-real", null, Now));
            Assert.Contains("culture", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Create_EmptyPatternForNumber_Throws() =>
            Assert.Throws<ArgumentException>(() =>
                FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "Bad", FormatKind.Number, "", "en-US", null, Now));

        [Fact]
        public void Create_UnrenderablePattern_Throws()
        {
            // 'zzzz' is not a custom date specifier the BCL knows how to render... but it is (z, zz, zzz are, and a run
            // of four is 'zzz' + 'z'), so plant a genuinely invalid one: an unterminated quoted literal.
            var ex = Assert.Throws<ArgumentException>(() =>
                FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "Bad", FormatKind.Date, "yyyy 'unterminated", "en-US", null, Now));
            Assert.Contains("not a valid Date pattern", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Create_PatternOverTheCap_Throws()
        {
            var tooLong = new string('y', FormatPattern.MaxLength + 1);
            Assert.Throws<ArgumentException>(() =>
                FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "Long", FormatKind.Date, tooLong, "", null, Now));
        }

        [Fact]
        public void Create_LocaleNeedsNoPattern()
        {
            var locale = FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "German", FormatKind.Locale, null, "de-DE", null, Now);

            Assert.Equal(string.Empty, locale.Pattern.Value);
            Assert.Equal("de-DE", locale.CultureName.Value);
        }

        [Fact]
        public void Update_ChangesPatternAndRaisesEvent()
        {
            var format = NewDate();
            format.ClearDomainEvents();
            var later = Now.AddMinutes(5);

            format.Update("Audit date", FormatKind.Date, "dd/MM/yyyy", "en-GB", null, later);

            Assert.Equal("dd/MM/yyyy", format.Pattern.Value);
            Assert.Equal("en-GB", format.CultureName.Value);
            Assert.Equal((Now, later), (format.CreatedUtc, format.ModifiedUtc));
            Assert.Single(format.DomainEvents.OfType<FormatDefinitionSavedEvent>());
        }

        [Fact]
        public void Update_RenamesAndRenormalises()
        {
            var format = NewDate();

            format.Update("  Invoice Date ", FormatKind.Date, "yyyy-MM-dd", "en-US", null, Now);

            Assert.Equal(("Invoice Date", "INVOICE DATE"), (format.Name, format.NameNormalized));
        }

        [Fact]
        public void MarkDeleted_RaisesTheDeletedEvent()
        {
            var format = NewDate();
            format.ClearDomainEvents();

            format.MarkDeleted();

            Assert.Equal(format.Id, Assert.Single(format.DomainEvents.OfType<FormatDefinitionDeletedEvent>()).FormatId);
        }

        [Fact]
        public void ToDescriptor_ProjectsTenantScope()
        {
            var descriptor = NewDate().ToDescriptor();

            Assert.Equal(FormatScope.Tenant, descriptor.Scope);
            Assert.Equal(FormatKind.Date, descriptor.Kind);
            Assert.Equal("2026-06-23", descriptor.Format(new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero)));
        }

        [Fact]
        public void Create_WithTimeZone_StoresAndProjects()
        {
            var format = FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "NY date", FormatKind.Date, "yyyy-MM-dd HH:mm", "en-US", "America/New_York", Now);

            Assert.Equal("America/New_York", format.TimeZoneId.Value);
            Assert.Equal("America/New_York", format.ToDescriptor().TimeZone);
        }

        [Fact]
        public void Create_NoTimeZone_DefaultsToNone()
        {
            Assert.Equal(string.Empty, NewDate().TimeZoneId.Value);
            Assert.Equal(string.Empty, NewDate().ToDescriptor().TimeZone);
        }

        [Fact]
        public void Create_InvalidTimeZone_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "Bad", FormatKind.Date, "yyyy-MM-dd", "en-US", "Not/AZone", Now));
            Assert.Contains("time zone", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Update_ChangesTimeZone()
        {
            var format = NewDate();

            format.Update("Audit date", FormatKind.Date, "yyyy-MM-dd HH:mm", "en-US", "Europe/London", Now);

            Assert.Equal("Europe/London", format.TimeZoneId.Value);
        }

        [Fact]
        public void The_value_objects_compare_by_value()
        {
            Assert.Equal(CultureName.Of("en-us"), CultureName.Of("en-US"));
            Assert.Equal(CultureName.Invariant, CultureName.Of("  "));
            Assert.Equal(TimeZoneId.None, TimeZoneId.Of(null));
            Assert.Equal(FormatPattern.For(FormatKind.Number, " N2 ", CultureName.Invariant), FormatPattern.For(FormatKind.Number, "N2", CultureName.Invariant));
            Assert.Equal(FormatPattern.None, FormatPattern.For(FormatKind.Locale, "ignored", CultureName.Invariant));
        }
    }
}
