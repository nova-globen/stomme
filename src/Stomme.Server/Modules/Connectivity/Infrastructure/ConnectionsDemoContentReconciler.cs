#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Connectivity.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Stomme.Modules.Connectivity.Domain;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Connectivity.Infrastructure
{
    /// <summary>
    /// The Connectivity module's demo content (D110 Q7): two connections an evaluator can open, test
    /// and edit - a REST endpoint and a PostgreSQL database - written per site on the boot/upgrade path through the
    /// framework's <see cref="SiteContentReconciliationRunner"/>, ONLY while <c>DemoContent:Enabled</c> is true (default
    /// off; while it is off this type is not registered), and ADDITIVELY: a site stamped at <see cref="Version"/> is left
    /// alone whatever an operator did to the rows, an unstamped site receives the connections it lacks by name.
    /// </summary>
    /// <remarks>
    /// The connections are written through the repository, not the create command: the command validates the connector
    /// TYPE against the catalog, and the in-tree catalog is empty until BL-96 lands the per-connector code - the demo rows
    /// therefore show <c>Untested</c> with the connector marked "not installed" on the page, and a Test answers
    /// <see cref="ConnectorOutcomes.ConnectorUnavailable"/> until a connector of that type is installed. That is the
    /// honest state, not a stub. No credential is written: the PostgreSQL connection declares a <c>password</c> field an
    /// operator fills in.
    /// </remarks>
    public sealed class ConnectionsDemoContentReconciler(IConnectionRepository connections, IUnitOfWork unitOfWork, DemoContentStamp stamp, TimeProvider clock, ILogger<ConnectionsDemoContentReconciler> logger) : ISiteContentReconciler
    {
        public const string Module = "Connectivity";

        /// <summary>Bump when the demo set changes; a site stamped at an older version receives the additions.</summary>
        public const string Version = "1";

        public string Name => "Demo connections";

        public static readonly IReadOnlyList<DemoConnection> Demo = new[]
        {
            new DemoConnection("Demo REST API", "rest", ConnectorEndpointKind.Url, "https://api.example.com/v1", new Dictionary<string, string> { ["method"] = "GET", ["path"] = "/status" }, Array.Empty<string>(), 30),
            new DemoConnection("Demo PostgreSQL", "postgresql", ConnectorEndpointKind.ConnectionString, "Host=localhost;Port=5432;Database=demo;Username=demo", new Dictionary<string, string>(), new[] { "password" }, 15),
        };

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var siteId = context.Site.SiteId;
            if (await stamp.IsAppliedAsync(context.TenantId, siteId, Module, Version, ct))
            {
                return;
            }

            var created = new List<string>();
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var demo in Demo)
            {
                if (await connections.FindByNameAsync(siteId, demo.Name, ct) is not null)
                {
                    continue;
                }

                var connection = ConnectionAggregate.Create(Guid.NewGuid(), context.TenantId, siteId, demo.Name, demo.ConnectorType, demo.EndpointKind, demo.Endpoint, demo.Settings, demo.CredentialFields, demo.TimeoutSeconds, now);
                await connections.AddAsync(connection, ct);
                created.Add(demo.Name);
            }

            if (created.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            await stamp.MarkAppliedAsync(context.TenantId, siteId, Module, Version, ct);
            logger.LogInformation("Demo connections on site {SiteId}: created {Created}.", siteId, created.Count == 0 ? "none (all present)" : string.Join(", ", created));
        }
    }

    public sealed record DemoConnection(string Name, string ConnectorType, ConnectorEndpointKind EndpointKind, string Endpoint, IReadOnlyDictionary<string, string> Settings, IReadOnlyList<string> CredentialFields, int TimeoutSeconds);
}
