// Stomme.Security.Abstractions holds the PORTS of the authorization bridge ([Requires], IAccessGuard,
// ICurrentUser, IFeatureEvaluator) - a framework building block, NOT a module: the host composes the
// implementations explicitly (AddStommeAuthorization). The module-convention reflection scan must skip it,
// exactly as it skips Stomme.Mediator and Stomme.Authorization (docs/decisions.md D13). Today the
// assembly declares no concrete type the scan could register; the marker is what keeps that true when one is
// added, and it is what the Server csproj comment beside this reference asserts.

// The canonical (public) marker type lives in Stomme.Shared; the framework matches the attribute by full
// name, so this leaf library declares its own internal copy rather than referencing Stomme.Shared (which
// would drag EF Core, gRPC and NodaTime into it). Keep the namespace and type name in sync with
// StommeFrameworkLibraryAttribute.TypeFullName.
[assembly: Stomme.Shared.StommeFrameworkLibrary]

namespace Stomme.Shared;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
internal sealed class StommeFrameworkLibraryAttribute : Attribute
{
}