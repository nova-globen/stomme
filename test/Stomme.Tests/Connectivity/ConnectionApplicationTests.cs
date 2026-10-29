#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Domain;
using Stomme.SharedKernel.Domain;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// The ConnectionApplicationTests cases (17), kept by name, on the application layer, over
    /// the module's real startup on the File store. CHANGED, each on purpose: the vault is the per-site secret store and a
    /// credential is a NAMED field the connector's scheme declares (the single free-form <c>Credential</c> is gone, so the
    /// fixture declares Basic, and a field once named <c>client_id</c> is the scheme's <c>client-id</c>); a connector's message
    /// never reaches the caller - a category does; a deleted connection's secrets are removed after its row. NOT covered here:
    /// <c>Heartbeat_TestsAllConnections_AndUpdatesStatus</c> - the health probe is default-off and per site, and
    /// ConnectionHealthProbeTests covers it.
    /// </summary>
    public sealed class ConnectionApplicationTests
    {
        private static readonly ConnectorConfigSchema RequiresUsername =
            new(new[] { new ConnectorConfigField("username", "Username", ConnectorConfigFieldType.Text, Required: true) });

        private static CreateConnectionCommand NewCommand(string? password = "s3cret") =>
            new("Prod", "fake", "host=db", new Dictionary<string, string> { ["username"] = "app" },
                password is null ? null : new Dictionary<string, string> { ["password"] = password });

        [Fact]
        public async Task Create_StoresCredentialInTheSecretStore_AndListShowsConnectionWithoutSecret()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));

            var id = await harness.SendAsync(NewCommand());

            Assert.Contains(harness.Secrets.Secrets, kv => kv.Value == "s3cret");
            Assert.True(harness.Secrets.Secrets.ContainsKey((ConnectivityApplicationHarness.SiteId, ConnectionCredentialKeys.For(id, "password"))));
            var view = Assert.Single(await harness.SendAsync(new ListConnectionsQuery()));
            Assert.Equal((id, "Untested", true), (view.Id, view.Status, view.HasCredentials));
            Assert.Equal(new[] { "password" }, view.CredentialFields);
            Assert.DoesNotContain("s3cret", JsonSerializer.Serialize(view));
        }

        [Fact]
        public async Task Create_WithUnknownConnector_Throws()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new CreateConnectionCommand("X", "nope", "host=db")));
        }

        [Fact]
        public async Task Create_WithBlankName_FailsValidation()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));
            await Assert.ThrowsAsync<ValidationException>(() => harness.SendAsync(new CreateConnectionCommand("", "fake", "host=db")));
        }

        [Fact]
        public async Task Create_WithADuplicateName_Throws_WhateverTheCase()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));
            await harness.SendAsync(NewCommand());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new CreateConnectionCommand("  PROD ", "fake", "host=other")));
            Assert.Contains("already exists", ex.Message);
        }

        [Fact]
        public async Task Create_WithAnUndeclaredCredentialField_IsRefused_BeforeAnythingIsWritten()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.SendAsync(new CreateConnectionCommand("Prod", "fake", "host=db",
                Credentials: new Dictionary<string, string> { ["password"] = "s3cret", ["api-key"] = "not-basic" })));
            Assert.Contains("api-key", ex.Message);
            Assert.Empty(harness.Secrets.Secrets);
            Assert.Empty(await harness.SendAsync(new ListConnectionsQuery()));
        }

        [Fact]
        public async Task Create_WithACredentialInTheConnectionString_IsRefused()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.SendAsync(new CreateConnectionCommand("Prod", "fake", "Host=db;Password=hunter2", Credentials: new Dictionary<string, string> { ["password"] = "s3cret" })));
            Assert.Contains("Password", ex.Message);
            Assert.Empty(harness.Secrets.Secrets); // the refusal came before the secret write
        }

        [Fact]
        public async Task Test_Success_SetsHealthy_AndPassesTheResolvedCredentialsToTheConnector()
        {
            var connector = new ProbeConnector("fake", (_, _) => Task.FromResult(ConnectionTestResult.Ok("up")));
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var id = await harness.SendAsync(NewCommand());

            var result = await harness.SendAsync(new TestConnectionCommand(id));

            Assert.Equal((true, ConnectorOutcome.Succeeded, "Healthy"), (result.Success, result.Outcome, result.Status));
            Assert.Equal("s3cret", connector.LastContext?.Credentials["password"]); // the connector received the credential resolved from the store
            Assert.Equal("app", connector.LastContext?.Settings["username"]);
            Assert.Equal("Healthy", (await harness.SendAsync(new ListConnectionsQuery()))[0].Status);
        }

        [Fact]
        public async Task Test_Failure_SetsFailing()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake", (_, _) => Task.FromResult(ConnectionTestResult.Fail("refused"))));
            var id = await harness.SendAsync(NewCommand());

            var result = await harness.SendAsync(new TestConnectionCommand(id));

            Assert.Equal((false, ConnectorOutcome.Failed, "Failing"), (result.Success, result.Outcome, result.Status));
            Assert.Equal(ConnectorOutcomes.Message(ConnectorOutcome.Failed), result.Message); // the category's text, not the connector's
        }

        [Fact]
        public async Task Update_Reconfigures_AndResetsToUntested()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));
            var id = await harness.SendAsync(NewCommand());
            await harness.SendAsync(new TestConnectionCommand(id)); // -> Healthy

            await harness.SendAsync(new UpdateConnectionCommand(id, "Prod East", "host=db2", TimeoutSeconds: 45));

            var view = (await harness.SendAsync(new ListConnectionsQuery()))[0];
            Assert.Equal(("Prod East", "host=db2", "Untested", 45), (view.Name, view.Endpoint, view.Status, view.TimeoutSeconds));
        }

        [Fact]
        public async Task Delete_RemovesTheConnection_AndItsSecrets()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));
            var id = await harness.SendAsync(NewCommand());
            Assert.Contains(harness.Secrets.Secrets, kv => kv.Value == "s3cret");

            await harness.SendAsync(new DeleteConnectionCommand(id));

            Assert.Empty(await harness.SendAsync(new ListConnectionsQuery()));
            Assert.DoesNotContain(harness.Secrets.Secrets, kv => kv.Value == "s3cret");
            await harness.SendAsync(new DeleteConnectionCommand(id)); // idempotent
        }

        [Fact]
        public async Task Delete_DispatchesConnectionDeletedEvent()
        {
            var recorder = new RecordingDomainEventHandler<ConnectionDeletedEvent>();
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"),
                services => services.AddSingleton<IDomainEventHandler<ConnectionDeletedEvent>>(recorder));
            var id = await harness.SendAsync(NewCommand(password: null));

            await harness.SendAsync(new DeleteConnectionCommand(id));

            var deleted = Assert.Single(recorder.Received);
            Assert.Equal((id, "Prod"), (deleted.ConnectionId, deleted.Name));
        }

        [Fact]
        public async Task Delete_IsRefused_WhenAProviderReportsTheConnectionInUse()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"),
                services => services.AddSingleton<IConnectionUsageProvider>(new FakeUsageProvider(ConnectionUsage.Used("Used by pipeline 'ETL'."))));
            var id = await harness.SendAsync(NewCommand());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new DeleteConnectionCommand(id)));

            Assert.Contains("ETL", ex.Message);
            Assert.Single(await harness.SendAsync(new ListConnectionsQuery())); // the connection survives the refused delete
            Assert.Contains(harness.Secrets.Secrets, kv => kv.Value == "s3cret"); // and so does its secret
        }

        [Fact]
        public async Task Delete_Proceeds_WhenProvidersReportNotInUse()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"),
                services => services.AddSingleton<IConnectionUsageProvider>(new FakeUsageProvider(ConnectionUsage.NotUsed)));
            var id = await harness.SendAsync(NewCommand());

            await harness.SendAsync(new DeleteConnectionCommand(id));

            Assert.Empty(await harness.SendAsync(new ListConnectionsQuery()));
        }

        [Fact]
        public async Task Create_WithMissingRequiredSetting_FailsSchemaValidation()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake") { ConfigSchema = RequiresUsername });
            var ex = await Assert.ThrowsAsync<ValidationException>(() => harness.SendAsync(new CreateConnectionCommand("Prod", "fake", "host=db"))); // no settings
            Assert.Contains(ex.Errors, failure => failure.ErrorMessage.Contains("username", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Create_WithRequiredSettingPresent_Succeeds()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake") { ConfigSchema = RequiresUsername });
            var id = await harness.SendAsync(NewCommand()); // settings carries username=app
            Assert.NotEqual(Guid.Empty, id);
        }

        [Fact]
        public async Task ListConnectors_ReturnsAvailableConnectorWithItsSchema()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake") { ConfigSchema = RequiresUsername });

            var connectors = await harness.SendAsync(new ListConnectorsQuery());

            var fake = Assert.Single(connectors, connector => connector.ConnectorType == "fake");
            Assert.Equal(("Basic", "ConnectionString", false), (fake.AuthType, fake.EndpointKind, fake.HostOnly)); // CHANGED from "None": the fixture declares Basic
            var field = Assert.Single(fake.Fields);
            Assert.Equal(("username", true), (field.Name, field.Required));
            Assert.Equal(new[] { ("username", false), ("password", true) }, fake.AuthFields.Select(auth => (auth.Name, auth.Secret)));
            Assert.Equal("uncategorized", Assert.Single(fake.Categories).Id);
            Assert.Empty(fake.Operations);
        }

        [Fact]
        public async Task Create_WithNamedCredentialSet_StoresEachSecret_AndTestResolvesThemToTheConnector()
        {
            var connector = new ProbeConnector("oauth") { AuthType = AuthenticationType.OAuth2ClientCredentials };
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var credentials = new Dictionary<string, string> { ["client-id"] = "cid", ["client-secret"] = "csecret", ["token-url"] = "https://auth/token" };

            var id = await harness.SendAsync(new CreateConnectionCommand("OAuth", "oauth", "host=api", Credentials: credentials));

            // Each secret is stored in the site's secret store under its own key (not in the connection view).
            Assert.Contains(harness.Secrets.Secrets, kv => kv.Value == "cid");
            Assert.Contains(harness.Secrets.Secrets, kv => kv.Value == "csecret");
            Assert.Contains(harness.Secrets.Secrets, kv => kv.Value == "https://auth/token");
            var view = Assert.Single(await harness.SendAsync(new ListConnectionsQuery()));
            Assert.True(view.HasCredentials);
            Assert.DoesNotContain("csecret", JsonSerializer.Serialize(view));

            await harness.SendAsync(new TestConnectionCommand(id));

            Assert.NotNull(connector.LastContext);
            Assert.Equal(("cid", "csecret", "https://auth/token"), (connector.LastContext!.Credentials["client-id"], connector.LastContext.Credentials["client-secret"], connector.LastContext.Credentials["token-url"]));
        }

        [Fact]
        public async Task Delete_RemovesEveryNamedSecretInTheCredentialSet()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("oauth") { AuthType = AuthenticationType.OAuth2ClientCredentials });
            var id = await harness.SendAsync(new CreateConnectionCommand("OAuth", "oauth", "host=api", Credentials: new Dictionary<string, string> { ["client-id"] = "cid", ["client-secret"] = "csecret" }));
            Assert.Contains(harness.Secrets.Secrets, kv => kv.Value == "csecret");

            await harness.SendAsync(new DeleteConnectionCommand(id));

            Assert.DoesNotContain(harness.Secrets.Secrets, kv => kv.Value == "cid");
            Assert.DoesNotContain(harness.Secrets.Secrets, kv => kv.Value == "csecret");
        }

        [Fact]
        public async Task Update_WithoutCredentials_PreservesTheExistingCredentialSet()
        {
            // Re-saving a connection without re-entering secrets keeps them working; a blank value is "leave it".
            var connector = new ProbeConnector("oauth") { AuthType = AuthenticationType.OAuth2ClientCredentials };
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var id = await harness.SendAsync(new CreateConnectionCommand("OAuth", "oauth", "host=api", Credentials: new Dictionary<string, string> { ["client-id"] = "cid", ["client-secret"] = "csecret" }));

            await harness.SendAsync(new UpdateConnectionCommand(id, "OAuth v2", "host=api2")); // no credentials supplied
            await harness.SendAsync(new UpdateConnectionCommand(id, "OAuth v2", "host=api2", Credentials: new Dictionary<string, string> { ["client-secret"] = "" })); // a blank one

            await harness.SendAsync(new TestConnectionCommand(id));
            Assert.NotNull(connector.LastContext);
            Assert.Equal(("cid", "csecret"), (connector.LastContext!.Credentials["client-id"], connector.LastContext.Credentials["client-secret"]));

            // A value given rotates the one field and keeps the other.
            await harness.SendAsync(new UpdateConnectionCommand(id, "OAuth v2", "host=api2", Credentials: new Dictionary<string, string> { ["client-secret"] = "rotated" }));
            await harness.SendAsync(new TestConnectionCommand(id));
            Assert.Equal(("cid", "rotated"), (connector.LastContext!.Credentials["client-id"], connector.LastContext.Credentials["client-secret"]));
        }

        [Fact]
        public async Task Get_ReturnsTheSitesConnection_AndNullForAnUnknownId()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake"));
            var id = await harness.SendAsync(NewCommand());

            var view = await harness.SendAsync(new GetConnectionQuery(id));
            Assert.Equal(("Prod", "Fake"), (view?.Name, view?.ConnectorDisplayName));
            Assert.Null(await harness.SendAsync(new GetConnectionQuery(Guid.NewGuid())));
        }
    }
}
