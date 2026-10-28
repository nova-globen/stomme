using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Shared;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// BL-248 - where the Data Protection key ring is kept. The framework set no location, so the keys went to ASP.NET's
    /// default for the process's user, and a deployment that mounted only the installation's data lost them on every
    /// redeploy: every visitor signed out, and every secret the site had stored - connection credentials, site secrets - read
    /// as Unreadable for good. The choice, in this order:
    /// <list type="number">
    /// <item><c>DataProtection:KeysPath</c>, when the deployment names one;</item>
    /// <item>ASP.NET's default location when keys are already there - an existing installation, or a deployment that mounts
    /// that folder (UAT and production do) - so nothing it protected becomes unreadable;</item>
    /// <item>otherwise <c>DataProtection-Keys</c> beside the installation's override file (the folder
    /// <c>STOMME_APPSETTINGS_PATH</c> names), or in the host's data directory - where a container already keeps the
    /// installation.</item>
    /// </list>
    /// The application name, which isolates one app's protected data from another's, is left as ASP.NET derives it unless
    /// <c>DataProtection:ApplicationName</c> sets it: changing it makes everything protected before unreadable.
    /// </summary>
    public static class DataProtectionKeyRing
    {
        public const string KeysPathKey = "DataProtection:KeysPath";
        public const string ApplicationNameKey = "DataProtection:ApplicationName";

        /// <summary>The folder to persist the key ring in, or null to leave ASP.NET's default.</summary>
        public static string Choose(IConfiguration configuration, string defaultKeysFolder, string installationFolder)
        {
            var named = configuration[KeysPathKey];
            if (!string.IsNullOrWhiteSpace(named))
            {
                return named;
            }

            if (!string.IsNullOrEmpty(defaultKeysFolder) && Directory.Exists(defaultKeysFolder) && Directory.EnumerateFiles(defaultKeysFolder, "key-*.xml").Any())
            {
                return null;
            }

            return string.IsNullOrEmpty(installationFolder) ? null : Path.Combine(installationFolder, "DataProtection-Keys");
        }

        /// <summary>ASP.NET Core's own default folder for this user (its DefaultKeyStorageDirectories), or empty.</summary>
        public static string AspNetDefaultFolder()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrEmpty(local) ? string.Empty : Path.Combine(local, "ASP.NET", "DataProtection-Keys");
            }

            var home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(home) ? string.Empty : Path.Combine(home, ".aspnet", "DataProtection-Keys");
        }

        public static void Configure(IServiceCollection services, IConfiguration configuration, string hostDataDirectory)
        {
            var overrideFolder = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            var installation = !string.IsNullOrWhiteSpace(overrideFolder) ? overrideFolder : hostDataDirectory;
            var builder = services.AddDataProtection();
            var folder = Choose(configuration, AspNetDefaultFolder(), installation);
            if (folder != null)
            {
                Directory.CreateDirectory(folder);
                builder.PersistKeysToFileSystem(new DirectoryInfo(folder));
            }

            var name = configuration[ApplicationNameKey];
            if (!string.IsNullOrWhiteSpace(name))
            {
                builder.SetApplicationName(name);
            }
        }
    }
}
