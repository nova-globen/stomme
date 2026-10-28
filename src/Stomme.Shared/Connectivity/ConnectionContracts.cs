#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Connectivity
{
    // ============================================================================================================
    // The Connectivity module's Shared contracts (BL-94 increment 6, D110): the permission keys, the shapes a page or
    // an API consumer reads (a connection, a connector, a test's verdict), the delete guard's port and the extraction
    // port a pipeline step consumes. BCL-ONLY like the connector contract beside it (SharedConnectivityDependencyTests).
    // ============================================================================================================

    /// <summary>
    /// The Connectivity module's permission KEYS, decided by the authorization bridge (D99): each key is a site
    /// Permission row <c>(EntityName = "connections", EntityId = -1, PermissionName = operation)</c>. <see cref="View"/>
    /// lists the site's connections and the connector catalog; <see cref="Manage"/> creates, edits, tests and deletes
    /// the site's connections. Both default to Administrators; a HostOnly connector needs the Host role on top.
    /// </summary>
    public static class ConnectionPermissions
    {
        public const string Resource = "connections";

        public const string View = "connections:view";

        public const string Manage = "connections:manage";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { "Administrators" }),
            (Manage, new[] { "Administrators" }),
        };
    }

    /// <summary>
    /// How a connector call ended, as a CATEGORY (D110 refutation 3): a connector's own text - which may echo the
    /// endpoint, a user name or a password - never reaches a page, an event or a log; the category does, and the log
    /// carries the exception TYPE.
    /// </summary>
    public enum ConnectorOutcome
    {
        /// <summary>The connector reported success.</summary>
        Succeeded = 0,

        /// <summary>The connector reported failure (the system refused, answered wrongly, or was unreachable in a way the connector recognised).</summary>
        Failed = 1,

        /// <summary>No connector of the connection's type is installed.</summary>
        ConnectorUnavailable = 2,

        /// <summary>The connection names no row for one of its credential fields; an operator re-enters it.</summary>
        CredentialsMissing = 3,

        /// <summary>A credential row no longer unprotects (the Data Protection key ring changed); an operator re-enters it.</summary>
        CredentialsUnreadable = 4,

        /// <summary>The host's egress policy refused the destination (loopback, private, link-local or metadata address, not allowlisted).</summary>
        DeniedByPolicy = 5,

        /// <summary>The connection's timeout elapsed.</summary>
        TimedOut = 6,

        /// <summary>The connector threw; the log carries the exception type.</summary>
        Faulted = 7,

        /// <summary>No connection with that id belongs to the request's site.</summary>
        ConnectionNotFound = 8,

        /// <summary>The connector does not extract.</summary>
        ExtractionUnsupported = 9,

        /// <summary>The operation named is not one the connector declares.</summary>
        OperationUndefined = 10,

        /// <summary>The server offered a host key the connection does not name - or it names none - and nothing was sent to it (BL-229, SFTP).</summary>
        HostKeyUntrusted = 11,
    }

    /// <summary>The fixed text for each <see cref="ConnectorOutcome"/> - the only message a page, an event or a bus ever carries for a connector call.</summary>
    public static class ConnectorOutcomes
    {
        public static bool IsSuccess(ConnectorOutcome outcome) => outcome == ConnectorOutcome.Succeeded;

        public static string Message(ConnectorOutcome outcome) => outcome switch
        {
            ConnectorOutcome.Succeeded => "Connection succeeded.",
            ConnectorOutcome.Failed => "The connector reported a failure.",
            ConnectorOutcome.ConnectorUnavailable => "No connector of this type is installed.",
            ConnectorOutcome.CredentialsMissing => "A credential this connection needs is not stored; enter it again.",
            ConnectorOutcome.CredentialsUnreadable => "A stored credential can no longer be read; enter it again.",
            ConnectorOutcome.DeniedByPolicy => "The destination is not permitted by the outbound policy.",
            ConnectorOutcome.TimedOut => "The connection timed out.",
            ConnectorOutcome.Faulted => "The connector faulted; the log carries the exception type.",
            ConnectorOutcome.ConnectionNotFound => "The connection was not found.",
            ConnectorOutcome.ExtractionUnsupported => "The connector does not support extraction.",
            ConnectorOutcome.OperationUndefined => "The operation is not one the connector defines.",
            ConnectorOutcome.HostKeyUntrusted => "The server's host key is not one this connection trusts, so nothing was sent to it: set its Host key to the server's own - its SHA256 fingerprint, or the line ssh-keyscan prints for it.",
            _ => "The connector call ended with an unknown outcome.",
        };
    }

    /// <summary>A connection as a page or an API consumer sees it: no secret, only the NAMES of the credential fields stored.</summary>
    public sealed record ConnectionView(
        Guid Id,
        string Name,
        string ConnectorType,
        string ConnectorDisplayName,
        string Endpoint,
        IReadOnlyDictionary<string, string> Settings,
        IReadOnlyList<string> CredentialFields,
        int TimeoutSeconds,
        string Status,
        DateTime? LastTestedUtc,
        bool ConnectorInstalled = true)
    {
        public bool HasCredentials => CredentialFields.Count > 0;
    }

    /// <summary>The verdict of testing a connection: the category, its fixed text, and the connection's status afterwards.</summary>
    public sealed record ConnectionTestView(bool Success, ConnectorOutcome Outcome, string Message, string Status);

    /// <summary>
    /// The site's egress allowlist (BL-180): the private and loopback hosts the site's connections may reach - and the AI
    /// assistant's model endpoint, which rides the same egress - as exact host names or IP addresses. A holder of
    /// connections:view reads it; only the Host role changes it (<see cref="CanEdit"/>), because widening a site's egress
    /// to private addresses opens the installation's own network to the site.
    /// </summary>
    public sealed record ConnectionEgressView(IReadOnlyList<string> AllowedHosts, bool CanEdit);

    /// <summary>The allowlist as typed: hosts separated by commas, semicolons, spaces or new lines.</summary>
    public sealed class ConnectionEgressRequest
    {
        public string AllowedHosts { get; set; } = string.Empty;
    }

    public sealed record ConnectorFieldOptionView(string Value, string Label);

    public sealed record ConnectorFieldVisibilityView(string Field, string EqualsValue);

    /// <summary>A configurable connector setting, projected for the connection editor (the type as the enum's name).</summary>
    public sealed record ConnectorFieldView(
        string Name,
        string DisplayName,
        string Type,
        bool Required,
        string? Description,
        string? Group,
        string? Default,
        string? Placeholder,
        string? HelpText,
        IReadOnlyList<ConnectorFieldOptionView> Options,
        ConnectorFieldVisibilityView? VisibleWhen);

    public sealed record ConnectorCategoryView(string Id, string Label, int Order);

    /// <summary>One credential field the connector's authentication scheme declares; <see cref="Secret"/> says whether an editor masks it.</summary>
    public sealed record ConnectorAuthFieldView(string Name, string DisplayName, bool Secret);

    public sealed record ConnectorOperationView(string Name, string DisplayName, string? Description, IReadOnlyList<ConnectorFieldView> Parameters);

    /// <summary>An installed connector type as the editor sees it: its schema, its scheme's credential fields, its categories, its operations.</summary>
    public sealed record ConnectorView(
        string ConnectorType,
        string DisplayName,
        string AuthType,
        string EndpointKind,
        bool HostOnly,
        IReadOnlyList<ConnectorFieldView> Fields,
        IReadOnlyList<ConnectorAuthFieldView> AuthFields,
        IReadOnlyList<ConnectorCategoryView> Categories,
        IReadOnlyList<ConnectorOperationView> Operations,
        string? WebsiteUrl,
        string? ApiDocumentationUrl,
        string Icon)
    {
        /// <summary>An endpoint of the connector's own for the form to show (BL-229: an IMAP server is no database connection string); null: the kind's generic example.</summary>
        public string? EndpointExample { get; init; }
    }

    /// <summary>What a create or an update carries (BL-94 increment 7): the connector type is fixed at creation; a credential value given rotates that field, a blank or absent one leaves it. The site is the request's.</summary>
    public sealed class ConnectionRequest
    {
        public string Name { get; set; } = string.Empty;

        public string ConnectorType { get; set; } = string.Empty;

        public string Endpoint { get; set; } = string.Empty;

        public Dictionary<string, string>? Settings { get; set; }

        public Dictionary<string, string>? Credentials { get; set; }

        public int TimeoutSeconds { get; set; } = 30;
    }

    /// <summary>
    /// A connector's catalog icon: the connector's own inline SVG when it declares one, otherwise a brand-neutral monogram
    /// tile - a rounded square tinted by the primary category carrying the display name's initials. Only letters and
    /// digits reach the initials and the text is escaped besides, so a display name carrying markup renders as text
    /// (BL-94 increment 7; the predecessor platform's renderer let a leading <c>&lt;</c> through).
    /// </summary>
    public static class ConnectorIcons
    {
        public static string For(IConnector connector)
        {
            ArgumentNullException.ThrowIfNull(connector);
            if (!string.IsNullOrWhiteSpace(connector.Icon))
            {
                return connector.Icon!;
            }

            return Monogram(connector.DisplayName, connector.Categories.Count > 0 ? connector.Categories[0] : ConnectorCategory.Uncategorized);
        }

        public static string Monogram(string? displayName, ConnectorCategory primaryCategory)
        {
            var initials = System.Security.SecurityElement.Escape(Initials(displayName)) ?? "?";
            return "<svg viewBox=\"0 0 40 40\" width=\"26\" height=\"26\" xmlns=\"http://www.w3.org/2000/svg\" class=\"connector-icon\" aria-hidden=\"true\" focusable=\"false\">" +
                $"<rect width=\"40\" height=\"40\" rx=\"9\" fill=\"{Fill(primaryCategory)}\"></rect>" +
                "<text x=\"20\" y=\"26\" text-anchor=\"middle\" font-family=\"system-ui,-apple-system,'Segoe UI',sans-serif\" font-size=\"15\" font-weight=\"600\" fill=\"#ffffff\">" +
                initials + "</text></svg>";
        }

        /// <summary>The leading letters of the first two words when they differ, else the first two letters of the first word; letters and digits only.</summary>
        public static string Initials(string? displayName)
        {
            var words = (displayName ?? string.Empty)
                .Split(new[] { ' ', '/', '-', '_', '.', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
                .Where(word => word.Length > 0)
                .ToArray();
            if (words.Length == 0)
            {
                return "?";
            }

            if (words.Length >= 2 && char.ToUpperInvariant(words[0][0]) != char.ToUpperInvariant(words[1][0]))
            {
                return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
            }

            return words[0].Length >= 2
                ? $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[0][1])}"
                : char.ToUpperInvariant(words[0][0]).ToString();
        }

        private static string Fill(ConnectorCategory category) => category switch
        {
            ConnectorCategory.DataAndFiles => "#1d4ed8",
            ConnectorCategory.SecurityVulnerability => "#b91c1c",
            ConnectorCategory.MicrosoftCloudDirectory => "#0e7490",
            ConnectorCategory.ItsmAssetManagement => "#b45309",
            ConnectorCategory.CloudAndSaasCost => "#047857",
            ConnectorCategory.NetworkInfrastructure => "#6d28d9",
            ConnectorCategory.AiProviderUsage => "#be185d",
            ConnectorCategory.Development => "#4338ca",
            ConnectorCategory.Productivity => "#0f766e",
            ConnectorCategory.Commerce => "#a16207",
            ConnectorCategory.Crm => "#0369a1",
            ConnectorCategory.Marketing => "#c2410c",
            _ => "#475569",
        };
    }

    /// <summary>Whether a connection is in use, with the reason when it is.</summary>
    public sealed record ConnectionUsage(bool InUse, string? Description)
    {
        public static ConnectionUsage NotUsed { get; } = new(false, null);

        public static ConnectionUsage Used(string description) => new(true, description);
    }

    /// <summary>
    /// A consumer that references connections by id says whether one is in use, so a connection a pipeline or a
    /// dataset still points at is not deleted (D110). Connectivity owns the contract; consumers implement it; the
    /// delete command consults every registered provider.
    /// </summary>
    public interface IConnectionUsageProvider
    {
        Task<ConnectionUsage> GetUsageAsync(Guid connectionId, CancellationToken ct = default);
    }

    /// <summary>The record cap an extraction materialises up to (D110): a consumer asks for fewer, never for more than <see cref="MaxMaxRecords"/>.</summary>
    public static class ConnectorExtractionLimits
    {
        public const int DefaultMaxRecords = 10_000;

        public const int MaxMaxRecords = 100_000;

        public static int Clamp(int maxRecords) => Math.Clamp(maxRecords, 1, MaxMaxRecords);
    }

    /// <summary>The outcome of a connection-scoped extract: the category and its fixed text, the records read, and whether the cap cut the read short.</summary>
    public sealed record ConnectorExtractionResult(bool Success, ConnectorOutcome Outcome, string Message, IReadOnlyList<ConnectorRecord> Records, bool Truncated)
    {
        public static ConnectorExtractionResult Ok(IReadOnlyList<ConnectorRecord> records, bool truncated, int cap) =>
            new(true, ConnectorOutcome.Succeeded,
                truncated ? $"Extracted {records.Count} record(s); the cap of {cap} was reached and the rest were left unread." : $"Extracted {records.Count} record(s).",
                records, truncated);

        public static ConnectorExtractionResult Fail(ConnectorOutcome outcome) =>
            new(false, outcome, ConnectorOutcomes.Message(outcome), Array.Empty<ConnectorRecord>(), false);
    }

    /// <summary>
    /// Resolves a connection id to a live extract (D110): the connection, its connector, the operation, the
    /// credentials from the per-site secret store and the HOST's egress, then the connector's records up to the cap.
    /// Implemented by the Connectivity module; consumed by a pipeline's extraction step, which never reaches the
    /// aggregate or a secret directly. A failure is a result with a category, never an exception.
    /// </summary>
    public interface IConnectorExtractionService
    {
        Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters,
            int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default);

        /// <summary>
        /// The same extract with the files of the place it runs in - a pipeline run's folder (BL-229) - for an operation that
        /// uploads or downloads. An implementation that does not take files hands the connector none, and such an operation
        /// refuses: fail-closed, never a host path.
        /// </summary>
        Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters,
            int maxRecords, IConnectorFiles? files, CancellationToken cancellationToken) =>
            ExtractAsync(connectionId, operation, parameters, maxRecords, cancellationToken);
    }
}
