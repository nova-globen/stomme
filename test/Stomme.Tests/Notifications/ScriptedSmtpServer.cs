#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// An SMTP server of the few commands MailKit's client sends (BL-279): on loopback, plain (no TLS), with a throwaway
    /// login per server - EHLO advertising AUTH PLAIN LOGIN, AUTH PLAIN or LOGIN against that login (any other is refused),
    /// MAIL FROM, RCPT TO, DATA, RSET, NOOP, QUIT. Every message accepted is kept, parsed, for the test to read; a send
    /// without a login is refused. In ScriptedImapServer's pattern: no container, no package, nothing outside the test.
    /// </summary>
    internal sealed class ScriptedSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public ScriptedSmtpServer()
        {
            Username = "relay-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Password = Guid.NewGuid().ToString("N");
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string Username { get; }

        public string Password { get; }

        public ConcurrentQueue<MimeMessage> Messages { get; } = new();

        /// <summary>Waits until <paramref name="count"/> messages have arrived, or the time runs out.</summary>
        public async Task<bool> WaitForAsync(int count, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (Messages.Count < count && DateTime.UtcNow < until)
            {
                await Task.Delay(50);
            }

            return Messages.Count >= count;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            var authenticated = false;
            var inTransaction = false;
            try
            {
                await writer.WriteLineAsync("220 scripted ESMTP ready");
                while (!_stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                    {
                        return;
                    }

                    var verb = (line.Length >= 4 ? line.Substring(0, 4) : line).ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250-scripted");
                            await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "HELO":
                            await writer.WriteLineAsync("250 scripted");
                            break;
                        case "AUTH":
                            authenticated = await AuthenticateAsync(line, reader, writer);
                            await writer.WriteLineAsync(authenticated ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication failed");
                            break;
                        case "MAIL":
                            if (!authenticated)
                            {
                                await writer.WriteLineAsync("530 5.7.0 Authentication required");
                                break;
                            }

                            inTransaction = true;
                            await writer.WriteLineAsync("250 2.1.0 OK");
                            break;
                        case "RCPT":
                            await writer.WriteLineAsync(inTransaction ? "250 2.1.5 OK" : "503 5.5.1 MAIL first");
                            break;
                        case "DATA":
                            if (!inTransaction)
                            {
                                await writer.WriteLineAsync("503 5.5.1 MAIL first");
                                break;
                            }

                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                            {
                                data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine.Substring(1) : dataLine).Append("\r\n");
                            }

                            Messages.Enqueue(MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(data.ToString()))));
                            inTransaction = false;
                            await writer.WriteLineAsync("250 2.0.0 Queued");
                            break;
                        case "RSET":
                            inTransaction = false;
                            await writer.WriteLineAsync("250 2.0.0 OK");
                            break;
                        case "NOOP":
                            await writer.WriteLineAsync("250 2.0.0 OK");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 2.0.0 Bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 5.5.2 Command not implemented");
                            break;
                    }
                }
            }
            catch (IOException)
            {
                // the client went away
            }
        }

        private async Task<bool> AuthenticateAsync(string line, StreamReader reader, StreamWriter writer)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var mechanism = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
            if (mechanism == "PLAIN")
            {
                var payload = parts.Length > 2 ? parts[2] : null;
                if (payload is null)
                {
                    await writer.WriteLineAsync("334 ");
                    payload = await reader.ReadLineAsync();
                }

                var fields = Encoding.UTF8.GetString(Convert.FromBase64String(payload ?? string.Empty)).Split('\0');
                return fields.Length == 3 && fields[1] == Username && fields[2] == Password;
            }

            if (mechanism == "LOGIN")
            {
                await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                var user = Encoding.UTF8.GetString(Convert.FromBase64String(await reader.ReadLineAsync() ?? string.Empty));
                await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                var password = Encoding.UTF8.GetString(Convert.FromBase64String(await reader.ReadLineAsync() ?? string.Empty));
                return user == Username && password == Password;
            }

            return false;
        }
    }
}
