using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenIddict.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace Stomme.Identity.OpenIddict.Raven
{
    /// <summary>RavenDB-backed <see cref="IOpenIddictScopeStore{TScope}"/> (D62).</summary>
    internal sealed class OpenIddictRavenScopeStore : IOpenIddictScopeStore<OpenIddictRavenScope>
    {
        private readonly IRavenOpenIddictSessionProvider _sessions;

        public OpenIddictRavenScopeStore(IRavenOpenIddictSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.Query<OpenIddictRavenScope>().Customize(c => c.WaitForNonStaleResults()).CountAsync(cancellationToken);
        }

        public async ValueTask<long> CountAsync<TResult>(
            Func<IQueryable<OpenIddictRavenScope>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenScope>(session, cancellationToken);
            return query(all.AsQueryable()).AsEnumerable().LongCount();
        }

        public async ValueTask CreateAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scope);
            scope.Id ??= Guid.NewGuid().ToString("n");
            scope.ConcurrencyToken = Guid.NewGuid().ToString();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            await session.StoreAsync(scope, scope.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        public async ValueTask DeleteAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scope);
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            session.Delete(scope.Id);
            await session.SaveChangesAsync(cancellationToken);
        }

        public async ValueTask<OpenIddictRavenScope> FindByIdAsync(string identifier, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.LoadAsync<OpenIddictRavenScope>(identifier, cancellationToken);
        }

        public async ValueTask<OpenIddictRavenScope> FindByNameAsync(string name, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            return await session.Query<OpenIddictRavenScope>().Customize(c => c.WaitForNonStaleResults()).FirstOrDefaultAsync(s => s.Name == name, cancellationToken);
        }

        public async IAsyncEnumerable<OpenIddictRavenScope> FindByNamesAsync(
            ImmutableArray<string> names, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var nameList = names.ToArray();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var matches = await session.Query<OpenIddictRavenScope>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(s => s.Name != null && nameList.Contains(s.Name))
                .ToListAsync(cancellationToken);
            foreach (var scope in matches)
            {
                yield return scope;
            }
        }

        public async IAsyncEnumerable<OpenIddictRavenScope> FindByResourceAsync(
            string resource, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var matches = await session.Query<OpenIddictRavenScope>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(s => s.Resources.Contains(resource))
                .ToListAsync(cancellationToken);
            foreach (var scope in matches)
            {
                yield return scope;
            }
        }

        public async ValueTask<TResult> GetAsync<TState, TResult>(
            Func<IQueryable<OpenIddictRavenScope>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenScope>(session, cancellationToken);
            return query(all.AsQueryable(), state).FirstOrDefault();
        }

        public ValueTask<string> GetDescriptionAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken) =>
            new(scope.Description);

        public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDescriptionsAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken) =>
            new(RavenOpenIddictStoreHelpers.FromCultureMap(scope.Descriptions));

        public ValueTask<string> GetDisplayNameAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken) =>
            new(scope.DisplayName);

        public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken) =>
            new(RavenOpenIddictStoreHelpers.FromCultureMap(scope.DisplayNames));

        public ValueTask<string> GetIdAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken) =>
            new(scope.Id);

        public ValueTask<string> GetNameAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken) =>
            new(scope.Name);

        public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken) =>
            new(RavenOpenIddictStoreHelpers.DeserializeProperties(scope.Properties));

        public ValueTask<ImmutableArray<string>> GetResourcesAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken) =>
            new([.. scope.Resources]);

        public ValueTask<OpenIddictRavenScope> InstantiateAsync(CancellationToken cancellationToken) =>
            new(new OpenIddictRavenScope());

        public async IAsyncEnumerable<OpenIddictRavenScope> ListAsync(
            int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            IQueryable<OpenIddictRavenScope> query = session.Query<OpenIddictRavenScope>().Customize(c => c.WaitForNonStaleResults()).OrderBy(s => s.Id);
            if (offset.HasValue)
            {
                query = query.Skip(offset.Value);
            }

            if (count.HasValue)
            {
                query = query.Take(count.Value);
            }

            foreach (var scope in await query.ToListAsync(cancellationToken))
            {
                yield return scope;
            }
        }

        public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
            Func<IQueryable<OpenIddictRavenScope>, TState, IQueryable<TResult>> query, TState state,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            var all = await RavenOpenIddictStoreHelpers.LoadAllAsync<OpenIddictRavenScope>(session, cancellationToken);
            foreach (var result in query(all.AsQueryable(), state))
            {
                yield return result;
            }
        }

        public ValueTask SetDescriptionAsync(OpenIddictRavenScope scope, string description, CancellationToken cancellationToken)
        {
            scope.Description = description;
            return default;
        }

        public ValueTask SetDescriptionsAsync(OpenIddictRavenScope scope, ImmutableDictionary<CultureInfo, string> descriptions, CancellationToken cancellationToken)
        {
            scope.Descriptions = RavenOpenIddictStoreHelpers.ToCultureMap(descriptions);
            return default;
        }

        public ValueTask SetDisplayNameAsync(OpenIddictRavenScope scope, string name, CancellationToken cancellationToken)
        {
            scope.DisplayName = name;
            return default;
        }

        public ValueTask SetDisplayNamesAsync(OpenIddictRavenScope scope, ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken)
        {
            scope.DisplayNames = RavenOpenIddictStoreHelpers.ToCultureMap(names);
            return default;
        }

        public ValueTask SetNameAsync(OpenIddictRavenScope scope, string name, CancellationToken cancellationToken)
        {
            scope.Name = name;
            return default;
        }

        public ValueTask SetPropertiesAsync(OpenIddictRavenScope scope, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
        {
            scope.Properties = RavenOpenIddictStoreHelpers.SerializeProperties(properties);
            return default;
        }

        public ValueTask SetResourcesAsync(OpenIddictRavenScope scope, ImmutableArray<string> resources, CancellationToken cancellationToken)
        {
            scope.Resources = [.. resources];
            return default;
        }

        public async ValueTask UpdateAsync(OpenIddictRavenScope scope, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scope);
            scope.ConcurrencyToken = Guid.NewGuid().ToString();
            using var session = await _sessions.OpenSessionAsync(cancellationToken);
            await session.StoreAsync(scope, scope.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
    }
}
