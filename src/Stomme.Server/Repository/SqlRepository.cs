using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Stomme.Databases.Interfaces;
using Stomme.Models;
// ReSharper disable ConvertToUsingDeclaration
// ReSharper disable InvertIf
// ReSharper disable BuiltInTypeReferenceStyleForMemberAccess

namespace Stomme.Repository
{
    public interface ISqlRepository
    {
        Task ExecuteScriptAsync(Tenant tenant, string script);

        Task<bool> ExecuteScriptAsync(string connectionString, string databaseType, Assembly assembly, string filename);

        Task<bool> ExecuteScriptAsync(Tenant tenant, Assembly assembly, string filename);

        Task<int> ExecuteNonQueryAsync(Tenant tenant, string query);

        Task<int> ExecuteNonQueryAsync(string connectionString, string databaseType, string query);

        Task<IDataReader> ExecuteReaderAsync(Tenant tenant, string query);

        Task<IDataReader> ExecuteReaderAsync(string DBType, string DBConnectionString, string query);

    }

    public class SqlRepository : ISqlRepository
    {
        private IConfigurationRoot _config;

        public SqlRepository(IConfigurationRoot config)
        {
            _config = config;
        }

        public async Task ExecuteScriptAsync(Tenant tenant, string script)
        {
            // execute script in current tenant
            foreach (var query in script.Split("GO", StringSplitOptions.RemoveEmptyEntries))
            {
                await ExecuteNonQueryAsync(tenant, query);
            }
        }

        public async Task<bool> ExecuteScriptAsync(string connectionString, string databaseType, Assembly assembly, string fileName)
        {
            var success = true;
            var script = this.GetScriptFromAssembly(assembly, fileName);

            if (!string.IsNullOrEmpty(script))
            {
                try
                {
                    foreach (var query in script.Split("GO", StringSplitOptions.RemoveEmptyEntries))
                    {
                        await ExecuteNonQueryAsync(connectionString, databaseType, query);
                    }
                }
                catch
                {
                    success = false;
                }
            }

            return success;
        }

        public async Task<bool> ExecuteScriptAsync(Tenant tenant, Assembly assembly, string fileName)
        {
            var success = true;
            var script = this.GetScriptFromAssembly(assembly, fileName);

            if (!string.IsNullOrEmpty(script))
            {
                try
                {
                    await ExecuteScriptAsync(tenant, script);
                }
                catch
                {
                    success = false;
                }
            }

            return success;
        }

        public async Task<int> ExecuteNonQueryAsync(Tenant tenant, string query)
        {
            return await ExecuteNonQueryAsync(tenant.DBConnectionString, tenant.DBType, query);
        }

        public async Task<IDataReader> ExecuteReaderAsync(Tenant tenant, string query)
        {
            var db = GetActiveDatabase(tenant.DBType);
            return await db.ExecuteReaderAsync(GetConnectionString(tenant.DBConnectionString), query);
        }

        public async Task<IDataReader> ExecuteReaderAsync(string DBType, string DBConnectionString, string query)
        {
            var db = GetActiveDatabase(DBType);
            return await db.ExecuteReaderAsync(GetConnectionString(DBConnectionString), query);
        }

        public async Task<int> ExecuteNonQueryAsync(string connectionString, string databaseType, string query)
        {
            var db = GetActiveDatabase(databaseType);
            var connectionstring = GetConnectionString(connectionString);
            if (connectionstring != null)
            {
                return await db.ExecuteNonQueryAsync(GetConnectionString(connectionString), query);
            }
            else
            {
                return 0;
            }
        }

        private IRelationalDatabase GetActiveDatabase(string databaseType)
        {
            // These callers (ExecuteReaderAsync/ExecuteNonQueryAsync) invoke relational members, so the
            // provider must be relational. A non-relational provider narrows to null here — its script
            // execution has no relational meaning; that is the honest partial until the module-script path is
            // provider-neutral.
            IRelationalDatabase activeDatabase = null;
            if (!String.IsNullOrEmpty(databaseType))
            {
                var type = Type.GetType(databaseType);
                activeDatabase = Activator.CreateInstance(type) as IRelationalDatabase;
            }

            return activeDatabase;
        }

        private string GetConnectionString(string connectionString)
        {
            if (!connectionString.Contains("="))
            {
                connectionString = _config.GetConnectionString(connectionString);
            }
            return connectionString;
        }
    }

    /// <summary>
    /// The pure half of the SQL surface: reading a script out of an assembly's embedded resources.
    /// </summary>
    /// <remarks>
    /// <c>GetScriptFromAssembly</c> lives here rather than on <see cref="ISqlRepository"/> because it does
    /// not touch a database at all — it is <see cref="Assembly.GetManifestResourceStream(string)"/> over an
    /// already-loaded assembly, i.e. a read from memory-mapped metadata. The convention bans sync members on
    /// store-touching interfaces; the honest response for genuinely synchronous work is to take it OFF the
    /// store-touching interface, not to wrap it in <c>Task.FromResult</c> and call it async. An extension on
    /// <see cref="ISqlRepository"/> keeps the existing call site
    /// (<c>sql.GetScriptFromAssembly(assembly, script)</c>) compiling unchanged.
    /// </remarks>
    public static class SqlRepositoryExtensions
    {
        /// <summary>
        /// Returns the named script from <paramref name="assembly"/>'s embedded resources, or "" if absent.
        /// </summary>
        public static string GetScriptFromAssembly(this ISqlRepository repository, Assembly assembly, string fileName)
        {
            // script must be included as an Embedded Resource within an assembly
            var script = "";

            if (assembly != null)
            {
                var name = assembly.GetManifestResourceNames().FirstOrDefault(item => item.EndsWith("." + fileName));
                if (name != null)
                {
                    var resourceStream = assembly.GetManifestResourceStream(name);
                    if (resourceStream != null)
                    {
                        using (var reader = new StreamReader(resourceStream))
                        {
                            script = reader.ReadToEnd();
                        }
                    }
                }
            }

            return script;
        }
    }
}
