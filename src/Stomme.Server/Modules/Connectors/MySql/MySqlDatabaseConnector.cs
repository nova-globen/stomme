#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using MySqlConnector;

namespace Stomme.Modules.Connectors.MySql
{
    /// <summary>
    /// The MySQL connector (n8n's MySQL node; MariaDB too, through the same driver - named apart from the driver's own
    /// namespace, MySqlConnector): the endpoint is a MySqlConnector connection string WITHOUT credentials (a connection string is never a place for a credential); the Basic scheme's
    /// <c>username</c>/<c>password</c> are applied from the per-site secret store. A test opens a connection and runs
    /// <c>SELECT 1</c> within the timeout, naming the server's version; the <c>query</c> operation runs a statement and streams
    /// each row into a record, its <c>@name</c> values bound as parameters (<see cref="SqlValues"/>) - a statement that returns
    /// no rows (MySQL has no RETURNING) answers one record of the rows it changed and the id it inserted last.
    /// </summary>
    /// <remarks>
    /// <b>The driver dials only an address the host has checked</b>, as PostgreSQL's does: the connection string's one host is
    /// handed to <see cref="IConnectorEgress.ResolveAndPinAsync"/> and the permitted address written in its place. MySQL adds
    /// three ways round that, each refused before anything is dialled: a Unix socket, a named pipe or shared memory instead of
    /// TCP (a local endpoint the egress never sees), the server's own redirection to another host (Azure's), and more than one
    /// host. And one way into this host: <c>LOAD DATA LOCAL INFILE</c> lets the SERVER ask the client for a file of its
    /// choosing - refused when asked for, and switched off always. A value that reads as a whole or decimal number (no leading
    /// zero) is bound as one, as n8n's node does, so <c>LIMIT @n</c> works; anything else as text, which MySQL converts.
    /// </remarks>
    public sealed class MySqlDatabaseConnector : IConnector, IConnectorExtractor
    {
        private static readonly Regex WholeNumber = new("^-?(0|[1-9][0-9]{0,17})$", RegexOptions.CultureInvariant);

        private static readonly Regex DecimalNumber = new("^-?(0|[1-9][0-9]{0,17})\\.[0-9]{1,10}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "mysql";

        public string DisplayName => "MySQL";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string? EndpointExample => "Server=db.example.com;Port=3306;Database=kassa";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri? WebsiteUrl => new("https://www.mysql.com");

        public Uri? ApiDocumentationUrl => new("https://dev.mysql.com/doc/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("application_name", "Application name", ConnectorConfigFieldType.Text, Required: false,
                Description: "Optional name reported to the server as the connection's program_name."),
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("query", "SQL query", "Runs a SQL statement and reads each result row into a record - a statement that returns no rows answers the rows it changed and the id it inserted last; values given as @name parameters are bound, never spliced into the SQL.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("query", "SQL query", ConnectorConfigFieldType.Multiline, Required: true, Description: "The statement to execute - a SELECT, or a write (its changed rows and last inserted id come back). Write each value as @name and give it as an '@name = value' parameter."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            MySqlConnectionStringBuilder builder;
            try
            {
                builder = Build(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidCastException)
            {
                return ConnectionTestResult.Fail($"Invalid MySQL connection string: {ex.Message}");
            }

            await PinAsync(builder, context.Egress, cancellationToken);

            try
            {
                await using var connection = new MySqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new MySqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return ConnectionTestResult.Ok($"Connected to MySQL {connection.ServerVersion}.");
            }
            catch (Exception ex) when (ex is MySqlException or TimeoutException or InvalidOperationException or SocketException)
            {
                return ConnectionTestResult.Fail(ex.Message); // the server's or the network's failure; the message stays inside the runner
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!context.Parameters.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
            {
                throw new InvalidOperationException("The 'query' parameter is required for the query operation.");
            }

            var values = SqlValues.Of(context.Parameters); // a bad name refused before anything is dialled

            var builder = Build(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            await PinAsync(builder, context.Egress, cancellationToken);

            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand(query, connection);
            foreach (var (name, value) in values)
            {
                command.Parameters.AddWithValue("@" + name, Typed(value));
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = false;
            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                rows = true;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        fields[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
                    }

                    yield return new ConnectorRecord(fields);
                }
            }
            while (await reader.NextResultAsync(cancellationToken));

            if (!rows)
            {
                // A write: MySQL has no RETURNING, so what it changed is the answer.
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["affected_rows"] = (long)Math.Max(reader.RecordsAffected, 0),
                    ["last_insert_id"] = command.LastInsertedId,
                });
            }
        }

        /// <summary>A value that reads as a whole or decimal number - no leading zero, no exponent - bound as one; anything else as text.</summary>
        internal static object Typed(string value) =>
            WholeNumber.IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? whole
            : DecimalNumber.IsMatch(value) && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var exact) ? exact
            : value;

        /// <summary>
        /// The scheme's named credentials, the optional application name, a clamped timeout - the endpoint itself carries no
        /// credential; LOCAL INFILE refused when asked for and off always; TCP to one host, no redirection.
        /// </summary>
        internal static MySqlConnectionStringBuilder Build(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout)
        {
            var builder = new MySqlConnectionStringBuilder(endpoint);
            if (builder.AllowLoadLocalInfile)
            {
                throw new ArgumentException("AllowLoadLocalInfile is never allowed - through LOAD DATA LOCAL INFILE the server could read any file of this host.");
            }

            if (builder.ConnectionProtocol != MySqlConnectionProtocol.Sockets)
            {
                throw new ConnectorEgressDeniedException("A MySQL connection dials a host over TCP; a Unix socket, a named pipe or shared memory is a local endpoint this site's egress policy cannot check.");
            }

            if (builder.ServerRedirectionMode != MySqlServerRedirectionMode.Disabled)
            {
                throw new ConnectorEgressDeniedException("A MySQL connection does not follow the server's redirection - it would dial a host this site's egress policy never checked.");
            }

            var server = builder.Server ?? string.Empty;
            if (server.Contains(',', StringComparison.Ordinal))
            {
                throw new ConnectorEgressDeniedException("A MySQL connection names ONE host; a list of hosts cannot be pinned to a checked address.");
            }

            if (server.Trim().Length == 0)
            {
                throw new ArgumentException("it names no server - Server=db.example.com.");
            }

            if (server.StartsWith('/') || server.Contains('\\', StringComparison.Ordinal) || server == ".")
            {
                throw new ConnectorEgressDeniedException($"'{server}' is not a host to dial over TCP - name the database server's host (Server=db.example.com).");
            }

            if (credentials.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password))
            {
                builder.Password = password;
            }

            if (credentials.TryGetValue("username", out var username) && !string.IsNullOrWhiteSpace(username))
            {
                builder.UserID = username;
            }

            if (settings.TryGetValue("application_name", out var applicationName) && !string.IsNullOrWhiteSpace(applicationName))
            {
                builder.ApplicationName = applicationName;
            }

            builder.AllowLoadLocalInfile = false;
            builder.ConnectionProtocol = MySqlConnectionProtocol.Sockets;
            builder.ServerRedirectionMode = MySqlServerRedirectionMode.Disabled;
            builder.ConnectionTimeout = (uint)Math.Clamp((int)timeout.TotalSeconds, 1, 1024);
            return builder;
        }

        /// <summary>Resolves, checks and pins the one host the driver may dial; a refused or unresolvable host is the guard's own refusal.</summary>
        private static async Task PinAsync(MySqlConnectionStringBuilder builder, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            var host = builder.Server.Trim().Trim('[', ']');
            var port = (int)builder.Port; // the driver's own 3306 when the string names none
            var pinned = await egress.ResolveAndPinAsync(host, port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The database host '{host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            builder.Server = pinned.ToString();
            builder.Port = (uint)port;
        }
    }
}
