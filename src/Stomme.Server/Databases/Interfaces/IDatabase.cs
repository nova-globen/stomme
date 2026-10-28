namespace Stomme.Databases.Interfaces
{
    /// <summary>
    /// The storage-agnostic core of a Stomme database provider (D53). It carries only what EVERY store —
    /// relational or document — can honestly supply: identity/metadata used by installation, module
    /// discovery and the <c>AvailableDatabases</c> registry. A provider is constructed reflectively via
    /// <c>Type.GetType(dbtype)</c> + <c>Activator.CreateInstance</c>, so a document-store provider
    /// (<c>RavenDatabase</c>) implements this and NOTHING else.
    /// <para>
    /// The relational-migrations surface — EF Core options, SQL rewriting, DDL helpers — moved to
    /// <see cref="IRelationalDatabase"/>, which every EF provider (<c>DatabaseBase</c> and its three
    /// concrete providers) implements. Code that needs a relational member must accept
    /// <see cref="IRelationalDatabase"/> or test <c>is IRelationalDatabase</c>; a non-relational provider
    /// yields <c>null</c>/skips, which is exactly the install branch <c>DatabaseManager</c> takes.
    /// </para>
    /// </summary>
    public interface IDatabase
    {
        public string AssemblyName { get; }

        public string FriendlyName { get; }

        public string Name { get; }

        public string Provider { get; }

        public string TypeName { get; }
    }
}
