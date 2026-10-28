using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Extensions
{
    public static class HttpContextExtensions
    {
        // this method should only be used in scenarios where HttpContent exists (ie. within Controllers)
        public static Alias GetAlias(this HttpContext context)
        {
            if (context != null && context.Items.ContainsKey(Constants.HttpContextAliasKey))
            {
                return context.Items[Constants.HttpContextAliasKey] as Alias;
            }
            return null;
        }

        // this method should only be used in scenarios where HttpContext exists (ie. within Controllers)
        public static Dictionary<string, string> GetSiteSettings(this HttpContext context)
        {
            if (context != null && context.Items.ContainsKey(Constants.HttpContextSiteSettingsKey))
            {
                return context.Items[Constants.HttpContextSiteSettingsKey] as Dictionary<string, string>;
            }
            return null;
        }
    }
}
