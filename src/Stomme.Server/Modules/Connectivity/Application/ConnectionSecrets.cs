#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectivity.Domain;

namespace Stomme.Modules.Connectivity.Application
{
    /// <summary>What a resolve returned: the field values that could be read, and the fields whose secret is unreadable (the Data Protection key ring changed - an operator re-enters them; never a raw exception, never ciphertext).</summary>
    public sealed record ResolvedCredentials(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Unreadable, IReadOnlyList<string> Missing)
    {
        public bool IsComplete => Unreadable.Count == 0 && Missing.Count == 0;
    }

    /// <summary>
    /// A connection's secrets, written and read ONLY through the per-site secret store under the closed grammar
    /// <c>connectivity.connection-&lt;id:N&gt;.&lt;field&gt;</c>: a field is stored when the connector's authentication scheme
    /// declares it and its name already fits the store's segment grammar, never rewritten; a delete removes every key the
    /// aggregate records; a value is never echoed - a read answers Found / Unreadable / NotFound per field.
    /// </summary>
    public sealed class ConnectionSecrets(ISiteSecretStore secrets)
    {
        /// <summary>
        /// Stores the given credential values for the connection and returns the field NAMES stored - what the aggregate
        /// records. A blank value for a field leaves that field as it was (an edit form that does not re-enter a secret);
        /// an undeclared or malformed field name is refused before anything is written.
        /// </summary>
        public async Task<IReadOnlyList<string>> StoreAsync(int siteId, Guid connectionId, AuthenticationType authType, IReadOnlyDictionary<string, string>? credentials, IEnumerable<string>? alreadyStored, CancellationToken ct = default)
        {
            var stored = new List<string>((alreadyStored ?? Array.Empty<string>()).Select(ConnectionCredentialKeys.RequireField));
            if (credentials is null || credentials.Count == 0)
            {
                return stored;
            }

            // Every name is checked BEFORE the first write, so a refusal writes nothing.
            var toWrite = new List<(string Field, string Value)>();
            foreach (var (field, value) in credentials)
            {
                var name = ConnectionCredentialKeys.RequireDeclared(authType, field);
                if (!string.IsNullOrEmpty(value))
                {
                    toWrite.Add((name, value));
                }
            }

            foreach (var (field, value) in toWrite)
            {
                await secrets.SetAsync(siteId, ConnectionCredentialKeys.For(connectionId, field), value, ct);
                if (!stored.Contains(field, StringComparer.Ordinal))
                {
                    stored.Add(field);
                }
            }

            return stored;
        }

        /// <summary>Reads every field the connection records; a value that cannot be unprotected is reported as Unreadable, a missing one as Missing.</summary>
        public async Task<ResolvedCredentials> ResolveAsync(int siteId, ConnectionAggregate connection, CancellationToken ct = default)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var unreadable = new List<string>();
            var missing = new List<string>();
            foreach (var field in connection.CredentialFields)
            {
                var result = await secrets.GetAsync(siteId, ConnectionCredentialKeys.For(connection.Id, field), ct);
                switch (result.Status)
                {
                    case SiteSecretStatus.Found:
                        values[field] = result.Secret!;
                        break;
                    case SiteSecretStatus.Unreadable:
                        unreadable.Add(field);
                        break;
                    default:
                        missing.Add(field);
                        break;
                }
            }

            return new ResolvedCredentials(values, unreadable, missing);
        }

        /// <summary>Removes every key the connection records (and the fields named, for a reconfiguration that dropped some). Idempotent.</summary>
        public async Task DeleteAsync(int siteId, Guid connectionId, IEnumerable<string> fields, CancellationToken ct = default)
        {
            foreach (var field in fields.Distinct(StringComparer.Ordinal))
            {
                await secrets.DeleteAsync(siteId, ConnectionCredentialKeys.For(connectionId, field), ct);
            }
        }
    }
}
