#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Mcp.Persistence;
using Stomme.Models;
using Stomme.Modules.Mcp.Application;
using Stomme.Modules.Mcp.Domain;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Stomme.Tests.Fixtures.McpTestModule;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The catalog composed WITHOUT an <see cref="IAccessGuard"/> - and without a caller - lists nothing and runs nothing
    /// ("the guard is an OPTIONAL constructor dependency and the catalog DENIES everything when absent").
    /// </summary>
    /// <remarks>
    /// A driven host always composes a guard, so the drive cannot see this branch: the mutant that made a missing guard
    /// ALLOW survived <c>McpCatalogDriveTests</c>. This is the case that kills it.
    /// </remarks>
    public class McpCatalogFailsClosedTests
    {
        [Fact]
        public async Task Without_a_guard_the_catalog_lists_nothing_runs_nothing_and_still_writes_the_refusal()
        {
            var audit = new RecordingAudit();
            var catalog = Catalog(audit, guard: null, user: new FakeUser(7), siteId: 3);

            Assert.Empty(await catalog.GetToolsAsync());
            Assert.Empty(await catalog.GetResourcesAsync());

            var ran = McpTestContributor.Invocations;
            Assert.Null(await catalog.InvokeToolAsync(McpTestContributor.Echo, "{}"));
            Assert.Null(await catalog.ReadResourceAsync(McpTestContributor.Greeting));
            Assert.Equal(ran, McpTestContributor.Invocations);

            Assert.Equal(new[] { McpAuditOutcome.Denied, McpAuditOutcome.Denied }, audit.Entries.Select(entry => entry.Outcome));
        }

        [Fact]
        public async Task With_a_guard_that_allows_everything_the_same_catalog_lists_and_runs()
        {
            // The control: the same composition with a guard is open, so the case above is about the guard's ABSENCE.
            var catalog = Catalog(new RecordingAudit(), guard: new AllowAll(), user: new FakeUser(7), siteId: 3);

            Assert.Contains(await catalog.GetToolsAsync(), tool => tool.Name == McpTestContributor.Echo);
            Assert.NotNull(await catalog.InvokeToolAsync(McpTestContributor.Echo, "{}"));
        }

        [Fact]
        public async Task Without_an_authenticated_caller_or_a_site_nothing_is_listed_nothing_runs_and_nothing_is_written()
        {
            foreach (var (user, siteId) in new (ICurrentUser, int)[] { (new FakeUser(null), 3), (new FakeUser(7), -1) })
            {
                var audit = new RecordingAudit();
                var catalog = Catalog(audit, guard: new AllowAll(), user: user, siteId: siteId);

                Assert.Empty(await catalog.GetToolsAsync());
                Assert.Null(await catalog.InvokeToolAsync(McpTestContributor.Echo, "{}"));
                Assert.Null(await catalog.ReadResourceAsync(McpTestContributor.Greeting));

                // Nobody the ledger could NAME - so no row, rather than a row that lies about who.
                Assert.Empty(audit.Entries);
            }
        }

        private static McpServerCatalog Catalog(RecordingAudit audit, IAccessGuard? guard, ICurrentUser user, int siteId) =>
            new(
                new McpContributorRegistry(new IMcpContributor[] { new McpTestContributor() }),
                audit,
                user,
                new FakeTenant(),
                new SiteState { Alias = siteId > 0 ? new Alias { SiteId = siteId, Name = "localhost" } : null },
                new NoSettings(),
                new FixedClock(),
                NullLogger<McpServerCatalog>.Instance,
                guard);

        private sealed class AllowAll : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        private sealed class FakeUser(int? userId) : ICurrentUser
        {
            public int? UserId => userId;

            public string? UserName => userId is null ? null : "user" + userId;

            public bool IsAuthenticated => userId is not null;

            public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public int SiteId => 3;
        }

        private sealed class FakeTenant : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;

            public bool IsSystemScope => false;
        }

        private sealed class FixedClock : IMcpClock
        {
            public DateTime UtcNow => new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        }

        private sealed class RecordingAudit : IMcpAuditRepository
        {
            public List<McpAuditEntryAggregate> Entries { get; } = new();

            public Task AddAsync(McpAuditEntryAggregate aggregate, CancellationToken ct = default)
            {
                Entries.Add(aggregate);
                return Task.CompletedTask;
            }

            public Task<McpAuditEntryAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Entries.FirstOrDefault(entry => entry.Id == id));

            public Task<IReadOnlyList<McpAuditEntryAggregate>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<McpAuditEntryAggregate>>(Entries);

            public Task UpdateAsync(McpAuditEntryAggregate aggregate, CancellationToken ct = default) => throw new NotSupportedException();

            public Task DeleteAsync(McpAuditEntryAggregate aggregate, CancellationToken ct = default) => throw new NotSupportedException();

            public Task<IReadOnlyList<McpAuditEntryAggregate>> FindAsync(System.Linq.Expressions.Expression<Func<McpAuditEntryAggregate, bool>> predicate, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<McpAuditEntryAggregate>>(Entries.AsQueryable().Where(predicate).ToList());

            public Task<IReadOnlyList<McpAuditEntryAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<McpAuditEntryAggregate>>(Entries.Where(entry => entry.SiteId == siteId).ToList());

            public Task<IReadOnlyList<McpAuditEntryAggregate>> ListSinceAsync(int siteId, DateTime fromUtc, CancellationToken ct = default) => ListBySiteAsync(siteId, ct);

            public Task<IReadOnlyList<McpAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default) => ListBySiteAsync(siteId, ct);
        }

        /// <summary>A site with no settings at all - every approval is the descriptor's own.</summary>
        private sealed class NoSettings : ISettingRepository
        {
            public Task<Setting> GetSettingAsync(string entityName, int entityId, string settingName) => Task.FromResult<Setting>(null!);

            public Task<IEnumerable<Setting>> GetSettingsAsync(string entityName) => throw new NotSupportedException();

            public Task<IEnumerable<Setting>> GetSettingsAsync(string entityName, int entityId) => throw new NotSupportedException();

            public Task<IEnumerable<Setting>> GetSettingsAsync(string entityName1, int entityId1, string entityName2, int entityId2) => throw new NotSupportedException();

            public Task<Setting> AddSettingAsync(Setting setting) => throw new NotSupportedException();

            public Task<Setting> UpdateSettingAsync(Setting setting) => throw new NotSupportedException();

            public Task<Setting> GetSettingAsync(string entityName, int settingId) => throw new NotSupportedException();

            public Task DeleteSettingAsync(string entityName, int settingId) => throw new NotSupportedException();

            public Task DeleteSettingsAsync(string entityName, int entityId) => throw new NotSupportedException();

            public Task<IEnumerable<string>> GetEntityNamesAsync() => throw new NotSupportedException();

            public Task<IEnumerable<int>> GetEntityIdsAsync(string entityName) => throw new NotSupportedException();

            public Task<string> GetSettingValueAsync(string entityName, int entityId, string settingName, string defaultValue) => Task.FromResult(defaultValue);
        }
    }
}
