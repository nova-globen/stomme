using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Stomme.FeatureManagement.Application;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// The <see cref="HostCompositionTests"/> composition (the REAL <c>AddStomme</c>, the real assembly scan, ValidateOnBuild +
    /// ValidateScopes, a throwaway content root, an in-memory <see cref="IConfigurationRoot"/>), copied rather than extracted
    /// so that gate file is only ever EXTENDED. It also returns the configuration root, so a test can hand the SAME root to
    /// <c>EntitlementStartupValidation.Validate</c> after building.
    /// </summary>
    internal sealed class LicensingHostComposition : IDisposable
    {
        private readonly List<string> _tempRoots = new();

        public (WebApplicationBuilder Builder, IConfigurationRoot Configuration) ComposeHostBuilder(Dictionary<string, string?> settings)
        {
            LoadFrameworkAssemblies();

            var contentRoot = Path.Combine(Path.GetTempPath(), "stomme-licensing-composition", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot"));
            _tempRoots.Add(contentRoot);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = contentRoot,
                WebRootPath = "wwwroot",
                EnvironmentName = Environments.Development,
                ApplicationName = typeof(Stomme.Server.Program).Assembly.GetName().Name,
            });

            builder.Logging.ClearProviders();
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });

            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(contentRoot, "Data"));

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            builder.Services.AddStomme(configuration, builder.Environment);
            return (builder, configuration);
        }

        /// <summary>
        /// One line per descriptor, in registration order: service type, implementation (type, instance type or
        /// <c>factory</c>), lifetime and service key. Keyed descriptors are read through their keyed accessors — the
        /// unkeyed ones throw on a keyed descriptor.
        /// </summary>
        public static List<string> Signature(IEnumerable<ServiceDescriptor> services) =>
            services.Select(d => $"{d.ServiceType}|{Implementation(d)}|{d.Lifetime}|{d.ServiceKey}").ToList();

        /// <summary>True for a descriptor this row owns: <see cref="IEntitlementProvider"/>, or any type in the licensing namespace.</summary>
        public static bool IsLicensingDescriptor(ServiceDescriptor d)
        {
            var implementation = d.IsKeyedService
                ? d.KeyedImplementationType ?? d.KeyedImplementationInstance?.GetType()
                : d.ImplementationType ?? d.ImplementationInstance?.GetType();
            return d.ServiceType == typeof(IEntitlementProvider)
                || implementation == typeof(IEntitlementProvider)
                || InLicensingNamespace(d.ServiceType)
                || (implementation is not null && InLicensingNamespace(implementation));
        }

        private static bool InLicensingNamespace(Type type) =>
            string.Equals(type.Namespace, "Stomme.Infrastructure.Licensing", StringComparison.Ordinal);

        private static string Implementation(ServiceDescriptor d)
        {
            if (d.IsKeyedService)
            {
                return d.KeyedImplementationType?.ToString()
                    ?? (d.KeyedImplementationInstance is not null ? "instance:" + d.KeyedImplementationInstance.GetType() : "factory");
            }

            return d.ImplementationType?.ToString()
                ?? (d.ImplementationInstance is not null ? "instance:" + d.ImplementationInstance.GetType() : "factory");
        }

        private static void LoadFrameworkAssemblies()
        {
            var probePath = Path.GetDirectoryName(typeof(LicensingHostComposition).Assembly.Location)!;
            foreach (var dll in Directory.EnumerateFiles(probePath, "Stomme*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Assembly.Load(AssemblyName.GetAssemblyName(dll));
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                    // Not a managed Stomme assembly, or already loaded from elsewhere (HostCompositionTests does the same).
                }
            }
        }

        public void Dispose()
        {
            foreach (var root in _tempRoots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    // A file the host still holds open; the OS reclaims the temp folder.
                }
            }
        }
    }
}