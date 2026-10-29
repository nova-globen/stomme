using System;

namespace Stomme.Modules
{
    /// <summary>
    /// Names the switch that turns a module assembly's background work off on a host: with
    /// <c>module.&lt;id&gt;.enabled=false</c> in configuration (the <c>FeatureManagement</c> section, e.g. the environment
    /// variable <c>FeatureManagement__module.lek.enabled=false</c>) the host registers none of the assembly's hosted
    /// services and calls none of its endpoint contributors, so one image can carry a module that runs on one host only.
    /// Put it on a module's SERVER assembly, where its hosted services and hubs live:
    /// <c>[assembly: ModuleSwitch("lek")]</c>. Several assemblies may share one id. Without it the switch ids are the
    /// names of the modules the assembly itself declares (a single-assembly module).
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class ModuleSwitchAttribute : Attribute
    {
        public ModuleSwitchAttribute(string id)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
        }

        /// <summary>The <c>&lt;id&gt;</c> of <c>module.&lt;id&gt;.enabled</c>; lower-cased with spaces removed when read.</summary>
        public string Id { get; }
    }
}
