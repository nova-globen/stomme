#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp.Persistence;
using Stomme.Portability;

namespace Stomme.Modules.Mcp.Application
{
    /// <summary>
    /// A person's personal MCP tokens in their copy of their data (BL-101): each one's name, what it may do, when it was made,
    /// last used, expires and was revoked - what their profile lists. Never the secret, which is stored nowhere, nor its hash.
    /// </summary>
    public sealed class McpPersonalTokenSubjectContributor(IMcpPersonalTokenRepository tokens) : ISubjectDataContributor
    {
        public const string Type = "mcp-token";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
            (await tokens.ListForUserAsync(scope.SiteId, scope.UserId, cancellationToken))
                .OrderBy(token => token.CreatedOnUtc)
                .Select(token => new PortableEntity(
                    new PortableEntityRef(Type, token.Id.ToString("N", CultureInfo.InvariantCulture)),
                    token.CreatedOnUtc.ToString("u", CultureInfo.InvariantCulture),
                    1,
                    JsonSerializer.Serialize(new
                    {
                        name = token.Name,
                        prefix = token.Prefix,
                        scopes = token.ScopeList(),
                        createdOnUtc = token.CreatedOnUtc,
                        lastUsedOnUtc = token.LastUsedOnUtc,
                        expiresOnUtc = token.ExpiresOnUtc,
                        revokedOnUtc = token.RevokedOnUtc,
                    })))
                .ToList();
    }
}