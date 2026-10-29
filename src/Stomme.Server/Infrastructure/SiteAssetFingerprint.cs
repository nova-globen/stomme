#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Stomme.Shared;
using Microsoft.Extensions.FileProviders;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The <c>?v=</c> on the site's own static assets: a hash of the CONTENT of the files it is put on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was the hash of <c>InstallationDate</c>, and a later fallback used the framework version when that was empty. It is
    /// never empty in a container: nothing there persists <c>InstallationVersion</c>, so <c>ConfigManager.EnsureInstallationId</c>
    /// sees a version change at EVERY boot and writes <c>InstallationDate</c> = the boot minute - one fingerprint went
    /// <c>F8427461</c> to <c>F84FA39D</c> (the hash of <c>202610050055</c>) across one restart of 10.7.0, so every browser and
    /// the CDN fetched the files again. And neither the date nor the version follows an app built from the template that
    /// changes its own <c>app.css</c> without a framework release - the stale direction, the one that hurts.
    /// </para>
    /// <para>
    /// The content is the same on every boot of one build and changes exactly when a file does. The framework version is the
    /// answer only when none of the files can be read.
    /// </para>
    /// </remarks>
    public static class SiteAssetFingerprint
    {
        /// <summary>The files <c>App.razor</c> puts the fingerprint on, relative to the web root.</summary>
        internal static readonly IReadOnlyList<string> Files = new[] { "css/app.css", "js/app.js", "js/loadjs.min.js", "js/interop.js", "js/reload.js" };

        // Keyed by what was found - each file's location, length and last write - so a file changed while the host runs is
        // noticed, and one host's answer is never another's: a test run composes many hosts in one process.
        private static readonly ConcurrentDictionary<string, string> Computed = new(StringComparer.Ordinal);

        public static string Of(IFileProvider? webRoot)
        {
            var found = new List<IFileInfo>();
            var key = new StringBuilder();
            if (webRoot is not null)
            {
                foreach (var path in Files)
                {
                    var file = webRoot.GetFileInfo(path);
                    if (!file.Exists || file.IsDirectory)
                    {
                        continue;
                    }

                    found.Add(file);
                    key.Append(file.PhysicalPath ?? path).Append('|').Append(file.Length).Append('|').Append(file.LastModified.UtcTicks).Append(';');
                }
            }

            return found.Count == 0
                ? Utilities.GenerateSimpleHash(Constants.Version)
                : Computed.GetOrAdd(key.ToString(), _ => HashOf(found));
        }

        private static string HashOf(List<IFileInfo> files)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            foreach (var file in files)
            {
                using var stream = file.CreateReadStream();
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }

            // Eight hex digits, the shape the ?v= has always had.
            return Convert.ToHexString(hash.GetHashAndReset(), 0, 4);
        }
    }
}
