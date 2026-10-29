namespace Stomme.FeatureManagement.Application;

// These three types are COMPILED INTO Stomme.Security.Abstractions and keep the namespace they were born in.
// A module declares its flags through them, and a module built on the slim SDK
// (examples/Stomme.Sdk.Slim) could not: they lived in Stomme.FeatureManagement, whose package carries EF Core and
// Microsoft.FeatureManagement - the closure the slim SDK exists to keep out of a module's restore. Their neighbour
// IFeatureEvaluator already lives here for the same reason ([Requires] gates on a flag). Stomme.FeatureManagement
// FORWARDS all three, so a package compiled against their old home still binds; the namespace is unchanged, so no source
// that uses them changed either.

/// <summary>
/// A declared feature flag: its stable key plus the metadata that makes it discoverable (a human
/// description and optional category) and its defaults (initial enabled state and any gating on a licence
/// entitlement or another flag). Declaring a flag does not create per-tenant state — it makes the flag
/// known to the catalog so it can be surfaced and seeded.
/// </summary>
public sealed record FeatureDescriptor(
    string Key,
    string Description,
    string? Category = null,
    bool DefaultEnabled = false,
    string? RequiredEntitlement = null,
    string? RequiredFlag = null);

/// <summary>
/// A module contributes the feature flags it owns so they are discoverable before any tenant has toggled
/// them. The platform unions all contributors into the feature catalog. Stomme discovers every
/// <see cref="IFeatureContributor"/> with a public parameterless constructor across the loaded
/// <c>Stomme.*</c> assemblies — the same reflection seam that discovers <c>IServerStartup</c>. A module
/// that needs constructor state can instead register its contributor explicitly in its startup:
/// <c>services.AddSingleton&lt;IFeatureContributor&gt;(new FeatureContributor(MyFeatures.All));</c>
/// </summary>
public interface IFeatureContributor
{
    IReadOnlyCollection<FeatureDescriptor> Features { get; }
}

/// <summary>A simple <see cref="IFeatureContributor"/> over a fixed set of descriptors.</summary>
public sealed class FeatureContributor(IReadOnlyCollection<FeatureDescriptor> features) : IFeatureContributor
{
    public IReadOnlyCollection<FeatureDescriptor> Features { get; } = features;
}
