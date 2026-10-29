#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// The ConnectionTests cases (7), kept by name, over the reshaped aggregate (a site, a
    /// normalised name, a lower-cased type, an endpoint value object that refuses a credential, settings as one JSON
    /// value, credential field NAMES only, a bounded timeout, UTC instants; CredentialRef and MaxRetries gone), plus the
    /// endpoint refusals (ConnectionEndpointCredentialRefusalTests) and the credential key grammar in both directions
    /// (ConnectionCredentialKeyTests).
    /// </summary>
    public sealed class ConnectionAggregateTests
    {
        private static readonly DateTime Now = new(2026, 9, 27, 2, 0, 0, DateTimeKind.Utc);

        private static ConnectionAggregate NewPostgres(string name = "Prod DB") =>
            ConnectionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, name, "PostgreSQL", ConnectorEndpointKind.ConnectionString, "Host=db;Database=app",
                new Dictionary<string, string> { ["username"] = "app", ["ssl"] = "true" }, new[] { "password" }, 30, Now);

        // ---- ConnectionTests -----------------------------------------------------------------------------------------

        [Fact]
        public void Create_StartsUntested_AndRaisesCreatedEvent()
        {
            var connection = NewPostgres();

            Assert.Equal(ConnectionStatus.Untested, connection.Status);
            Assert.Null(connection.LastTestedUtc);
            Assert.Equal("postgresql", connection.ConnectorType);
            Assert.Equal("app", connection.Settings.GetSettings()["username"]);
            Assert.Equal(new[] { "password" }, connection.CredentialFields);
            Assert.Equal(("Prod DB", "PROD DB", 1, 30), (connection.Name, connection.NameNormalized, connection.SiteId, connection.TimeoutSeconds));
            Assert.Equal((Now, Now), (connection.CreatedUtc, connection.ModifiedUtc));
            var created = Assert.Single(connection.DomainEvents.OfType<ConnectionCreatedEvent>());
            Assert.Equal(("Prod DB", "postgresql"), (created.Name, created.ConnectorType));
        }

        [Theory]
        [InlineData("", "Host=db", 30)]
        [InlineData("Name", "", 30)]
        [InlineData("Name", "Host=db", 0)]
        [InlineData("Name", "Host=db", 301)]
        public void Create_RejectsInvalidInput(string name, string endpoint, int timeout) =>
            Assert.ThrowsAny<ArgumentException>(() =>
                ConnectionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, name, "postgresql", ConnectorEndpointKind.ConnectionString, endpoint, null, null, timeout, Now));

        [Fact]
        public void Create_NoSite_OrBlankType_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionAggregate.Create(Guid.NewGuid(), TenantId.Default, 0, "x", "rest", ConnectorEndpointKind.Url, "https://api.example.com", null, null, 30, Now));
            Assert.ThrowsAny<ArgumentException>(() => ConnectionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "x", " ", ConnectorEndpointKind.Url, "https://api.example.com", null, null, 30, Now));
        }

        [Fact]
        public void RecordTestResult_Success_SetsHealthy_AndRaisesTestedOnly()
        {
            var connection = NewPostgres();
            connection.ClearDomainEvents();

            connection.RecordTestResult(true, "ok", Now.AddMinutes(1));

            Assert.Equal(ConnectionStatus.Healthy, connection.Status);
            Assert.Equal(Now.AddMinutes(1), connection.LastTestedUtc);
            Assert.True(Assert.Single(connection.DomainEvents.OfType<ConnectionTestedEvent>()).Success);
            Assert.Empty(connection.DomainEvents.OfType<ConnectionFailedEvent>());
        }

        [Fact]
        public void RecordTestResult_Failure_SetsFailing_AndRaisesTestedAndFailed()
        {
            var connection = NewPostgres();
            connection.ClearDomainEvents();

            connection.RecordTestResult(false, "timeout", Now.AddMinutes(1));

            Assert.Equal(ConnectionStatus.Failing, connection.Status);
            Assert.False(Assert.Single(connection.DomainEvents.OfType<ConnectionTestedEvent>()).Success);
            Assert.Equal("timeout", Assert.Single(connection.DomainEvents.OfType<ConnectionFailedEvent>()).Message);
        }

        [Fact]
        public void Reconfigure_ResetsToUntested_AndUpdatesConfiguration()
        {
            var connection = NewPostgres();
            connection.RecordTestResult(true, "ok", Now.AddMinutes(1));
            connection.ClearDomainEvents();

            connection.Reconfigure("  Prod DB 2 ", ConnectorEndpointKind.ConnectionString, "Host=db2;Database=app", new Dictionary<string, string> { ["username"] = "app2" }, new[] { "password", "password" }, 45, Now.AddMinutes(2));

            Assert.Equal(ConnectionStatus.Untested, connection.Status);
            Assert.Null(connection.LastTestedUtc);
            Assert.Equal(("Prod DB 2", "PROD DB 2", "Host=db2;Database=app", "app2", 45), (connection.Name, connection.NameNormalized, connection.Endpoint.Value, connection.Settings.GetSettings()["username"], connection.TimeoutSeconds));
            Assert.Equal(new[] { "password" }, connection.CredentialFields);
            Assert.Equal((Now, Now.AddMinutes(2)), (connection.CreatedUtc, connection.ModifiedUtc));
            Assert.Single(connection.DomainEvents.OfType<ConnectionReconfiguredEvent>());
        }

        [Fact]
        public void MarkDeleted_RaisesTheDeletedEvent()
        {
            var connection = NewPostgres();
            connection.ClearDomainEvents();

            connection.MarkDeleted();

            Assert.Equal("Prod DB", Assert.Single(connection.DomainEvents.OfType<ConnectionDeletedEvent>()).Name);
        }

        [Fact]
        public void Settings_round_trip_as_one_json_value_sorted_by_key()
        {
            var settings = ConnectionSettings.From(new Dictionary<string, string> { ["zeta"] = "1", [" alpha "] = "2" });

            Assert.Equal("{\"alpha\":\"2\",\"zeta\":\"1\"}", settings.Value);
            Assert.Equal(settings, ConnectionSettings.Create(settings.Value));
            Assert.Equal(ConnectionSettings.Empty, ConnectionSettings.From(null));
            Assert.Empty(ConnectionSettings.Create("not json").GetSettings());
            Assert.ThrowsAny<ArgumentException>(() => ConnectionSettings.From(new Dictionary<string, string> { [" "] = "x" }));
        }

        // ---- ConnectionEndpointCredentialRefusalTests ---------------------------------------------------------------------

        [Theory]
        [InlineData("Host=db;Password=hunter2")]
        [InlineData("Host=db;pwd=hunter2")]
        [InlineData("host=db; User Id=app; Secret = s")]
        [InlineData("Server=db;Token=t")]
        public void A_connection_string_carrying_a_credential_is_refused(string endpoint)
        {
            var ex = Assert.Throws<ArgumentException>(() => ConnectionEndpoint.For(ConnectorEndpointKind.ConnectionString, endpoint));

            Assert.Contains("credential", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Host=db;Database=app;Username=app")]
        [InlineData("Server=db;Trusted_Connection=True")]
        public void A_connection_string_without_a_credential_is_accepted(string endpoint) =>
            Assert.Equal(endpoint, ConnectionEndpoint.For(ConnectorEndpointKind.ConnectionString, endpoint).Value);

        [Theory]
        [InlineData("https://api.example.com/v1")]
        [InlineData("http://api.internal:8080/")]
        public void A_url_endpoint_must_be_absolute_http_or_https(string endpoint) =>
            Assert.Equal(endpoint, ConnectionEndpoint.For(ConnectorEndpointKind.Url, endpoint).Value);

        [Theory]
        [InlineData("api.example.com")]
        [InlineData("ftp://files.example.com")]
        [InlineData("https://user:pass@api.example.com/")]
        public void A_url_endpoint_that_is_relative_non_http_or_carries_user_info_is_refused(string endpoint) =>
            Assert.Throws<ArgumentException>(() => ConnectionEndpoint.For(ConnectorEndpointKind.Url, endpoint));

        // ---- ConnectionCredentialKeyTests ---------------------------------------------------------------------------------

        [Fact]
        public void A_credential_key_is_the_closed_grammar_and_fits_the_secret_store()
        {
            var id = Guid.NewGuid();

            var key = ConnectionCredentialKeys.For(id, "client-secret");

            Assert.Equal($"connectivity.connection-{id:N}.client-secret", key);
            Assert.True(Stomme.Infrastructure.Security.SiteSecretStore.IsValidReferenceKey(key));
            Assert.True(key.Length <= Stomme.Infrastructure.Security.SiteSecretStore.MaxReferenceKeyLength);
        }

        [Theory]
        [InlineData("../x")]
        [InlineData("a/b")]
        [InlineData("client--secret")]
        [InlineData("-x")]
        [InlineData("x-")]
        [InlineData("Client")]
        [InlineData("a.b")]
        [InlineData("api_key")]
        [InlineData("")]
        [InlineData("   ")]
        public void A_malformed_field_name_is_rejected_never_rewritten(string field) =>
            Assert.Throws<ArgumentException>(() => ConnectionCredentialKeys.For(Guid.NewGuid(), field));

        [Fact]
        public void A_field_must_be_one_the_scheme_declares()
        {
            Assert.Equal("client-secret", ConnectionCredentialKeys.RequireDeclared(AuthenticationType.OAuth2ClientCredentials, " client-secret "));
            Assert.Throws<ArgumentException>(() => ConnectionCredentialKeys.RequireDeclared(AuthenticationType.Basic, "client-secret"));
            Assert.Throws<ArgumentException>(() => ConnectionCredentialKeys.RequireDeclared(AuthenticationType.None, "password"));
        }

        [Fact]
        public void Every_declared_field_of_every_scheme_makes_a_valid_key()
        {
            foreach (var type in Enum.GetValues<AuthenticationType>())
            {
                foreach (var field in AuthenticationScheme.FieldsFor(type))
                {
                    Assert.True(Stomme.Infrastructure.Security.SiteSecretStore.IsValidReferenceKey(ConnectionCredentialKeys.For(Guid.NewGuid(), field.Name)), $"{type}.{field.Name}");
                }
            }
        }
    }
}
