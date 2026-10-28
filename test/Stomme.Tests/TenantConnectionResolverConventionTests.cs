using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Repository;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests
{
    // ONE convention for Tenant.DBConnectionString (BL-29). The column stores a connection-string NAME:
    // DatabaseManager writes "DefaultConnection" (or the tenant's name) at install, and
    // MigrateConnectionStringAsync actively rewrites legacy inline strings into names. DBContextBase reads it
    // that way (_config.GetConnectionString(tenant.DBConnectionString)); the seam's resolver read it as a
    // LITERAL, so the moment anything consumed it on a real install it would hand out the string
    // "DefaultConnection" as if it were a connection string. D37 logged the divergence safe because nothing
    // called the resolver; these tests pin the convention so that stops being load-bearing.
    public class TenantConnectionResolverConventionTests
    {
        private sealed class StubTenantRepository(Tenant tenant) : ITenantRepository
        {
            public Task<Tenant> GetTenantAsync(int tenantId) => Task.FromResult(tenant);

            public Task<IEnumerable<Tenant>> GetTenantsAsync() => throw new NotImplementedException();
            public Task<Tenant> AddTenantAsync(Tenant tenant) => throw new NotImplementedException();
            public Task<Tenant> UpdateTenantAsync(Tenant tenant) => throw new NotImplementedException();
            public Task DeleteTenantAsync(int tenantId) => throw new NotImplementedException();
        }

        private static IConfigurationRoot Config(Dictionary<string, string> settings) =>
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        [Fact]
        public async Task The_stored_connection_string_name_resolves_through_configuration_like_DBContextBase()
        {
            var tenant = new Tenant { TenantId = 1, Name = "Master", DBConnectionString = "DefaultConnection" };
            var config = Config(new Dictionary<string, string>
            {
                ["ConnectionStrings:DefaultConnection"] = "Data Source=stomme.db"
            });

            var resolver = new StommeTenantConnectionResolver(new StubTenantRepository(tenant), config);
            var connection = await resolver.ResolveAsync(FrameworkTenantId.ToTenantId(1));

            Assert.Equal("Data Source=stomme.db", connection.ConnectionString);
        }

        // A name that configuration does not carry must throw — handing back the NAME as if it were a
        // connection string (the literal reading) silently points the caller at a non-existent database,
        // which is exactly what the resolver's own contract forbids.
        [Fact]
        public async Task A_name_missing_from_configuration_throws_instead_of_masquerading_as_a_literal()
        {
            var tenant = new Tenant { TenantId = 2, Name = "Tenant2", DBConnectionString = "Tenant2" };

            var resolver = new StommeTenantConnectionResolver(new StubTenantRepository(tenant), Config([]));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => resolver.ResolveAsync(FrameworkTenantId.ToTenantId(2)));

            Assert.Contains("Tenant2", exception.Message);
        }
    }
}
