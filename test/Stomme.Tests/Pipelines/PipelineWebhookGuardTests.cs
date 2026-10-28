#nullable enable
using System.Collections.Generic;
using System.Net;
using Stomme.Modules.Pipelines.Triggers;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// the predecessor platform's per-endpoint webhook guards as the receiver's configuration (a BL-95 residue, 2026-09-28): the allowlist's
    /// rule - empty admits anyone; a literal address or a CIDR range; parsed, so equivalent spellings match - and the per-name
    /// settings inheriting what a name leaves unset. The receiver itself is driven by <see cref="PipelineWebhookGuardsDriveTests"/>.
    /// </summary>
    public sealed class PipelineWebhookGuardTests
    {
        [Theory]
        [InlineData("203.0.113.7", true)]        // a literal entry
        [InlineData("198.51.100.200", true)]     // inside the CIDR entry
        [InlineData("198.51.101.1", false)]      // just outside it
        [InlineData("2001:db8::5", true)]        // inside the IPv6 range
        [InlineData("2001:db9::5", false)]
        [InlineData("::ffff:203.0.113.7", true)] // an IPv4-mapped IPv6 caller is its IPv4 address
        [InlineData("192.0.2.1", false)]
        public void A_caller_is_admitted_by_a_literal_or_a_range_it_falls_in(string caller, bool admitted)
        {
            var allowlist = new[] { "203.0.113.7", " 198.51.100.0/24 ", "2001:db8::/32", "not an address", "" };
            Assert.Equal(admitted, PipelineWebhooks.IsCallerAllowed(allowlist, IPAddress.Parse(caller)));
        }

        [Fact]
        public void An_empty_allowlist_admits_anyone_and_a_list_refuses_an_unknown_caller()
        {
            Assert.True(PipelineWebhooks.IsCallerAllowed(new List<string>(), null));
            Assert.True(PipelineWebhooks.IsCallerAllowed(new List<string>(), IPAddress.Parse("192.0.2.1")));
            Assert.False(PipelineWebhooks.IsCallerAllowed(new[] { "10.0.0.0/8" }, null)); // no known address
        }

        [Fact]
        public void A_name_sets_what_it_sets_and_inherits_the_rest()
        {
            var options = new WebhookTriggerOptions
            {
                AllowedCallers = { "192.0.2.0/24" },
                DeduplicationSeconds = 30,
                Endpoints =
                {
                    ["Orders"] = new WebhookEndpointOptions { AllowedCallers = new() { "10.0.0.0/8" }, DeduplicationHeader = "X-Request-Id" },
                    ["refunds"] = new WebhookEndpointOptions { DeduplicationSeconds = 0 },
                },
            };

            var orders = options.For("orders"); // names are matched case-insensitively
            Assert.Equal(new[] { "10.0.0.0/8" }, orders.AllowedCallers);
            Assert.Equal((30, "X-Request-Id"), (orders.DeduplicationSeconds, orders.DeduplicationHeader));

            var refunds = options.For("refunds");
            Assert.Equal(new[] { "192.0.2.0/24" }, refunds.AllowedCallers);
            Assert.Equal((0, (string?)null), (refunds.DeduplicationSeconds, refunds.DeduplicationHeader));

            var other = options.For("anything-else");
            Assert.Equal(new[] { "192.0.2.0/24" }, other.AllowedCallers);
            Assert.Equal(30, other.DeduplicationSeconds);
        }
    }
}
