#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// An IMAP4rev1 server of the few commands a client reading a mailbox sends (BL-229, for the IMAP connector and the "Email
    /// received" trigger): on loopback, plain (no TLS), one mailbox - INBOX - with messages added by a test, each with its UID.
    /// LOGIN against one username and password; LIST of INBOX alone (any other name is not found); EXAMINE and SELECT saying
    /// UIDVALIDITY and UIDNEXT; UID SEARCH by a UID range - "n:*" holding the highest UID even when it is not above n, as RFC
    /// 3501 says and real servers do; UID FETCH of a message's size and its whole body; UID STORE of \Seen; LOGOUT. Every
    /// command line is kept, a login's password blanked, so a test can say what was - and was not - sent.
    /// </summary>
    internal sealed class ScriptedImapServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly List<(uint Uid, byte[] Raw)> _messages = new();
        private readonly HashSet<uint> _seen = new();
        private readonly string _username;
        private readonly string _password;
        private uint _next = 1;

        public ScriptedImapServer(string username, string password)
        {
            _username = username;
            _password = password;
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public uint UidValidity { get; set; } = 7;

        public ConcurrentQueue<string> Commands { get; } = new();

        public int Logins { get; private set; }

        /// <summary>Adds a message to INBOX; its UID.</summary>
        public uint Add(MimeMessage message)
        {
            using var raw = new MemoryStream();
            message.WriteTo(raw);
            lock (_gate)
            {
                var uid = _next++;
                _messages.Add((uid, raw.ToArray()));
                return uid;
            }
        }

        public bool IsSeen(uint uid)
        {
            lock (_gate)
            {
                return _seen.Contains(uid);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
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
                catch (Exception)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    async Task Send(string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
                    await Send("* OK [CAPABILITY IMAP4rev1] Scripted IMAP ready\r\n");
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        var parts = line.Split(' ', 3);
                        var tag = parts[0];
                        var command = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
                        var rest = parts.Length > 2 ? parts[2] : "";
                        if (command == "UID" && rest.Split(' ', 2) is { Length: 2 } uid)
                        {
                            command = "UID " + uid[0].ToUpperInvariant();
                            rest = uid[1];
                        }

                        Commands.Enqueue(command == "LOGIN" ? tag + " LOGIN " + Tokens(rest).FirstOrDefault() + " (password)" : line);
                        switch (command)
                        {
                            case "CAPABILITY":
                                await Send($"* CAPABILITY IMAP4rev1\r\n{tag} OK CAPABILITY completed\r\n");
                                break;
                            case "LOGIN":
                                var credentials = Tokens(rest);
                                if (credentials.Count == 2 && credentials[0] == _username && credentials[1] == _password)
                                {
                                    Logins++;
                                    await Send($"{tag} OK [CAPABILITY IMAP4rev1] LOGIN completed\r\n");
                                }
                                else
                                {
                                    await Send($"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials\r\n");
                                }

                                break;
                            case "LIST":
                                var names = Tokens(rest);
                                var pattern = names.Count == 2 ? names[1] : "";
                                if (pattern.Length == 0)
                                {
                                    await Send("* LIST (\\Noselect) \"/\" \"\"\r\n");
                                }
                                else if (string.Equals(pattern, "INBOX", StringComparison.OrdinalIgnoreCase) || pattern is "*" or "%")
                                {
                                    await Send("* LIST (\\HasNoChildren) \"/\" INBOX\r\n");
                                }

                                await Send($"{tag} OK LIST completed\r\n");
                                break;
                            case "EXAMINE" or "SELECT":
                                int count;
                                uint next;
                                lock (_gate)
                                {
                                    count = _messages.Count;
                                    next = _next;
                                }

                                await Send($"* FLAGS (\\Seen)\r\n* {count} EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY {UidValidity}] UIDs valid\r\n* OK [UIDNEXT {next}] Predicted next UID\r\n"
                                    + $"* OK [PERMANENTFLAGS (\\Seen)] Limited\r\n{tag} OK [{(command == "SELECT" ? "READ-WRITE" : "READ-ONLY")}] {command} completed\r\n");
                                break;
                            case "UID SEARCH":
                                await Send("* SEARCH" + string.Concat(Matching(rest.Replace("UID ", "", StringComparison.OrdinalIgnoreCase).Trim()).Select(found => " " + found.Uid)) + $"\r\n{tag} OK SEARCH completed\r\n");
                                break;
                            case "UID FETCH":
                                var set = rest.Split(' ', 2);
                                foreach (var message in Matching(set[0]))
                                {
                                    var at = Sequence(message.Uid);
                                    if (set[1].Contains("BODY", StringComparison.OrdinalIgnoreCase))
                                    {
                                        await Send($"* {at} FETCH (UID {message.Uid} BODY[] {{{message.Raw.Length}}}\r\n");
                                        await stream.WriteAsync(message.Raw);
                                        await Send(")\r\n");
                                    }
                                    else
                                    {
                                        await Send($"* {at} FETCH (UID {message.Uid} RFC822.SIZE {message.Raw.Length})\r\n");
                                    }
                                }

                                await Send($"{tag} OK FETCH completed\r\n");
                                break;
                            case "UID STORE":
                                foreach (var message in Matching(rest.Split(' ', 2)[0]))
                                {
                                    lock (_gate)
                                    {
                                        _seen.Add(message.Uid);
                                    }
                                }

                                await Send($"{tag} OK STORE completed\r\n");
                                break;
                            case "LOGOUT":
                                await Send($"* BYE Logging out\r\n{tag} OK LOGOUT completed\r\n");
                                return;
                            case "NOOP" or "CHECK" or "CLOSE" or "UNSELECT" or "ENABLE":
                                await Send($"{tag} OK {command} completed\r\n");
                                break;
                            default:
                                await Send($"{tag} BAD {command} is not a command this server knows\r\n");
                                break;
                        }
                    }
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                    // the client went away
                }
            }
        }

        // The messages a UID set names: "4", "4,6", "4:6", "4:*" - and "n:*" holds the highest UID even when below n (RFC 3501).
        private List<(uint Uid, byte[] Raw)> Matching(string set)
        {
            lock (_gate)
            {
                var found = new List<(uint Uid, byte[] Raw)>();
                foreach (var part in set.Split(','))
                {
                    var range = part.Split(':');
                    var highest = _messages.Count == 0 ? 0 : _messages.Max(message => message.Uid);
                    uint Bound(string text) => text == "*" ? highest : uint.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                    var low = Bound(range[0]);
                    var high = range.Length > 1 ? Bound(range[1]) : low;
                    if (low > high)
                    {
                        (low, high) = (high, low);
                    }

                    found.AddRange(_messages.Where(message => message.Uid >= low && message.Uid <= high));
                }

                return found.DistinctBy(message => message.Uid).OrderBy(message => message.Uid).ToList();
            }
        }

        private int Sequence(uint uid)
        {
            lock (_gate)
            {
                return _messages.FindIndex(message => message.Uid == uid) + 1;
            }
        }

        // An IMAP argument list: atoms and "quoted strings", the quotes and escapes taken off.
        private static List<string> Tokens(string text)
        {
            var tokens = new List<string>();
            var at = 0;
            while (at < text.Length)
            {
                if (text[at] == ' ')
                {
                    at++;
                    continue;
                }

                var token = new StringBuilder();
                if (text[at] == '"')
                {
                    at++;
                    while (at < text.Length && text[at] != '"')
                    {
                        if (text[at] == '\\' && at + 1 < text.Length)
                        {
                            at++;
                        }

                        token.Append(text[at++]);
                    }

                    at++;
                }
                else
                {
                    while (at < text.Length && text[at] != ' ')
                    {
                        token.Append(text[at++]);
                    }
                }

                tokens.Add(token.ToString());
            }

            return tokens;
        }
    }
}
