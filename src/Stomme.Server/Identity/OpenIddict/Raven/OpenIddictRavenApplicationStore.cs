using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace Stomme.Identity.OpenIddict.Raven
{
    /// <summary>RavenDB-backed <see cref="IOpenIddictApplicationStore{TApplication}"/> (D62). A fresh async
    /// session is opened per operation, bound to the current tenant's dedicated OpenIddict database; the get/set
    /// members operate on the in-memory document, only CRUD/Find/List touch the database. Deleting an application
    /// cascades to its authorizations and tokens.</summary>
    internal sealed class OpenIddictRavenApplicationStore : IOpenIddictApplicationStore<OpenIddictRavenApplication>
    {
        private readonly IRavenOpenIddictSessionProvider _sessions;

        public OpenIddictRavenApplicationStore(IRavenOpenIddictSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.Query<OpenIddictRavenApplication>().Customize(c => c.WaitForNonStaleResults()).CountAsync(cancellationToken);
        }

        public async ValueTask<long> CountAsync<TResult>(
            Func<IQueryable<OpenIddictRavenApplication>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenApplication>(session, cancellationToken);
            return query(all.AsQueryable()).AsEnumerable().LongCount();
        }

        public async ValueTask CreateAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            application.Id ??= Guid.NewGuid().ToString("n");
            application.ConcurrencyToken = Guid.NewGuid().ToString();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            await session.StoreAsync(application, application.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        public async ValueTask DeleteAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            using var session = await _sessions.OpenSessionAsync(cancellationToken);

            // Cascade: remove authorizations and tokens that reference this application.
            foreach (var authorization in await session.Query<OpenIddictRavenAuthorization>()
                         .Where(a => a.ApplicationId == application.Id).ToListAsync(cancellationToken))
            {
                session.Delete(authorization.Id);
            }

            foreach (var token in await session.Query<OpenIddictRavenToken>()
                         .Where(t => t.ApplicationId == application.Id).ToListAsync(cancellationToken))
            {
                session.Delete(token.Id);
            }

            session.Delete(application.Id);
            await session.SaveChangesAsync(cancellationToken);
        }

        public async ValueTask<OpenIddictRavenApplication> FindByIdAsync(string identifier, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.LoadAsync<OpenIddictRavenApplication>(identifier, cancellationToken);
        }

        public async ValueTask<OpenIddictRavenApplication> FindByClientIdAsync(string identifier, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);

            // D70: client_id is CASE-SENSITIVE per RFC 6749/OIDC, and the EF OpenIddict store's `==` already is
            // (case-sensitive on SQLite/PostgreSQL). RavenDB's `==` is case-INSENSITIVE, so a bare Raven query
            // diverges from EF for a mixed-case client_id (Raven matches where EF misses). Query the selective
            // field on the server (Raven may return case-insensitive candidates), then post-filter in memory with
            // an ORDINAL (case-sensitive) comparison so Raven matches EF — the D49/D64 "run the query, filter in
            // memory" pattern. `.ToLower()` is NOT used: it THROWS on Raven.
            var candidates = await session.Query<OpenIddictRavenApplication>().Customize(c => c.WaitForNonStaleResults())
                .Where(a => a.ClientId == identifier).ToListAsync(cancellationToken);
            return candidates.FirstOrDefault(a => string.Equals(a.ClientId, identifier, StringComparison.Ordinal));
        }

        public async IAsyncEnumerable<OpenIddictRavenApplication> FindByPostLogoutRedirectUriAsync(
            string uri, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var matches = await session.Query<OpenIddictRavenApplication>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(a => a.PostLogoutRedirectUris.Contains(uri))
                .ToListAsync(cancellationToken);
            foreach (var application in matches)
            {
                yield return application;
            }
        }

        public async IAsyncEnumerable<OpenIddictRavenApplication> FindByRedirectUriAsync(
            string uri, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var matches = await session.Query<OpenIddictRavenApplication>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(a => a.RedirectUris.Contains(uri))
                .ToListAsync(cancellationToken);
            foreach (var application in matches)
            {
                yield return application;
            }
        }

        public ValueTask<string> GetApplicationTypeAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(application.ApplicationType);

        public async ValueTask<TResult> GetAsync<TState, TResult>(
            Func<IQueryable<OpenIddictRavenApplication>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenApplication>(session, cancellationToken);
            return query(all.AsQueryable(), state).FirstOrDefault();
        }

        public ValueTask<string> GetClientIdAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(application.ClientId);

        public ValueTask<string> GetClientSecretAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(application.ClientSecret);

        public ValueTask<string> GetClientTypeAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(application.ClientType);

        public ValueTask<string> GetConsentTypeAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(application.ConsentType);

        public ValueTask<string> GetDisplayNameAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(application.DisplayName);

        public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(RavenOpenIddictStoreHelpers.FromCultureMap(application.DisplayNames));

        public ValueTask<string> GetIdAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(application.Id);

        public ValueTask<JsonWebKeySet> GetJsonWebKeySetAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(RavenOpenIddictStoreHelpers.DeserializeJsonWebKeySet(application.JsonWebKeySet));

        public ValueTask<ImmutableArray<string>> GetPermissionsAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new([.. application.Permissions]);

        public ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new([.. application.PostLogoutRedirectUris]);

        public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(RavenOpenIddictStoreHelpers.DeserializeProperties(application.Properties));

        public ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new([.. application.RedirectUris]);

        public ValueTask<ImmutableArray<string>> GetRequirementsAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new([.. application.Requirements]);

        public ValueTask<ImmutableDictionary<string, string>> GetSettingsAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken) =>
            new(application.Settings is null ? ImmutableDictionary.Create<string, string>() : application.Settings.ToImmutableDictionary());

        public ValueTask<OpenIddictRavenApplication> InstantiateAsync(CancellationToken cancellationToken) =>
            new(new OpenIddictRavenApplication());

        public async IAsyncEnumerable<OpenIddictRavenApplication> ListAsync(
            int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            IQueryable<OpenIddictRavenApplication> query = session.Query<OpenIddictRavenApplication>().Customize(c => c.WaitForNonStaleResults()).OrderBy(a => a.Id);
            if (offset.HasValue)
            {
                query = query.Skip(offset.Value);
            }

            if (count.HasValue)
            {
                query = query.Take(count.Value);
            }

            foreach (var application in await query.ToListAsync(cancellationToken))
            {
                yield return application;
            }
        }

        public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
            Func<IQueryable<OpenIddictRavenApplication>, TState, IQueryable<TResult>> query, TState state,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenApplication>(session, cancellationToken);
            foreach (var result in query(all.AsQueryable(), state))
            {
                yield return result;
            }
        }

        public ValueTask SetApplicationTypeAsync(OpenIddictRavenApplication application, string type, CancellationToken cancellationToken)
        {
            application.ApplicationType = type;
            return default;
        }

        public ValueTask SetClientIdAsync(OpenIddictRavenApplication application, string identifier, CancellationToken cancellationToken)
        {
            application.ClientId = identifier;
            return default;
        }

        public ValueTask SetClientSecretAsync(OpenIddictRavenApplication application, string secret, CancellationToken cancellationToken)
        {
            application.ClientSecret = secret;
            return default;
        }

        public ValueTask SetClientTypeAsync(OpenIddictRavenApplication application, string type, CancellationToken cancellationToken)
        {
            application.ClientType = type;
            return default;
        }

        public ValueTask SetConsentTypeAsync(OpenIddictRavenApplication application, string type, CancellationToken cancellationToken)
        {
            application.ConsentType = type;
            return default;
        }

        public ValueTask SetDisplayNameAsync(OpenIddictRavenApplication application, string name, CancellationToken cancellationToken)
        {
            application.DisplayName = name;
            return default;
        }

        public ValueTask SetDisplayNamesAsync(OpenIddictRavenApplication application, ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken)
        {
            application.DisplayNames = RavenOpenIddictStoreHelpers.ToCultureMap(names);
            return default;
        }

        public ValueTask SetJsonWebKeySetAsync(OpenIddictRavenApplication application, JsonWebKeySet set, CancellationToken cancellationToken)
        {
            application.JsonWebKeySet = RavenOpenIddictStoreHelpers.SerializeJsonWebKeySet(set);
            return default;
        }

        public ValueTask SetPermissionsAsync(OpenIddictRavenApplication application, ImmutableArray<string> permissions, CancellationToken cancellationToken)
        {
            application.Permissions = [.. permissions];
            return default;
        }

        public ValueTask SetPostLogoutRedirectUrisAsync(OpenIddictRavenApplication application, ImmutableArray<string> uris, CancellationToken cancellationToken)
        {
            application.PostLogoutRedirectUris = [.. uris];
            return default;
        }

        public ValueTask SetPropertiesAsync(OpenIddictRavenApplication application, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
        {
            application.Properties = RavenOpenIddictStoreHelpers.SerializeProperties(properties);
            return default;
        }

        public ValueTask SetRedirectUrisAsync(OpenIddictRavenApplication application, ImmutableArray<string> uris, CancellationToken cancellationToken)
        {
            application.RedirectUris = [.. uris];
            return default;
        }

        public ValueTask SetRequirementsAsync(OpenIddictRavenApplication application, ImmutableArray<string> requirements, CancellationToken cancellationToken)
        {
            application.Requirements = [.. requirements];
            return default;
        }

        public ValueTask SetSettingsAsync(OpenIddictRavenApplication application, ImmutableDictionary<string, string> settings, CancellationToken cancellationToken)
        {
            application.Settings = settings.IsEmpty ? null : settings.ToDictionary(pair => pair.Key, pair => pair.Value);
            return default;
        }

        public async ValueTask UpdateAsync(OpenIddictRavenApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            application.ConcurrencyToken = Guid.NewGuid().ToString();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            await session.StoreAsync(application, application.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
    }
}
