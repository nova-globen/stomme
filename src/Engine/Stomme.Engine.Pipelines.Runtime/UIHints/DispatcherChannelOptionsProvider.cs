using System.Reflection;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.UIHints.Dropdown;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime.UIHints;

/// <summary>
/// Provides options for steps that dispatch pipelines to channels.
/// </summary>
public class DispatcherChannelOptionsProvider : DropDownOptionsProviderBase
{
    private readonly PipelineDispatcherOptions _options;

    /// <inheritdoc />
    public DispatcherChannelOptionsProvider(IOptions<PipelineDispatcherOptions> options)
    {
        _options = options.Value;
    }

    /// <inheritdoc />
    protected override ValueTask<ICollection<SelectListItem>> GetItemsAsync(PropertyInfo propertyInfo, object? context, CancellationToken cancellationToken)
    {
        var channelNames = _options.Channels.Select(x => x.Name).ToList();
        var selectListItems = new List<SelectListItem> { new("Default", "") };

        selectListItems.AddRange(channelNames.Select(x => new SelectListItem(x, x)));

        return new(selectListItems);
    }
}