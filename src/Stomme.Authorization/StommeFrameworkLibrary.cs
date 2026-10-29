// Stomme.Authorization is a framework building block, NOT a module: the host opts into it explicitly
// (AddStommeAuthorization registers IAccessGuard / ICurrentUser / IFeatureEvaluator and the outermost
// AuthorizationBehavior). The module-convention reflection scan must skip it, exactly as it skips
// Stomme.Mediator.

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
