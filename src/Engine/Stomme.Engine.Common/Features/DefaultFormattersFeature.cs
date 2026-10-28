using System.ComponentModel;
using Stomme.Engine.Common.Serialization;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Common.Features;

public class DefaultFormattersFeature(IModule module) : FeatureBase(module)
{
    public override void Configure()
    {
        TypeDescriptor.AddAttributes(typeof(Type), new TypeConverterAttribute(typeof(TypeTypeConverter)));
        Module.Services.AddSingleton<IFormatter, JsonFormatter>();
    }
}