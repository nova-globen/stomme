using Stomme.Engine.Common.Codecs;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Common.Features;

[UsedImplicitly]
public class StringCompressionFeature(IModule module) : FeatureBase(module)
{
    public override void Apply()
    {
        Services
            .AddSingleton<ICompressionCodecResolver, CompressionCodecResolver>()
            .AddSingleton<ICompressionCodec, None>()
            .AddSingleton<ICompressionCodec, GZip>()
            .AddSingleton<ICompressionCodec, Zstd>();
    }
}