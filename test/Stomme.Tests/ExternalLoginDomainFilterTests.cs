using Stomme.Extensions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>ExternalLogin:DomainFilter</c> (BL-251, BL-239's D#7): an address is accepted when its domain is a listed one or a
    /// subdomain of it and none of the <c>!</c>-refused ones. It matched by substring and required every listed domain at once.
    /// </summary>
    public sealed class ExternalLoginDomainFilterTests
    {
        [Theory]
        [InlineData("ann@company.com", "company.com", true)]
        [InlineData("ann@eu.company.com", "company.com", true)]
        [InlineData("ann@company.com", "@company.com", true)]
        [InlineData("ann@COMPANY.com", "company.com", true)]
        [InlineData("ann@company.com.attacker.example", "company.com", false)]
        [InlineData("ann@notcompany.com", "company.com", false)]
        [InlineData("ann@other.org", "company.com,other.org", true)]
        [InlineData("ann@gmail.com", "!gmail.com", false)]
        [InlineData("ann@company.com", "!gmail.com", true)]
        [InlineData("ann@company.com", "company.com,!contractors.company.com", true)]
        [InlineData("ann@contractors.company.com", "company.com,!contractors.company.com", false)]
        [InlineData("ann@company.com", "", true)]
        [InlineData("", "", true)]
        [InlineData("", "company.com", false)]
        [InlineData("not-an-address", "", false)]
        [InlineData("ann@localhost", "", false)]
        public void An_address_passes_the_filter_by_its_domain(string email, string filter, bool expected) =>
            Assert.Equal(expected, StommeSiteAuthenticationBuilderExtensions.EmailValid(email, filter));
    }
}
