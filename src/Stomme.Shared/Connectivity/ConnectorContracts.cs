#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Connectivity
{
    // ============================================================================================================
    // The connector contract (BL-94 increment 3, D110): BCL-ONLY, so the connectors of BL-96 reference
    // Stomme.Shared and nothing else. The catalog implementation and the DI extension live in the Server;
    // egress is HOST-supplied through the contexts (a guarded HttpClient, a resolve-check-and-pin call), never a
    // connector's own socket policy. SharedConnectivityDependencyTests pins the BCL-only rule.
    // ============================================================================================================

    /// <summary>The kind of endpoint a connector is configured with: a URL, or a driver connection string.</summary>
    public enum ConnectorEndpointKind
    {
        Url = 0,
        ConnectionString = 1,
    }

    /// <summary>What a connector can do. Every connector can test a connection; one that implements <see cref="IConnectorExtractor"/> advertises Extract on its own.</summary>
    [Flags]
    public enum ConnectorCapabilities
    {
        None = 0,
        TestConnection = 1,
        Extract = 2,
    }

    /// <summary>The authentication scheme a connector uses; it decides which named credential fields a connection stores (see <see cref="AuthenticationScheme.FieldsFor"/>).</summary>
    public enum AuthenticationType
    {
        None = 0,
        ApiKey = 1,
        ApiKeyPair = 2,
        Basic = 3,
        Bearer = 4,
        OAuth2ClientCredentials = 5,
        OAuth2RefreshToken = 6,
        Certificate = 7,
        Windows = 8,
    }

    /// <summary>One named credential a scheme needs; a secret one is stored in the per-site secret store and never echoed.</summary>
    public sealed record AuthCredentialField(string Name, string DisplayName, bool Secret = true);

    /// <summary>The credential fields each <see cref="AuthenticationType"/> declares - the closed set a connection's secrets are keyed by (D110).</summary>
    public static class AuthenticationScheme
    {
        private static readonly IReadOnlyList<AuthCredentialField> NoFields = Array.Empty<AuthCredentialField>();

        public static IReadOnlyList<AuthCredentialField> FieldsFor(AuthenticationType type) => type switch
        {
            AuthenticationType.None => NoFields,
            AuthenticationType.ApiKey => new[] { new AuthCredentialField("api-key", "API key") },
            AuthenticationType.ApiKeyPair => new[] { new AuthCredentialField("access-key", "Access key", Secret: false), new AuthCredentialField("secret-key", "Secret key") },
            AuthenticationType.Basic => new[] { new AuthCredentialField("username", "Username", Secret: false), new AuthCredentialField("password", "Password") },
            AuthenticationType.Bearer => new[] { new AuthCredentialField("token", "Bearer token") },
            AuthenticationType.OAuth2ClientCredentials => new[] { new AuthCredentialField("client-id", "Client id", Secret: false), new AuthCredentialField("client-secret", "Client secret"), new AuthCredentialField("token-url", "Token endpoint URL", Secret: false) },
            AuthenticationType.OAuth2RefreshToken => new[] { new AuthCredentialField("client-id", "Client id", Secret: false), new AuthCredentialField("client-secret", "Client secret"), new AuthCredentialField("refresh-token", "Refresh token") },
            AuthenticationType.Certificate => new[] { new AuthCredentialField("certificate", "Certificate (PEM/PFX)"), new AuthCredentialField("passphrase", "Passphrase") },
            AuthenticationType.Windows => new[] { new AuthCredentialField("username", "Username (DOMAIN\\user)", Secret: false), new AuthCredentialField("password", "Password") },
            _ => NoFields,
        };
    }

    /// <summary>The categories a connector may carry - tag-like, a connector may carry several and pickers list it under each.</summary>
    public enum ConnectorCategory
    {
        Uncategorized = 0,
        DataAndFiles = 1,
        SecurityVulnerability = 2,
        MicrosoftCloudDirectory = 3,
        ItsmAssetManagement = 4,
        CloudAndSaasCost = 5,
        NetworkInfrastructure = 6,
        AiProviderUsage = 7,
        Messaging = 8,
        Development = 9,
        Productivity = 10,
        Commerce = 11,
        Crm = 12,
        Marketing = 13,
    }

    public sealed record ConnectorCategoryInfo(ConnectorCategory Category, string Id, string Label, string Description, int Order);

    /// <summary>The category registry: every category once, with a stable id, a label and the picker order; Uncategorized last.</summary>
    public static class ConnectorCategories
    {
        public static IReadOnlyList<ConnectorCategoryInfo> All { get; } = new[]
        {
            new ConnectorCategoryInfo(ConnectorCategory.DataAndFiles, "data-and-files", "Data & Files", "Databases, generic APIs and file transports.", 1),
            new ConnectorCategoryInfo(ConnectorCategory.SecurityVulnerability, "security-vulnerability", "Security & Vulnerability", "Security and vulnerability-management platforms.", 2),
            new ConnectorCategoryInfo(ConnectorCategory.MicrosoftCloudDirectory, "microsoft-cloud-directory", "Microsoft Cloud & Directory", "Microsoft cloud and directory services.", 3),
            new ConnectorCategoryInfo(ConnectorCategory.ItsmAssetManagement, "itsm-asset-management", "ITSM & Asset Management", "IT service and asset management platforms.", 4),
            new ConnectorCategoryInfo(ConnectorCategory.CloudAndSaasCost, "cloud-and-saas-cost", "Cloud & SaaS Cost", "Cloud and SaaS spend and usage sources.", 5),
            new ConnectorCategoryInfo(ConnectorCategory.NetworkInfrastructure, "network-infrastructure", "Network & Infrastructure", "Network and infrastructure management platforms.", 6),
            new ConnectorCategoryInfo(ConnectorCategory.AiProviderUsage, "ai-provider-usage", "AI Provider Usage", "AI provider usage and spend sources.", 7),
            new ConnectorCategoryInfo(ConnectorCategory.Messaging, "messaging", "Messaging", "Chat services a pipeline sends messages through.", 8),
            new ConnectorCategoryInfo(ConnectorCategory.Development, "development", "Development", "Code hosting - repositories, issues, releases and workflows.", 9),
            new ConnectorCategoryInfo(ConnectorCategory.Productivity, "productivity", "Productivity", "Calendars, tasks and projects.", 10),
            new ConnectorCategoryInfo(ConnectorCategory.Commerce, "commerce", "Commerce", "Payments, shops and orders.", 11),
            new ConnectorCategoryInfo(ConnectorCategory.Crm, "crm", "CRM & Sales", "Customers, contacts, deals and support cases.", 12),
            new ConnectorCategoryInfo(ConnectorCategory.Marketing, "marketing", "Marketing", "Audiences, subscribers and email campaigns.", 13),
            new ConnectorCategoryInfo(ConnectorCategory.Uncategorized, "uncategorized", "Uncategorized", "Connectors that declare no category yet.", int.MaxValue),
        };

        private static readonly IReadOnlyDictionary<ConnectorCategory, ConnectorCategoryInfo> ByCategory = All.ToDictionary(info => info.Category);

        public static ConnectorCategoryInfo Info(ConnectorCategory category) =>
            ByCategory.TryGetValue(category, out var info) ? info : throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown connector category.");
    }

    public enum ConnectorConfigFieldType
    {
        Text = 0,
        Secret = 1,
        Integer = 2,
        Boolean = 3,
        Select = 4,
        Url = 5,
        Multiline = 6,
        Number = 7,
        Duration = 8,
    }

    public sealed record ConnectorConfigFieldOption(string Value, string Label)
    {
        public ConnectorConfigFieldOption(string value) : this(value, value)
        {
        }
    }

    /// <summary>A field shown only while another field equals a value (compared case-insensitively).</summary>
    public sealed record ConnectorConfigFieldVisibility(string Field, string EqualsValue);

    /// <summary>One setting a connector accepts: its name, how it is shown, its type, whether it is required.</summary>
    public sealed record ConnectorConfigField(string Name, string DisplayName, ConnectorConfigFieldType Type = ConnectorConfigFieldType.Text, bool Required = false, string? Description = null)
    {
        public string? Group { get; init; }

        public string? Default { get; init; }

        public string? Placeholder { get; init; }

        public string? HelpText { get; init; }

        public IReadOnlyList<ConnectorConfigFieldOption> Options { get; init; } = Array.Empty<ConnectorConfigFieldOption>();

        public ConnectorConfigFieldVisibility? VisibleWhen { get; init; }
    }

    /// <summary>The settings a connector accepts, with validation by type; unknown keys pass, a hidden field is neither required nor validated.</summary>
    public sealed record ConnectorConfigSchema(IReadOnlyList<ConnectorConfigField> Fields)
    {
        public static ConnectorConfigSchema Empty { get; } = new(Array.Empty<ConnectorConfigField>());

        public static bool IsVisible(ConnectorConfigField field, IReadOnlyDictionary<string, string> settings)
        {
            ArgumentNullException.ThrowIfNull(field);
            ArgumentNullException.ThrowIfNull(settings);
            if (field.VisibleWhen is null)
            {
                return true;
            }

            return settings.TryGetValue(field.VisibleWhen.Field, out var value) && string.Equals(value, field.VisibleWhen.EqualsValue, StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var errors = new List<string>();
            foreach (var field in Fields)
            {
                if (!IsVisible(field, settings))
                {
                    continue;
                }

                var present = settings.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value);
                if (!present)
                {
                    if (field.Required)
                    {
                        errors.Add($"Setting '{field.Name}' is required.");
                    }

                    continue;
                }

                switch (field.Type)
                {
                    case ConnectorConfigFieldType.Integer when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                        errors.Add($"Setting '{field.Name}' must be an integer.");
                        break;
                    case ConnectorConfigFieldType.Number when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                        errors.Add($"Setting '{field.Name}' must be a number.");
                        break;
                    case ConnectorConfigFieldType.Boolean when !bool.TryParse(value, out _):
                        errors.Add($"Setting '{field.Name}' must be true or false.");
                        break;
                    case ConnectorConfigFieldType.Url when !Uri.TryCreate(value, UriKind.Absolute, out _):
                        errors.Add($"Setting '{field.Name}' must be an absolute URL.");
                        break;
                    case ConnectorConfigFieldType.Duration when !IsDuration(value):
                        errors.Add($"Setting '{field.Name}' must be a duration (whole seconds or hh:mm:ss).");
                        break;
                    case ConnectorConfigFieldType.Select when field.Options.Count > 0 && !field.Options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)):
                        errors.Add($"Setting '{field.Name}' must be one of: {string.Join(", ", field.Options.Select(option => option.Value))}.");
                        break;
                }
            }

            return errors;
        }

        private static bool IsDuration(string? value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>One row a connector extracts: named fields with scalar values.</summary>
    public sealed record ConnectorRecord(IReadOnlyDictionary<string, object?> Fields);

    /// <summary>One thing an extracting connector can do, with the parameters it takes.</summary>
    public sealed record ConnectorOperation(string Name, string DisplayName, string? Description = null)
    {
        public ConnectorConfigSchema Parameters { get; init; } = ConnectorConfigSchema.Empty;
    }

    /// <summary>The outcome of testing a connection: a sanitised message - a connector's own error text never reaches a page or an event (D110).</summary>
    public sealed record ConnectionTestResult(bool Success, string Message)
    {
        public static ConnectionTestResult Ok(string message = "Connection succeeded.") => new(true, message);

        public static ConnectionTestResult Fail(string message) => new(false, message);
    }

    /// <summary>
    /// The network access the HOST hands a connector (D110: egress is host-owned). An HTTP connector takes a guarded client
    /// (private, link-local, loopback and metadata addresses refused at connect, redirects off, one attempt with a timeout);
    /// a driver connector asks the host to resolve, check and pin its endpoint before the driver dials - the residual
    /// window between the check and the driver's own resolution is stated, not solved.
    /// </summary>
    public interface IConnectorEgress
    {
        /// <summary>A client whose every connection is checked against the egress policy and the site's allowlist.</summary>
        HttpClient CreateHttpClient(TimeSpan timeout);

        /// <summary>Resolves <paramref name="host"/>, checks each address against the policy, and returns the one the driver should dial - or null when every address is refused.</summary>
        Task<IPAddress?> ResolveAndPinAsync(string host, int port, CancellationToken cancellationToken = default);

        /// <summary>
        /// Connects to <paramref name="address"/> - the one <see cref="ResolveAndPinAsync"/> returned for <paramref name="host"/> -
        /// on <paramref name="port"/> and closes, within <paramref name="timeout"/> (BL-229): for a driver that connects
        /// synchronously with the system's own timeout whatever its own says (libldap on Linux, about two minutes), so an address
        /// that does not answer fails in the connection's time. A refusal or no answer is thrown as an
        /// <see cref="InvalidOperationException"/> naming <paramref name="service"/>, the host and the port; an address the policy
        /// refuses as its denial; the caller's own cancellation stays its own. An egress that does not reach returns at once, and
        /// the driver dials as before.
        /// </summary>
        Task ReachAsync(IPAddress address, int port, string host, TimeSpan timeout, string service, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Whether <paramref name="exception"/> (or an inner exception) is the HOST's refusal of the destination - which a
        /// connector must let propagate, never fold into a <see cref="ConnectionTestResult.Fail"/>, so the runner
        /// categorises it <c>DeniedByPolicy</c> (BL-96). A connector references the contract only; the host's guard answers.
        /// </summary>
        bool IsDenial(Exception exception) => exception is ConnectorEgressDeniedException || exception.InnerException is ConnectorEgressDeniedException;
    }

    /// <summary>What a connection test runs with: the endpoint, the settings, the credential fields the scheme declares, a timeout, and the host's egress.</summary>
    public sealed record ConnectorTestContext(string Endpoint, IReadOnlyDictionary<string, string> Settings, TimeSpan Timeout, IConnectorEgress Egress)
    {
        public IReadOnlyDictionary<string, string> Credentials { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>What an extraction runs with: the test context's members plus the operation and its parameters.</summary>
    public sealed record ConnectorExtractContext(string Endpoint, IReadOnlyDictionary<string, string> Settings, TimeSpan Timeout, IConnectorEgress Egress, string OperationName, IReadOnlyDictionary<string, string> Parameters)
    {
        public IReadOnlyDictionary<string, string> Credentials { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The host's managed file storage for an operation that produces files (BL-96 increment 2); null when the host offers none, and such an operation says so.</summary>
        public IConnectorFileSink? FileSink { get; init; }

        /// <summary>The files of the place the operation runs in - a pipeline run's folder (BL-229) - to upload from and download to; null outside a run, and an operation that needs it says so.</summary>
        public IConnectorFiles? Files { get; init; }
    }

    /// <summary>
    /// The connector type contract: a plugin that knows how to reach one kind of external system. Shipped as a
    /// <c>*.Stomme</c> assembly, registered as <see cref="IConnector"/>, surfaced through <see cref="IConnectorCatalog"/>.
    /// A Connection (the Connectivity module) is a configured instance; testing it runs <see cref="TestConnectionAsync"/>
    /// with the Connection's endpoint, settings, the credentials resolved from the per-site secret store, and the host's egress.
    /// </summary>
    public interface IConnector
    {
        /// <summary>Stable connector type id (e.g. <c>postgresql</c>, <c>rest</c>); unique across connectors, compared case-insensitively.</summary>
        string ConnectorType { get; }

        string DisplayName { get; }

        ConnectorConfigSchema ConfigSchema => ConnectorConfigSchema.Empty;

        AuthenticationType AuthType => AuthenticationType.None;

        /// <summary>Whether the endpoint is a URL or a driver connection string; a connection string is never a place for a credential (D110).</summary>
        ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.Url;

        /// <summary>An endpoint of this connector's own, shown in the connection form where one is typed; null: the kind's generic example.</summary>
        string? EndpointExample => null;

        /// <summary>A connector that reaches the HOST itself (a process, the file system): creating or editing a connection of this type requires the Host role.</summary>
        bool HostOnly => false;

        ConnectorCapabilities Capabilities => ConnectorCapabilities.TestConnection | (this is IConnectorExtractor ? ConnectorCapabilities.Extract : ConnectorCapabilities.None);

        IReadOnlyList<ConnectorCategory> Categories => Array.Empty<ConnectorCategory>();

        Uri? WebsiteUrl => null;

        Uri? ApiDocumentationUrl => null;

        /// <summary>An optional self-contained inline SVG icon; null means the catalogue derives a brand-neutral monogram.</summary>
        string? Icon => null;

        /// <summary>
        /// The credential fields' labels in the service's own words - a field of the scheme shown as the service names it (a key
        /// pair's secret key as Trello's "Token"). Only the LABEL changes: the field's name, and so where its secret is kept, stays
        /// the scheme's (D110), and a label for a name the scheme does not declare is refused by the catalogue's test. Null: the
        /// scheme's own labels.
        /// </summary>
        IReadOnlyDictionary<string, string>? CredentialLabels => null;

        Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default);
    }

    /// <summary>A connector that reads records out of its system, one operation at a time.</summary>
    public interface IConnectorExtractor
    {
        IReadOnlyList<ConnectorOperation> Operations { get; }

        IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, CancellationToken cancellationToken = default);
    }

    /// <summary>Every registered connector, by type key (case-insensitive). The host's catalog REFUSES a duplicate key at composition (D110; the predecessor platform's first-registration-wins is the hijack D101 and D103 refused).</summary>
    public interface IConnectorCatalog
    {
        IConnector? Find(string connectorType);

        IReadOnlyCollection<IConnector> All { get; }
    }

    /// <summary>One group of a connector picker: a category with the connectors carrying it, by display name.</summary>
    public sealed record ConnectorPickerGroup(string Id, string Label, IReadOnlyList<IConnector> Connectors);

    /// <summary>Pickers group connectors by category in the registry's order, Uncategorized last; a tag-like connector appears under each category it carries; a connector with no category is Uncategorized.</summary>
    public static class ConnectorPickerGroups
    {
        public static IReadOnlyList<ConnectorPickerGroup> Build(IEnumerable<IConnector> connectors)
        {
            ArgumentNullException.ThrowIfNull(connectors);
            return connectors
                .SelectMany(connector => (connector.Categories.Count == 0 ? new[] { ConnectorCategory.Uncategorized } : connector.Categories).Distinct().Select(category => (Info: ConnectorCategories.Info(category), Connector: connector)))
                .GroupBy(pair => pair.Info.Id)
                .OrderBy(group => group.First().Info.Order)
                .Select(group => new ConnectorPickerGroup(group.Key, group.First().Info.Label, group.Select(pair => pair.Connector).OrderBy(connector => connector.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();
        }
    }
}
