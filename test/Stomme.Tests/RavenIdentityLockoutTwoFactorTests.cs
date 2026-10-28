using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Identity;
using Stomme.Repository.Stores;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-6 — the LOCKOUT + TWO-FACTOR facets of the hand-written Raven <c>IUserStore</c> (D54b), driven through
    /// <c>UserManager&lt;IdentityUser&gt;</c> against a LIVE RavenDB, exactly as the login page's
    /// <c>CheckPasswordSignInAsync(..., lockoutOnFailure: true)</c> path exercises them.
    /// <para>
    /// EF's <c>UserStoreBase</c> implements <c>IUserLockoutStore</c>/<c>IUserTwoFactorStore</c> as IN-MEMORY
    /// mutators persisted by <c>UpdateAsync</c> (LockoutEnd/AccessFailedCount/LockoutEnabled/TwoFactorEnabled are
    /// plain columns on AspNetUsers); until BL-6 the Raven store omitted both facets, so
    /// <c>SupportsUserLockout</c>/<c>SupportsUserTwoFactor</c> were false and every lockout/2FA
    /// <c>IdentityOptions</c> knob was SILENTLY INERT on a RavenDB install — five wrong passwords and the sixth
    /// correct one still logged in. These tests assert the EF semantics hold identically on Raven, including the
    /// physical round-trip (raw document assertions, not just what the manager reports back).
    /// </para>
    /// <para>
    /// GATED — FAILS rather than skips when RavenDB is unreachable (the D46 principle): a run that cannot reach
    /// the document store proves nothing about parity. Point <c>RAVENDB_TEST_URL</c> at a reachable server.
    /// </para>
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenIdentityLockoutTwoFactorTests : IClassFixture<RavenIdentityLockoutTwoFactorTests.RavenLockoutFixture>, IDisposable
    {
        private const string Password = "L0ckout!Pass$2026";

        private readonly RavenLockoutFixture _fixture;
        private readonly ITestOutputHelper _output;
        private readonly RavenIdentityUserStore _store;

        public RavenIdentityLockoutTwoFactorTests(RavenLockoutFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
            _store = new RavenIdentityUserStore(_fixture.Store, new RavenFrameworkStoreOptions());
        }

        public void Dispose() => _store.Dispose();

        [Fact]
        public void Raven_store_advertises_lockout_and_two_factor_support()
        {
            using var manager = CreateUserManager();

            // UserManager guards every lockout/2FA call with these flags; false means the whole feature is
            // silently skipped, which is exactly the BL-6 defect.
            Assert.True(manager.SupportsUserLockout,
                "SupportsUserLockout is false — the Raven IUserStore does not implement IUserLockoutStore, so " +
                "MaxFailedAccessAttempts/DefaultLockoutTimeSpan are inert on a RavenDB install.");
            Assert.True(manager.SupportsUserTwoFactor,
                "SupportsUserTwoFactor is false — the Raven IUserStore does not implement IUserTwoFactorStore, so " +
                "the two-factor flag is inert on a RavenDB install.");
        }

        [Fact]
        public async Task Failed_access_attempts_lock_the_user_out_and_the_lockout_end_round_trips()
        {
            using var manager = CreateUserManager(options => options.Lockout.MaxFailedAccessAttempts = 3);
            var user = await CreateUserAsync(manager);

            // AllowedForNewUsers = true (set in CreateUserManager) => CreateAsync stamped LockoutEnabled.
            Assert.True(await manager.GetLockoutEnabledAsync(user));

            // Two failures: below the threshold — counted, not locked.
            await Succeeded(manager.AccessFailedAsync(user));
            await Succeeded(manager.AccessFailedAsync(user));
            Assert.False(await manager.IsLockedOutAsync(user));

            // The third failure crosses MaxFailedAccessAttempts: EF semantics are LockoutEnd = now + span and the
            // counter RESET to zero (UserManager drives both through the store; the store persists via UpdateAsync).
            await Succeeded(manager.AccessFailedAsync(user));

            var reloaded = await manager.FindByIdAsync(user.Id);
            Assert.NotNull(reloaded);
            Assert.True(await manager.IsLockedOutAsync(reloaded!),
                "Three failed attempts (MaxFailedAccessAttempts = 3) did not lock the user out on Raven.");
            var lockoutEnd = await manager.GetLockoutEndDateAsync(reloaded);
            Assert.NotNull(lockoutEnd);
            Assert.True(lockoutEnd > DateTimeOffset.UtcNow, $"LockoutEnd {lockoutEnd:o} is not in the future.");
            Assert.Equal(0, await manager.GetAccessFailedCountAsync(reloaded)); // reset on lockout, as EF does

            // The PHYSICAL round-trip: the raw Raven document carries the lockout scalars.
            var document = await LoadDocumentAsync(user.Id);
            _output.WriteLine($"[raven doc] LockoutEnd={document.LockoutEnd:o} AccessFailedCount={document.AccessFailedCount} LockoutEnabled={document.LockoutEnabled}");
            Assert.True(document.LockoutEnabled);
            Assert.NotNull(document.LockoutEnd);
            Assert.Equal(0, document.AccessFailedCount);
        }

        [Fact]
        public async Task Access_failed_count_round_trips_and_resets()
        {
            using var manager = CreateUserManager(options => options.Lockout.MaxFailedAccessAttempts = 5);
            var user = await CreateUserAsync(manager);

            await Succeeded(manager.AccessFailedAsync(user));
            await Succeeded(manager.AccessFailedAsync(user));

            var reloaded = await manager.FindByIdAsync(user.Id);
            Assert.Equal(2, await manager.GetAccessFailedCountAsync(reloaded!));
            Assert.Equal(2, (await LoadDocumentAsync(user.Id)).AccessFailedCount);
            Assert.False(await manager.IsLockedOutAsync(reloaded!));

            await Succeeded(manager.ResetAccessFailedCountAsync(reloaded!));

            reloaded = await manager.FindByIdAsync(user.Id);
            Assert.Equal(0, await manager.GetAccessFailedCountAsync(reloaded!));
            Assert.Equal(0, (await LoadDocumentAsync(user.Id)).AccessFailedCount);
        }

        [Fact]
        public async Task Lockout_end_set_through_the_manager_persists_and_lockout_enabled_gates_it()
        {
            using var manager = CreateUserManager();
            var user = await CreateUserAsync(manager);

            // An explicit lockout end in the future (the admin "lock this account" shape).
            var until = DateTimeOffset.UtcNow.AddHours(1);
            await Succeeded(manager.SetLockoutEndDateAsync(user, until));

            var reloaded = await manager.FindByIdAsync(user.Id);
            Assert.True(await manager.IsLockedOutAsync(reloaded!));
            var persisted = await manager.GetLockoutEndDateAsync(reloaded!);
            Assert.NotNull(persisted);
            // Same instant (allow for serialization precision), not merely "some future time".
            Assert.True((persisted!.Value - until).Duration() < TimeSpan.FromSeconds(1),
                $"LockoutEnd round-tripped as {persisted:o}, expected {until:o}.");

            // EF semantics: IsLockedOutAsync consults GetLockoutEnabledAsync FIRST — disabling lockout unlocks
            // regardless of a future LockoutEnd.
            await Succeeded(manager.SetLockoutEnabledAsync(reloaded!, false));
            reloaded = await manager.FindByIdAsync(user.Id);
            Assert.False(await manager.IsLockedOutAsync(reloaded!));
            Assert.False((await LoadDocumentAsync(user.Id)).LockoutEnabled);

            // A lockout end in the past does not lock either.
            await Succeeded(manager.SetLockoutEnabledAsync(reloaded!, true));
            await Succeeded(manager.SetLockoutEndDateAsync(reloaded!, DateTimeOffset.UtcNow.AddMinutes(-5)));
            reloaded = await manager.FindByIdAsync(user.Id);
            Assert.False(await manager.IsLockedOutAsync(reloaded!));
        }

        [Fact]
        public async Task Two_factor_flag_round_trips_through_raven()
        {
            using var manager = CreateUserManager();
            var user = await CreateUserAsync(manager);

            Assert.False(await manager.GetTwoFactorEnabledAsync(user));

            await Succeeded(manager.SetTwoFactorEnabledAsync(user, true));
            var reloaded = await manager.FindByIdAsync(user.Id);
            Assert.True(await manager.GetTwoFactorEnabledAsync(reloaded!));
            Assert.True((await LoadDocumentAsync(user.Id)).TwoFactorEnabled);

            await Succeeded(manager.SetTwoFactorEnabledAsync(reloaded!, false));
            reloaded = await manager.FindByIdAsync(user.Id);
            Assert.False(await manager.GetTwoFactorEnabledAsync(reloaded!));
            Assert.False((await LoadDocumentAsync(user.Id)).TwoFactorEnabled);
        }

        // ---- plumbing ------------------------------------------------------------------------------------

        /// <summary>
        /// A UserManager over the RAW Raven store, composed the way ASP.NET Identity composes it (same hasher,
        /// validators and normalizer the host registers) so the semantics driven here are the login page's.
        /// </summary>
        private UserManager<IdentityUser> CreateUserManager(Action<IdentityOptions>? configure = null)
        {
            var options = new IdentityOptions();
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 3;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
            configure?.Invoke(options);

            return new UserManager<IdentityUser>(
                _store,
                Options.Create(options),
                new PasswordHasher<IdentityUser>(),
                new IUserValidator<IdentityUser>[] { new UserValidator<IdentityUser>() },
                new IPasswordValidator<IdentityUser>[] { new PasswordValidator<IdentityUser>() },
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                services: null!,
                logger: NullLogger<UserManager<IdentityUser>>.Instance);
        }

        private static async Task<IdentityUser> CreateUserAsync(UserManager<IdentityUser> manager)
        {
            var user = new IdentityUser("lockout" + Guid.NewGuid().ToString("N").Substring(0, 10))
            {
                Email = "lockout@example.com"
            };
            await Succeeded(manager.CreateAsync(user, Password));
            return user;
        }

        private static async Task Succeeded(Task<IdentityResult> operation)
        {
            var result = await operation;
            Assert.True(result.Succeeded, string.Join("; ", (result.Errors ?? Enumerable.Empty<IdentityError>()).Select(error => error.Description)));
        }

        private async Task<RavenIdentityUser> LoadDocumentAsync(string userId)
        {
            using var session = _fixture.Store.OpenAsyncSession();
            var document = await session.LoadAsync<RavenIdentityUser>("RavenIdentityUsers/" + userId);
            Assert.NotNull(document);
            return document;
        }

        /// <summary>
        /// One database per class run, swept and torn down the RavenGuestbookStoreConformanceTests way (janitor
        /// sweep at init; delete through a FRESH store at teardown so a disposed executor cannot swallow it).
        /// Throws — fails, never skips — when RavenDB is unreachable.
        /// </summary>
        public sealed class RavenLockoutFixture : IAsyncLifetime
        {
            private const string DatabasePrefix = "stomme-identity-lockout-";

            private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

            private readonly string _database = DatabasePrefix + Guid.NewGuid().ToString("N");

            public DocumentStore Store { get; private set; } = default!;

            public async Task InitializeAsync()
            {
                if (!IsReachable())
                {
                    throw new InvalidOperationException(
                        $"RavenDB is not reachable at {Url}, so nothing here proves the lockout/2FA facets work on " +
                        "Raven — this suite FAILS rather than skips (D46). Point RAVENDB_TEST_URL at a reachable server.");
                }

                Store = (DocumentStore)new DocumentStore { Urls = new[] { Url }, Database = _database }.InitializeStommeStore();
                await RavenTestDatabaseJanitor.SweepAsync(Store, DatabasePrefix);
                await Store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
            }

            public async Task DisposeAsync()
            {
                Store?.Dispose();
                try
                {
                    using var cleanup = new DocumentStore { Urls = new[] { Url } }.Initialize();
                    await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
                }
                catch
                {
                    // best-effort: the janitor sweep catches a leftover on the next run
                }
            }

            private static bool IsReachable()
            {
                try
                {
                    var endpoint = new Uri(Url);
                    using var tcp = new TcpClient();
                    return tcp.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
