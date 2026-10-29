#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using Npgsql;

namespace Stomme.Modules.Connectors.PostgreSql
{
    /// <summary>
    /// The PostgreSQL connector (lifted from the predecessor platform's `postgresql`): the endpoint is an Npgsql connection
    /// string WITHOUT credentials (a connection string is never a place for a credential); the Basic scheme's
    /// <c>username</c>/<c>password</c> are applied from the per-site secret store. A test opens a connection and runs
    /// <c>SELECT 1</c> within the timeout; the <c>query</c> operation runs a statement and streams each row into a record, its
    /// <c>@name</c> values bound as parameters (<see cref="SqlValues"/>).
    /// </summary>
    /// <remarks>
    /// <b>The driver dials only an address the host has checked.</b> Before Npgsql opens anything the connection string's host
    /// is handed to <see cref="IConnectorEgress.ResolveAndPinAsync"/>; the one permitted address comes back and is written
    /// into the connection string as the host, so the driver dials THAT (a driver that re-resolves on its own reopens the
    /// window between the check and its connect - stated, not solved). A host every address of which the policy
    /// refuses - a private database not on the site's allowlist, loopback - is a refusal, thrown as the contract's own
    /// <see cref="ConnectorEgressDeniedException"/> (which the host's guard recognises beside its own) so the runner
    /// categorises it <c>DeniedByPolicy</c> and the page says why without a socket ever opening. A multi-host connection
    /// string is refused: one host, one pin. The connector references the contract only - the shape an external one has.
    /// </remarks>
    public sealed class PostgreSqlConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "postgresql";

        public string DisplayName => "PostgreSQL";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri? WebsiteUrl => new("https://www.postgresql.org");

        public Uri? ApiDocumentationUrl => new("https://www.postgresql.org/docs/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("application_name", "Application name", ConnectorConfigFieldType.Text, Required: false,
                Description: "Optional Npgsql application_name reported to the server."),
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
            NpgsqlConnectionStringBuilder builder;
            try
            {
                builder = Build(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException) // Npgsql answers an unknown keyword with KeyNotFoundException
            {
                return ConnectionTestResult.Fail($"Invalid PostgreSQL connection string: {ex.Message}");
            }

            await PinAsync(builder, context.Egress, cancellationToken);

            try
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return ConnectionTestResult.Ok("Connected to PostgreSQL.");
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException)
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

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(query, connection);
            foreach (var (name, value) in values)
            {
                // Sent as text of no stated type, so the server reads it as the column or expression it fills needs (an integer, a date).
                command.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Unknown) { Value = value });
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

        /// <summary>The scheme's named credentials, the optional application name, a clamped timeout - the endpoint itself carries no credential.</summary>
        private static NpgsqlConnectionStringBuilder Build(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout)
        {
            var builder = new NpgsqlConnectionStringBuilder(endpoint);
            if (credentials.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password))
            {
                builder.Password = password;
            }

            if (credentials.TryGetValue("username", out var username) && !string.IsNullOrWhiteSpace(username))
            {
                builder.Username = username;
            }

            if (settings.TryGetValue("application_name", out var applicationName) && !string.IsNullOrWhiteSpace(applicationName))
            {
                builder.ApplicationName = applicationName;
            }

            builder.Timeout = Math.Clamp((int)timeout.TotalSeconds, 1, 1024);
            return builder;
        }

        /// <summary>Resolves, checks and pins the one host the driver may dial; a refused or unresolvable host is the guard's own refusal.</summary>
        private static async Task PinAsync(NpgsqlConnectionStringBuilder builder, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            var host = builder.Host ?? string.Empty;
            if (host.Contains(',', StringComparison.Ordinal))
            {
                throw new ConnectorEgressDeniedException("A PostgreSQL connection names ONE host; a multi-host connection string cannot be pinned to a checked address.");
            }

            var port = builder.Port > 0 ? builder.Port : 5432;
            var pinned = await egress.ResolveAndPinAsync(host, port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The database host '{host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            builder.Host = pinned.ToString();
            builder.Port = port;
        }
    }
}
