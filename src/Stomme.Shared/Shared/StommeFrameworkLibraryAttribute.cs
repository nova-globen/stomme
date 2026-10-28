using System;

namespace Stomme.Shared
{
    /// <summary>
    /// Marks an assembly as a Stomme framework <em>building-block library</em> rather than a module or
    /// theme, opting it out of the framework's convention-based service discovery
    /// (<c>AddStommeServices()</c>: <c>IService</c> -> scoped, <c>ITransientService</c> -> transient,
    /// <c>IHostedService</c> -> singleton, <c>IServerStartup</c>/<c>IClientStartup</c> execution).
    ///
    /// The convention scan exists for modules and themes, which plug in by shipping an assembly whose name
    /// contains "Stomme" and expect the framework to wire them up with no host code changes. A building-block
    /// library is the opposite contract: it wires its own DI explicitly through an <c>Add*()</c> extension the
    /// host calls, usually behind a configuration gate (e.g. <c>Messaging:Enabled</c>). Convention-registering
    /// such a library's hosted services activates them regardless of that gate, while their dependencies only
    /// exist inside the gated extension — which breaks container construction outright. Applying this marker is
    /// what keeps an opt-in library opt-in.
    ///
    /// The framework matches this attribute by its <see cref="TypeFullName"/>, not by type identity, so a leaf
    /// library that deliberately does not reference <c>Stomme.Shared</c> opts out by declaring its own
    /// <c>internal</c> attribute of the same full name and applying it at assembly level. Assemblies that
    /// already reference <c>Stomme.Shared</c> simply use this type. See docs/decisions.md D13.
    /// </summary>
    /// <example>
    /// <code>
    /// [assembly: Stomme.Shared.StommeFrameworkLibrary]
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public sealed class StommeFrameworkLibraryAttribute : Attribute
    {
        /// <summary>
        /// The full type name the framework's assembly scan matches on. Matching by name (rather than by
        /// <c>typeof</c>) is what lets a library opt out without taking a dependency on Stomme.Shared.
        /// </summary>
        public const string TypeFullName = "Stomme.Shared.StommeFrameworkLibraryAttribute";
    }
}
