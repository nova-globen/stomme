using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Stomme.Infrastructure;
using Stomme.Models;

namespace Microsoft.Extensions.DependencyInjection
{
    public partial class StommeSiteOptionsBuilder
    {
        public IServiceCollection Services { get; set; }

        public StommeSiteOptionsBuilder(IServiceCollection services)
        {
            Services = services;
        }

        public StommeSiteOptionsBuilder AddSiteOptions<TOptions>(
            Action<TOptions, Alias, Dictionary<string, string>> configureOptions) where TOptions : class, new()
        {
            Services.TryAddSingleton<IOptionsMonitorCache<TOptions>, SiteOptionsCache<TOptions>>();
            Services.AddSingleton<ISiteOptions<TOptions>, SiteOptions<TOptions>> (_ => new SiteOptions<TOptions>(configureOptions));
            Services.TryAddTransient<IOptionsFactory<TOptions>, SiteOptionsFactory<TOptions>>();
            Services.TryAddScoped<IOptionsSnapshot<TOptions>>(sp => BuildOptionsManager<TOptions>(sp));
            Services.TryAddSingleton<IOptions<TOptions>>(sp => BuildOptionsManager<TOptions>(sp));

            return this;
        }

        public StommeSiteOptionsBuilder AddSiteNamedOptions<TOptions>(string name,
            Action<TOptions, Alias, Dictionary<string, string>> configureOptions) where TOptions : class, new()
        {
            Services.TryAddSingleton<IOptionsMonitorCache<TOptions>, SiteOptionsCache<TOptions>>();
            Services.AddSingleton<ISiteNamedOptions<TOptions>, SiteNamedOptions<TOptions>>(_ => new SiteNamedOptions<TOptions>(name, configureOptions));
            Services.TryAddTransient<IOptionsFactory<TOptions>, SiteOptionsFactory<TOptions>>();
            Services.TryAddScoped<IOptionsSnapshot<TOptions>>(sp => BuildOptionsManager<TOptions>(sp));
            Services.TryAddSingleton<IOptions<TOptions>>(sp => BuildOptionsManager<TOptions>(sp));

            return this;
        }

        private static SiteOptionsManager<TOptions> BuildOptionsManager<TOptions>(IServiceProvider sp)
            where TOptions : class, new()
        {
            var cache = ActivatorUtilities.CreateInstance(sp, typeof(SiteOptionsCache<TOptions>));
            return (SiteOptionsManager<TOptions>)ActivatorUtilities.CreateInstance(sp, typeof(SiteOptionsManager<TOptions>), new[] { cache });
        }

    }
}
