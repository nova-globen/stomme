using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Documentation;

namespace Stomme.Models
{
    /// <summary>
    /// Describes a Module type (Definition) in Stomme.
    /// The available Modules are determined at StartUp.
    /// </summary>
    public class ModuleDefinition : ModelBase
    {
        [PrivateApi("The constructor is probably just for internal use and shouldn't appear in the docs")]
        public ModuleDefinition()
        {
            Name = "";
            Description = "";
            Categories = "";
            Version = "";
            Owner = "";
            Url = "";
            Contact = "";
            License = "";
            Dependencies = "";
            PermissionNames = "";
            ServerManagerType = "";
            ControlTypeRoutes = "";
            ReleaseVersions = "";
            DefaultAction = "";
            SettingsType = "";
            ConfigSettingsType = "";
            PackageName = "";
            Runtimes = "";
            Databases = "";
            Transports = "";
            Template = "";
            Resources = null;
            IsAutoEnabled = true;
            PageTemplates = null;
        }

        /// <summary>
        /// Reference to the <see cref="ModuleDefinition"/>.
        /// </summary>
        public int ModuleDefinitionId { get; set; }

        /// <summary>
        /// Name of the <see cref="ModuleDefinition"/>
        /// </summary>
        public string ModuleDefinitionName { get; set; }

        /// <summary>
        /// Friendly name to show in UI
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Module description for admin dialogs.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Categories this Module will be shown in (in the admin-dialogs)
        /// </summary>
        public string Categories { get; set; }

        /// <summary>
        /// Version information of this Module based on the information stored in its assembly
        /// </summary>
        public string Version { get; set; }

        // additional IModule properties 
        [NotMapped]
        public string Owner { get; set; }

        [NotMapped]
        public string Url { get; set; }

        [NotMapped]
        public string Contact { get; set; }

        [NotMapped]
        public string License { get; set; }

        [NotMapped]
        public string Runtimes { get; set; } // comma-delimited list of runtimes ie. Server, WebAssembly

        [NotMapped]
        public string Databases { get; set; } // comma-delimited list of database names ie. SQLServer, SQLite

        /// <summary>
        /// Comma-delimited list of opt-in transports this module REQUIRES, ie. "Grpc", "SignalR" or
        /// "Grpc,SignalR" (the flag names under the host's <c>Transport</c> configuration section). Empty
        /// means no transport requirement. Unlike <see cref="Runtimes"/>/<see cref="Databases"/> — whose
        /// enforcement silently filters the module — a declared transport that the install has NOT enabled
        /// is reported LOUDLY: the transport pipeline logs an error naming the module and the missing
        /// transport at startup (BL-53c). [NotMapped]: populated from the module's <c>IModule</c> on load,
        /// exactly like <see cref="Runtimes"/>.
        /// </summary>
        [NotMapped]
        public string Transports { get; set; } // added in BL-53

        [NotMapped]
        public string Dependencies { get; set; }

        [NotMapped]
        public string PermissionNames { get; set; }

        [NotMapped]
        public string ServerManagerType { get; set; }

        [NotMapped]
        public string ControlTypeRoutes { get; set; }

        /// <summary>
        /// ReleaseVersions contains a comma delimited list of all official release versions of a module ie "1.0.0,1.0.1"
        /// Must be set for modules which use SQL scripts or include version-based logic in IInstallable implementations
        /// </summary>
        [NotMapped]
        public string ReleaseVersions { get; set; }

        [NotMapped]
        public string DefaultAction { get; set; }

        [NotMapped]
        public string SettingsType { get; set; } // added in 2.0.2

        /// <summary>
        /// Type name of a module-wide (installation-scoped) configuration control (D86). Mirrors
        /// <see cref="SettingsType"/> — which is PER-PLACEMENT — but persists through the master-DB-routed
        /// <c>EntityNames.ModuleDefinition</c> settings channel, so it is one config for the whole installation.
        /// Rendered by the "Configure" tab on the module-definition admin editor and by the installer's
        /// module-config onboarding step. [NotMapped]: populated from the module's <c>IModule</c> on load.
        /// </summary>
        [NotMapped]
        public string ConfigSettingsType { get; set; } // added in D86

        [NotMapped]
        public string PackageName { get; set; } // added in 2.1.0

        [NotMapped]
        public List<Resource> Resources { get; set; } // added in 4.0.0

        [NotMapped]
        public bool IsAutoEnabled { get; set; } // added in 4.0.0

        [NotMapped]
        public List<PageTemplate> PageTemplates { get; set; } // added in 4.0.0

        // internal properties
        [NotMapped]
        public int SiteId { get; set; }

        [NotMapped]
        public bool IsEnabled { get; set; }

        [NotMapped]
        public string ControlTypeTemplate { get; set; }

        [NotMapped]
        public string AssemblyName { get; set; }

        [NotMapped]
        public List<Permission> PermissionList { get; set; }

        [NotMapped]
        public string Template { get; set; }

        [NotMapped]
        public bool IsPortable { get; set; }

        [NotMapped]
        public string Fingerprint { get; set; }

        #region Deprecated Properties

        [Obsolete("The Permissions property is deprecated. Use PermissionList instead", false)]
        [NotMapped]
        [JsonIgnore] // exclude from API payload
        public string Permissions
        {
            get
            {
                return JsonSerializer.Serialize(PermissionList);
            }
            set
            {
                PermissionList = JsonSerializer.Deserialize<List<Permission>>(value);
            }
        }

        #endregion
    }
}
