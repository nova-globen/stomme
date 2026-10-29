using Stomme.Shared;

// This library is a framework building block, not a module: the host opts into it explicitly via
// AddDomainEvents/AddOutbox. Without this marker the framework's module convention scan would register
// OutboxBackgroundService as a hosted service in every host, whether or not AddOutbox was ever called —
// and OutboxProcessor, which it depends on, is only registered by AddOutbox.
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
