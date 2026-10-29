#nullable enable
using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The configuration THIS host actually runs on - the one that includes <c>appsettings.override.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>In this host <see cref="IConfiguration"/> is NOT the merged configuration.</b> <c>Program.BuildAppAsync</c>
    /// builds its own root - <c>appsettings.json</c>, the environment file, <c>features.json</c>, environment variables
    /// and, last, the override file named by <c>STOMME_APPSETTINGS_PATH</c> - and <c>AddStomme</c> registers THAT
    /// as <see cref="IConfigurationRoot"/>. The <see cref="IConfiguration"/> in the container is the web builder's
    /// default: the same <c>appsettings.json</c> WITHOUT the override, which is where a deployed install keeps
    /// every setting it writes. <b>CORRECTION:</b> <c>Program</c> now adds the override file to the builder's
    /// configuration as well, so the container's <see cref="IConfiguration"/> has it; it still lacks <c>features.json</c>,
    /// and the root remains the one answer.
    /// </para>
    /// <para>
    /// MEASURED 2026-09-21 by driving a real install: with <c>Health:Enabled = true</c> and <c>Metering:Enabled = true</c>
    /// in the override, the Health probe job logged "Health Probing Is Disabled" every minute and the page said "never
    /// checked" - the jobs asked <see cref="IConfiguration"/>, which saw the shipped file's <c>false</c>. Every suite
    /// was green, because each set the key on <c>builder.Configuration</c>, the one place production never writes.
    /// <c>AdminSiteTemplate</c>, <c>SiteRepository</c> and <c>OpenIddictController</c> had each met this separately and
    /// each wrote its own comment; this is the one place to ask instead.
    /// </para>
    /// <para>
    /// The fallback to <see cref="IConfiguration"/> exists for a composition of a module ALONE (no host, so no root) -
    /// never as a second opinion: when the root is there, it is the answer.
    /// </para>
    /// </remarks>
    public static class HostConfiguration
    {
        public static IConfiguration? Resolve(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);
            return services.GetService<IConfigurationRoot>() ?? services.GetService<IConfiguration>();
        }

        /// <summary>
        /// The same answer at COMPOSITION, before a container exists - for an <c>IServerStartup.ConfigureServices</c> that
        /// decides what to register from a switch (a module's demo content, its MCP contribution). <c>AddStomme</c>
        /// registers the layered root as an instance before any module startup runs, so it can be read off the collection;
        /// the last registration wins, as it would in the container (the exemplar module needed it, and
        /// each in-tree module had written its own copy).
        /// </summary>
        public static IConfiguration? Resolve(IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            return services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationRoot))?.ImplementationInstance as IConfigurationRoot
                ?? services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfiguration))?.ImplementationInstance as IConfiguration;
        }
    }
}
