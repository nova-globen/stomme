#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// A connector obtains the network ONLY through the contract's egress. This source
    /// gate scans every file under the Connectors family for the ways a connector could open its own socket - a client or
    /// handler of its own, a raw socket, its own DNS, a process - and, for a driver connector, requires that a file which
    /// constructs a driver connection also pins the host through <c>ResolveAndPinAsync</c>. A <c>HostOnly</c> connector that
    /// must start a process is allowlisted by file with its reason. Validated both directions.
    /// </summary>
    public sealed class ConnectorEgressOnlyTests
    {
        private static readonly Regex OwnNetwork = new(
            @"new\s+HttpClient\s*\(|HttpClientHandler|SocketsHttpHandler|new\s+TcpClient|new\s+Socket\s*\(|\bDns\s*\.|WebRequest\s*\.|Process\s*\.\s*Start|new\s+ProcessStartInfo|new\s+WebClient|new\s+UdpClient",
            RegexOptions.Compiled);

        private static readonly Regex DriverConnection = new(@"new\s+(Npgsql|Sql|MySql|Oracle|Sqlite)Connection\s*\(|new\s+(Async)?FtpClient\s*\(|new\s+LdapConnection\s*\(|new\s+LdapDirectoryIdentifier\s*\(|new\s+ImapClient\s*[\({]", RegexOptions.Compiled);

        /// <summary>Files allowed to start a process, with the reason - a HostOnly connector reaches the host itself, by contract.</summary>
        private static readonly IReadOnlyDictionary<string, string> ProcessAllowlist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Xml/XmlProcessRunner.cs"] = "HostOnly: the XML connector starts the transform process on the host; creating a connection of its type requires the Host role, which XmlConnector.HostOnly declares.",
        };

        [Fact]
        public void Every_connector_reaches_the_network_only_through_the_contracts_egress()
        {
            var root = Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "Modules", "Connectors");
            var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            Assert.True(files.Length >= 4, "vacuous: the Connectors family was not found");

            // A driver dial is judged per connector FOLDER: the LDAP client file dials the address its connector pinned in the file beside it.
            var pinnedFolders = files
                .Where(file => File.ReadAllText(file).Contains("ResolveAndPinAsync(", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var violations = new List<string>();
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var folder = Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace('\\', '/');
                foreach (var hit in Violations(File.ReadAllText(file), ProcessAllowlist.ContainsKey(relative), pinnedFolders.Contains(folder)))
                {
                    violations.Add(relative + ": " + hit);
                }
            }

            // CloudUsage fetches billing payloads, so it reaches the network only through the same egress
            // (the query carries the host's IConnectorEgress). No process, no driver: every rule applies, nothing is pinned.
            var cloudUsage = Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "Modules", "CloudUsage");
            var cloudUsageFiles = Directory.GetFiles(cloudUsage, "*.cs", SearchOption.AllDirectories);
            Assert.True(cloudUsageFiles.Length >= 4, "vacuous: the CloudUsage module was not found");
            foreach (var file in cloudUsageFiles)
            {
                foreach (var hit in Violations(File.ReadAllText(file), false, false))
                {
                    violations.Add("CloudUsage/" + Path.GetRelativePath(cloudUsage, file).Replace('\\', '/') + ": " + hit);
                }
            }

            Assert.Empty(violations);
            foreach (var entry in ProcessAllowlist.Keys)
            {
                Assert.True(File.Exists(Path.Combine(root, entry)), $"stale allowlist entry: {entry}");
            }
        }

        [Fact]
        public void The_scanner_works_in_both_directions()
        {
            Assert.Empty(Violations("using var client = context.Egress.CreateHttpClient(context.Timeout);\nvar pinned = await egress.ResolveAndPinAsync(host, port, ct);\nawait using var connection = new NpgsqlConnection(builder.ConnectionString);", false));
            Assert.Single(Violations("using var client = new HttpClient(handler);", false));
            Assert.Single(Violations("var handler = new SocketsHttpHandler();", false));
            Assert.Single(Violations("var addresses = Dns.GetHostAddresses(host);", false));
            Assert.Single(Violations("Process.Start(\"xsltproc\");", false));
            Assert.Empty(Violations("Process.Start(\"xsltproc\");", true));
            Assert.Single(Violations("await using var connection = new NpgsqlConnection(cs);", false)); // a driver dial with no pin
            Assert.Single(Violations("var client = new AsyncFtpClient(host, user, pass, port);", false));
            Assert.Single(Violations("var identifier = new LdapDirectoryIdentifier(host, port);", false));
            Assert.Single(Violations("var client = new ImapClient { Timeout = 30000 };", false)); // MailKit dials too
            Assert.Empty(Violations("var client = new AsyncFtpClient(pinned.ToString(), user, pass, port); // after egress.ResolveAndPinAsync(host, port)", false));
            Assert.Empty(Violations("var identifier = new LdapDirectoryIdentifier(settings.Address.ToString(), port);", false, pinnedInFolder: true)); // the pin in the connector beside it
        }

        internal static IReadOnlyList<string> Violations(string source, bool processAllowed, bool pinnedInFolder = false)
        {
            var hits = new List<string>();
            foreach (Match match in OwnNetwork.Matches(source))
            {
                var text = match.Value.Trim();
                if (processAllowed && (text.StartsWith("Process", StringComparison.Ordinal) || text.Contains("ProcessStartInfo", StringComparison.Ordinal)))
                {
                    continue;
                }

                hits.Add(text);
            }

            if (DriverConnection.IsMatch(source) && !pinnedInFolder && !source.Contains("ResolveAndPinAsync(", StringComparison.Ordinal))
            {
                hits.Add("a driver connection is opened without ResolveAndPinAsync");
            }

            return hits;
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
        }
    }
}
