using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Tenancy.Abstractions;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A job must run each tenant in its OWN scope, and one tenant's failure must not skip the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HostedServiceBase</c> opened ONE DI scope per tick and ran every tenant on it, switching tenants with
    /// <c>TenantManager.SetTenant</c> alone and wrapping the whole loop in a single catch. Scoped services that
    /// resolve the tenant once per scope therefore kept answering for the FIRST tenant, and one throwing tenant
    /// skipped every later one and replaced the notes of the tenants already done.
    /// </para>
    /// <para>
    /// Driven through the real <c>RunTenantsAsync</c> with a probe job, against the real DI container of an
    /// installed host, so the scope really is a Stomme scope and the ambient tenant really is the framework's.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HostedServiceBaseTenantIsolationTests
    {
        private const string Host = EntityPermissionWriteValidationTests.Host;

        private readonly ITestOutputHelper _output;

        public HostedServiceBaseTenantIsolationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Each_tenant_runs_in_its_own_scope_and_one_failure_does_not_skip_the_rest()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();

                // Three tenants: the framework's own, plus two ids that are framework ids but have no store - the
                // job body never touches a store, so what is asserted is the SCOPE each run saw.
                var tenants = new List<Tenant>
                {
                    new Tenant { TenantId = 1, Name = "Tenant One" },
                    new Tenant { TenantId = 2, Name = "Tenant Two" },
                    new Tenant { TenantId = 3, Name = "Tenant Three" }
                };

                // ---- (a) every tenant ran, each in its own scope, with its OWN ambient tenant ----------------
                var probe = new ProbeJob(scopeFactory);
                var run = await probe.RunTenantsAsync(tenants, CancellationToken.None);
                _output.WriteLine($"[scopes] notes: {run.Notes}");
                _output.WriteLine($"[scopes] ambient tenants seen: {string.Join(", ", probe.AmbientTenants)}");
                _output.WriteLine($"[scopes] distinct scopes: {probe.Scopes.Distinct().Count()} of {probe.Scopes.Count}");

                Assert.Equal(3, probe.Scopes.Count);
                Assert.Equal(3, probe.Scopes.Distinct().Count());
                Assert.Equal(3, probe.AmbientTenants.Count);
                Assert.Equal(3, probe.AmbientTenants.Distinct().Count());
                Assert.DoesNotContain(null, probe.AmbientTenants);
                Assert.True(run.Succeeded);

                // ---- (b) a throwing tenant is contained: the later tenants still run --------------------------
                var failing = new ProbeJob(scopeFactory) { ThrowForTenant = "00000000-0000-0000-0000-000000000001" };
                var failedRun = await failing.RunTenantsAsync(tenants, CancellationToken.None);
                _output.WriteLine($"[contained] notes: {failedRun.Notes}");
                Assert.False(failedRun.Succeeded);
                Assert.Contains("Error Processing Tenant One", failedRun.Notes);
                Assert.Contains("ran 00000000-0000-0000-0000-000000000002", failedRun.Notes);
                Assert.Contains("ran 00000000-0000-0000-0000-000000000003", failedRun.Notes);

                // ---- (c) a cancelled host stops between tenants ----------------------------------------------
                using var cancelled = new CancellationTokenSource();
                await cancelled.CancelAsync();
                var stopped = new ProbeJob(scopeFactory);
                var stoppedRun = await stopped.RunTenantsAsync(tenants, cancelled.Token);
                _output.WriteLine($"[cancelled] notes: {stoppedRun.Notes}");
                Assert.Empty(stopped.Scopes);
                Assert.Contains("were not processed", stoppedRun.Notes);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A job whose body records the scope and the ambient tenant it was handed.</summary>
        private sealed class ProbeJob : HostedServiceBase
        {
            public ProbeJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
            {
                Name = "Tenant isolation probe";
            }

            public string ThrowForTenant { get; init; } = "";

            public List<int> Scopes { get; } = new();

            public List<string> AmbientTenants { get; } = new();

            public override Task<string> ExecuteJobAsync(IServiceProvider provider)
            {
                // The ambient tenant is what this suite is about, and it is the only identity available for a
                // tenant with no row in this scratch store: TenantManager resolves names from the store.
                var tenant = provider.GetRequiredService<ITenantContext>();
                var current = tenant.CurrentTenantId?.ToString() ?? "";

                Scopes.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(provider));
                AmbientTenants.Add(current);

                if (!string.IsNullOrEmpty(ThrowForTenant) && current == ThrowForTenant)
                {
                    throw new InvalidOperationException($"probe failure for {current}");
                }

                return Task.FromResult($"ran {current}<br />");
            }
        }
    }
}
