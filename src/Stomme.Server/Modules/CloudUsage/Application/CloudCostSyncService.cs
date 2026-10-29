#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.CloudUsage.Persistence;
using Stomme.Connectivity;
using Stomme.Datasets;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.CloudUsage.Providers;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.CloudUsage.Application
{
    /// <summary>What to sync: the source, the connection, the window, and whether and where to materialise a dataset.</summary>
    public sealed record CloudCostSyncInstruction(
        string? Provider,
        Guid? ConnectionId,
        string? Window = null,
        string? DatasetName = null,
        string? DatasetScope = null,
        string? RequestBody = null,
        string? OriginReference = null,
        OriginKind DatasetOrigin = OriginKind.PipelineOutput,
        bool MaterializeDataset = true);

    public sealed record CloudCostSyncOutcome(CloudProvider Provider, DateOnly From, DateOnly To, IReadOnlyList<CloudCostEntry> Entries, string? DatasetName, string Reason);

    public sealed record CloudCostSyncError(string Code, string Reason, string Detail);

    public sealed record CloudCostSyncResult
    {
        private CloudCostSyncResult(CloudCostSyncOutcome? outcome, CloudCostSyncError? error)
        {
            Outcome = outcome;
            Error = error;
        }

        public bool Success => Error is null;

        public CloudCostSyncOutcome? Outcome { get; }

        public CloudCostSyncError? Error { get; }

        public static CloudCostSyncResult Ok(CloudCostSyncOutcome outcome) => new(outcome, null);

        public static CloudCostSyncResult Fail(string code, string reason, string detail) => new(null, new CloudCostSyncError(code, reason, detail));
    }

    public interface ICloudCostSyncService
    {
        Task<CloudCostSyncResult> SyncAsync(CloudCostSyncInstruction instruction, CancellationToken ct = default);
    }

    /// <summary>
    /// The one fetch-normalise-persist-materialise path (the predecessor platform's <c>CloudCostSyncService</c>): the
    /// request's site and tenant, the source's adapter, a daily (yesterday) or monthly (last month) window, the connection
    /// through the site, the host egress for that site, the rows recorded, and - when asked - a dataset registered or
    /// refreshed through D110's registrar (the classifier may escalate it, never lower it). A refusal is a code, a reason
    /// and a detail; nothing throws out of it.
    /// </summary>
    /// <remarks>
    /// <para><b>CHANGED from the predecessor platform.</b> The connection must be the SOURCE's own cost connector type (an <c>azurecost</c>
    /// connection for Azure...), refused by name otherwise - the predecessor platform sent any connection's secret to any provider's endpoint.
    /// The site and the tenant are the request's, refused when none is resolved (the predecessor platform fell back to the default tenant).
    /// A fetch failure's detail is a CATEGORY (the answer's status, a timeout, the egress's refusal), never the exception's
    /// words, which carry the endpoint - D110's sanitised verdict; the log carries the exception type.</para>
    /// </remarks>
    public sealed class CloudCostSyncService(
        ICloudCostProviderRegistry providers,
        ICloudCostConnectionResolver connectionResolver,
        ICloudCostRecordRepository records,
        IUnitOfWork unitOfWork,
        IDatasetRegistrar datasetRegistrar,
        ITenantContext tenantContext,
        SiteState siteState,
        IConnectorEgressFactory egressFactory,
        TimeProvider clock,
        ILogger<CloudCostSyncService> logger) : ICloudCostSyncService
    {
        public const string DailyWindow = "daily";
        public const string MonthlyWindow = "monthly";
        public const string RunScope = "run";
        public const string LedgerScope = "ledger";

        internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        public async Task<CloudCostSyncResult> SyncAsync(CloudCostSyncInstruction instruction, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(instruction);
            if (siteState.Alias?.SiteId is not { } siteId)
            {
                return CloudCostSyncResult.Fail("SITE_REQUIRED", "A cloud cost sync runs for a site; none is resolved.", "Cost rows belong to the site whose connection fetched them.");
            }

            if (tenantContext.CurrentTenantId is not { } tenantId)
            {
                return CloudCostSyncResult.Fail("TENANT_REQUIRED", "A cloud cost sync runs for a tenant; none is resolved.", "Cost rows belong to the tenant of the site.");
            }

            if (string.IsNullOrWhiteSpace(instruction.Provider))
            {
                return CloudCostSyncResult.Fail("PROVIDER_MISSING", "The cloud cost sync names no provider.", "A cloud cost sync must name its cost source (e.g. 'azure').");
            }

            if (!Enum.TryParse<CloudProvider>(instruction.Provider, ignoreCase: true, out var provider) || !Enum.IsDefined(provider) || int.TryParse(instruction.Provider, out _))
            {
                return CloudCostSyncResult.Fail("PROVIDER_UNKNOWN", $"'{instruction.Provider}' is not a known cost source.", $"No cloud provider is named '{instruction.Provider}'.");
            }

            if (!providers.TryResolve(provider, out var costProvider))
            {
                return CloudCostSyncResult.Fail("PROVIDER_UNSUPPORTED", $"No cost adapter is registered for '{provider}'.", $"The '{provider}' cost source has no registered adapter.");
            }

            var window = (instruction.Window ?? DailyWindow).Trim();
            if (!TryGetWindow(window, out var from, out var to))
            {
                return CloudCostSyncResult.Fail("WINDOW_INVALID", $"'{window}' is not a valid sync window.", $"The sync window must be '{DailyWindow}' or '{MonthlyWindow}'.");
            }

            var scope = (instruction.DatasetScope ?? RunScope).Trim();
            if (!scope.Equals(RunScope, StringComparison.OrdinalIgnoreCase) && !scope.Equals(LedgerScope, StringComparison.OrdinalIgnoreCase))
            {
                return CloudCostSyncResult.Fail("DATASET_SCOPE_INVALID", $"'{scope}' is not a valid dataset scope.", $"The dataset scope must be '{RunScope}' or '{LedgerScope}'.");
            }

            if (instruction.ConnectionId is not { } connectionId)
            {
                return CloudCostSyncResult.Fail("CONNECTION_REQUIRED", $"The '{provider}' cost sync has no connection.", "A cloud cost sync must bind a Connection to the user's own billing account.");
            }

            var connection = await connectionResolver.ResolveAsync(connectionId, ct);
            if (connection is null)
            {
                return CloudCostSyncResult.Fail("CONNECTION_NOT_FOUND", $"Connection '{connectionId}' was not found.", $"No connection with id '{connectionId}'.");
            }

            var expectedType = CloudCostConnectorTypes.For(provider);
            if (!string.Equals(connection.ConnectorType, expectedType, StringComparison.OrdinalIgnoreCase))
            {
                return CloudCostSyncResult.Fail("CONNECTION_TYPE_MISMATCH", $"Connection '{connectionId}' is a '{connection.ConnectorType}' connection, not a '{expectedType}' one.", $"The {provider} cost source is fetched through a '{expectedType}' connection.");
            }

            if (string.IsNullOrWhiteSpace(connection.Endpoint))
            {
                return CloudCostSyncResult.Fail("ENDPOINT_MISSING", $"Connection '{connectionId}' carries no cost-API endpoint.", "The Connection must name the provider's cost-API endpoint.");
            }

            if (string.IsNullOrEmpty(connection.Secret))
            {
                return CloudCostSyncResult.Fail("CREDENTIAL_MISSING", $"Connection '{connectionId}' resolved no credential.",
                    $"The connection must hold its '{CloudCostConnectionResolver.CredentialField}' credential; AWS SigV4 and GCP OAuth signing are not supported yet.");
            }

            IReadOnlyList<CloudCostEntry> entries;
            try
            {
                var egress = await egressFactory.ForSiteAsync(siteId, ct);
                entries = await costProvider.FetchCostsAsync(new CloudCostQuery(from, to, BuildSettings(instruction, connection), egress), ct);
            }
            catch (CloudCostPayloadFormatException ex)
            {
                return CloudCostSyncResult.Fail("PAYLOAD_INVALID", $"The {provider} cost payload was not in the provider's documented shape.", ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ConnectorEgressDeniedException or System.IO.IOException)
            {
                logger.LogWarning("Fetching {Provider} costs through connection {ConnectionId} failed: {ExceptionType}.", provider, connectionId, ex.GetType().Name);
                return CloudCostSyncResult.Fail("FETCH_FAILED", $"Fetching {provider} costs failed.", FetchCategory(ex));
            }

            foreach (var entry in entries)
            {
                await records.AddAsync(CloudCostRecordAggregate.Record(Guid.NewGuid(), tenantId, siteId, entry), ct);
            }

            if (entries.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            string? registeredDataset = null;
            if (instruction.MaterializeDataset)
            {
                var datasetName = string.IsNullOrWhiteSpace(instruction.DatasetName) ? $"{provider} cost" : instruction.DatasetName.Trim();
                var datasetRows = scope.Equals(LedgerScope, StringComparison.OrdinalIgnoreCase) ? await ReadLedgerAsync(siteId, ct) : entries;
                if (datasetRows.Count > 0)
                {
                    await datasetRegistrar.RegisterOrRefreshAsync(
                        new DatasetRegistration(datasetName, instruction.DatasetOrigin, instruction.OriginReference ?? string.Empty,
                            JsonSerializer.Serialize(datasetRows, SerializerOptions), DataClassification.Internal, datasetRows.Count), ct);
                    registeredDataset = datasetName;
                }
            }

            var reason = entries.Count == 0
                ? $"The {provider} cost window {from:yyyy-MM-dd}..{to:yyyy-MM-dd} returned no rows."
                : registeredDataset is null
                    ? $"Synced {entries.Count} {provider} cost row(s)."
                    : $"Synced {entries.Count} {provider} cost row(s) into '{registeredDataset}'.";
            return CloudCostSyncResult.Ok(new CloudCostSyncOutcome(provider, from, to, entries, registeredDataset, reason));
        }

        // A category, never the exception's words (they carry the endpoint): D110's sanitised verdict.
        private static string FetchCategory(Exception ex) => ex switch
        {
            _ when ex is ConnectorEgressDeniedException || ex.InnerException is ConnectorEgressDeniedException => "The host's egress refused the destination.",
            HttpRequestException { StatusCode: { } status } => $"The cost API answered {(int)status} ({status}).",
            TaskCanceledException => "The cost API did not answer in time.",
            InvalidOperationException => "The connection's settings do not describe a request that can be made.",
            _ => "The cost API could not be reached.",
        };

        // The transport settings the payload source reads: the connection's own settings plus its endpoint and credential,
        // and the optional POST body authored on the sync. They live for this call and are never logged or shown.
        private static Dictionary<string, string> BuildSettings(CloudCostSyncInstruction instruction, ResolvedConnection connection)
        {
            var settings = new Dictionary<string, string>(connection.Settings, StringComparer.OrdinalIgnoreCase)
            {
                [ConnectorCostPayloadSource.EndpointSettingKey] = connection.Endpoint,
                [ConnectorCostPayloadSource.CredentialSettingKey] = connection.Secret!,
            };
            if (!string.IsNullOrWhiteSpace(instruction.RequestBody))
            {
                settings[ConnectorCostPayloadSource.RequestBodySettingKey] = instruction.RequestBody;
            }

            return settings;
        }

        private bool TryGetWindow(string window, out DateOnly from, out DateOnly to)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (window.Equals(DailyWindow, StringComparison.OrdinalIgnoreCase))
            {
                from = to = today.AddDays(-1);
                return true;
            }

            if (window.Equals(MonthlyWindow, StringComparison.OrdinalIgnoreCase))
            {
                to = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
                from = new DateOnly(to.Year, to.Month, 1);
                return true;
            }

            from = to = default;
            return false;
        }

        // The ledger view: every row the SITE holds, in a deterministic order - what a chain of per-provider syncs
        // materialises into one dataset.
        private async Task<IReadOnlyList<CloudCostEntry>> ReadLedgerAsync(int siteId, CancellationToken ct) =>
            (await records.ListForSiteAsync(siteId, ct: ct))
                .Select(r => new CloudCostEntry(DateOnly.FromDateTime(r.DateUtc), r.Provider, r.ServiceCategory, r.Service, r.Resource, r.Cost, r.Currency, r.Department, r.Project, r.Environment))
                .ToList();
    }
}
