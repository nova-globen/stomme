#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The <c>IFeatureContributor</c> discovery convention, for the contribution seams that live in the host (MCP, help).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every concrete, non-generic implementation of <typeparamref name="TContract"/> with a public parameterless
    /// constructor, in a loaded assembly whose name contains <c>Stomme</c>, is registered as a singleton. A
    /// contributor WITH dependencies registers itself in its own startup; both kinds land in the same enumerable, and
    /// the seam's registry is where a duplicate is refused.
    /// </para>
    /// <para>
    /// <b>The filter is deliberately wide, and that is why no seam may treat a collision as fatal</b> (BL-105): it
    /// matches a package <c>InstallationManager</c> unpacked into <c>/bin</c> as readily as an in-tree assembly, so a
    /// fatal check would let a third party stop the host. Each registry REJECTS instead, and a reflective build gate
    /// catches our own collisions before they ship.
    /// </para>
    /// </remarks>
    public static class ConventionContributorDiscovery
    {
        public static void AddDiscovered<TContract>(IServiceCollection services)
            where TContract : class
        {
            ArgumentNullException.ThrowIfNull(services);

            foreach (var type in Discover<TContract>())
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(TContract), type));
            }
        }

        /// <summary>The types the convention finds - what the build gates sweep, so they cannot drift from what the host registers.</summary>
        public static IReadOnlyList<Type> Discover<TContract>()
            where TContract : class =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.FullName is not null && assembly.FullName.Contains("Stomme", StringComparison.OrdinalIgnoreCase))
                .SelectMany(SafeTypes)
                .Where(type => type is { IsInterface: false, IsAbstract: false, IsGenericType: false }
                               && typeof(TContract).IsAssignableFrom(type)
                               && type.GetConstructor(Type.EmptyTypes) is not null)
                .ToList();

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type is not null)!;
            }
        }
    }
}
