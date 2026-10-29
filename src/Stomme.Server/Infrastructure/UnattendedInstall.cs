using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The unattended install, driven by the <c>Installation</c> section alone: whether one is configured, and the host
    /// account's password, which may come from a file (a container secret) rather than from the configuration itself.
    /// </summary>
    internal static class UnattendedInstall
    {
        // Any one of these says the operator intends an unattended install. The browser wizard is then refused, page and API
        // alike, so a stranger who reaches a public host whose unattended install has not completed cannot claim it.
        private static readonly string[] IntentKeys =
        {
            SettingKeys.DefaultAliasKey, SettingKeys.HostEmailKey, SettingKeys.HostPasswordKey, SettingKeys.HostPasswordFileKey
        };

        public static bool IsConfigured(IConfigManager config) =>
            IntentKeys.Any(key => !string.IsNullOrEmpty(config.GetSetting(SettingKeys.InstallationSection, key, "")));

        /// <summary>
        /// The host password: <c>Installation:HostPassword</c>, or the contents of the file <c>Installation:HostPasswordFile</c>
        /// names, with its trailing line break removed. Setting both is refused, and so is a file that is missing or empty;
        /// the error names the key and the path, never a password.
        /// </summary>
        public static async Task<(string Password, string Error)> ReadHostPasswordAsync(IConfigManager config)
        {
            var password = config.GetSetting(SettingKeys.InstallationSection, SettingKeys.HostPasswordKey, "");
            var file = config.GetSetting(SettingKeys.InstallationSection, SettingKeys.HostPasswordFileKey, "");
            if (string.IsNullOrEmpty(file))
            {
                return (password, null);
            }
            if (!string.IsNullOrEmpty(password))
            {
                return ("", "Installation:HostPassword and Installation:HostPasswordFile are both set; set one.");
            }
            if (!File.Exists(file))
            {
                return ("", $"Installation:HostPasswordFile names a file that does not exist: {file}");
            }

            var text = (await File.ReadAllTextAsync(file)).TrimEnd('\r', '\n');
            if (text.Length == 0)
            {
                return ("", $"Installation:HostPasswordFile names an empty file: {file}");
            }
            return (text, null);
        }
    }
}
