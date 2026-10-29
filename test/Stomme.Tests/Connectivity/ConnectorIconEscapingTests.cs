#nullable enable
using Stomme.Connectivity;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>A connector's display name reaches its monogram as letters and digits only, escaped besides, so markup in a name renders as text; a declared icon wins.</summary>
    public sealed class ConnectorIconEscapingTests
    {
        [Theory]
        [InlineData("PostgreSQL", "PO")]
        [InlineData("REST API", "RA")]
        [InlineData("Azure AD", "AZ")]
        [InlineData("x", "X")]
        [InlineData("", "?")]
        [InlineData("<img src=x onerror=alert(1)> Co", "IS")]
        [InlineData("&lt;b&gt; Bold", "LB")]
        public void Initials_are_letters_and_digits_only(string displayName, string expected) =>
            Assert.Equal(expected, ConnectorIcons.Initials(displayName));

        [Fact]
        public void A_display_name_carrying_markup_renders_as_text_in_the_monogram()
        {
            var svg = ConnectorIcons.Monogram("<script>alert(1)</script> Evil", ConnectorCategory.SecurityVulnerability);

            Assert.DoesNotContain("<script", svg);
            Assert.DoesNotContain("alert", svg);
            Assert.Contains(">SC<", svg); // "scriptalert1" and "script" share a first letter, so the first word's first two
            Assert.StartsWith("<svg ", svg);
            Assert.Contains("#b91c1c", svg);
        }

        [Fact]
        public void A_declared_icon_wins_and_an_undeclared_one_is_the_monogram_of_the_primary_category()
        {
            var declared = new FixtureConnector("declared", "Declared") { };
            Assert.StartsWith("<svg ", ConnectorIcons.For(declared));
            Assert.Contains("#475569", ConnectorIcons.For(declared)); // Uncategorized
            Assert.Contains("#1d4ed8", ConnectorIcons.For(new FixtureConnector("data", "Data Files", ConnectorCategory.DataAndFiles)));
        }
    }
}
