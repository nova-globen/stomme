using Stomme.Shared;

// This library is a framework building block, not a module: the host opts into it explicitly via
// AddRavenPersistence/AddRavenFrameworkStore. The marker became load-bearing the moment Stomme.Server took a
// project reference on this assembly (for the RavenDB framework stores): the convention scan filters on the
// FILE NAME containing "Stomme" (AssemblyExtensions.IsStommeAssembly), so shipping
// Stomme.Persistence.RavenDb.dll into the host's /bin subjects every type in it to
// AddStommeServices() — which would register this library's hosted services and Rebus transport in every
// host, whether or not the RavenDB provider was ever selected. See docs/decisions.md D13.
[assembly: StommeFrameworkLibrary]

namespace Stomme.Shared;

/// <summary>
/// Local declaration of the framework's convention-scan opt-out marker. The canonical (public) type lives in
/// Stomme.Shared; the framework matches the attribute by full name, so a leaf library declares its own copy
/// rather than taking a dependency on Stomme.Shared (which would drag EF Core, gRPC and NodaTime into it).
/// Keep the namespace and type name in sync with StommeFrameworkLibraryAttribute.TypeFullName.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
internal sealed class StommeFrameworkLibraryAttribute : Attribute
{
}
