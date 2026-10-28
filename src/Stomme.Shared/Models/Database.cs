namespace Stomme.Models
{
    /// <summary>
    /// Information about a Database used in the current Stomme installation
    /// </summary>
    public class Database
    {
        /// <summary>
        /// Name of the Database
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Namespace &amp; name of the UI control to configure this database, like `Stomme.Installer.Controls.SqlServerConfig, Stomme.Client`
        /// </summary>
        public string ControlType { get; set; }

        /// <summary>
        /// Type of DB using the full namespace, like `Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server`
        /// </summary>
        public string DBType { get; set; }

        /// <summary>
        /// whether this item is the default database provider ( ie. specified by DefaultDBType in appsettings.json )
        /// </summary>
        public bool IsDefault { get; set; }
    }
}
