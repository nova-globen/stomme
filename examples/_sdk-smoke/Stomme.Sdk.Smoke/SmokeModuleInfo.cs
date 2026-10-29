using Stomme.Models;
using Stomme.Modules;

namespace Stomme.Sdk.Smoke;

/// <summary>
/// The module manifest (<see cref="IModule"/> from the <c>Stomme.Client</c> package). The host's
/// convention scan discovers it by reflection at startup; here it only has to COMPILE against the package
/// to prove <see cref="ModuleDefinition"/> and <see cref="IModule"/> are reachable from the SDK.
/// </summary>
public sealed class SmokeModuleInfo : IModule
{
    public ModuleDefinition ModuleDefinition => new ModuleDefinition
    {
        Name = "SdkSmoke",
        Description = "SDK independence smoke module",
        Version = "1.0.0",
        ReleaseVersions = "1.0.0",
        PackageName = "Stomme.Sdk.Smoke"
    };
}
