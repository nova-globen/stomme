#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectivity.Application;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-180 - the site's egress allowlist as a request: each entry held to what the guard can match (it compares a
    /// connection's host with an entry EXACTLY, so a range, a wildcard, a scheme, a port or a short-form address would
    /// match nothing and is refused rather than stored); read by a holder of connections:view, changed by the Host role
    /// alone. Through the module's real startup on the file store; the storage is a fake, the drive uses the real row.
    /// </summary>
    public sealed class ConnectionEgressAllowlistTests
    {
        private sealed class MemoryEgressSettings : IConnectivityEgressSettings
        {
            public Dictionary<int, string> Values { get; } = new();

            public Task<string?> ReadAsync(int siteId, CancellationToken ct = default) => Task.FromResult(Values.TryGetValue(siteId, out var value) ? value : null);

            public Task WriteAsync(int siteId, string value, CancellationToken ct = default)
            {
                Values[siteId] = value;
                return Task.CompletedTask;
            }
        }

        private static async Task<(ConnectivityApplicationHarness Harness, MemoryEgressSettings Settings)> StartAsync()
        {
            var settings = new MemoryEgressSettings();
            var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("probe"), services => services.AddSingleton<IConnectivityEgressSettings>(settings));
            return (harness, settings);
        }

        [Theory]
        [InlineData("127.0.0.1", "127.0.0.1")]
        [InlineData("10.0.0.5, db.corp.example.com", "10.0.0.5|db.corp.example.com")]
        [InlineData("::1", "::1")]
        [InlineData("[::1]", "::1")]
        [InlineData("localhost;ollama\nLOCALHOST", "localhost|ollama")]
        [InlineData("", "")]
        public void An_exact_host_name_or_ip_address_is_kept_as_the_guard_reads_it(string typed, string expected)
        {
            Assert.Equal(expected.Split('|', StringSplitOptions.RemoveEmptyEntries), EgressAllowlist.Parse(typed));
        }

        [Theory]
        [InlineData("10.0.0.0/8")] // a range: the guard matches a host, never a network
        [InlineData("*.corp.example.com")] // a wildcard
        [InlineData("http://db.corp.example.com")] // a scheme
        [InlineData("db.corp.example.com:5432")] // a port
        [InlineData("127.1")] // parses as 127.0.0.1, but the guard would compare "127.1"
        [InlineData("0:0::1")] // the same for IPv6
        public void What_the_guard_could_never_match_is_refused_rather_than_stored(string typed)
        {
            var refused = Assert.Throws<ArgumentException>(() => EgressAllowlist.Parse("127.0.0.1, " + typed));
            Assert.Contains(typed, refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_allowlist_is_bounded()
        {
            Assert.Throws<ArgumentException>(() => EgressAllowlist.Parse(new string('a', EgressAllowlist.MaxLength + 1)));
            Assert.Throws<ArgumentException>(() => EgressAllowlist.Parse(string.Join(", ", Enumerable.Range(1, EgressAllowlist.MaxHosts + 1).Select(i => $"host{i}.example.com"))));
        }

        [Fact]
        public async Task The_host_saves_the_allowlist_and_an_administrator_reads_it_but_cannot_change_it()
        {
            var (harness, settings) = await StartAsync();
            await using (harness)
            {
                harness.User.RoleSet.Add(RoleNames.Host);
                var saved = await harness.SendAsync(new SaveConnectionEgressCommand("127.0.0.1\n db.corp.example.com"));
                Assert.Equal(new[] { "127.0.0.1", "db.corp.example.com" }, saved.AllowedHosts);
                Assert.True(saved.CanEdit);
                Assert.Equal("127.0.0.1, db.corp.example.com", settings.Values[ConnectivityApplicationHarness.SiteId]);

                harness.User.RoleSet.Remove(RoleNames.Host); // an Administrator of the site, nothing more
                var read = await harness.SendAsync(new GetConnectionEgressQuery());
                Assert.Equal(new[] { "127.0.0.1", "db.corp.example.com" }, read.AllowedHosts);
                Assert.False(read.CanEdit);

                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.SendAsync(new SaveConnectionEgressCommand("10.0.0.9")));
                Assert.Equal("127.0.0.1, db.corp.example.com", settings.Values[ConnectivityApplicationHarness.SiteId]);
            }
        }

        [Fact]
        public async Task A_refused_entry_stores_nothing()
        {
            var (harness, settings) = await StartAsync();
            await using (harness)
            {
                harness.User.RoleSet.Add(RoleNames.Host);
                await Assert.ThrowsAsync<ArgumentException>(() => harness.SendAsync(new SaveConnectionEgressCommand("127.0.0.1, 10.0.0.0/8")));
                Assert.Empty(settings.Values);
            }
        }
    }
}
