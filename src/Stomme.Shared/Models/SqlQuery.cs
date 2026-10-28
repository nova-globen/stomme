using System.Collections.Generic;

namespace Stomme.Models
{
    public class SqlQuery
    {
        /// <summary>
        /// Reference to the <see cref="Tenant"/> this belongs to
        /// </summary>
        public int TenantId { get; set; }
        public string DBType { get; set; }
        public string DBConnectionString { get; set; }
        public string Query { get; set; }
        public List<Dictionary<string, string>> Results { get; set; }
    }
}
