using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Models;

namespace Stomme.Interfaces
{
    public interface ISearchable
    {
        public Task<List<SearchContent>> GetSearchContentsAsync(PageModule pageModule, DateTime lastIndexedOn);
    }
}
