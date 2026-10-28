using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    public class EventDistributorHostedService : IHostedService, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ISyncManager _syncManager;
        private readonly ICacheManager _cache;
        private readonly ILogger<EventDistributorHostedService> _filelogger;

        public EventDistributorHostedService(IServiceProvider serviceProvider, ISyncManager syncManager, ICacheManager cache, ILogger<EventDistributorHostedService> filelogger)
        {
            _serviceProvider = serviceProvider;
            _syncManager = syncManager;
            _cache = cache;
            _filelogger = filelogger;
        }

        async Task EntityChangedAsync(SyncEvent syncEvent)
        {
            List<string> eventSubscribers = await _cache.GetCacheAsync("EventSubscribers", ct =>
            {
                eventSubscribers = new List<string>();
                var assemblies = AppDomain.CurrentDomain.GetStommeAssemblies();
                foreach (Assembly assembly in assemblies)
                {
                    foreach (var type in assembly.GetTypes(typeof(IEventSubscriber)))
                    {
                        eventSubscribers.Add(type.AssemblyQualifiedName);
                    }
                }
                // the factory is a reflection scan over already-loaded assemblies: it reaches no store, so
                // there is nothing to await and nothing being blocked on
                return Task.FromResult(eventSubscribers);
            }, TimeSpan.MaxValue, TimeSpan.MaxValue);

            foreach (var eventSubscriber in eventSubscribers)
            {
                try
                {
                    var type = Type.GetType(eventSubscriber);
                    var obj = ActivatorUtilities.CreateInstance(_serviceProvider, type) as IEventSubscriber;
                    if (obj != null)
                    {
                        await obj.EntityChangedAsync(syncEvent);
                    }
                }
                catch (Exception ex)
                {
                    _filelogger.LogError(Utilities.LogMessage(this, $"Error In EventSubscriber {eventSubscriber} - {ex.Message}"));
                }
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _syncManager.Subscribe(EntityChangedAsync);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            _syncManager.Unsubscribe(EntityChangedAsync);
        }
    }
}
