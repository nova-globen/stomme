using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Stomme.Models;

namespace Stomme.Extensions
{
    public static class PermissionExtension
    {
        public static string EncodePermissions(this IEnumerable<Permission> permissions)
        {
            return JsonSerializer.Serialize(permissions);
        }
    }
}
