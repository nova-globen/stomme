using System;
using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Portability
{
    /// <summary>
    /// The portability permission keys, declared per key: four for Administrators, and
    /// <see cref="ExportOwn"/> for Registered Users - a person may ask for their OWN data without anyone's help.
    /// </summary>
    public static class PortabilityPermissions
    {
        /// <summary>The key resource - the Permission row's <c>EntityName</c>.</summary>
        public const string Resource = "portability";

        /// <summary>See every package on the site.</summary>
        public const string View = "portability:view";

        /// <summary>Build a configuration package.</summary>
        public const string Export = "portability:export";

        /// <summary>Import a configuration package.</summary>
        public const string Import = "portability:import";

        /// <summary>Build and download any person's data package on the site (a subject-access request).</summary>
        public const string SubjectData = "portability:subject-data";

        /// <summary>Build and download one's OWN data package.</summary>
        public const string ExportOwn = "portability:export-own";

        public static readonly IReadOnlyList<string> All = new[] { View, Export, Import, SubjectData, ExportOwn };

        /// <summary>
        /// Every key with its DEFAULT roles; <c>PermissionKeys.Declare</c> appends Administrators to each.
        /// </summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, Array.Empty<string>()),
            (Export, Array.Empty<string>()),
            (Import, Array.Empty<string>()),
            (SubjectData, Array.Empty<string>()),
            (ExportOwn, new[] { RoleNames.Registered }),
        };
    }

    /// <summary>One package as a list shows it.</summary>
    public sealed class PortabilityPackageView
    {
        public Guid Id { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int RequestedByUserId { get; set; }

        public int? SubjectUserId { get; set; }

        public int EntityCount { get; set; }

        public string BundleFormat { get; set; } = string.Empty;

        public string FailureText { get; set; } = string.Empty;

        public DateTime CreatedOnUtc { get; set; }

        public DateTime? ReadyOnUtc { get; set; }

        public DateTime? ExpiresOnUtc { get; set; }

        /// <summary>Whether THIS caller may download it now.</summary>
        public bool CanDownload { get; set; }
    }

    /// <summary>What an import did - or, when <see cref="Accepted"/> is false, every reason nothing was done.</summary>
    public sealed class PortabilityImportResult
    {
        public bool Accepted { get; set; }

        public List<string> Refusals { get; set; } = new();

        public List<PortabilityImportLine> Lines { get; set; } = new();
    }

    public sealed class PortabilityImportLine
    {
        public string EntityType { get; set; } = string.Empty;

        public string EntityId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Result { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>A configuration build: every entity of each named type on the site.</summary>
    public sealed class PortabilityConfigRequest
    {
        public List<string> EntityTypes { get; set; } = new();
    }

    /// <summary>A subject-access request, by the person's user name on this site.</summary>
    public sealed class PortabilitySubjectRequest
    {
        public string Username { get; set; } = string.Empty;
    }
}
