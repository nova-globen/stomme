using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class DatabaseController : Controller
    {
        private IOptions<List<Models.Database>> _databaseOptions;
        private IConfigManager _config;

        public DatabaseController(IOptions<List<Models.Database>> databaseOptions, IConfigManager config)
        {
            _databaseOptions = databaseOptions;
            _config = config;
        }

        // GET: api/<controller>
        [HttpGet]
        public IEnumerable<Models.Database> Get()
        {
            var databases = ResolveAvailableDatabases(_databaseOptions.Value);
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                // on non-Windows platforms, LocalDB is not supported, so remove it from the list of available databases
                databases.RemoveAll(item => item.Name == "LocalDB");
            }
            var master = _config.GetDatabaseType();
            if (master != "" && databases.Exists(item => item.DBType == master))
            {
                databases.Find(item => item.DBType == master).IsDefault = true;
            }
            return databases;
        }

        /// <summary>
        /// The catalog is resolved on READ — booting never writes it. A missing/empty
        /// AvailableDatabases section yields the stock defaults, and a pre-6.1.5 ", Stomme.Database."
        /// entry is served with its migrated ", Stomme.Server" assembly, exactly as
        /// IConfigManager.GetDatabaseType migrates DefaultDBType on read. Always returns fresh copies —
        /// the bound options list is a cached singleton, and mutating it (RemoveAll/IsDefault above) must
        /// not bleed across requests.
        /// </summary>
        private static List<Models.Database> ResolveAvailableDatabases(List<Models.Database> configured)
        {
            if (configured == null || configured.Count == 0)
            {
                return DefaultDatabases();
            }
            return configured.Select(item => new Models.Database
            {
                Name = item.Name,
                ControlType = item.ControlType,
                DBType = item.DBType != null && item.DBType.Contains(", Stomme.Database.")
                    ? item.DBType.Substring(0, item.DBType.IndexOf(", ")) + ", Stomme.Server"
                    : item.DBType
            }).ToList();
        }

        // Keep in lockstep with appsettings.json's AvailableDatabases (RavenDB included — dropping it
        // here would silently hide the document-store option from any install without the section).
        private static List<Models.Database> DefaultDatabases() => new List<Models.Database>
        {
            new Models.Database { Name = "LocalDB", ControlType = "Stomme.Installer.Controls.LocalDBConfig, Stomme.Client", DBType = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server" },
            new Models.Database { Name = "SQL Server", ControlType = "Stomme.Installer.Controls.SqlServerConfig, Stomme.Client", DBType = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server" },
            new Models.Database { Name = "SQLite", ControlType = "Stomme.Installer.Controls.SqliteConfig, Stomme.Client", DBType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
            new Models.Database { Name = "PostgreSQL", ControlType = "Stomme.Installer.Controls.PostgreSQLConfig, Stomme.Client", DBType = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server" },
            new Models.Database { Name = "Azure SQL", ControlType = "Stomme.Installer.Controls.AzureSqlConfig, Stomme.Client", DBType = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server" },
            new Models.Database { Name = "RavenDB", ControlType = "Stomme.Installer.Controls.RavenDBConfig, Stomme.Client", DBType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server" }
        };
    }
}
