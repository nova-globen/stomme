#nullable enable
using System;

namespace Stomme.Connectivity
{
    /// <summary>
    /// The contract's refusal of a server that did not prove the key its connection trusts (BL-229, the SFTP connectors; D110's
    /// correction of 2026-10-17): thrown by a connector whose server offered a host key the connection does not name - or the
    /// connection names none - before anything was sent to it. The runner categorises it <c>HostKeyUntrusted</c>, whose fixed
    /// text says what to set; its message, which names the key the server offered, never reaches a page or an event (D110) -
    /// a key is trusted for what was learned of it elsewhere, never for what a server answered.
    /// </summary>
    public sealed class ConnectorHostKeyUntrustedException : Exception
    {
        public ConnectorHostKeyUntrustedException(string message)
            : base(message)
        {
        }
    }
}
