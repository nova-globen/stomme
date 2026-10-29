#nullable enable
using System;
using System.IO;

namespace Stomme.Connectivity
{
    /// <summary>
    /// The contract's own refusal: thrown by a connector whose host pinned to no permitted address
    /// (<see cref="IConnectorEgress.ResolveAndPinAsync"/> answered null) or refused a destination for a reason the
    /// connector can state without the host's types. The host's guard recognises it beside its own denial, so the
    /// runner categorises it <c>DeniedByPolicy</c> and its message never reaches a page or an event.
    /// </summary>
    public sealed class ConnectorEgressDeniedException : IOException
    {
        public ConnectorEgressDeniedException(string message)
            : base(message)
        {
        }
    }
}
