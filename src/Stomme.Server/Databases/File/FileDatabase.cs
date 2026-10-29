using System;
using Stomme.Databases.Interfaces;

namespace Stomme.Database.File
{
    /// <summary>
    /// The file-store database provider. Implements the storage-agnostic <see cref="IDatabase"/> core ONLY —
    /// deliberately NOT <c>DatabaseBase</c>/<see cref="IRelationalDatabase"/>, because a JSON-file store has no EF
    /// Core provider, no <c>UseDatabase</c> and no SQL to rewrite (the same shape <c>RavenDatabase</c> takes). Its
    /// purpose is to be selectable and identifiable: a deployment sets <c>Database:DefaultDBType</c> to this type's
    /// name and the module persistence registry (<c>ModuleRepositoryProviderRegistry</c>) resolves the
    /// <c>FileRepository</c> factory for it. It is the metadata seam the file provider is keyed on — the actual
    /// store lives in <c>Stomme.Persistence.File</c>.
    /// <para>
    /// <b>Config-driven, not (yet) wizard-selectable.</b> This type is intentionally absent from
    /// <c>AvailableDatabases</c>: the file store proves provider EXTENSIBILITY for module persistence via
    /// <c>DefaultDBType=Stomme.Database.File.FileDatabase, Stomme.Server</c>, which is sufficient for the D80
    /// proof. A full install-wizard entry (its own installer control, plus a framework-store twin) is a separate,
    /// larger piece of work and is deferred.
    /// </para>
    /// </summary>
    public class FileDatabase : IDatabase
    {
        private static readonly string _typeName;
        private static readonly string _assemblyName;

        static FileDatabase()
        {
            // Mirror DatabaseBase/RavenDatabase: the version-stripped type/assembly names read back through
            // Type.GetType from Database:DefaultDBType.
            var type = typeof(FileDatabase);
            var typeQualifiedName = type.AssemblyQualifiedName;
            var assemblyName = type.Assembly.FullName;
            _typeName = typeQualifiedName.Substring(0, typeQualifiedName.IndexOf(", Version"));
            _assemblyName = assemblyName.Substring(0, assemblyName.IndexOf(", Version"));
        }

        public string AssemblyName => _assemblyName;

        public string FriendlyName => "File store";

        public string Name => "File";

        public string Provider => "File";

        public string TypeName => _typeName;
    }
}
