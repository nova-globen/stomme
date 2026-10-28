using System.Runtime.CompilerServices;
using Microsoft.Extensions.Localization;

[assembly: RootNamespace("Stomme")]

// BL-78 (D100 point 8): HubRoutes moved to Stomme.Shared (same namespace) so the Client can name a hub route; a
// packaged consumer compiled against Stomme.Server's copy still binds through this forward.
[assembly: TypeForwardedTo(typeof(Stomme.Transport.Realtime.HubRoutes))]
