using Stomme.Shared;

// This library is a framework building block, not a module: the host opts into it explicitly via
// AddMessaging (gated on Messaging:Enabled) and AddBusSubscription<T>. Without this marker the framework's
// module convention scan would register BusTransportFlusher and BusSubscriptionInitializer as hosted services
// in every host regardless of that gate, while their dependencies (BusTransportBuffer, Rebus' IBus) are only
// registered by the gated extension. See docs/decisions.md D13.
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
