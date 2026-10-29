using System;
using Stomme.Databases.Interfaces;

namespace Stomme.Database.RavenDb
{
    /// <summary>
    /// The RavenDB database provider. Implements the storage-agnostic <see cref="IDatabase"/> core ONLY —
    /// deliberately NOT <c>DatabaseBase</c>/<see cref="IRelationalDatabase"/>, because a document store has no
    /// EF Core provider, no <c>UseDatabase</c> and no SQL to rewrite. Its purpose is to be selectable and
    /// identifiable: the installer resolves it reflectively via <c>Type.GetType(dbtype)</c> +
    /// <c>Activator.CreateInstance</c> exactly like the relational providers, and
    /// <c>DatabaseManager</c> branches on <c>is IRelationalDatabase</c> — which this is not — to skip the
    /// EF-Core create/migrate path and use <c>RavenDatabaseBootstrap.EnsureDatabaseAsync</c> instead.
    /// <para>
    /// <b>Install-selectable</b>: a wizard entry in <c>AvailableDatabases</c> (appsettings.json) and
    /// in <c>DatabaseController.DefaultDatabases</c> — the read-time catalog that replaced the old
    /// <c>ValidateConfiguration</c> boot rewrite; keep those two lists in lockstep or the RavenDB
    /// option silently vanishes from installs without the config section. Store-provider wiring
    /// (<c>AddRavenFrameworkStore</c>) and the hand-written Raven <c>IUserStore</c> are separate pieces; the
    /// module contract is its own.
    /// </para>
    /// </summary>
    public class RavenDatabase : IDatabase
    {
        private static readonly string _typeName;
        private static readonly string _assemblyName;

        static RavenDatabase()
        {
            // Mirror DatabaseBase.Initialize: the version-stripped type/assembly names the installer stores
            // in Tenant.DBType and reads back through Type.GetType.
            var type = typeof(RavenDatabase);
            var typeQualifiedName = type.AssemblyQualifiedName;
            var assemblyName = type.Assembly.FullName;
            _typeName = typeQualifiedName.Substring(0, typeQualifiedName.IndexOf(", Version"));
            _assemblyName = assemblyName.Substring(0, assemblyName.IndexOf(", Version"));
        }

        public string AssemblyName => _assemblyName;

        public string FriendlyName => "RavenDB";

        public string Name => "RavenDB";

        public string Provider => "RavenDB";

        public string TypeName => _typeName;
    }
}
