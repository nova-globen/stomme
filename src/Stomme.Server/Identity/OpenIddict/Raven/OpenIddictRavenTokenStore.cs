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
    /// <summary>RavenDB-backed <see cref="IOpenIddictTokenStore{TToken}"/>.</summary>
    internal sealed class OpenIddictRavenTokenStore : IOpenIddictTokenStore<OpenIddictRavenToken>
    {
        private readonly IRavenOpenIddictSessionProvider _sessions;

        public OpenIddictRavenTokenStore(IRavenOpenIddictSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.Query<OpenIddictRavenToken>().Customize(c => c.WaitForNonStaleResults()).CountAsync(cancellationToken);
        }

        public async ValueTask<long> CountAsync<TResult>(
            Func<IQueryable<OpenIddictRavenToken>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenToken>(session, cancellationToken);
            return query(all.AsQueryable()).AsEnumerable().LongCount();
        }

        public async ValueTask CreateAsync(OpenIddictRavenToken token, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(token);
            token.Id ??= Guid.NewGuid().ToString("n");
            token.ConcurrencyToken = Guid.NewGuid().ToString();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            await session.StoreAsync(token, token.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        public async ValueTask DeleteAsync(OpenIddictRavenToken token, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(token);
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            session.Delete(token.Id);
            await session.SaveChangesAsync(cancellationToken);
        }

        public async IAsyncEnumerable<OpenIddictRavenToken> FindAsync(
            string subject, string client, string status, string type,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            IRavenQueryable<OpenIddictRavenToken> query = session.Query<OpenIddictRavenToken>()
                .Customize(c => c.WaitForNonStaleResults());
            if (subject is not null)
            {
                query = query.Where(t => t.Subject == subject);
            }

            if (client is not null)
            {
                query = query.Where(t => t.ApplicationId == client);
            }

            if (status is not null)
            {
                query = query.Where(t => t.Status == status);
            }

            if (type is not null)
            {
                query = query.Where(t => t.Type == type);
            }

            foreach (var token in await query.ToListAsync(cancellationToken))
            {
                yield return token;
            }
        }

        public async IAsyncEnumerable<OpenIddictRavenToken> FindByApplicationIdAsync(
            string identifier, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            foreach (var token in await session.Query<OpenIddictRavenToken>().Customize(c => c.WaitForNonStaleResults())
                         .Where(t => t.ApplicationId == identifier).ToListAsync(cancellationToken))
            {
                yield return token;
            }
        }

        public async IAsyncEnumerable<OpenIddictRavenToken> FindByAuthorizationIdAsync(
            string identifier, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            foreach (var token in await session.Query<OpenIddictRavenToken>().Customize(c => c.WaitForNonStaleResults())
                         .Where(t => t.AuthorizationId == identifier).ToListAsync(cancellationToken))
            {
                yield return token;
            }
        }

        public async ValueTask<OpenIddictRavenToken> FindByIdAsync(string identifier, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.LoadAsync<OpenIddictRavenToken>(identifier, cancellationToken);
        }

        public async ValueTask<OpenIddictRavenToken> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);

            // A reference-token identifier is an opaque, case-SENSITIVE secret (OpenIddict generates it as
            // random base64url), and the EF OpenIddict store's `==` matches it case-sensitively. RavenDB's `==`
            // is case-INSENSITIVE, so a bare Raven query could resolve the WRONG token for a differently-cased
            // identifier (a real security divergence). Query the selective field on the server, then post-filter
            // in memory with an ORDINAL comparison so Raven matches EF (D49/D64 pattern; `.ToLower()` THROWS on
            // Raven and is not used).
            var candidates = await session.Query<OpenIddictRavenToken>().Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.ReferenceId == identifier).ToListAsync(cancellationToken);
            return candidates.FirstOrDefault(t => string.Equals(t.ReferenceId, identifier, StringComparison.Ordinal));
        }

        public async IAsyncEnumerable<OpenIddictRavenToken> FindBySubjectAsync(
            string subject, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            foreach (var token in await session.Query<OpenIddictRavenToken>().Customize(c => c.WaitForNonStaleResults())
                         .Where(t => t.Subject == subject).ToListAsync(cancellationToken))
            {
                yield return token;
            }
        }

        public ValueTask<string> GetApplicationIdAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.ApplicationId);

        public async ValueTask<TResult> GetAsync<TState, TResult>(
            Func<IQueryable<OpenIddictRavenToken>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenToken>(session, cancellationToken);
            return query(all.AsQueryable(), state).FirstOrDefault();
        }

        public ValueTask<string> GetAuthorizationIdAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.AuthorizationId);

        public ValueTask<DateTimeOffset?> GetCreationDateAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.CreationDate);

        public ValueTask<DateTimeOffset?> GetExpirationDateAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.ExpirationDate);

        public ValueTask<string> GetIdAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.Id);

        public ValueTask<string> GetPayloadAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.Payload);

        public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(RavenOpenIddictStoreHelpers.DeserializeProperties(token.Properties));

        public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.RedemptionDate);

        public ValueTask<string> GetReferenceIdAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.ReferenceId);

        public ValueTask<string> GetStatusAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.Status);

        public ValueTask<string> GetSubjectAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.Subject);

        public ValueTask<string> GetTypeAsync(OpenIddictRavenToken token, CancellationToken cancellationToken) =>
            new(token.Type);

        public ValueTask<OpenIddictRavenToken> InstantiateAsync(CancellationToken cancellationToken) =>
            new(new OpenIddictRavenToken());

        public async IAsyncEnumerable<OpenIddictRavenToken> ListAsync(
            int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            IQueryable<OpenIddictRavenToken> query = session.Query<OpenIddictRavenToken>().Customize(c => c.WaitForNonStaleResults()).OrderBy(t => t.Id);
            if (offset.HasValue)
            {
                query = query.Skip(offset.Value);
            }

            if (count.HasValue)
            {
                query = query.Take(count.Value);
            }

            foreach (var token in await query.ToListAsync(cancellationToken))
            {
                yield return token;
            }
        }

        public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
            Func<IQueryable<OpenIddictRavenToken>, TState, IQueryable<TResult>> query, TState state,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenToken>(session, cancellationToken);
            foreach (var result in query(all.AsQueryable(), state))
            {
                yield return result;
            }
        }

        public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenToken>(session, cancellationToken);

            long pruned = 0;
            foreach (var token in all.Where(t => t.CreationDate < threshold))
            {
                var inactive = !string.Equals(token.Status, Statuses.Valid, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(token.Status, Statuses.Inactive, StringComparison.OrdinalIgnoreCase);
                var expired = token.ExpirationDate.HasValue && token.ExpirationDate.Value < threshold;
                if (inactive || expired)
                {
                    session.Delete(token.Id);
                    pruned++;
                }
            }

            await session.SaveChangesAsync(cancellationToken);
            return pruned;
        }

        public ValueTask<long> RevokeAsync(string subject, string client, string status, string type, CancellationToken cancellationToken) =>
            RevokeWhereAsync(t =>
                (subject is null || t.Subject == subject) &&
                (client is null || t.ApplicationId == client) &&
                (status is null || t.Status == status) &&
                (type is null || t.Type == type), cancellationToken);

        public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default) =>
            RevokeWhereAsync(t => t.ApplicationId == identifier, cancellationToken);

        public ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken) =>
            RevokeWhereAsync(t => t.AuthorizationId == identifier, cancellationToken);

        public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default) =>
            RevokeWhereAsync(t => t.Subject == subject, cancellationToken);

        public ValueTask SetApplicationIdAsync(OpenIddictRavenToken token, string identifier, CancellationToken cancellationToken)
        {
            token.ApplicationId = identifier;
            return default;
        }

        public ValueTask SetAuthorizationIdAsync(OpenIddictRavenToken token, string identifier, CancellationToken cancellationToken)
        {
            token.AuthorizationId = identifier;
            return default;
        }

        public ValueTask SetCreationDateAsync(OpenIddictRavenToken token, DateTimeOffset? date, CancellationToken cancellationToken)
        {
            token.CreationDate = date;
            return default;
        }

        public ValueTask SetExpirationDateAsync(OpenIddictRavenToken token, DateTimeOffset? date, CancellationToken cancellationToken)
        {
            token.ExpirationDate = date;
            return default;
        }

        public ValueTask SetPayloadAsync(OpenIddictRavenToken token, string payload, CancellationToken cancellationToken)
        {
            token.Payload = payload;
            return default;
        }

        public ValueTask SetPropertiesAsync(OpenIddictRavenToken token, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
        {
            token.Properties = RavenOpenIddictStoreHelpers.SerializeProperties(properties);
            return default;
        }

        public ValueTask SetRedemptionDateAsync(OpenIddictRavenToken token, DateTimeOffset? date, CancellationToken cancellationToken)
        {
            token.RedemptionDate = date;
            return default;
        }

        public ValueTask SetReferenceIdAsync(OpenIddictRavenToken token, string identifier, CancellationToken cancellationToken)
        {
            token.ReferenceId = identifier;
            return default;
        }

        public ValueTask SetStatusAsync(OpenIddictRavenToken token, string status, CancellationToken cancellationToken)
        {
            token.Status = status;
            return default;
        }

        public ValueTask SetSubjectAsync(OpenIddictRavenToken token, string subject, CancellationToken cancellationToken)
        {
            token.Subject = subject;
            return default;
        }

        public ValueTask SetTypeAsync(OpenIddictRavenToken token, string type, CancellationToken cancellationToken)
        {
            token.Type = type;
            return default;
        }

        public async ValueTask UpdateAsync(OpenIddictRavenToken token, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(token);
            token.ConcurrencyToken = Guid.NewGuid().ToString();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            await session.StoreAsync(token, token.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        private async ValueTask<long> RevokeWhereAsync(Func<OpenIddictRavenToken, bool> predicate, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenToken>(session, cancellationToken);
            long revoked = 0;
            foreach (var token in all.Where(predicate))
            {
                if (!string.Equals(token.Status, Statuses.Revoked, StringComparison.OrdinalIgnoreCase))
                {
                    token.Status = Statuses.Revoked;
                    await session.StoreAsync(token, token.Id, cancellationToken);
                    revoked++;
                }
            }

            await session.SaveChangesAsync(cancellationToken);
            return revoked;
        }
    }
}
