#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Modules;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// On a WebAssembly site every client service a page injects is registered in the BROWSER under the interface the page asks for.
    /// </summary>
    /// <remarks>
    /// Found on a WebAssembly site verifying 10.5.0 in a browser: the home page's Landing module failed to
    /// render - "There is no registered service of type 'Stomme.Landing.ILandingService'" - and the renderer's later batches
    /// failed after it. The WebAssembly client's <c>Program</c> registers each <c>IClientService</c> under the interface it
    /// derives by NAME in the implementation's own namespace and assembly (<c>...Services.LandingService</c> ->
    /// <c>...Services.ILandingService</c>); a module whose contract lives in <c>Stomme.Shared</c>, in another namespace, got no
    /// registration under its interface - only under its class, which no page injects. Server-runtime sites never saw it: the
    /// server registers its own twins (<c>ClientServiceServerTwinTests</c>). This runs the client's registration and asks it for
    /// every interface a client service implements.
    /// </remarks>
    public sealed class WebAssemblyClientServiceRegistrationTests
    {
        [Fact]
        public void Every_client_service_is_registered_under_its_interface_in_the_browser()
        {
            var client = typeof(ModuleBase).Assembly;
            // What the WebAssembly client's Program composes, in its order: the framework's own services, then every module's
            // by the convention, then each module's IClientStartup.
            var services = new ServiceCollection();
            services.AddStommeClientScopedServices();
            foreach (var step in new[] { "RegisterModuleServices", "RegisterClientStartups" })
            {
                var method = typeof(Stomme.Client.Program).GetMethod(step, BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Stomme.Client.Program.{step} was not found; this test runs the registration the WebAssembly client runs.");
                method.Invoke(null, new object[] { client, services });
            }

            var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();
            var missing = new List<string>();
            var checkedServices = 0;
            foreach (var implementation in client.GetTypes().Where(type => type.IsClass && !type.IsAbstract
                && (typeof(IClientService).IsAssignableFrom(type) || typeof(IService).IsAssignableFrom(type))))
            {
                var contract = implementation.GetInterfaces().FirstOrDefault(contract => contract.Name == "I" + implementation.Name);
                if (contract is null)
                {
                    continue; // a class with no contract of its own name is injected by its class
                }

                checkedServices++;
                if (!registered.Contains(contract))
                {
                    missing.Add($"{implementation.FullName} implements {contract.FullName}, which the browser's container cannot provide");
                }
            }

            Assert.True(checkedServices >= 20, $"Only {checkedServices} client services were checked; the scan is not reading the client.");
            Assert.True(missing.Count == 0, "Client services the WebAssembly runtime registers under their class only:\n" + string.Join("\n", missing));
        }
    }
}
