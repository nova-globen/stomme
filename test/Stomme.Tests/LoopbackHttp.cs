#nullable enable
using System.Net;
using System.Net.Sockets;

namespace Stomme.Tests
{
    /// <summary>
    /// A stand-in's <see cref="HttpListener"/> started on a free loopback port. A port a TCP probe found free can still be
    /// refused by http.sys - "Access is denied", measured failing a fast gate twice (GoogleSheets' drive, then AwsDynamoDb's,
    /// green on the re-run each time) - or taken between the probe and the start; another port is tried then. A failed start
    /// closes its listener, so each try is a new one.
    /// </summary>
    internal static class LoopbackHttp
    {
        public static (HttpListener Listener, int Port) Start()
        {
            for (var attempt = 1; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    return (listener, port);
                }
                catch (HttpListenerException) when (attempt < 10)
                {
                    listener.Close();
                }
            }
        }
    }
}
