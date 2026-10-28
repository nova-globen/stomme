using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Stomme.FeatureManagement.Application;

namespace Stomme.Modules.FeatureManagement.Infrastructure
{
    /// <summary>
    /// Supplies the ambient <see cref="FeatureContext"/> from the current request's principal: the signed-in
    /// user's id (for per-user overrides and the percentage rollout) and role names (for role targeting).
    /// Outside a request — a background job, or before sign-in — it yields the anonymous context, so a flag
    /// still evaluates (role-gated and partially rolled-out flags simply resolve to off).
    /// </summary>
    public class HttpFeatureContextProvider : IFeatureContextProvider
    {
        private readonly IHttpContextAccessor _accessor;

        public HttpFeatureContextProvider(IHttpContextAccessor accessor)
        {
            _accessor = accessor;
        }

        public FeatureContext GetContext()
        {
            var user = _accessor.HttpContext?.User;
            if (user?.Identity == null || !user.Identity.IsAuthenticated)
            {
                return FeatureContext.Anonymous;
            }

            var roles = new HashSet<string>(
                user.FindAll(ClaimTypes.Role).Select(claim => claim.Value),
                StringComparer.OrdinalIgnoreCase);

            var identifier = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userId = int.TryParse(identifier, out var id) ? FeatureUserId.FromUserId(id) : (Guid?)null;

            return new FeatureContext(userId, roles);
        }
    }

    /// <summary>
    /// Projects Stomme's integer user id onto the GUID the flag aggregate keys per-user overrides by.
    /// The mapping is deterministic and stable (a fixed namespace prefix plus the id), so a user's overrides
    /// and rollout bucket survive restarts.
    /// </summary>
    /// <remarks>
    /// Integration point (backlog item 02 — identity): the identity work introduces a User aggregate with a
    /// native GUID id. When it lands, the flag system's per-user targeting should key off that id directly
    /// and this projection goes away — overrides recorded against a projected id would need a one-time
    /// re-key. The GUID surface is kept here (rather than an int) precisely so that switch is a swap of this
    /// provider, not a change to the flag aggregate or its store.
    /// </remarks>
    public static class FeatureUserId
    {
        // A fixed prefix keeps projected ids in their own space, so they cannot collide with real GUIDs.
        private static readonly byte[] Prefix =
        [
            0xD1, 0xDA, 0xC0, 0x2E, 0xFE, 0xA7, 0x40, 0x00, 0x8A, 0x00, 0x00, 0x00
        ];

        public static Guid FromUserId(int userId)
        {
            Span<byte> bytes = stackalloc byte[16];
            Prefix.CopyTo(bytes);
            BinaryPrimitives.WriteInt32BigEndian(bytes[12..], userId);
            return new Guid(bytes);
        }
    }
}
