#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using Microsoft.Data.SqlClient;

namespace Stomme.Modules.Connectors.SqlServer
{
    /// <summary>
    /// The SQL Server connector (SQL mode only, no ODBC): the
    /// endpoint is a SqlClient connection string WITHOUT credentials; the Basic scheme's <c>username</c>/<c>password</c>
    /// are applied from the per-site secret store. A test opens and runs <c>SELECT 1</c>; the <c>query</c> operation
    /// streams rows, its <c>@name</c> values bound as parameters (<see cref="SqlValues"/>).
    /// </summary>
    /// <remarks>
    /// <b>The driver dials only the address the host pinned.</b> The connection string's data source is parsed (a
    /// <c>tcp:</c> prefix, a <c>host,port</c> pair; a named instance WITHOUT a port is refused, because it would be
    /// resolved by the SQL Browser on a UDP port the host never checked), the host goes to
    /// <see cref="IConnectorEgress.ResolveAndPinAsync"/>, and the permitted address is written back as
    /// <c>address,port</c> with <c>HostNameInCertificate</c> set to the original name so an encrypted connection still
    /// validates the server's certificate. <c>trust_server_certificate</c> stays a per-connection setting, default off:
    /// the driver's TLS policy is not the egress's (the egress decides WHERE bytes go), and a dev server's self-signed
    /// certificate is a fact of the target, not a way around the policy.
    /// </remarks>
    public sealed class SqlServerConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "sqlserver";

        public string DisplayName => "Microsoft SQL Server";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri? WebsiteUrl => new("https://www.microsoft.com/sql-server");

        public Uri? ApiDocumentationUrl => new("https://learn.microsoft.com/sql/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("application_name", "Application name", ConnectorConfigFieldType.Text, Required: false, Description: "Optional application name reported to the server."),
            new ConnectorConfigField("trust_server_certificate", "Trust server certificate", ConnectorConfigFieldType.Boolean, Required: false,
                Description: "Accept the server's TLS certificate without validation. SqlClient encrypts by default, so a dev/container server's self-signed certificate fails unless this is enabled.")
            {
                Default = "false",
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("query", "SQL query", "Runs a SQL statement and reads each result row into a record; values given as @name parameters are bound, never spliced into the SQL.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("query", "SQL query", ConnectorConfigFieldType.Multiline, Required: true, Description: "The statement to execute - a SELECT, or a write that returns rows (RETURNING / OUTPUT). Write each value as @name and give it as an '@name = value' parameter."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            SqlConnectionStringBuilder builder;
            try
            {
                builder = BuildConnectionString(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
            {
                return ConnectionTestResult.Fail($"Invalid SQL Server connection string: {ex.Message}");
            }

            await PinAsync(builder, context.Egress, cancellationToken);

            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return ConnectionTestResult.Ok("Connected to SQL Server.");
            }
            catch (Exception ex) when (ex is SqlException or TimeoutException or InvalidOperationException or SocketException)
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

            var builder = BuildConnectionString(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            await PinAsync(builder, context.Egress, cancellationToken);

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(query, connection);
            foreach (var (name, value) in values)
            {
                // nvarchar(max): SQL Server converts it to the type the column or expression it fills needs (an int, a date).
                command.Parameters.Add(new SqlParameter("@" + name, System.Data.SqlDbType.NVarChar, -1) { Value = value });
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

        /// <summary>The scheme's named credentials, the optional settings, a clamped timeout - the password is never read from a setting.</summary>
        internal static SqlConnectionStringBuilder BuildConnectionString(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout)
        {
            var builder = new SqlConnectionStringBuilder(endpoint);
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

            if (settings.TryGetValue("trust_server_certificate", out var trust) && bool.TryParse(trust, out var trustServerCertificate))
            {
                builder.TrustServerCertificate = trustServerCertificate;
            }

            builder.ConnectTimeout = Math.Clamp((int)timeout.TotalSeconds, 1, 1024);
            return builder;
        }

        /// <summary>The data source's host and port: a <c>tcp:</c> prefix dropped, <c>host,port</c> split, a named instance without a port refused (the SQL Browser would resolve it on a port the host never checked).</summary>
        internal static (string Host, int Port) ParseDataSource(string? dataSource)
        {
            var value = (dataSource ?? string.Empty).Trim();
            if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            {
                value = value[4..].Trim();
            }

            var port = 1433;
            var comma = value.LastIndexOf(',');
            if (comma >= 0)
            {
                if (!int.TryParse(value[(comma + 1)..].Trim(), out port) || port is < 1 or > 65535)
                {
                    throw new ConnectorEgressDeniedException($"The data source '{dataSource}' names a port that is not a TCP port.");
                }

                value = value[..comma].Trim();
            }
            else if (value.Contains('\\', StringComparison.Ordinal))
            {
                throw new ConnectorEgressDeniedException($"The data source '{dataSource}' names an instance without a port; the SQL Browser would resolve it on a port the host cannot check. Name the port (host,port).");
            }

            if (value.Contains('\\', StringComparison.Ordinal))
            {
                value = value[..value.IndexOf('\\')];
            }

            if (value.Length == 0 || value is "." or "(local)")
            {
                value = "localhost";
            }

            return (value.Trim('[', ']'), port);
        }

        private static async Task PinAsync(SqlConnectionStringBuilder builder, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            var (host, port) = ParseDataSource(builder.DataSource);
            var pinned = await egress.ResolveAndPinAsync(host, port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The database host '{host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            builder.DataSource = (pinned.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{pinned}]" : pinned.ToString()) + "," + port;
            if (!IPAddress.TryParse(host, out _))
            {
                builder.HostNameInCertificate = host; // the certificate names the host, the socket dials the pinned address
            }
        }
    }
}
