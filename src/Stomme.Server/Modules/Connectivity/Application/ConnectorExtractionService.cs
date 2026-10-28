#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Connectivity.Persistence;
using Stomme.Infrastructure.Connectivity;
using Stomme.Shared;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Connectivity.Application
{
    /// <summary>
    /// The extraction port's implementation (BL-94 increment 6, D110): the connection (the request's site's, when a
    /// site is resolved), its connector as an extractor, the operation (the first declared when none is named), the
    /// credentials from the per-site secret store, the HOST's egress, and the records up to the cap - one more read
    /// than the cap tells whether the cap cut the read short. A failure is a category, never an exception; a fault is
    /// logged by type.
    /// </summary>
    public sealed class ConnectorExtractionService(IConnectionRepository connections, IConnectorCatalog catalog, ConnectionSecrets secrets,
        IConnectorEgressFactory egressFactory, SiteState siteState, ILogger<ConnectorExtractionService> logger, IConnectorFileSink? fileSink = null) : IConnectorExtractionService
    {
        public Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters,
            int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default) =>
            ExtractAsync(connectionId, operation, parameters, maxRecords, null, cancellationToken);

        public async Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters,
            int maxRecords, IConnectorFiles? files, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var connection = await connections.GetByIdAsync(connectionId, cancellationToken);
            if (connection is null || (siteState.Alias?.SiteId is { } siteId && connection.SiteId != siteId))
            {
                return ConnectorExtractionResult.Fail(ConnectorOutcome.ConnectionNotFound);
            }

            if (catalog.Find(connection.ConnectorType) is not { } connector)
            {
                return ConnectorExtractionResult.Fail(ConnectorOutcome.ConnectorUnavailable);
            }

            if (connector is not IConnectorExtractor extractor)
            {
                return ConnectorExtractionResult.Fail(ConnectorOutcome.ExtractionUnsupported);
            }

            var operationName = operation;
            if (string.IsNullOrWhiteSpace(operationName))
            {
                operationName = extractor.Operations.Count > 0 ? extractor.Operations[0].Name : string.Empty;
            }
            else if (extractor.Operations.Count > 0 && !extractor.Operations.Any(declared => string.Equals(declared.Name, operationName, StringComparison.OrdinalIgnoreCase)))
            {
                return ConnectorExtractionResult.Fail(ConnectorOutcome.OperationUndefined);
            }

            var resolved = await secrets.ResolveAsync(connection.SiteId, connection, cancellationToken);
            if (resolved.Unreadable.Count > 0)
            {
                return ConnectorExtractionResult.Fail(ConnectorOutcome.CredentialsUnreadable);
            }

            if (resolved.Missing.Count > 0)
            {
                return ConnectorExtractionResult.Fail(ConnectorOutcome.CredentialsMissing);
            }

            var egress = await egressFactory.ForSiteAsync(connection.SiteId, cancellationToken);
            var context = new ConnectorExtractContext(connection.Endpoint.Value, connection.Settings.GetSettings(), TimeSpan.FromSeconds(connection.TimeoutSeconds), egress, operationName!, parameters)
            {
                Credentials = resolved.Values,
                FileSink = fileSink, // the site's managed storage (BL-96 increment 2), null where the host composes none - an operation that needs it says so
                Files = files, // the run's pipeline folder (BL-229), null outside a run - an upload or a download refuses without it
            };

            var cap = ConnectorExtractionLimits.Clamp(maxRecords);
            var records = new List<ConnectorRecord>();
            var truncated = false;
            try
            {
                await using var enumerator = extractor.ExtractAsync(context, cancellationToken).GetAsyncEnumerator(cancellationToken);
                while (await enumerator.MoveNextAsync())
                {
                    if (records.Count == cap)
                    {
                        truncated = true;
                        break;
                    }

                    records.Add(enumerator.Current);
                }
            }
            // A cancellation the caller did not ask for is the connector's own timeout - an HttpClient's elapses as a
            // TaskCanceledException - and is categorised like any failure (TimedOut); only the caller's own passes through.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return ConnectorExtractionResult.Fail(ConnectionTestRunner.Classify(ex, connection, logger));
            }

            return ConnectorExtractionResult.Ok(records, truncated, cap);
        }
    }
}
