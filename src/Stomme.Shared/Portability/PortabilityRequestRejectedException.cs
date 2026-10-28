using System;

namespace Stomme.Portability
{
    /// <summary>
    /// A portability request the server refused for a reason written for the person who made it (no such person on this
    /// site, nothing to export, only a host may request a host's data). Shared so the Client can show that reason where the
    /// call runs in-process (an Interactive Server circuit); over HTTP the controller turns it into a 400 with the same text.
    /// </summary>
    public sealed class PortabilityRequestRejectedException(string message) : Exception(message);
}