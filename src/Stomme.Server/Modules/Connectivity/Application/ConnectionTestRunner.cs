#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectivity.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Connectivity.Application
{
    /// <summary>
    /// Runs a connector's test for a connection - the one path the Test command and, in
    /// increment 11, the health probe share: the connector from the catalog, the credentials from the per-site secret
    /// store, the HOST's egress for the connection's site, the connection's timeout, and a CATEGORY back. The
    /// connector's message is discarded (it may echo the endpoint, a user name, a password); an exception is
    /// classified and logged by TYPE only.
    /// </summary>
    public sealed class ConnectionTestRunner(IConnectorCatalog catalog, ConnectionSecrets secrets, IConnectorEgressFactory egressFactory, ILogger<ConnectionTestRunner> logger)
    {
        public async Task<ConnectorOutcome> TestAsync(ConnectionAggregate connection, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            if (catalog.Find(connection.ConnectorType) is not { } connector)
            {
                return ConnectorOutcome.ConnectorUnavailable;
            }

            var resolved = await secrets.ResolveAsync(connection.SiteId, connection, ct);
            if (resolved.Unreadable.Count > 0)
            {
                return ConnectorOutcome.CredentialsUnreadable;
            }

            if (resolved.Missing.Count > 0)
            {
                return ConnectorOutcome.CredentialsMissing;
            }

            var timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds);
            var egress = await egressFactory.ForSiteAsync(connection.SiteId, ct);
            var context = new ConnectorTestContext(connection.Endpoint.Value, connection.Settings.GetSettings(), timeout, egress) { Credentials = resolved.Values };

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            try
            {
                var result = await connector.TestConnectionAsync(context, bounded.Token);
                return result.Success ? ConnectorOutcome.Succeeded : ConnectorOutcome.Failed; // result.Message is discarded on purpose
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ConnectorOutcome.TimedOut;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Classify(ex, connection, logger);
            }
        }

        /// <summary>The category for an exception a connector let out; the log carries the exception TYPE and the connection, never the message.</summary>
        public static ConnectorOutcome Classify(Exception exception, ConnectionAggregate connection, ILogger logger)
        {
            if (HostConnectorEgress.IsDenial(exception))
            {
                return ConnectorOutcome.DeniedByPolicy;
            }

            if (exception is ConnectorHostKeyUntrustedException)
            {
                return ConnectorOutcome.HostKeyUntrusted;
            }

            if (exception is TimeoutException || (exception is TaskCanceledException) || exception.InnerException is TimeoutException)
            {
                return ConnectorOutcome.TimedOut;
            }

            logger.LogWarning("Connection {ConnectionId} ({ConnectorType}) faulted with {ExceptionType}.", connection.Id, connection.ConnectorType, exception.GetType().FullName);
            return ConnectorOutcome.Faulted;
        }
    }
}
