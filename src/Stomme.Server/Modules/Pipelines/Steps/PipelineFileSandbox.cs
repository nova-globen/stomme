#nullable enable
using System;
using System.IO;
using System.Linq;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// Where the file steps may read and write: one folder per engine tenant (a Stomme site) under the configured
    /// root, so a pipeline on one site cannot see another site's files and none can see the host's. A relative path
    /// is resolved under the site's folder and REFUSED when it is rooted, a UNC path, or resolves outside the folder
    /// (traversal) - the predecessor platform's sandbox boundary, kept, and made per-site.
    /// </summary>
    internal static class PipelineFileSandbox
    {
        public static string SiteRoot(StepExecutionContext context) =>
            SiteRoot(context.GetService<IOptions<PipelineStepOptions>>()?.Value ?? new PipelineStepOptions(), context.GetService<ITenantAccessor>()?.TenantId);

        /// <summary>The site's folder for an engine tenant - the steps' and the Files page's one rule.</summary>
        public static string SiteRoot(PipelineStepOptions options, string? tenant)
        {
            var root = options.Files.Root;
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("Pipelines:Files:Root is not configured and the host gave the file steps no data directory.");
            }

            return Path.GetFullPath(Path.Combine(root, SiteFolder(tenant)));
        }

        /// <summary>The folder an engine tenant's files are in, under the root: its id made a name ("1/2" is "1-2"), "default" for none.</summary>
        public static string SiteFolder(string? tenant) => string.IsNullOrWhiteSpace(tenant) ? "default" : Sanitize(tenant);

        /// <summary>The Stomme tenant a site's folder belongs to (a tenant-scoped restore) - false for "default" and any other name.</summary>
        public static bool TenantOfSiteFolder(string folder, out int tenantId)
        {
            tenantId = 0;
            var separator = folder.IndexOf('-', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            var engineTenant = folder[..separator] + "/" + folder[(separator + 1)..];
            if (!Tenancy.EngineTenantIds.TryParse(engineTenant, out var parsed, out _) || SiteFolder(engineTenant) != folder)
            {
                return false;
            }

            tenantId = parsed;
            return true;
        }

        /// <summary>The full path of a relative one inside the site's folder, or null with the refusal.</summary>
        public static string? Resolve(StepExecutionContext context, string relative, out string? refusal) =>
            Resolve(SiteRoot(context), relative, out refusal);

        /// <summary>The full path of a relative one inside <paramref name="root"/>, the site's folder, or null with the refusal.</summary>
        public static string? Resolve(string root, string relative, out string? refusal)
        {
            refusal = null;
            if (string.IsNullOrWhiteSpace(relative))
            {
                refusal = "The path is empty.";
                return null;
            }

            if (Path.IsPathRooted(relative) || relative.StartsWith("\\\\", StringComparison.Ordinal) || relative.StartsWith("//", StringComparison.Ordinal) || relative.Contains(':'))
            {
                refusal = $"'{relative}' is not a relative path; file steps read and write under the site's own folder only.";
                return null;
            }

            var full = Path.GetFullPath(Path.Combine(root, relative));
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                refusal = $"'{relative}' resolves outside the site's folder.";
                return null;
            }

            return full;
        }

        private static string Sanitize(string tenant)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToHashSet();
            return new string(tenant.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        }
    }
}
