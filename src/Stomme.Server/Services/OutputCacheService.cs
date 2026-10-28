using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;

using Stomme.Documentation;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Shared;

namespace Stomme.Services
{
    [PrivateApi("Don't show in the documentation, as everything should use the Interface")]
    public class ServerOutputCacheService : IOutputCacheService
    {
        private readonly IOutputCacheStore _outputCacheStore;
        private readonly ILogManager _logger;
        private readonly IHttpContextAccessor _accessor;

        public ServerOutputCacheService(IOutputCacheStore outputCacheStore, ILogManager logger, IHttpContextAccessor accessor)
        {
            _outputCacheStore = outputCacheStore;
            _logger = logger;
            _accessor = accessor;
        }

        public async Task EvictByTag(string tag)
        {
            if (_accessor.HttpContext.User.IsInRole(RoleNames.Admin))
            {
                await _outputCacheStore.EvictByTagAsync(tag, default);
                _logger.Log(LogLevel.Information, this, LogFunction.Other, "Evicted Output Cache for Tag {Tag}", tag);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Output Cache Eviction for {Tag}", tag);
            }
        }
    }
}
