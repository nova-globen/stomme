using System.Runtime.CompilerServices;
using Stomme.FeatureManagement.Application;

// The feature-contributor contract moved to Stomme.Security.Abstractions so that a
// module on the slim SDK can declare a flag without this package's EF Core closure. A module package compiled against
// its OLD home names this assembly; these forwards send the runtime to the new one, so such a package still binds.
[assembly: TypeForwardedTo(typeof(FeatureDescriptor))]
[assembly: TypeForwardedTo(typeof(IFeatureContributor))]
[assembly: TypeForwardedTo(typeof(FeatureContributor))]
