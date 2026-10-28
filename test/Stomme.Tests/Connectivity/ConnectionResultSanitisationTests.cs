#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Domain;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-94 increment 6 (D110 refutation 3) - a connector's own words never reach a page, an event or a log. A fixture
    /// answering with a message that carries the host, the user and the password produces a CATEGORY in the response and
    /// in the events; a fixture that throws produces a category, and the log carries the exception TYPE. The other
    /// categories - the policy's denial, a timeout, an unreadable or missing credential - are decided without a dial.
    /// </summary>
    public sealed class ConnectionResultSanitisationTests
    {
        private const string Leak = "host=db;user=sa;password=hunter2 refused";

        [Fact]
        public async Task A_connectors_failure_text_reaches_neither_the_response_nor_the_events()
        {
            var tested = new RecordingDomainEventHandler<ConnectionTestedEvent>();
            var failed = new RecordingDomainEventHandler<ConnectionFailedEvent>();
            await using var harness = await ConnectivityApplicationHarness.StartAsync(
                new ProbeConnector("fake", (_, _) => Task.FromResult(ConnectionTestResult.Fail(Leak))),
                services =>
                {
                    services.AddSingleton<IDomainEventHandler<ConnectionTestedEvent>>(tested);
                    services.AddSingleton<IDomainEventHandler<ConnectionFailedEvent>>(failed);
                });
            var id = await harness.SendAsync(new CreateConnectionCommand("Prod", "fake", "host=db", Credentials: new Dictionary<string, string> { ["password"] = "hunter2" }));

            var result = await harness.SendAsync(new TestConnectionCommand(id));

            Assert.Equal((false, ConnectorOutcome.Failed), (result.Success, result.Outcome));
            Assert.Equal(ConnectorOutcomes.Message(ConnectorOutcome.Failed), result.Message);
            Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(result));
            Assert.Equal(ConnectorOutcomes.Message(ConnectorOutcome.Failed), Assert.Single(tested.Received).Message);
            Assert.Equal(ConnectorOutcomes.Message(ConnectorOutcome.Failed), Assert.Single(failed.Received).Message);
            Assert.DoesNotContain(harness.Logs, line => line.Contains("hunter2", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_connectors_exception_is_a_category_and_the_log_carries_the_type_not_the_text()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake", (_, _) => throw new InvalidOperationException(Leak)));
            var id = await harness.SendAsync(new CreateConnectionCommand("Prod", "fake", "host=db", Credentials: new Dictionary<string, string> { ["password"] = "hunter2" }));

            var result = await harness.SendAsync(new TestConnectionCommand(id));

            Assert.Equal((false, ConnectorOutcome.Faulted, "Failing"), (result.Success, result.Outcome, result.Status));
            Assert.DoesNotContain("hunter2", result.Message);
            Assert.Contains(harness.Logs, line => line.Contains("System.InvalidOperationException", StringComparison.Ordinal) && line.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(harness.Logs, line => line.Contains("hunter2", StringComparison.Ordinal));
        }

        [Fact]
        public async Task The_policys_denial_and_a_timeout_are_their_own_categories()
        {
            var denied = new ProbeConnector("denied", (_, _) => throw new HttpRequestException("refused", new OutboundDestinationDeniedException("The destination is not permitted.")));
            var slow = new ProbeConnector("slow", async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ConnectionTestResult.Ok(); });
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new IConnector[] { denied, slow });
            var deniedId = await harness.SendAsync(new CreateConnectionCommand("Denied", "denied", "host=db", Credentials: new Dictionary<string, string> { ["password"] = "x" }));
            var slowId = await harness.SendAsync(new CreateConnectionCommand("Slow", "slow", "host=db", Credentials: new Dictionary<string, string> { ["password"] = "x" }, TimeoutSeconds: 1));

            Assert.Equal(ConnectorOutcome.DeniedByPolicy, (await harness.SendAsync(new TestConnectionCommand(deniedId))).Outcome);
            Assert.Equal(ConnectorOutcome.TimedOut, (await harness.SendAsync(new TestConnectionCommand(slowId))).Outcome);
        }

        [Fact]
        public async Task An_untrusted_host_key_is_its_own_category_and_the_key_offered_reaches_neither_the_response_nor_the_log()
        {
            // BL-229 (D110's correction of 2026-10-17): the fixed text says what to set; the key the server offered - a server's
            // answer, never a reason to trust it - stays inside the connector's exception.
            const string Offered = "ssh-ed25519 SHA256:OFFEREDBYTHESERVERxxxxxxxxxxxxxxxxxxxxxxxxx";
            var tested = new RecordingDomainEventHandler<ConnectionTestedEvent>();
            await using var harness = await ConnectivityApplicationHarness.StartAsync(
                new ProbeConnector("sftpish", (_, _) => throw new ConnectorHostKeyUntrustedException($"No host key is set - it offered {Offered}.")),
                services => services.AddSingleton<IDomainEventHandler<ConnectionTestedEvent>>(tested));
            var id = await harness.SendAsync(new CreateConnectionCommand("Kassa", "sftpish", "sftp://files.kafe.example", Credentials: new Dictionary<string, string> { ["password"] = "x" }));

            var result = await harness.SendAsync(new TestConnectionCommand(id));

            Assert.Equal((false, ConnectorOutcome.HostKeyUntrusted, "Failing"), (result.Success, result.Outcome, result.Status));
            Assert.Equal("The server's host key is not one this connection trusts, so nothing was sent to it: set its Host key to the server's own - its SHA256 fingerprint, or the line ssh-keyscan prints for it.", result.Message);
            Assert.DoesNotContain("OFFERED", JsonSerializer.Serialize(result), StringComparison.Ordinal);
            Assert.Equal(result.Message, Assert.Single(tested.Received).Message);
            Assert.DoesNotContain(harness.Logs, line => line.Contains("OFFERED", StringComparison.Ordinal));
        }

        [Fact]
        public async Task An_unreadable_or_missing_credential_is_a_category_decided_without_a_dial()
        {
            var connector = new ProbeConnector("fake");
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var id = await harness.SendAsync(new CreateConnectionCommand("Prod", "fake", "host=db", Credentials: new Dictionary<string, string> { ["password"] = "s3cret" }));
            var key = (ConnectivityApplicationHarness.SiteId, ConnectionCredentialKeys.For(id, "password"));

            harness.Secrets.Unreadable.Add(key);
            var unreadable = await harness.SendAsync(new TestConnectionCommand(id));
            Assert.Equal((ConnectorOutcome.CredentialsUnreadable, "Failing"), (unreadable.Outcome, unreadable.Status));

            harness.Secrets.Unreadable.Remove(key);
            harness.Secrets.Secrets.Remove(key);
            var missing = await harness.SendAsync(new TestConnectionCommand(id));
            Assert.Equal(ConnectorOutcome.CredentialsMissing, missing.Outcome);

            Assert.Equal(0, connector.Calls); // neither verdict dialled
        }

        [Fact]
        public async Task A_connection_whose_connector_is_no_longer_installed_is_a_category_for_a_test_and_an_extraction()
        {
            // The row outlives the package: a connection of a type no longer in /bin is stored (here, added straight to the
            // store - a create refuses an unknown type), listed with its type as the display name, and every call on it
            // answers ConnectorUnavailable rather than throwing.
            await using var harness = await ConnectivityApplicationHarness.StartAsync(Array.Empty<IConnector>());
            var id = Guid.NewGuid();
            await harness.InScopeAsync(async sp =>
            {
                var connection = ConnectionAggregate.Create(id, Stomme.Tenancy.Domain.TenantId.Default, ConnectivityApplicationHarness.SiteId, "Gone", "gone",
                    ConnectorEndpointKind.ConnectionString, "host=x", null, null, 30, ConnectivityApplicationHarness.Start);
                await sp.GetRequiredService<Stomme.Connectivity.Persistence.IConnectionRepository>().AddAsync(connection);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return 0;
            });

            Assert.Equal("gone", Assert.Single(await harness.SendAsync(new ListConnectionsQuery())).ConnectorDisplayName);
            var tested = await harness.SendAsync(new TestConnectionCommand(id));
            Assert.Equal((ConnectorOutcome.ConnectorUnavailable, "Failing"), (tested.Outcome, tested.Status));
            Assert.Equal(ConnectorOutcome.ConnectorUnavailable, (await harness.ExtractAsync(id, null)).Outcome);
        }
    }
}
