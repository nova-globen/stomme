using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenIddict.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Stomme.Identity.OpenIddict.Raven
{
    /// <summary>RavenDB-backed <see cref="IOpenIddictAuthorizationStore{TAuthorization}"/>.</summary>
    internal sealed class OpenIddictRavenAuthorizationStore : IOpenIddictAuthorizationStore<OpenIddictRavenAuthorization>
    {
        private readonly IRavenOpenIddictSessionProvider _sessions;

        public OpenIddictRavenAuthorizationStore(IRavenOpenIddictSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.Query<OpenIddictRavenAuthorization>().Customize(c => c.WaitForNonStaleResults()).CountAsync(cancellationToken);
        }

        public async ValueTask<long> CountAsync<TResult>(
            Func<IQueryable<OpenIddictRavenAuthorization>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenAuthorization>(session, cancellationToken);
            return query(all.AsQueryable()).AsEnumerable().LongCount();
        }

        public async ValueTask CreateAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            authorization.Id ??= Guid.NewGuid().ToString("n");
            authorization.ConcurrencyToken = Guid.NewGuid().ToString();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            await session.StoreAsync(authorization, authorization.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        public async ValueTask DeleteAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            using var session = await _sessions.OpenSessionAsync(cancellationToken);

            // Cascade: remove tokens attached to this authorization.
            foreach (var token in await session.Query<OpenIddictRavenToken>()
                         .Where(t => t.AuthorizationId == authorization.Id).ToListAsync(cancellationToken))
            {
                session.Delete(token.Id);
            }

            session.Delete(authorization.Id);
            await session.SaveChangesAsync(cancellationToken);
        }

        public async IAsyncEnumerable<OpenIddictRavenAuthorization> FindAsync(
            string subject, string client, string status, string type, ImmutableArray<string>? scopes,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            IRavenQueryable<OpenIddictRavenAuthorization> query = session.Query<OpenIddictRavenAuthorization>()
                .Customize(c => c.WaitForNonStaleResults());
            if (subject is not null)
            {
                query = query.Where(a => a.Subject == subject);
            }

            if (client is not null)
            {
                query = query.Where(a => a.ApplicationId == client);
            }

            if (status is not null)
            {
                query = query.Where(a => a.Status == status);
            }

            if (type is not null)
            {
                query = query.Where(a => a.Type == type);
            }

            if (scopes is not null)
            {
                foreach (var scope in scopes.Value)
                {
                    query = query.Where(a => a.Scopes.Contains(scope));
                }
            }

            foreach (var authorization in await query.ToListAsync(cancellationToken))
            {
                yield return authorization;
            }
        }

        public async IAsyncEnumerable<OpenIddictRavenAuthorization> FindByApplicationIdAsync(
            string identifier, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            foreach (var authorization in await session.Query<OpenIddictRavenAuthorization>().Customize(c => c.WaitForNonStaleResults())
                         .Where(a => a.ApplicationId == identifier).ToListAsync(cancellationToken))
            {
                yield return authorization;
            }
        }

        public async ValueTask<OpenIddictRavenAuthorization> FindByIdAsync(string identifier, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.LoadAsync<OpenIddictRavenAuthorization>(identifier, cancellationToken);
        }

        public async IAsyncEnumerable<OpenIddictRavenAuthorization> FindBySubjectAsync(
            string subject, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            foreach (var authorization in await session.Query<OpenIddictRavenAuthorization>().Customize(c => c.WaitForNonStaleResults())
                         .Where(a => a.Subject == subject).ToListAsync(cancellationToken))
            {
                yield return authorization;
            }
        }

        public ValueTask<string> GetApplicationIdAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken) =>
            new(authorization.ApplicationId);

        public async ValueTask<TResult> GetAsync<TState, TResult>(
            Func<IQueryable<OpenIddictRavenAuthorization>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenAuthorization>(session, cancellationToken);
            return query(all.AsQueryable(), state).FirstOrDefault();
        }

        public ValueTask<DateTimeOffset?> GetCreationDateAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken) =>
            new(authorization.CreationDate);

        public ValueTask<string> GetIdAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken) =>
            new(authorization.Id);

        public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken) =>
            new(RavenOpenIddictStoreHelpers.DeserializeProperties(authorization.Properties));

        public ValueTask<ImmutableArray<string>> GetScopesAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken) =>
            new([.. authorization.Scopes]);

        public ValueTask<string> GetStatusAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken) =>
            new(authorization.Status);

        public ValueTask<string> GetSubjectAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken) =>
            new(authorization.Subject);

        public ValueTask<string> GetTypeAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken) =>
            new(authorization.Type);

        public ValueTask<OpenIddictRavenAuthorization> InstantiateAsync(CancellationToken cancellationToken) =>
            new(new OpenIddictRavenAuthorization());

        public async IAsyncEnumerable<OpenIddictRavenAuthorization> ListAsync(
            int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            IQueryable<OpenIddictRavenAuthorization> query = session.Query<OpenIddictRavenAuthorization>().Customize(c => c.WaitForNonStaleResults()).OrderBy(a => a.Id);
            if (offset.HasValue)
            {
                query = query.Skip(offset.Value);
            }

            if (count.HasValue)
            {
                query = query.Take(count.Value);
            }

            foreach (var authorization in await query.ToListAsync(cancellationToken))
            {
                yield return authorization;
            }
        }

        public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
            Func<IQueryable<OpenIddictRavenAuthorization>, TState, IQueryable<TResult>> query, TState state,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenAuthorization>(session, cancellationToken);
            foreach (var result in query(all.AsQueryable(), state))
            {
                yield return result;
            }
        }

        public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenAuthorization>(session, cancellationToken);

            var tokenAuthorizationIds = (await session.Query<OpenIddictRavenToken>().ToListAsync(cancellationToken))
                .Select(t => t.AuthorizationId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);

            long pruned = 0;
            foreach (var authorization in all.Where(a => a.CreationDate < threshold))
            {
                var inactive = !string.Equals(authorization.Status, Statuses.Valid, StringComparison.OrdinalIgnoreCase);
                var orphanAdHoc = string.Equals(authorization.Type, AuthorizationTypes.AdHoc, StringComparison.OrdinalIgnoreCase)
                    && !tokenAuthorizationIds.Contains(authorization.Id);
                if (inactive || orphanAdHoc)
                {
                    session.Delete(authorization.Id);
                    pruned++;
                }
            }

            await session.SaveChangesAsync(cancellationToken);
            return pruned;
        }

        public ValueTask<long> RevokeAsync(string subject, string client, string status, string type, CancellationToken cancellationToken) =>
            RevokeWhereAsync(a =>
                (subject is null || a.Subject == subject) &&
                (client is null || a.ApplicationId == client) &&
                (status is null || a.Status == status) &&
                (type is null || a.Type == type), cancellationToken);

        public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken) =>
            RevokeWhereAsync(a => a.ApplicationId == identifier, cancellationToken);

        public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken) =>
            RevokeWhereAsync(a => a.Subject == subject, cancellationToken);

        public ValueTask SetApplicationIdAsync(OpenIddictRavenAuthorization authorization, string identifier, CancellationToken cancellationToken)
        {
            authorization.ApplicationId = identifier;
            return default;
        }

        public ValueTask SetCreationDateAsync(OpenIddictRavenAuthorization authorization, DateTimeOffset? date, CancellationToken cancellationToken)
        {
            authorization.CreationDate = date;
            return default;
        }

        public ValueTask SetPropertiesAsync(OpenIddictRavenAuthorization authorization, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
        {
            authorization.Properties = RavenOpenIddictStoreHelpers.SerializeProperties(properties);
            return default;
        }

        public ValueTask SetScopesAsync(OpenIddictRavenAuthorization authorization, ImmutableArray<string> scopes, CancellationToken cancellationToken)
        {
            authorization.Scopes = [.. scopes];
            return default;
        }

        public ValueTask SetStatusAsync(OpenIddictRavenAuthorization authorization, string status, CancellationToken cancellationToken)
        {
            authorization.Status = status;
            return default;
        }

        public ValueTask SetSubjectAsync(OpenIddictRavenAuthorization authorization, string subject, CancellationToken cancellationToken)
        {
            authorization.Subject = subject;
            return default;
        }

        public ValueTask SetTypeAsync(OpenIddictRavenAuthorization authorization, string type, CancellationToken cancellationToken)
        {
            authorization.Type = type;
            return default;
        }

        public async ValueTask UpdateAsync(OpenIddictRavenAuthorization authorization, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            authorization.ConcurrencyToken = Guid.NewGuid().ToString();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            await session.StoreAsync(authorization, authorization.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        private async ValueTask<long> RevokeWhereAsync(Func<OpenIddictRavenAuthorization, bool> predicate, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenAuthorization>(session, cancellationToken);
            long revoked = 0;
            foreach (var authorization in all.Where(predicate))
            {
                if (!string.Equals(authorization.Status, Statuses.Revoked, StringComparison.OrdinalIgnoreCase))
                {
                    authorization.Status = Statuses.Revoked;
                    await session.StoreAsync(authorization, authorization.Id, cancellationToken);
                    revoked++;
                }
            }

            await session.SaveChangesAsync(cancellationToken);
            return revoked;
        }
    }
}
