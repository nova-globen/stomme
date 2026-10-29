using Stomme.Persistence.RavenDb;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// The SAME conformance suite against the RavenDB document store, live against a real server
/// (RAVENDB_TEST_URL, default http://127.0.0.1:7000), each run in its own database. Composed the way a RavenDB
/// install composes the flag store: the provider-neutral <c>AddFeatureManagementCore</c> plus
/// <c>AddRavenPersistence</c>, which points the module's open-generic <c>IRepository&lt;,&gt;</c> at
/// <c>RavenRepository&lt;,&gt;</c> — so the flags live in the document store rather than a side-car SQLite file.
/// <para>
/// Like the D46 parity suite this FAILS rather than skips when RavenDB is unreachable: an EF-only run proves
/// nothing about the both-providers invariant this exists to hold.
/// </para>
/// </summary>
[Xunit.Trait("Category", "ExternalDatabase")]
public sealed class RavenFeatureFlagStoreConformanceTests : FeatureFlagStoreConformanceTests
{
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

    private readonly string _database = "stomme-features-conformance-" + Guid.NewGuid().ToString("N");
    private DocumentStore _store = default!;

    protected override async Task InitializeStoreAsync()
    {
        _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
        await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
    }

    protected override void AddStore(IServiceCollection services)
    {
        services.AddFeatureManagementCore();
        services.AddRavenPersistence(_store);
    }

    protected override async Task DisposeStoreAsync()
    {
        try
        {
            await _store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
        }
        catch
        {
            // best-effort cleanup
        }

        _store.Dispose();
    }
}
