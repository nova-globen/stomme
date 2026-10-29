using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
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

        /// <summary>The key the host password came from, for a message that names it: the file's when one is set.</summary>
        public static string HostPasswordSource(IConfigManager config) =>
            "Installation:" + (string.IsNullOrEmpty(config.GetSetting(SettingKeys.InstallationSection, SettingKeys.HostPasswordFileKey, ""))
                ? SettingKeys.HostPasswordKey : SettingKeys.HostPasswordFileKey);

        /// <summary>
        /// What the host password lacks under the password policy, the rules ASP.NET Identity's validator applies - or null
        /// when it meets them. Checked BEFORE anything is installed: a password the identity store refuses would otherwise
        /// leave an installation with no host account and so no administrator. Names the rules, never the password.
        /// </summary>
        public static string PolicyFailure(string password, PasswordOptions policy)
        {
            var unmet = new List<string>();
            if (password.Length < policy.RequiredLength) unmet.Add($"at least {policy.RequiredLength} characters");
            if (policy.RequireDigit && !password.Any(char.IsDigit)) unmet.Add("a digit");
            if (policy.RequireLowercase && !password.Any(char.IsLower)) unmet.Add("a lower-case letter");
            if (policy.RequireUppercase && !password.Any(char.IsUpper)) unmet.Add("an upper-case letter");
            if (policy.RequireNonAlphanumeric && password.All(char.IsLetterOrDigit)) unmet.Add("a character that is not a letter or digit");
            if (password.Distinct().Count() < policy.RequiredUniqueChars) unmet.Add($"at least {policy.RequiredUniqueChars} different characters");
            return unmet.Count == 0 ? null : "it needs " + string.Join(", ", unmet);
        }
    }
}
