#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>A test-only connector (BL-94 increment 3): with or without an extractor, any type key, any categories.</summary>
    public sealed class FixtureConnector(string type, string name, params ConnectorCategory[] categories) : IConnector
    {
        public string ConnectorType => type;

        public string DisplayName => name;

        public IReadOnlyList<ConnectorCategory> Categories => categories;

        public Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConnectionTestResult.Ok());
    }

    public sealed class FixtureExtractingConnector(string type, string name) : IConnector, IConnectorExtractor
    {
        public string ConnectorType => type;

        public string DisplayName => name;

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public bool HostOnly => true;

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[] { new ConnectorOperation("read", "Read") };

        public Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConnectionTestResult.Ok());

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ConnectorRecord(new Dictionary<string, object?> { ["endpoint"] = context.Endpoint, ["operation"] = context.OperationName });
        }
    }

    /// <summary>An egress double: the contexts need one; nothing here dials.</summary>
    public sealed class NoEgress : IConnectorEgress
    {
        public static readonly NoEgress Instance = new();

        public HttpClient CreateHttpClient(TimeSpan timeout) => throw new InvalidOperationException("The test double never dials.");

        public Task<IPAddress?> ResolveAndPinAsync(string host, int port, CancellationToken cancellationToken = default) => Task.FromResult<IPAddress?>(null);
    }

    /// <summary>
    /// BL-94 increment 3 (D110) - the predecessor platform's connector-contract cases ported by name onto the BCL-only contract in
    /// <c>Stomme.Shared/Connectivity</c>: the config schema (ConnectorConfigSchemaTests 4, ConnectorConfigSchemaRichFieldsTests 16),
    /// the authentication schemes (AuthenticationSchemeTests 10), the picker groups (ConnectorPickerGroupsTests 4), the
    /// catalog (ConnectorCatalogTests 4 - with the one deliberate change: a duplicate key THROWS instead of the first
    /// registration winning), plus the contract's defaults, HostOnly and the endpoint kind.
    /// </summary>
    public sealed class ConnectorContractTests
    {
        private static Dictionary<string, string> Settings(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        private static readonly ConnectorConfigSchema Schema = new(new[]
        {
            new ConnectorConfigField("host", "Host", ConnectorConfigFieldType.Text, Required: true),
            new ConnectorConfigField("port", "Port", ConnectorConfigFieldType.Integer),
            new ConnectorConfigField("ssl", "Use SSL", ConnectorConfigFieldType.Boolean),
        });

        // ---- ConnectorConfigSchemaTests ------------------------------------------------------------------------------

        [Fact]
        public void Validate_MissingRequiredField_ReportsError() => Assert.Contains(Schema.Validate(new Dictionary<string, string>()), e => e.Contains("host"));

        [Fact]
        public void Validate_NonParsingTypedFields_ReportErrors()
        {
            var errors = Schema.Validate(Settings(("host", "db"), ("port", "not-a-number"), ("ssl", "maybe")));

            Assert.Contains(errors, e => e.Contains("port"));
            Assert.Contains(errors, e => e.Contains("ssl"));
        }

        [Fact]
        public void Validate_ValidSettings_NoErrors() => Assert.Empty(Schema.Validate(Settings(("host", "db"), ("port", "5432"), ("ssl", "true"), ("extra", "allowed"))));

        [Fact]
        public void Empty_ValidatesAnything() => Assert.Empty(ConnectorConfigSchema.Empty.Validate(Settings(("x", "y"))));

        // ---- ConnectorConfigSchemaRichFieldsTests ------------------------------------------------------------------------

        [Theory]
        [InlineData("https://api.example.com", true)]
        [InlineData("http://localhost:5080/path", true)]
        [InlineData("not-a-url", false)]
        [InlineData("/relative/only", false)]
        public void Url_Field_ValidatesAbsoluteUrl(string value, bool valid)
        {
            var schema = new ConnectorConfigSchema(new[] { new ConnectorConfigField("endpoint", "Endpoint", ConnectorConfigFieldType.Url, Required: true) });
            Assert.Equal(valid, schema.Validate(Settings(("endpoint", value))).Count == 0);
        }

        [Theory]
        [InlineData("3.14", true)]
        [InlineData("42", true)]
        [InlineData("abc", false)]
        public void Number_Field_ValidatesDouble(string value, bool valid)
        {
            var schema = new ConnectorConfigSchema(new[] { new ConnectorConfigField("ratio", "Ratio", ConnectorConfigFieldType.Number) });
            Assert.Equal(valid, schema.Validate(Settings(("ratio", value))).Count == 0);
        }

        [Theory]
        [InlineData("90", true)]
        [InlineData("00:05:00", true)]
        [InlineData("later", false)]
        public void Duration_Field_AcceptsSecondsOrTimeSpan(string value, bool valid)
        {
            var schema = new ConnectorConfigSchema(new[] { new ConnectorConfigField("timeout", "Timeout", ConnectorConfigFieldType.Duration) });
            Assert.Equal(valid, schema.Validate(Settings(("timeout", value))).Count == 0);
        }

        [Fact]
        public void Select_Field_RejectsValueOutsideItsOptions()
        {
            var schema = new ConnectorConfigSchema(new[] { new ConnectorConfigField("method", "Method", ConnectorConfigFieldType.Select) { Options = new[] { new ConnectorConfigFieldOption("GET"), new ConnectorConfigFieldOption("POST"), new ConnectorConfigFieldOption("PUT") } } });

            Assert.Empty(schema.Validate(Settings(("method", "POST"))));
            Assert.Single(schema.Validate(Settings(("method", "DELETE"))));
        }

        [Fact]
        public void Select_Field_WithoutDeclaredOptions_AcceptsAnyValue()
        {
            var schema = new ConnectorConfigSchema(new[] { new ConnectorConfigField("mode", "Mode", ConnectorConfigFieldType.Select) });
            Assert.Empty(schema.Validate(Settings(("mode", "anything"))));
        }

        [Fact]
        public void HiddenField_IsNeitherRequiredNorValidated()
        {
            var schema = new ConnectorConfigSchema(new[]
            {
                new ConnectorConfigField("auth", "Auth"),
                new ConnectorConfigField("mfa_code", "MFA code", ConnectorConfigFieldType.Integer, Required: true) { VisibleWhen = new ConnectorConfigFieldVisibility("auth", "mfa") },
            });

            Assert.Empty(schema.Validate(Settings(("auth", "basic"))));
            Assert.Single(schema.Validate(Settings(("auth", "mfa"))));
            Assert.Single(schema.Validate(Settings(("auth", "mfa"), ("mfa_code", "notint"))));
            Assert.Empty(schema.Validate(Settings(("auth", "mfa"), ("mfa_code", "123456"))));
        }

        [Fact]
        public void IsVisible_MatchesCaseInsensitively()
        {
            var field = new ConnectorConfigField("code", "Code") { VisibleWhen = new ConnectorConfigFieldVisibility("enabled", "true") };

            Assert.True(ConnectorConfigSchema.IsVisible(field, Settings(("enabled", "True"))));
            Assert.False(ConnectorConfigSchema.IsVisible(field, Settings(("enabled", "false"))));
            Assert.False(ConnectorConfigSchema.IsVisible(field, Settings()));
        }

        [Fact]
        public void FieldOption_SingleArgConstructor_UsesValueAsLabel()
        {
            var option = new ConnectorConfigFieldOption("GET");
            Assert.Equal(("GET", "GET"), (option.Value, option.Label));
        }

        [Fact]
        public void ExistingTextIntegerBooleanTypes_StillValidateAsBefore()
        {
            var schema = new ConnectorConfigSchema(new[]
            {
                new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true),
                new ConnectorConfigField("count", "Count", ConnectorConfigFieldType.Integer),
                new ConnectorConfigField("flag", "Flag", ConnectorConfigFieldType.Boolean),
            });

            Assert.Empty(schema.Validate(Settings(("name", "n"), ("count", "3"), ("flag", "true"))));
            Assert.Equal(2, schema.Validate(Settings(("name", "n"), ("count", "x"), ("flag", "maybe"))).Count);
        }

        // ---- AuthenticationSchemeTests ---------------------------------------------------------------------------------

        [Fact]
        public void None_HasNoCredentialFields() => Assert.Empty(AuthenticationScheme.FieldsFor(AuthenticationType.None));

        [Fact]
        public void OAuth2ClientCredentials_DeclaresClientIdSecretAndTokenUrl()
        {
            var fields = AuthenticationScheme.FieldsFor(AuthenticationType.OAuth2ClientCredentials);

            Assert.Equal(new[] { "client-id", "client-secret", "token-url" }, fields.Select(f => f.Name));
            Assert.False(fields.Single(f => f.Name == "client-id").Secret);
            Assert.True(fields.Single(f => f.Name == "client-secret").Secret);
        }

        [Fact]
        public void ApiKeyPair_DeclaresAccessAndSecretKeys() =>
            Assert.Equal(new[] { "access-key", "secret-key" }, AuthenticationScheme.FieldsFor(AuthenticationType.ApiKeyPair).Select(f => f.Name));

        [Theory]
        [InlineData(AuthenticationType.ApiKey, 1)]
        [InlineData(AuthenticationType.Bearer, 1)]
        [InlineData(AuthenticationType.Basic, 2)]
        [InlineData(AuthenticationType.OAuth2RefreshToken, 3)]
        [InlineData(AuthenticationType.Certificate, 2)]
        [InlineData(AuthenticationType.Windows, 2)]
        public void EachScheme_DeclaresTheExpectedNumberOfFields(AuthenticationType type, int count) => Assert.Equal(count, AuthenticationScheme.FieldsFor(type).Count);

        [Fact]
        public void FieldNames_AreUniqueWithinAScheme_AndMatchTheSecretStoresSegmentGrammar()
        {
            foreach (var type in Enum.GetValues<AuthenticationType>())
            {
                var names = AuthenticationScheme.FieldsFor(type).Select(f => f.Name).ToList();
                Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
                // D110: each connection secret is keyed connectivity.connection-<id>.<field>, and <field> must ALREADY fit the
                // per-site secret store's segment grammar - so every scheme's field name is a valid key segment as declared
                // (the predecessor platform's api_key, client_id, ... are hyphenated here for exactly that reason).
                Assert.All(names, name => Assert.True(Stomme.Infrastructure.Security.SiteSecretStore.IsValidReferenceKey($"connectivity.connection-{Guid.NewGuid():N}.{name}"), name));
            }
        }

        // ---- ConnectorPickerGroupsTests --------------------------------------------------------------------------------

        [Fact]
        public void Build_GroupsFollowRegistryPickerOrderWithUncategorizedLast()
        {
            var groups = ConnectorPickerGroups.Build(new IConnector[]
            {
                new FixtureConnector("stub-bare", "Bare Stub", ConnectorCategory.Uncategorized),
                new FixtureConnector("stub-cost", "Cost Stub", ConnectorCategory.CloudAndSaasCost),
                new FixtureConnector("stub-data", "Data Stub", ConnectorCategory.DataAndFiles),
            });

            Assert.Equal(new[] { "data-and-files", "cloud-and-saas-cost", "uncategorized" }, groups.Select(g => g.Id));
            Assert.Equal(new[] { "Data & Files", "Cloud & SaaS Cost", "Uncategorized" }, groups.Select(g => g.Label));
        }

        [Fact]
        public void Build_TagLikeConnector_AppearsUnderEveryCategoryItCarries()
        {
            var groups = ConnectorPickerGroups.Build(new IConnector[]
            {
                new FixtureConnector("stub-azurecost", "Azure Cost Stub", ConnectorCategory.MicrosoftCloudDirectory, ConnectorCategory.CloudAndSaasCost),
                new FixtureConnector("stub-entra", "Entra Stub", ConnectorCategory.MicrosoftCloudDirectory),
            });

            var microsoft = Assert.Single(groups, g => g.Id == "microsoft-cloud-directory");
            var cost = Assert.Single(groups, g => g.Id == "cloud-and-saas-cost");
            Assert.Equal(new[] { "stub-azurecost", "stub-entra" }, microsoft.Connectors.Select(c => c.ConnectorType));
            Assert.Equal(new[] { "stub-azurecost" }, cost.Connectors.Select(c => c.ConnectorType));
        }

        [Fact]
        public void Build_ConnectorsSortByDisplayNameWithinAGroup()
        {
            var groups = ConnectorPickerGroups.Build(new IConnector[]
            {
                new FixtureConnector("stub-z", "Zeta Stub", ConnectorCategory.DataAndFiles),
                new FixtureConnector("stub-a", "alpha Stub", ConnectorCategory.DataAndFiles),
                new FixtureConnector("stub-m", "Mid Stub", ConnectorCategory.DataAndFiles),
            });

            Assert.Equal(new[] { "alpha Stub", "Mid Stub", "Zeta Stub" }, Assert.Single(groups).Connectors.Select(c => c.DisplayName));
        }

        [Fact]
        public void Build_EmptyCatalogue_YieldsNoGroups() => Assert.Empty(ConnectorPickerGroups.Build(Array.Empty<IConnector>()));

        [Fact]
        public void Build_AConnectorWithNoCategory_IsUncategorized() =>
            Assert.Equal("uncategorized", Assert.Single(ConnectorPickerGroups.Build(new IConnector[] { new FixtureConnector("plain", "Plain") })).Id);

        // ---- ConnectorCatalogTests ---------------------------------------------------------------------------------------

        [Fact]
        public void Find_ReturnsConnectorByType_CaseInsensitive()
        {
            var catalog = new ConnectorCatalog(new IConnector[] { new FixtureConnector("postgresql", "PostgreSQL"), new FixtureConnector("rest", "REST") });

            Assert.Equal("PostgreSQL", catalog.Find("postgresql")?.DisplayName);
            Assert.Equal("PostgreSQL", catalog.Find("PostgreSQL")?.DisplayName);
            Assert.Equal(2, catalog.All.Count);
        }

        [Fact]
        public void Find_ReturnsNull_ForUnknownOrBlankType()
        {
            var catalog = new ConnectorCatalog(new IConnector[] { new FixtureConnector("postgresql", "PostgreSQL") });

            Assert.Null(catalog.Find("mysql"));
            Assert.Null(catalog.Find(""));
            Assert.Null(catalog.Find("  "));
        }

        [Fact]
        public void DuplicateConnectorType_ThrowsAndNamesBothOwners_PredecessorLetTheFirstWin()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new ConnectorCatalog(new IConnector[] { new FixtureConnector("rest", "First"), new FixtureConnector("REST", "Second") }));

            Assert.Contains("'REST'", ex.Message);
            Assert.Contains(nameof(FixtureConnector), ex.Message);
        }

        [Fact]
        public void AddConnectorCatalog_ResolvesACatalogOverRegisteredConnectors()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConnector>(new FixtureConnector("postgresql", "PostgreSQL"));
            services.AddConnectorCatalog();
            using var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<IConnectorCatalog>().Find("postgresql"));
        }

        // ---- the contract's defaults, and what the port added ------------------------------------------------------------

        [Fact]
        public void The_defaults_are_empty_and_the_extractor_advertises_itself()
        {
            IConnector plain = new FixtureConnector("plain", "Plain");
            IConnector extracting = new FixtureExtractingConnector("x", "Extracting");

            Assert.Equal(ConnectorConfigSchema.Empty, plain.ConfigSchema);
            Assert.Equal((AuthenticationType.None, ConnectorEndpointKind.Url, false), (plain.AuthType, plain.EndpointKind, plain.HostOnly));
            Assert.Equal(ConnectorCapabilities.TestConnection, plain.Capabilities);
            Assert.Null(plain.Icon);
            Assert.Null(plain.WebsiteUrl);
            Assert.Equal(ConnectorCapabilities.TestConnection | ConnectorCapabilities.Extract, extracting.Capabilities);
            Assert.Equal((ConnectorEndpointKind.ConnectionString, true, AuthenticationType.Bearer), (extracting.EndpointKind, extracting.HostOnly, extracting.AuthType));
        }

        [Fact]
        public async Task The_contexts_carry_the_hosts_egress_and_an_extractor_reads_through_them()
        {
            var extractor = new FixtureExtractingConnector("x", "Extracting");
            var context = new ConnectorExtractContext("Server=db;Database=x", Settings(), TimeSpan.FromSeconds(5), NoEgress.Instance, "read", Settings())
            {
                Credentials = Settings(("token", "t")),
            };

            var records = new List<ConnectorRecord>();
            await foreach (var record in extractor.ExtractAsync(context))
            {
                records.Add(record);
            }

            Assert.Equal(("Server=db;Database=x", "read"), (Assert.Single(records).Fields["endpoint"], records[0].Fields["operation"]));
            Assert.Same(NoEgress.Instance, context.Egress);
            Assert.Null(await context.Egress.ResolveAndPinAsync("db", 5432));
        }

        [Fact]
        public void Every_category_is_registered_once_with_a_unique_id_and_uncategorized_last()
        {
            Assert.Equal(Enum.GetValues<ConnectorCategory>().Length, ConnectorCategories.All.Count);
            Assert.Equal(ConnectorCategories.All.Count, ConnectorCategories.All.Select(info => info.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(ConnectorCategory.Uncategorized, ConnectorCategories.All.OrderBy(info => info.Order).Last().Category);
            Assert.All(Enum.GetValues<ConnectorCategory>(), category => Assert.NotNull(ConnectorCategories.Info(category)));
        }
    }
}
