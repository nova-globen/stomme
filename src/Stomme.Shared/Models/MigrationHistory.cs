using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Stomme.Models
{
    // EF's migration ledger, read for System Info. It has no key, which TenantDBContext says (HasNoKey): an EF Core
    // attribute here made this assembly - and every WebAssembly site's browser - carry EF Core.
    [Table("__EFMigrationsHistory")]
    public class MigrationHistory
    {
        public string MigrationId { get; set; }
        public string ProductVersion { get; set; }
        public DateTime AppliedDate { get; set; }
        public string AppliedVersion { get; set; }
    }
}
