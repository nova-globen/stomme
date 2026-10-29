#nullable enable
using System;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The <c>?v=</c> on a module's or a theme's static resources (its scripts and stylesheets, put on by <c>SiteRouter</c>).
    /// </summary>
    /// <remarks>
    /// It was the hash of the definition row's <c>ModifiedOn</c> alone, and a built-in module's row never changes: its declared
    /// version is fixed (Pipelines' is 1.0.0), so nothing rewrites the row on an upgrade. The pipeline editor's script and
    /// stylesheet kept ONE fingerprint across releases - measured, <c>pipelines-editor.js?v=70298F2B</c> served
    /// by the CDN from its cache (no <c>Cache-Control</c> from the origin, so the edge's default lifetime) - so after a release
    /// a browser or the edge could keep the previous release's editor for hours. The framework version and the declared version
    /// are now in it: every release, and every upgrade of an installed package, is a new URL; one build's boots all give the
    /// same one; a Save on the admin page (which stamps <c>ModifiedOn</c>) still busts it by hand. The site's own files carry the
    /// hash of their content instead (<see cref="SiteAssetFingerprint"/>).
    /// </remarks>
    public static class ResourceFingerprint
    {
        public static string Of(DateTime modifiedOn, string? declaredVersion, string? frameworkVersion = null) =>
            Utilities.GenerateSimpleHash(modifiedOn.ToString("yyyyMMddHHmm") + "|" + (frameworkVersion ?? Constants.Version) + "|" + (declaredVersion ?? ""));
    }
}
