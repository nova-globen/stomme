#nullable enable
using System;
using Microsoft.Extensions.Configuration;

namespace Stomme.Infrastructure.Licensing
{
    /// <summary>The two deployment shapes.</summary>
    internal enum DeploymentModeKind
    {
        /// <summary>The default: nothing licensing-related is registered and a stock install is unchanged.</summary>
        Hosted,

        /// <summary>A restricted on-premises install: the offline licence ring is composed.</summary>
        OnPrem
    }

    /// <summary>
    /// Reads <c>Deployment:Mode</c>. Absent, empty or whitespace is <see cref="DeploymentModeKind.Hosted"/>;
    /// <c>Hosted</c> and <c>OnPrem</c> match case-insensitively; ANY other value is a loud failure naming the key, because
    /// a typo that silently fell back to Hosted would switch the licence ring off without a word.
    /// </summary>
    internal static class DeploymentMode
    {
        public const string ConfigKey = "Deployment:Mode";

        public static DeploymentModeKind Read(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var raw = configuration[ConfigKey];
            if (string.IsNullOrWhiteSpace(raw))
            {
                return DeploymentModeKind.Hosted;
            }

            var value = raw.Trim();
            if (string.Equals(value, nameof(DeploymentModeKind.Hosted), StringComparison.OrdinalIgnoreCase))
            {
                return DeploymentModeKind.Hosted;
            }

            if (string.Equals(value, nameof(DeploymentModeKind.OnPrem), StringComparison.OrdinalIgnoreCase))
            {
                return DeploymentModeKind.OnPrem;
            }

            throw new InvalidOperationException(
                $"Configuration value '{ConfigKey}' is '{raw}', which is not a valid deployment mode. Valid values are " +
                $"'{nameof(DeploymentModeKind.Hosted)}' (the default when the key is absent or empty) and " +
                $"'{nameof(DeploymentModeKind.OnPrem)}', matched case-insensitively.");
        }

        public static bool IsOnPrem(IConfiguration configuration) => Read(configuration) == DeploymentModeKind.OnPrem;
    }
}