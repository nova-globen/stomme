#nullable enable
using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Stomme.Modules.Connectors.ActiveDirectory
{
    /// <summary>The LDAP search scope for a directory query.</summary>
    internal enum AdSearchScope
    {
        Base,
        OneLevel,
        Subtree,
    }

    /// <summary>One directory entry: its distinguished name plus each returned attribute's (possibly multi-) values.</summary>
    internal sealed record AdEntry(string DistinguishedName, IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes);

    /// <summary>
    /// The resolved parameters for one LDAP session: the directory HOST as named (what the certificate must name and what
    /// every row's <c>ADTarget</c> says), the ADDRESS the host pinned for it (what the socket dials), the port, LDAPS, the
    /// bind credential (null username = anonymous), the timeout.
    /// </summary>
    internal sealed record ActiveDirectoryConnection(string Host, IPAddress Address, int Port, bool UseSsl, string? Username, string? Password, TimeSpan Timeout);

    /// <summary>The testability seam over an LDAP directory: the real one binds and searches through System.DirectoryServices.Protocols; a test substitutes canned entries.</summary>
    internal interface IActiveDirectoryClient : IDisposable
    {
        IReadOnlyList<string> ReadRootDse(string attribute);

        IEnumerable<AdEntry> Search(string baseDn, string filter, AdSearchScope scope, IReadOnlyList<string> attributes, int pageSize, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The real LDAP client. It dials the PINNED address, never the name; with LDAPS the server's certificate is verified
    /// by chain and checked against the ORIGINAL host name (the pin moved the socket, not whom the certificate names).
    /// The API is synchronous by nature (a bind, a request) and is called as such from the connector's iterator.
    /// </summary>
    internal sealed class SystemDirectoryServicesClient : IActiveDirectoryClient
    {
        private readonly LdapConnection connection;

        public SystemDirectoryServicesClient(ActiveDirectoryConnection settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var identifier = new LdapDirectoryIdentifier(settings.Address.ToString(), settings.Port);
            connection = new LdapConnection(identifier)
            {
                Timeout = settings.Timeout,
                AuthType = string.IsNullOrWhiteSpace(settings.Username) ? AuthType.Anonymous : AuthType.Basic,
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.SecureSocketLayer = settings.UseSsl;
            if (settings.UseSsl)
            {
                var host = settings.Host;
                connection.SessionOptions.VerifyServerCertificate = (_, certificate) => Verifies(certificate, host);
            }

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                connection.Credential = new NetworkCredential(settings.Username, settings.Password);
            }

            connection.Bind();
        }

        public IReadOnlyList<string> ReadRootDse(string attribute)
        {
            var request = new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base, attribute);
            var response = (SearchResponse)connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                if (entry.Attributes.Contains(attribute))
                {
                    return ReadValues(entry.Attributes[attribute]);
                }
            }

            return Array.Empty<string>();
        }

        public IEnumerable<AdEntry> Search(string baseDn, string filter, AdSearchScope scope, IReadOnlyList<string> attributes, int pageSize, CancellationToken cancellationToken)
        {
            var pageControl = new PageResultRequestControl(pageSize <= 0 ? 512 : pageSize);
            var request = new SearchRequest(baseDn, filter, Map(scope), attributes.ToArray());
            request.Controls.Add(pageControl);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = (SearchResponse)connection.SendRequest(request);
                foreach (SearchResultEntry entry in response.Entries)
                {
                    yield return Map(entry);
                }

                var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (pageResponse is null || pageResponse.Cookie.Length == 0)
                {
                    yield break;
                }

                pageControl.Cookie = pageResponse.Cookie;
            }
        }

        public void Dispose() => connection.Dispose();

        /// <summary>The chain must build and the certificate must name the ORIGINAL host.</summary>
        internal static bool Verifies(X509Certificate certificate, string host)
        {
            using var leaf = new X509Certificate2(certificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(leaf) && leaf.MatchesHostname(host);
        }

        private static SearchScope Map(AdSearchScope scope) => scope switch
        {
            AdSearchScope.Base => SearchScope.Base,
            AdSearchScope.OneLevel => SearchScope.OneLevel,
            _ => SearchScope.Subtree,
        };

        private static AdEntry Map(SearchResultEntry entry)
        {
            var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryAttribute attribute in entry.Attributes.Values)
            {
                attributes[attribute.Name] = ReadValues(attribute);
            }

            return new AdEntry(entry.DistinguishedName, attributes);
        }

        private static IReadOnlyList<string> ReadValues(DirectoryAttribute attribute)
        {
            var values = new List<string>(attribute.Count);
            for (var i = 0; i < attribute.Count; i++)
            {
                values.Add(attribute[i] switch
                {
                    string s => s,
                    byte[] b => Convert.ToBase64String(b),
                    { } other => other.ToString() ?? string.Empty,
                    _ => string.Empty,
                });
            }

            return values;
        }
    }
}
