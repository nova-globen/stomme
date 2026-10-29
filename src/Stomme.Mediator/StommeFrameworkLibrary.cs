// Stomme.Mediator is a framework building block, NOT a module: the host opts into it explicitly via
// AddMediator/AddMediatorBehavior (called from a module's IServerStartup — the Guestbook sample module
// does this). The module-convention reflection scan must therefore SKIP this assembly, exactly as it skips
// Stomme.Persistence and Stomme.FeatureManagement. Without this marker the scan would reflect over the
// mediator's abstractions and behaviors looking for IService/IServerStartup/IHostedService and register
// nothing useful while the open-generic behaviors are meant to be wired only through AddMediatorBehavior.

// The canonical (public) marker type lives in Stomme.Shared; the framework matches the attribute by full
// name, so this leaf library declares its own internal copy rather than referencing Stomme.Shared (which
// would drag EF Core, gRPC and NodaTime into the mediator). Keep the namespace and type name in sync with
// StommeFrameworkLibraryAttribute.TypeFullName.
[assembly: Stomme.Shared.StommeFrameworkLibrary]

namespace Stomme.Shared;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
internal sealed class StommeFrameworkLibraryAttribute : Attribute
{
}
