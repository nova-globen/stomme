using Stomme.Persistence.RavenDb;
using Stomme.Tenancy.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Repository.Stores;
using Microsoft.AspNetCore.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Stomme.Repository.Identity
{
    /// <summary>
    /// A hand-written RavenDB ASP.NET Identity store (D54b), the document-store counterpart of EF Core's
    /// <c>UserOnlyStore&lt;IdentityUser, TenantDBContext&gt;</c>. It implements the EIGHT interfaces stock
    /// Identity actually exercises in this host — CRUD + email + password + security stamp + external logins +
    /// passkeys + lockout + two-factor — and persists through the LAZY <see cref="IDocumentStore"/> D54a wired,
    /// NOT the framework seam (<c>IFrameworkStoreSessionFactory</c>): Identity is a separate concern with its
    /// own lifecycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lockout/two-factor parity (BL-6 — closes the STEP-5 gap this remark used to declare).</b> EF's
    /// <c>UserStoreBase</c> implements <c>IUserLockoutStore</c>/<c>IUserTwoFactorStore</c> as IN-MEMORY mutators
    /// persisted by <c>UpdateAsync</c> (LockoutEnd/AccessFailedCount/LockoutEnabled/TwoFactorEnabled are plain
    /// columns on AspNetUsers; <c>UserManager</c> calls the store facet then <c>UpdateUserAsync</c>); this store
    /// now does exactly the same, so the lockout options configured at <c>ConfigureStommeIdentityOptions</c>
    /// (<c>DefaultLockoutTimeSpan</c>, <c>MaxFailedAccessAttempts</c>, <c>AllowedForNewUsers</c>) and the
    /// per-user two-factor flag behave identically on a RavenDB install. Driven, not assumed:
    /// <c>IdentityLockoutHttpDriveTests</c> fails the real <c>POST /pages/login</c> five times on a live Raven
    /// install and asserts the sixth, CORRECT password is rejected while locked out (identical steps green on
    /// the EF control), and <c>RavenIdentityLockoutTwoFactorTests</c> drives the manager-level semantics plus
    /// the raw-document round-trip. Claims, roles and phone-number lookups remain unimplemented — unused in this
    /// host — so <c>UserManager</c>'s <c>Supports*</c> guards report them false and skip those paths. Deliberate
    /// scope edge: the two-factor FLAG persists, but <c>IUserAuthenticatorKeyStore</c>/
    /// <c>IUserTwoFactorRecoveryCodeStore</c> are still absent, so authenticator-app 2FA cannot enrol on Raven
    /// (exactly as before BL-6); the email/phone token providers work off the security stamp, which this store
    /// has always persisted.
    /// </para>
    /// <para>
    /// <b>Provider divergence (D48/D49).</b> <see cref="FindByNameAsync"/>/<see cref="FindByEmailAsync"/> filter
    /// on the NORMALIZED (upper-cased) columns, exactly as EF does, so RavenDB's case-INSENSITIVE <c>==</c> and
    /// EF's case-SENSITIVE <c>==</c> agree on already-upper-cased input — the data is normalized, the query is
    /// never translated with <c>.ToLower()</c> (which throws on Raven). Verified by DRIVING a mixed-case login
    /// against live Raven, not assumed.
    /// </para>
    /// <para>
    /// <b>Staleness.</b> RavenDB indexes asynchronously, so a query issued right after a write can miss it — and
    /// a fresh install creates the host user then immediately authenticates it. Reads by id use
    /// <see cref="IAsyncDocumentSession.LoadAsync{T}(string, CancellationToken)"/> (immediately consistent); the
    /// normalized-lookup queries wait for non-stale results per <see cref="RavenFrameworkStoreOptions"/> (default
    /// true), the same conservative default the framework store takes.
    /// </para>
    /// <para>
    /// <b>Session-per-operation.</b> There is no shared change tracker across store calls, so scalar
    /// <c>Set*</c>/<c>Get*</c> members mutate the in-memory <see cref="IdentityUser"/> only (matching EF, which
    /// defers persistence to <see cref="UpdateAsync"/>), while the mutating login/passkey members load the
    /// document, apply the change and save immediately.
    /// </para>
    /// </remarks>
    public sealed class RavenIdentityUserStore :
        IUserStore<IdentityUser>,
        IUserEmailStore<IdentityUser>,
        IUserPasswordStore<IdentityUser>,
        IUserSecurityStampStore<IdentityUser>,
        IUserLockoutStore<IdentityUser>,
        IUserTwoFactorStore<IdentityUser>,
        IUserLoginStore<IdentityUser>,
        IUserPasskeyStore<IdentityUser>
    {
        private const string IdPrefix = "RavenIdentityUsers/";

        private readonly IDocumentStore _store;
        private readonly RavenFrameworkStoreOptions _options;
        private readonly ITenantContext _tenantContext;
        private readonly IRavenTenantDatabaseResolver _databaseResolver;

        /// <summary>The installation's own database for every user - a store composed without the host's tenancy.</summary>
        public RavenIdentityUserStore(IDocumentStore store, RavenFrameworkStoreOptions options)
            : this(store, options, null, null)
        {
        }

        /// <summary>The users of the current tenant, in its database (BL-176) - where its user rows and role assignments are.</summary>
        public RavenIdentityUserStore(IDocumentStore store, RavenFrameworkStoreOptions options, ITenantContext tenantContext, IRavenTenantDatabaseResolver databaseResolver)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _tenantContext = tenantContext;
            _databaseResolver = databaseResolver;
        }

        // ---- session helpers ---------------------------------------------------------------------------

        private IAsyncDocumentSession OpenSession()
        {
            var database = RavenTenantRouting.DatabaseOf(_databaseResolver, _tenantContext, _options) ?? _store.Database;
            return _store.OpenAsyncSession(new SessionOptions { Database = database });
        }

        private static string DocumentId(string userId) => IdPrefix + userId;

        private IRavenQueryable<RavenIdentityUser> Query(IAsyncDocumentSession session)
        {
            var query = session.Query<RavenIdentityUser>();
            return _options.WaitForNonStaleResults
                ? (IRavenQueryable<RavenIdentityUser>)query.Customize(customize => customize.WaitForNonStaleResults())
                : query;
        }

        // ---- IUserStore --------------------------------------------------------------------------------

        public Task<string> GetUserIdAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.Id);
        }

        public Task<string> GetUserNameAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.UserName);
        }

        public Task SetUserNameAsync(IdentityUser user, string userName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.UserName = userName;
            return Task.CompletedTask;
        }

        public Task<string> GetNormalizedUserNameAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.NormalizedUserName);
        }

        public Task SetNormalizedUserNameAsync(IdentityUser user, string normalizedName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.NormalizedUserName = normalizedName;
            return Task.CompletedTask;
        }

        public async Task<IdentityResult> CreateAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            using var session = OpenSession();
            var document = ToDocument(user);
            await session.StoreAsync(document, DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdentityResult.Success;
        }

        public async Task<IdentityResult> UpdateAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            if (document == null)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "UserNotFound",
                    Description = $"The identity user '{user.Id}' does not exist."
                });
            }

            // Scalar columns come from the in-memory user; the login/passkey collections are owned by their own
            // members and preserved untouched here, exactly as EF's related tables survive an AspNetUsers update.
            ApplyScalars(user, document);
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdentityResult.Success;
        }

        public async Task<IdentityResult> DeleteAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            using var session = OpenSession();
            session.Delete(DocumentId(user.Id));
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdentityResult.Success;
        }

        public async Task<IdentityUser> FindByIdAsync(string userId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(userId);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(userId), cancellationToken).ConfigureAwait(false);
            return ToUser(document);
        }

        public async Task<IdentityUser> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(normalizedUserName);

            using var session = OpenSession();
            var document = await Query(session)
                .FirstOrDefaultAsync(item => item.NormalizedUserName == normalizedUserName, cancellationToken)
                .ConfigureAwait(false);
            return ToUser(document);
        }

        // ---- IUserEmailStore ---------------------------------------------------------------------------

        public Task SetEmailAsync(IdentityUser user, string email, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.Email = email;
            return Task.CompletedTask;
        }

        public Task<string> GetEmailAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.Email);
        }

        public Task<bool> GetEmailConfirmedAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.EmailConfirmed);
        }

        public Task SetEmailConfirmedAsync(IdentityUser user, bool confirmed, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.EmailConfirmed = confirmed;
            return Task.CompletedTask;
        }

        public async Task<IdentityUser> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(normalizedEmail);

            using var session = OpenSession();
            var document = await Query(session)
                .FirstOrDefaultAsync(item => item.NormalizedEmail == normalizedEmail, cancellationToken)
                .ConfigureAwait(false);
            return ToUser(document);
        }

        public Task<string> GetNormalizedEmailAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.NormalizedEmail);
        }

        public Task SetNormalizedEmailAsync(IdentityUser user, string normalizedEmail, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.NormalizedEmail = normalizedEmail;
            return Task.CompletedTask;
        }

        // ---- IUserPasswordStore ------------------------------------------------------------------------

        public Task SetPasswordHashAsync(IdentityUser user, string passwordHash, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.PasswordHash = passwordHash;
            return Task.CompletedTask;
        }

        public Task<string> GetPasswordHashAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.PasswordHash);
        }

        public Task<bool> HasPasswordAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
        }

        // ---- IUserSecurityStampStore -------------------------------------------------------------------

        public Task SetSecurityStampAsync(IdentityUser user, string stamp, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.SecurityStamp = stamp;
            return Task.CompletedTask;
        }

        public Task<string> GetSecurityStampAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.SecurityStamp);
        }

        // ---- IUserLockoutStore (BL-6) ------------------------------------------------------------------
        // Every member below is an IN-MEMORY mutation persisted by UpdateAsync, byte-for-byte the EF
        // UserStoreBase semantics: UserManager.AccessFailedAsync increments through the store, trips
        // SetLockoutEndDateAsync + ResetAccessFailedCountAsync at MaxFailedAccessAttempts, then persists the
        // lot with one UpdateUserAsync. Matching that exactly is what makes lockout on a Raven install behave
        // like the EF install.

        public Task<DateTimeOffset?> GetLockoutEndDateAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.LockoutEnd);
        }

        public Task SetLockoutEndDateAsync(IdentityUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.LockoutEnd = lockoutEnd;
            return Task.CompletedTask;
        }

        public Task<int> IncrementAccessFailedCountAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.AccessFailedCount++;
            return Task.FromResult(user.AccessFailedCount);
        }

        public Task ResetAccessFailedCountAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.AccessFailedCount = 0;
            return Task.CompletedTask;
        }

        public Task<int> GetAccessFailedCountAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.AccessFailedCount);
        }

        public Task<bool> GetLockoutEnabledAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.LockoutEnabled);
        }

        public Task SetLockoutEnabledAsync(IdentityUser user, bool enabled, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.LockoutEnabled = enabled;
            return Task.CompletedTask;
        }

        // ---- IUserTwoFactorStore (BL-6) ----------------------------------------------------------------

        public Task SetTwoFactorEnabledAsync(IdentityUser user, bool enabled, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            user.TwoFactorEnabled = enabled;
            return Task.CompletedTask;
        }

        public Task<bool> GetTwoFactorEnabledAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return Task.FromResult(user.TwoFactorEnabled);
        }

        // ---- IUserLoginStore ---------------------------------------------------------------------------

        public async Task AddLoginAsync(IdentityUser user, UserLoginInfo login, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(login);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            if (document == null)
            {
                return;
            }

            document.Logins ??= new List<RavenStoredLogin>();
            document.Logins.Add(new RavenStoredLogin
            {
                LoginProvider = login.LoginProvider,
                ProviderKey = login.ProviderKey,
                ProviderDisplayName = login.ProviderDisplayName
            });
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task RemoveLoginAsync(IdentityUser user, string loginProvider, string providerKey, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            if (document?.Logins == null)
            {
                return;
            }

            document.Logins.RemoveAll(item => item.LoginProvider == loginProvider && item.ProviderKey == providerKey);
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<IList<UserLoginInfo>> GetLoginsAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            if (document?.Logins == null)
            {
                return new List<UserLoginInfo>();
            }

            return document.Logins
                .Select(item => new UserLoginInfo(item.LoginProvider, item.ProviderKey, item.ProviderDisplayName))
                .ToList();
        }

        public async Task<IdentityUser> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
        {
            using var session = OpenSession();

            // RavenDB rejects a multi-field predicate inside Any() on an auto-index ("A static fanout index
            // should be used instead"), so filter on the SINGLE selective field (ProviderKey) at the store and
            // complete the (provider, key) match in memory. The external-login path (D64) is the first caller to
            // drive this, and a login is uniquely keyed by its provider key, so the candidate set is tiny.
            var candidates = await Query(session)
                .Where(item => item.Logins.Any(login => login.ProviderKey == providerKey))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var document = candidates.FirstOrDefault(item =>
                item.Logins.Any(login => login.LoginProvider == loginProvider && login.ProviderKey == providerKey));
            return ToUser(document);
        }

        // ---- IUserPasskeyStore (.NET 10) ---------------------------------------------------------------

        public async Task AddOrUpdatePasskeyAsync(IdentityUser user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(passkey);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            if (document == null)
            {
                return;
            }

            document.Passkeys ??= new List<RavenStoredPasskey>();
            var credentialId = Convert.ToBase64String(passkey.CredentialId);
            document.Passkeys.RemoveAll(item => item.CredentialId == credentialId);
            document.Passkeys.Add(ToStoredPasskey(passkey));
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            if (document?.Passkeys == null)
            {
                return new List<UserPasskeyInfo>();
            }

            return document.Passkeys.Select(ToPasskeyInfo).ToList();
        }

        public async Task<IdentityUser> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credentialId);

            var key = Convert.ToBase64String(credentialId);
            using var session = OpenSession();
            var document = await Query(session)
                .FirstOrDefaultAsync(item => item.Passkeys.Any(passkey => passkey.CredentialId == key), cancellationToken)
                .ConfigureAwait(false);
            return ToUser(document);
        }

        public async Task<UserPasskeyInfo> FindPasskeyAsync(IdentityUser user, byte[] credentialId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(credentialId);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            if (document?.Passkeys == null)
            {
                return null;
            }

            var key = Convert.ToBase64String(credentialId);
            var stored = document.Passkeys.FirstOrDefault(item => item.CredentialId == key);
            return stored == null ? null : ToPasskeyInfo(stored);
        }

        public async Task RemovePasskeyAsync(IdentityUser user, byte[] credentialId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(credentialId);

            using var session = OpenSession();
            var document = await session.LoadAsync<RavenIdentityUser>(DocumentId(user.Id), cancellationToken).ConfigureAwait(false);
            if (document?.Passkeys == null)
            {
                return;
            }

            var key = Convert.ToBase64String(credentialId);
            document.Passkeys.RemoveAll(item => item.CredentialId == key);
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // ---- mapping -----------------------------------------------------------------------------------

        private static RavenIdentityUser ToDocument(IdentityUser user)
        {
            var document = new RavenIdentityUser { Id = user.Id };
            ApplyScalars(user, document);
            return document;
        }

        private static void ApplyScalars(IdentityUser user, RavenIdentityUser document)
        {
            document.UserName = user.UserName;
            document.NormalizedUserName = user.NormalizedUserName;
            document.Email = user.Email;
            document.NormalizedEmail = user.NormalizedEmail;
            document.EmailConfirmed = user.EmailConfirmed;
            document.PasswordHash = user.PasswordHash;
            document.SecurityStamp = user.SecurityStamp;
            document.ConcurrencyStamp = user.ConcurrencyStamp;
            document.PhoneNumber = user.PhoneNumber;
            document.PhoneNumberConfirmed = user.PhoneNumberConfirmed;
            document.LockoutEnd = user.LockoutEnd;
            document.LockoutEnabled = user.LockoutEnabled;
            document.AccessFailedCount = user.AccessFailedCount;
            document.TwoFactorEnabled = user.TwoFactorEnabled;
        }

        private static IdentityUser ToUser(RavenIdentityUser document)
        {
            if (document == null)
            {
                return null;
            }

            return new IdentityUser
            {
                Id = document.Id,
                UserName = document.UserName,
                NormalizedUserName = document.NormalizedUserName,
                Email = document.Email,
                NormalizedEmail = document.NormalizedEmail,
                EmailConfirmed = document.EmailConfirmed,
                PasswordHash = document.PasswordHash,
                SecurityStamp = document.SecurityStamp,
                ConcurrencyStamp = document.ConcurrencyStamp,
                PhoneNumber = document.PhoneNumber,
                PhoneNumberConfirmed = document.PhoneNumberConfirmed,
                LockoutEnd = document.LockoutEnd,
                LockoutEnabled = document.LockoutEnabled,
                AccessFailedCount = document.AccessFailedCount,
                TwoFactorEnabled = document.TwoFactorEnabled
            };
        }

        private static RavenStoredPasskey ToStoredPasskey(UserPasskeyInfo passkey) => new()
        {
            CredentialId = Convert.ToBase64String(passkey.CredentialId),
            PublicKey = passkey.PublicKey,
            Name = passkey.Name,
            CreatedAt = passkey.CreatedAt,
            SignCount = passkey.SignCount,
            Transports = passkey.Transports,
            IsUserVerified = passkey.IsUserVerified,
            IsBackupEligible = passkey.IsBackupEligible,
            IsBackedUp = passkey.IsBackedUp,
            AttestationObject = passkey.AttestationObject,
            ClientDataJson = passkey.ClientDataJson
        };

        private static UserPasskeyInfo ToPasskeyInfo(RavenStoredPasskey stored) => new(
            credentialId: Convert.FromBase64String(stored.CredentialId),
            publicKey: stored.PublicKey,
            createdAt: stored.CreatedAt,
            signCount: stored.SignCount,
            transports: stored.Transports,
            isUserVerified: stored.IsUserVerified,
            isBackupEligible: stored.IsBackupEligible,
            isBackedUp: stored.IsBackedUp,
            attestationObject: stored.AttestationObject,
            clientDataJson: stored.ClientDataJson)
        {
            Name = stored.Name
        };

        public void Dispose()
        {
            // No unmanaged state: each operation opens and disposes its own session.
        }
    }
}
