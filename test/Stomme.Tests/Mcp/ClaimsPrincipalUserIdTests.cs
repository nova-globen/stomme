#nullable enable
using System.Security.Claims;
using Stomme.Extensions;
using Xunit;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// <c>ClaimsPrincipal.UserId()</c> answers -1 for a principal that is not a Stomme user, and does not throw
    /// (BL-80, D101 item 3).
    /// </summary>
    /// <remarks>
    /// It was <c>int.Parse</c>. Every principal whose NameIdentifier is not an integer therefore produced an unhandled
    /// <c>FormatException</c> - an empty HTTP 500 - and Stomme mints such principals itself: a client-credentials
    /// token from its own per-tenant OpenIddict authority carries the CLIENT ID as <c>sub</c>. -1 is what every caller
    /// already treats as "no user".
    /// </remarks>
    public class ClaimsPrincipalUserIdTests
    {
        [Theory]
        [InlineData("42", 42)]
        [InlineData("-7", -7)]
        [InlineData("0", 0)]
        public void A_numeric_name_identifier_is_the_user_id(string claim, int expected) =>
            Assert.Equal(expected, Principal(claim).UserId());

        [Theory]
        [InlineData("my-service-client")]                      // a client-credentials sub: Stomme issues these itself
        [InlineData("8f14e45f-ceea-467a-9575-2e1e0e1d4f3c")]   // a GUID sub from an external IdP
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("2147483648")]                             // one past int.MaxValue
        public void A_name_identifier_that_is_not_a_Stomme_user_id_is_minus_one_and_does_not_throw(string claim) =>
            Assert.Equal(-1, Principal(claim).UserId());

        [Fact]
        public void A_principal_with_no_name_identifier_at_all_is_minus_one() =>
            Assert.Equal(-1, new ClaimsPrincipal(new ClaimsIdentity()).UserId());

        private static ClaimsPrincipal Principal(string nameIdentifier) =>
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, nameIdentifier) }, "test"));
    }
}
