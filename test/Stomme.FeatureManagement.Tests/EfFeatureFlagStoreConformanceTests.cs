using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// The conformance suite against the RELATIONAL (EF Core) store — a throwaway SQLite file, exactly as the host
/// composes the flag store on a relational install (<c>AddFeatureManagement</c>). This is the baseline the
/// RavenDB run must match member for member.
/// </summary>
public sealed class EfFeatureFlagStoreConformanceTests : FeatureFlagStoreConformanceTests
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"stomme-features-conformance-{Guid.NewGuid():N}.db");

    protected override void AddStore(IServiceCollection services) =>
        services.AddFeatureManagement(options => options.UseSqlite($"Data Source={_databasePath}"));

    protected override Task DisposeStoreAsync()
    {
        // The pooled SQLite connection must let go of the file before it can be removed — THIS database's pool,
        // by the exact string AddStore used. ClearAllPools is process-wide and drops the pooled connections of
        // whatever else happens to be running, mid-request.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a green test run over.
        }

        return Task.CompletedTask;
    }
}
