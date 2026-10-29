#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Connectivity.Application
{
    // ============================================================================================================
    // The site's egress allowlist: the private and loopback hosts the site's connections may reach - and the AI
    // assistant's model endpoint, which rides the same egress. The egress read it from a site setting that no page
    // set; a browser drive of the assistant had to write it into the database. Read by a holder of connections:view;
    // CHANGED BY THE HOST ROLE ALONE: widening a site's egress to private addresses opens the installation's own network
    // to whoever administers the site. The settings API refuses the same row to anyone but the Host.
    // ============================================================================================================

    /// <summary>The allowlist's storage - the Site setting the egress reads (<see cref="HostConnectorEgress.AllowedHostsSettingName"/>).</summary>
    public interface IConnectivityEgressSettings
    {
        Task<string?> ReadAsync(int siteId, CancellationToken ct = default);

        Task WriteAsync(int siteId, string value, CancellationToken ct = default);
    }

    /// <summary>The allowlist over the host's settings, as a PRIVATE row - the one the egress factory reads per call.</summary>
    public sealed class SettingRepositoryConnectivityEgressSettings(ISettingRepository settings) : IConnectivityEgressSettings
    {
        public async Task<string?> ReadAsync(int siteId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return (await settings.GetSettingAsync(EntityNames.Site, siteId, HostConnectorEgress.AllowedHostsSettingName))?.SettingValue;
        }

        public async Task WriteAsync(int siteId, string value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await settings.GetSettingAsync(EntityNames.Site, siteId, HostConnectorEgress.AllowedHostsSettingName);
            if (existing is null)
            {
                await settings.AddSettingAsync(new Setting
                {
                    EntityName = EntityNames.Site,
                    EntityId = siteId,
                    SettingName = HostConnectorEgress.AllowedHostsSettingName,
                    SettingValue = value,
                    IsPrivate = true,
                });
                return;
            }

            existing.SettingValue = value;
            existing.IsPrivate = true;
            await settings.UpdateSettingAsync(existing);
        }
    }

    /// <summary>
    /// The allowlist as the guard reads it (<see cref="SiteSettingConnectorEgressFactory.ParseAllowedHosts"/>), each entry
    /// held to what the guard can match. The guard compares a connection's host with an entry EXACTLY, so a range, a
    /// wildcard, a scheme, a port or an address in a short form would match nothing - it is refused, not stored.
    /// </summary>
    public static class EgressAllowlist
    {
        public const int MaxLength = 2000;

        public const int MaxHosts = 50;

        public static IReadOnlyList<string> Parse(string? value)
        {
            var text = value ?? string.Empty;
            if (text.Length > MaxLength)
            {
                throw new ArgumentException($"The allowlist is at most {MaxLength} characters.");
            }

            var hosts = SiteSettingConnectorEgressFactory.ParseAllowedHosts(text).ToList();
            var invalid = hosts.Where(host => !IsExactHost(host)).ToList();
            if (invalid.Count > 0)
            {
                // No parameter name: the message is shown to the Host as it is, and .NET would append "(Parameter 'value')".
                throw new ArgumentException(
                    $"Not an exact host name or IP address: {string.Join(", ", invalid.Take(5))}. The allowlist matches a connection's host exactly - no ranges, wildcards, schemes or ports, and an address written in full.");
            }

            if (hosts.Count > MaxHosts)
            {
                throw new ArgumentException($"The allowlist holds at most {MaxHosts} hosts.");
            }

            return hosts;
        }

        // An IP literal only as the guard would print it (so "127.1" or "0:0::1", which parse, are refused: they match
        // nothing); otherwise a DNS host name.
        private static bool IsExactHost(string host) =>
            IPAddress.TryParse(host, out var address)
                ? string.Equals(address.ToString(), host, StringComparison.OrdinalIgnoreCase)
                : Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    internal static class EgressScope
    {
        public static int Site(SiteState siteState) =>
            siteState.Alias?.SiteId ?? throw new InvalidOperationException("The allowlist belongs to the request's site; none is resolved.");

        public static bool IsHost(ICurrentUser user) => user.Roles.Contains(RoleNames.Host);
    }

    /// <summary>The site's allowlist, and whether the caller may change it.</summary>
    [Requires(ConnectionPermissions.View)]
    public sealed record GetConnectionEgressQuery : IQuery<ConnectionEgressView>;

    public sealed class GetConnectionEgressQueryHandler(IConnectivityEgressSettings settings, SiteState siteState, ICurrentUser currentUser)
        : IQueryHandler<GetConnectionEgressQuery, ConnectionEgressView>
    {
        public async Task<ConnectionEgressView> Handle(GetConnectionEgressQuery request, CancellationToken ct) =>
            new(SiteSettingConnectorEgressFactory.ParseAllowedHosts(await settings.ReadAsync(EgressScope.Site(siteState), ct)).ToList(), EgressScope.IsHost(currentUser));
    }

    /// <summary>Replaces the site's allowlist - the Host role alone; each entry an exact host name or IP address.</summary>
    [Requires(ConnectionPermissions.Manage)]
    public sealed record SaveConnectionEgressCommand(string? AllowedHosts) : ICommand<ConnectionEgressView>;

    public sealed class SaveConnectionEgressCommandHandler(IConnectivityEgressSettings settings, SiteState siteState, ICurrentUser currentUser, ILogger<SaveConnectionEgressCommandHandler> logger)
        : ICommandHandler<SaveConnectionEgressCommand, ConnectionEgressView>
    {
        public async Task<ConnectionEgressView> Handle(SaveConnectionEgressCommand request, CancellationToken ct)
        {
            if (!EgressScope.IsHost(currentUser))
            {
                throw new UnauthorizedAccessException("Only the Host role may change which private hosts this site may reach: it opens the installation's own network to the site's connections.");
            }

            var hosts = EgressAllowlist.Parse(request.AllowedHosts);
            var siteId = EgressScope.Site(siteState);
            await settings.WriteAsync(siteId, string.Join(", ", hosts), ct);
            logger.LogInformation("Site {SiteId}'s egress allowlist was saved by the Host: {Count} host(s).", siteId, hosts.Count);
            return new ConnectionEgressView(hosts, true);
        }
    }
}
