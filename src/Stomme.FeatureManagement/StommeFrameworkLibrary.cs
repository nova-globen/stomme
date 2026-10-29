using Stomme.Shared;

// This library is a framework building block, not a module: the host opts into it explicitly via
// AddFeatureManagement (called from the FeatureManagement module's ServerStartup), which registers
// FeatureSeeder itself. The module convention scan must not register FeatureSeeder independently of that
// call — the seeder depends on the store AddFeatureManagement wires.
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
