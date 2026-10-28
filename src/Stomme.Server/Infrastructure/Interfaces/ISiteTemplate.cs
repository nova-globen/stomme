using Stomme.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Stomme.Infrastructure
{
    public interface ISiteTemplate
    {
        string Name { get; }

        Task<List<PageTemplate>> CreateSiteAsync(Site site);
    }
}
