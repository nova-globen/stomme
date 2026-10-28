#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.ActiveDirectory;
using Stomme.Modules.Connectors.Ldap;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - the egress reaches a pinned address within a connection's timeout before a directory library is let dial it (on
    /// Linux libldap connects synchronously with the system's two-minute timeout, measured on UAT): a listening port answers; a
    /// closed one is refused in words; an address that does not answer is cut off at the timeout; the caller's own cancellation
    /// stays its own; the address checked against the policy where the bytes go - loopback refused unless the host is allowlisted
    /// and the address is its own; an egress that does not reach answers at once (the contract's default); and the LDAP and
    /// Active Directory connectors ask the egress for the pin they were given, failing a test and an extraction as it says with
    /// their clients never made - the real ones too, the library never let dial.
    /// </summary>
    public sealed class EgressReachTests
    {
        private static (TcpListener Listener, int Port) Listen()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
        }

        private static int Closed()
        {
            var (listener, port) = Listen();
            listener.Stop(); // nothing listens there now
            return port;
        }

        [Fact]
        public async Task A_listening_port_answers_and_a_closed_one_is_refused_in_words()
        {
            var egress = new HostConnectorEgress(new[] { "localhost" });
            var (listener, open) = Listen();
            try
            {
                await egress.ReachAsync(IPAddress.Loopback, open, "localhost", TimeSpan.FromSeconds(5), "LDAP");
            }
            finally
            {
                listener.Stop();
            }

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => egress.ReachAsync(IPAddress.Loopback, open, "localhost", TimeSpan.FromSeconds(5), "LDAP"));
            Assert.Equal("LDAP: localhost:" + open + " refused the connection (ConnectionRefused).", refused.Message);
        }

        [Fact]
        public async Task No_answer_in_time_is_said_and_the_callers_cancellation_stays_its_own()
        {
            var egress = new HostConnectorEgress(Array.Empty<string>());
            var late = await Assert.ThrowsAsync<InvalidOperationException>(() => egress.ReachAsync(IPAddress.Parse("192.0.2.1"), 389, "dir.kafe.example", TimeSpan.Zero, "Active Directory"));
            Assert.Equal("Active Directory: no answer from dir.kafe.example:389 within 0 seconds.", late.Message);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => egress.ReachAsync(IPAddress.Parse("192.0.2.1"), 389, "dir.kafe.example", TimeSpan.FromSeconds(5), "LDAP", cancelled.Token));
        }

        [Fact]
        public async Task The_address_is_checked_against_the_policy_where_the_bytes_go()
        {
            var port = Closed();
            async Task<Exception> Refused(IConnectorEgress egress, IPAddress address, string host) => await Assert.ThrowsAnyAsync<Exception>(() => egress.ReachAsync(address, port, host, TimeSpan.FromSeconds(5), "LDAP"));

            var none = new HostConnectorEgress(Array.Empty<string>());
            var denied = await Refused(none, IPAddress.Loopback, "dir.kafe.example");
            Assert.True(HostConnectorEgress.IsDenial(denied), denied.ToString());
            Assert.Equal("LDAP: 127.0.0.1 is not an address this site's egress policy permits for 'dir.kafe.example'.", denied.Message);
            Assert.True(HostConnectorEgress.IsDenial(await Refused(new HostConnectorEgress(new[] { "dir.kafe.example" }), IPAddress.Loopback, "dir.kafe.example"))); // allowlisted, but loopback is not its address
            Assert.True(HostConnectorEgress.IsDenial(await Refused(new HostConnectorEgress(new[] { "localhost" }), IPAddress.Parse("10.0.0.5"), "localhost"))); // nor is a private address localhost's
            Assert.True(HostConnectorEgress.IsDenial(await Refused(new HostConnectorEgress(new[] { "127.0.0.2" }), IPAddress.Loopback, "127.0.0.2"))); // a literal is its own address alone

            // Permitted, the reach goes on to the port - a refusal of the connection, not of the policy.
            Assert.False(HostConnectorEgress.IsDenial(await Refused(new HostConnectorEgress(new[] { "localhost" }), IPAddress.Loopback, "localhost")));
            Assert.False(HostConnectorEgress.IsDenial(await Refused(new HostConnectorEgress(new[] { "127.0.0.1" }), IPAddress.Loopback, "[127.0.0.1]")));
        }

        private sealed class Unreaching : IConnectorEgress
        {
            public HttpClient CreateHttpClient(TimeSpan timeout) => throw new NotSupportedException();

            public Task<IPAddress?> ResolveAndPinAsync(string host, int port, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        [Fact]
        public async Task An_egress_that_does_not_reach_answers_at_once()
        {
            IConnectorEgress egress = new Unreaching();
            var reached = egress.ReachAsync(IPAddress.Parse("192.0.2.1"), 389, "dir.kafe.example", TimeSpan.FromMinutes(2), "LDAP");
            Assert.True(reached.IsCompletedSuccessfully);
            await reached;
        }

        [Fact]
        public async Task The_directory_connectors_fail_where_the_pin_does_not_answer_their_clients_never_made()
        {
            var made = 0;
            Task Unreachable(IPAddress address, int port, string host) => throw new InvalidOperationException($"LDAP: no answer from {host}:{port} within 5 seconds.");
            var egress = new StubEgress { Pinned = IPAddress.Parse("192.0.2.7"), Reach = Unreachable };
            var ldap = new LdapConnector(_ => { made++; throw new InvalidOperationException("dialled"); });
            var tested = await ldap.TestConnectionAsync(Contexts.Test(egress, "ldap://dir.kafe.example", credentials: new Dictionary<string, string>(), timeoutSeconds: 7));
            Assert.Equal((false, "LDAP: no answer from dir.kafe.example:389 within 5 seconds."), (tested.Success, tested.Message));
            Assert.Equal((IPAddress.Parse("192.0.2.7"), 389, "dir.kafe.example", TimeSpan.FromSeconds(7), "LDAP"), egress.Reaches[0]); // the pin it was given, its port and the connection's timeout
            var extracted = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(ldap, Contexts.Extract(egress, "ldaps://dir.kafe.example", "delete", new Dictionary<string, string> { ["dn"] = "cn=Ada,dc=kafe,dc=example" })));
            Assert.Equal("LDAP: no answer from dir.kafe.example:636 within 5 seconds.", extracted.Message);

            var directory = new ActiveDirectoryConnector(_ => { made++; throw new InvalidOperationException("dialled"); });
            var adTested = await directory.TestConnectionAsync(Contexts.Test(egress, "ldaps://dc.corp.example.com", credentials: new Dictionary<string, string>()));
            Assert.Equal((false, "LDAP: no answer from dc.corp.example.com:636 within 5 seconds."), (adTested.Success, adTested.Message));
            Assert.Equal("Active Directory", egress.Reaches[^1].Service);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(directory, Contexts.Extract(egress, "ldap://dc.corp.example.com", "users")));
            Assert.Equal((389, 4), (egress.Reaches[^1].Port, egress.Reaches.Count));
            Assert.Equal(0, made); // reached first: no client made
        }

        [Fact]
        public async Task The_real_directory_connectors_reach_the_pin_before_their_library_dials_it()
        {
            var closed = Closed();
            var egress = new HostConnectorEgress(new[] { "127.0.0.1" });
            var ldap = await new LdapConnector().TestConnectionAsync(Contexts.Test(egress, $"ldap://127.0.0.1:{closed}", credentials: new Dictionary<string, string>()));
            Assert.Equal((false, $"LDAP: 127.0.0.1:{closed} refused the connection (ConnectionRefused)."), (ldap.Success, ldap.Message));
            var directory = await new ActiveDirectoryConnector().TestConnectionAsync(Contexts.Test(egress, $"ldap://127.0.0.1:{closed}", credentials: new Dictionary<string, string>()));
            Assert.Equal((false, $"Active Directory: 127.0.0.1:{closed} refused the connection (ConnectionRefused)."), (directory.Success, directory.Message));
        }
    }
}
