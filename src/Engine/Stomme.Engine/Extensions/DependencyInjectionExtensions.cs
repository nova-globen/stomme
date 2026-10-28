using System.Collections.Concurrent;
using Stomme.Engine.Features;
using Stomme.Engine.Features.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extension methods to <see cref="IServiceCollection"/>. 
/// </summary>
public static class ModuleExtensions
{
    private static readonly IDictionary<IServiceCollection, IModule> Modules = new ConcurrentDictionary<IServiceCollection, IModule>();
    
    /// <summary>
    /// Creates a new Stomme module and adds the <see cref="EngineFeature"/> to it.
    /// </summary>
    public static IModule AddEngine(this IServiceCollection services, Action<IModule>? configure = null)
    {
        var module = services.GetOrCreateModule();
        module.Configure<AppFeature>(app => app.Configurator += configure);
        module.Apply();
        
        return module;
    }
    
    /// <summary>
    /// Configures the Stomme module.
    /// </summary>
    public static IModule ConfigureEngine(this IServiceCollection services, Action<IModule>? configure = null)
    {
        var module = services.GetOrCreateModule();
        
        if(configure != null)
            module.Configure<AppFeature>(app => app.Configurator += configure);
        
        return module;
    }
    
    private static IModule GetOrCreateModule(this IServiceCollection services)
    {
        if(Modules.TryGetValue(services, out var module))
            return module;
        
        module = services.CreateModule();
        
        Modules[services] = module;
        return module;
    }
}