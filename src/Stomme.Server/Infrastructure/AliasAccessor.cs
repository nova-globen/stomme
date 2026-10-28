using Microsoft.AspNetCore.Http;
using Stomme.Extensions;
using Stomme.Models;

namespace Stomme.Infrastructure
{
    public interface IAliasAccessor
    {
        Alias Alias { get; }
    }

    public class AliasAccessor : IAliasAccessor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AliasAccessor(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public Alias Alias => _httpContextAccessor.HttpContext.GetAlias();
    }
}
