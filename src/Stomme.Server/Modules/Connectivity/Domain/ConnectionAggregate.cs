#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Stomme.Connectivity;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Connectivity.Domain
{
    public enum ConnectionStatus
    {
        Untested = 0,
        Healthy = 1,
        Failing = 2,
    }

    /// <summary>
    /// A configured instance of a connector type (BL-94 increment 5, D110): the endpoint, the settings the connector's
    /// schema accepts, the NAMES of the credential fields stored for it (the values live only in the per-site secret
    /// store under <see cref="ConnectionCredentialKeys"/>), a bounded timeout, the last test's verdict. Tenant-owned
    /// and site-scoped, named with a normalised twin (D49); the connector type is lower-cased so a key compares the
    /// same way everywhere. the predecessor platform's free-form <c>CredentialRef</c> and <c>MaxRetries</c> are gone (D110).
    /// </summary>
    public sealed class ConnectionAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxNameLength = 120;
        public const int MinTimeoutSeconds = 1;
        public const int MaxTimeoutSeconds = 300;

        private readonly List<string> _credentialFields = new();

        private ConnectionAggregate()
        {
            TenantId = null!;
            Name = string.Empty;
            NameNormalized = string.Empty;
            ConnectorType = string.Empty;
            Endpoint = null!;
            Settings = ConnectionSettings.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Name { get; private set; }

        public string NameNormalized { get; private set; }

        /// <summary>The connector type key, lower-cased.</summary>
        public string ConnectorType { get; private set; }

        public ConnectionEndpoint Endpoint { get; private set; }

        public ConnectionSettings Settings { get; private set; }

        /// <summary>The names of the credential fields stored for this connection - never the values (the JSON-list convention).</summary>
        public IReadOnlyList<string> CredentialFields => _credentialFields;

        public int TimeoutSeconds { get; private set; }

        public ConnectionStatus Status { get; private set; }

        public DateTime? LastTestedUtc { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public DateTime ModifiedUtc { get; private set; }

        public static string NormalizeName(string name) => (name ?? string.Empty).Trim().ToUpperInvariant();

        public static ConnectionAggregate Create(Guid id, TenantId tenantId, int siteId, string name, string connectorType, ConnectorEndpointKind endpointKind, string endpoint,
            IReadOnlyDictionary<string, string>? settings, IEnumerable<string>? credentialFields, int timeoutSeconds, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A connection belongs to a site.");
            }

            var connection = new ConnectionAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                ConnectorType = RequiredType(connectorType),
                CreatedUtc = nowUtc,
            };
            connection.Apply(name, endpointKind, endpoint, settings, credentialFields, timeoutSeconds, nowUtc);
            connection.RaiseDomainEvent(new ConnectionCreatedEvent(id, connection.Name, connection.ConnectorType));
            return connection;
        }

        /// <summary>A new configuration: everything but the identity and the type; the verdict resets to Untested.</summary>
        public void Reconfigure(string name, ConnectorEndpointKind endpointKind, string endpoint, IReadOnlyDictionary<string, string>? settings, IEnumerable<string>? credentialFields, int timeoutSeconds, DateTime nowUtc)
        {
            Apply(name, endpointKind, endpoint, settings, credentialFields, timeoutSeconds, nowUtc);
            RaiseDomainEvent(new ConnectionReconfiguredEvent(Id, Name));
        }

        public void MarkDeleted() => RaiseDomainEvent(new ConnectionDeletedEvent(Id, Name));

        /// <summary>The verdict of a test: Healthy or Failing, with a SANITISED message (a connector's own error text never reaches an event, D110).</summary>
        public void RecordTestResult(bool success, string message, DateTime nowUtc)
        {
            Status = success ? ConnectionStatus.Healthy : ConnectionStatus.Failing;
            LastTestedUtc = nowUtc;
            ModifiedUtc = nowUtc;
            RaiseDomainEvent(new ConnectionTestedEvent(Id, success, message ?? string.Empty));
            if (!success)
            {
                RaiseDomainEvent(new ConnectionFailedEvent(Id, message ?? string.Empty));
            }
        }

        private void Apply(string name, ConnectorEndpointKind endpointKind, string endpoint, IReadOnlyDictionary<string, string>? settings, IEnumerable<string>? credentialFields, int timeoutSeconds, DateTime nowUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var cleanName = name.Trim();
            if (cleanName.Length > MaxNameLength)
            {
                throw new ArgumentException($"A connection name must be at most {MaxNameLength} characters.", nameof(name));
            }

            if (timeoutSeconds < MinTimeoutSeconds || timeoutSeconds > MaxTimeoutSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), $"A connection timeout is {MinTimeoutSeconds} to {MaxTimeoutSeconds} seconds.");
            }

            Name = cleanName;
            NameNormalized = NormalizeName(cleanName);
            Endpoint = ConnectionEndpoint.For(endpointKind, endpoint);
            Settings = ConnectionSettings.From(settings);
            _credentialFields.Clear();
            _credentialFields.AddRange((credentialFields ?? Array.Empty<string>()).Select(ConnectionCredentialKeys.RequireField).Distinct(StringComparer.Ordinal));
            TimeoutSeconds = timeoutSeconds;
            Status = ConnectionStatus.Untested;
            LastTestedUtc = null;
            ModifiedUtc = nowUtc;
        }

        private static string RequiredType(string connectorType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectorType);
            return connectorType.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Where a connection reaches its system: a URL or a driver connection string, as the connector declares. A
    /// connection string carrying a credential keyword (<c>Password</c>, <c>Pwd</c>, <c>Secret</c>, <c>Token</c>) is
    /// REFUSED (D110 refutation 11): the endpoint is persisted plainly, listed to every <c>connections:view</c> holder
    /// and carried by export and backup - a secret belongs in the secret store under a declared field.
    /// </summary>
    public sealed class ConnectionEndpoint : ValueObject, IStringValueObject
    {
        public const int MaxLength = 2048;

        private static readonly string[] CredentialKeywords = { "password", "pwd", "secret", "token", "apikey", "api_key", "api-key" };

        private ConnectionEndpoint(string value) => Value = value;

        public string Value { get; }

        public static ConnectionEndpoint For(ConnectorEndpointKind kind, string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("A connection needs an endpoint.", nameof(value));
            }

            if (trimmed.Length > MaxLength)
            {
                throw new ArgumentException($"An endpoint must be at most {MaxLength} characters.", nameof(value));
            }

            if (kind == ConnectorEndpointKind.Url)
            {
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ArgumentException("A URL endpoint must be an absolute http or https URL.", nameof(value));
                }

                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    throw new ArgumentException("A URL endpoint must not carry credentials; store them as the connector's credential fields.", nameof(value));
                }
            }
            else if (CredentialKeyword(trimmed) is { } keyword)
            {
                throw new ArgumentException($"A connection string must not carry a credential ('{keyword}'); store it as the connector's credential field.", nameof(value));
            }

            return new ConnectionEndpoint(trimmed);
        }

        /// <summary>Rehydration from a stored value: validated when it was made.</summary>
        public static ConnectionEndpoint Create(string value) => new(value ?? string.Empty);

        /// <summary>The credential keyword a connection string carries as a key, or null.</summary>
        public static string? CredentialKeyword(string connectionString)
        {
            foreach (var part in (connectionString ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = part.Split('=', 2)[0].Trim().Replace(" ", string.Empty).ToLowerInvariant();
                if (CredentialKeywords.Contains(key))
                {
                    return part.Split('=', 2)[0].Trim();
                }
            }

            return null;
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>The connector's settings as ONE JSON string column (the D101 McpServer precedent); read back through <see cref="GetSettings"/>.</summary>
    public sealed class ConnectionSettings : ValueObject, IStringValueObject
    {
        public const int MaxLength = 8000;

        public static readonly ConnectionSettings Empty = new("{}");

        private ConnectionSettings(string value) => Value = value;

        public string Value { get; }

        public static ConnectionSettings From(IReadOnlyDictionary<string, string>? settings)
        {
            if (settings is null || settings.Count == 0)
            {
                return Empty;
            }

            var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in settings)
            {
                var trimmedKey = (key ?? string.Empty).Trim();
                ArgumentException.ThrowIfNullOrWhiteSpace(trimmedKey, nameof(settings));
                normalized[trimmedKey] = value ?? string.Empty;
            }

            var json = JsonSerializer.Serialize(normalized);
            if (json.Length > MaxLength)
            {
                throw new ArgumentException($"A connection's settings must serialise to at most {MaxLength} characters.", nameof(settings));
            }

            return new ConnectionSettings(json);
        }

        /// <summary>Rehydration from a stored value.</summary>
        public static ConnectionSettings Create(string value) => new(string.IsNullOrWhiteSpace(value) ? "{}" : value);

        public IReadOnlyDictionary<string, string> GetSettings()
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(Value) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>
    /// The closed key grammar for a connection's secrets (D110): <c>connectivity.connection-&lt;id:N&gt;.&lt;field&gt;</c>, where
    /// <c>field</c> is a name the connector's authentication scheme declares AND already fits the secret store's segment
    /// grammar - anything else is REJECTED, never rewritten, which closes the predecessor platform's free-form name and <c>../</c> class.
    /// </summary>
    public static class ConnectionCredentialKeys
    {
        public const string Prefix = "connectivity.connection-";

        public static string For(Guid connectionId, string field) => Prefix + connectionId.ToString("N") + "." + RequireField(field);

        /// <summary>The field as a valid key segment, or an exception naming why it is not.</summary>
        public static string RequireField(string? field)
        {
            var trimmed = (field ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("A credential field has a name.", nameof(field));
            }

            var key = Prefix + Guid.Empty.ToString("N") + "." + trimmed;
            if (trimmed.Contains('.') || !Stomme.Infrastructure.Security.SiteSecretStore.IsValidReferenceKey(key))
            {
                throw new ArgumentException($"'{trimmed}' is not a valid credential field name: lower-case letters, digits and single hyphens only.", nameof(field));
            }

            return trimmed;
        }

        /// <summary>The field must be one the connector's scheme declares; a connector with no scheme takes none.</summary>
        public static string RequireDeclared(AuthenticationType authType, string? field)
        {
            var name = RequireField(field);
            if (!AuthenticationScheme.FieldsFor(authType).Any(declared => string.Equals(declared.Name, name, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"'{name}' is not a credential field the {authType} scheme declares.", nameof(field));
            }

            return name;
        }
    }

    public sealed record ConnectionCreatedEvent(Guid ConnectionId, string Name, string ConnectorType) : DomainEvent;

    public sealed record ConnectionReconfiguredEvent(Guid ConnectionId, string Name) : DomainEvent;

    public sealed record ConnectionTestedEvent(Guid ConnectionId, bool Success, string Message) : DomainEvent;

    public sealed record ConnectionFailedEvent(Guid ConnectionId, string Message) : DomainEvent;

    public sealed record ConnectionDeletedEvent(Guid ConnectionId, string Name) : DomainEvent;
}
