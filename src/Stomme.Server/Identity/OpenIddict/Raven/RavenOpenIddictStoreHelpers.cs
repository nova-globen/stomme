using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Raven.Client.Documents.Session;

namespace Stomme.Identity.OpenIddict.Raven
{
    /// <summary>
    /// Conversions between OpenIddict's rich member types (immutable dictionaries of <see cref="JsonElement"/>,
    /// culture maps, <see cref="JsonWebKeySet"/>) and the serializer-friendly shapes stored on the RavenDB
    /// entities, plus a streaming "load all" used by the generic <c>IQueryable</c>-based store methods.
    /// </summary>
    internal static class RavenOpenIddictStoreHelpers
    {
        public static string SerializeProperties(ImmutableDictionary<string, JsonElement> properties) =>
            properties.IsEmpty ? null : JsonSerializer.Serialize(properties);

        public static ImmutableDictionary<string, JsonElement> DeserializeProperties(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return ImmutableDictionary.Create<string, JsonElement>();
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            return parsed is null ? ImmutableDictionary.Create<string, JsonElement>() : parsed.ToImmutableDictionary();
        }

        public static Dictionary<string, string> ToCultureMap(ImmutableDictionary<CultureInfo, string> values) =>
            values.IsEmpty ? null : values.ToDictionary(pair => pair.Key.Name, pair => pair.Value);

        public static ImmutableDictionary<CultureInfo, string> FromCultureMap(Dictionary<string, string> values)
        {
            if (values is null || values.Count == 0)
            {
                return ImmutableDictionary.Create<CultureInfo, string>();
            }

            return values.ToImmutableDictionary(pair => CultureInfo.GetCultureInfo(pair.Key), pair => pair.Value);
        }

        public static string SerializeJsonWebKeySet(JsonWebKeySet set) =>
            set is null ? null : JsonSerializer.Serialize(set);

        public static JsonWebKeySet DeserializeJsonWebKeySet(string json) =>
            string.IsNullOrEmpty(json) ? null : new JsonWebKeySet(json);

        /// <summary>
        /// Materializes every document of <typeparamref name="T"/> via a server-side stream (bypassing query
        /// page-size limits), so a caller-supplied <c>Func&lt;IQueryable&lt;T&gt;,…&gt;</c> can be applied in
        /// memory. Acceptable for the application/scope/authorization/token volumes of an on-prem authority.
        /// </summary>
        public static async Task<List<T>> LoadAllAsync<T>(IAsyncDocumentSession session, CancellationToken cancellationToken)
        {
            var results = new List<T>();
            await using var stream = await session.Advanced.StreamAsync(session.Query<T>(), cancellationToken).ConfigureAwait(false);
            while (await stream.MoveNextAsync().ConfigureAwait(false))
            {
                results.Add(stream.Current.Document);
            }

            return results;
        }
    }
}
