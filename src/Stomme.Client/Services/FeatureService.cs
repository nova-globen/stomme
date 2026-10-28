using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Services
{
    /// <summary>
    /// Service to manage feature flags (<see cref="Feature"/>) — list flags with their effective state,
    /// toggle them, set audience targeting and per-user overrides, and read the change history
    /// (<see cref="FeatureAudit"/>).
    /// </summary>
    /// <remarks>
    /// Reads are Admin-gated server-side; every WRITE is <b>Host-only</b>, because a flag change is
    /// installation-wide configuration. Flags gate <em>capabilities</em>, the role/permission model gates
    /// <em>access</em> — access to this surface is by role, never by a flag, so a misconfigured flag can
    /// never lock an administrator out of fixing it.
    /// </remarks>
    public interface IFeatureService
    {
        /// <summary>Returns every feature flag for the current tenant, with catalog metadata and effective state.</summary>
        Task<List<Feature>> GetFeaturesAsync();

        /// <summary>Returns the change history for the given flag key, newest first.</summary>
        Task<List<FeatureAudit>> GetFeatureAuditsAsync(string key, int take = 100);

        /// <summary>Enables or disables the given flag (creating it if new).</summary>
        Task ToggleFeatureAsync(string key, bool enabled);

        /// <summary>Replaces the given flag's audience targeting (roles + percentage rollout).</summary>
        Task SetTargetingAsync(string key, List<string> targetRoles, int rolloutPercentage);

        /// <summary>Sets (or, with a null <paramref name="enabled"/>, clears) a per-user override.</summary>
        Task SetUserOverrideAsync(string key, int userId, bool? enabled);
    }

    [PrivateApi("Don't show in the documentation, as everything should use the Interface")]
    public class FeatureService : ServiceBase, IFeatureService
    {
        public FeatureService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string Apiurl => CreateApiUrl("Feature");

        public async Task<List<Feature>> GetFeaturesAsync()
        {
            return await GetJsonAsync<List<Feature>>(Apiurl);
        }

        public async Task<List<FeatureAudit>> GetFeatureAuditsAsync(string key, int take = 100)
        {
            return await GetJsonAsync<List<FeatureAudit>>($"{Apiurl}/audit?key={WebUtility.UrlEncode(key)}&take={take}");
        }

        public async Task ToggleFeatureAsync(string key, bool enabled)
        {
            await PostJsonAsync($"{Apiurl}/{WebUtility.UrlEncode(key)}", new ToggleFeature { Enabled = enabled });
        }

        public async Task SetTargetingAsync(string key, List<string> targetRoles, int rolloutPercentage)
        {
            await PostJsonAsync($"{Apiurl}/{WebUtility.UrlEncode(key)}/targeting",
                new SetTargeting { TargetRoles = targetRoles, RolloutPercentage = rolloutPercentage });
        }

        public async Task SetUserOverrideAsync(string key, int userId, bool? enabled)
        {
            await PostJsonAsync($"{Apiurl}/{WebUtility.UrlEncode(key)}/override",
                new SetUserOverride { UserId = userId, Enabled = enabled });
        }

        // The request bodies FeatureController expects (kept private to this service — they are transport
        // shapes, not models).
        private class ToggleFeature
        {
            public bool Enabled { get; set; }
        }

        private class SetTargeting
        {
            public List<string> TargetRoles { get; set; }

            public int RolloutPercentage { get; set; } = 100;
        }

        private class SetUserOverride
        {
            public int UserId { get; set; }

            public bool? Enabled { get; set; }
        }
    }
}
