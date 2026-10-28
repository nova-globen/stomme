using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Repository
{
    public interface ISiteTemplateRepository
    {
        Task<IEnumerable<SiteTemplate>> GetSiteTemplatesAsync();
    }

    public class SiteTemplateRepository : ISiteTemplateRepository
    {
        private readonly IServiceProvider _serviceProvider;

        public SiteTemplateRepository(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        private List<SiteTemplate> LoadSiteTemplates()
        {
            List<SiteTemplate> siteTemplates = new List<SiteTemplate>();

            // iterate through Stomme site template assemblies
            var assemblies = AppDomain.CurrentDomain.GetStommeAssemblies();
                
            foreach (Assembly assembly in assemblies)
            {
                siteTemplates = LoadSiteTemplatesFromAssembly(siteTemplates, assembly);
            }

            return siteTemplates;
        }

        private List<SiteTemplate> LoadSiteTemplatesFromAssembly(List<SiteTemplate> siteTemplates, Assembly assembly)
        {
            Type[] siteTemplateTypes = assembly.GetTypes().Where(item => item.GetInterfaces().Contains(typeof(ISiteTemplate))).ToArray();
            foreach (Type siteTemplateType in siteTemplateTypes)
            {
                var siteTemplateObject = ActivatorUtilities.CreateInstance(_serviceProvider, siteTemplateType);
                if (siteTemplateObject != null)
                {
                    var typename = Utilities.GetFullTypeName(siteTemplateType.AssemblyQualifiedName);
                    var name = (string)siteTemplateType.GetProperty("Name")?.GetValue(siteTemplateObject);
                    if (typename != Constants.AdminSiteTemplate && !string.IsNullOrEmpty(name))
                    {
                        siteTemplates.Add(new SiteTemplate
                        {
                            Name = name,
                            TypeName = typename
                        });
                    }
                }
            }
            return siteTemplates;
        }

        public Task<IEnumerable<SiteTemplate>> GetSiteTemplatesAsync()
        {
            // Site templates are discovered by reflecting over the loaded assemblies — there is no store
            // behind this method, so there is nothing to await. It carries the async shape because
            // ISiteTemplateRepository is consumed alongside the repositories that do touch a store.
            return Task.FromResult<IEnumerable<SiteTemplate>>(LoadSiteTemplates());
        }
    }
}
