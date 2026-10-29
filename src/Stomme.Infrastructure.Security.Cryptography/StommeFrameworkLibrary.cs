// Stomme.Infrastructure.Security.Cryptography is a framework building block, NOT a module: the host composes it
// explicitly (AddSecretProtection in AddStomme, for the shared per-site secret store). The module-convention
// reflection scan must skip it; HostCompositionTests asserts the marker.

// The canonical (public) marker type lives in Stomme.Shared; the framework matches the attribute by full name, so
// this leaf library declares its own internal copy rather than referencing Stomme.Shared. Keep the namespace and
// type name in sync with StommeFrameworkLibraryAttribute.TypeFullName.
[assembly: Stomme.Shared.StommeFrameworkLibrary]

namespace Stomme.Shared;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
internal sealed class StommeFrameworkLibraryAttribute : Attribute
{
}