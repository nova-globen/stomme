using Microsoft.AspNetCore.Components;
using Stomme.Models;

namespace Stomme.Themes
{
    public abstract class ContainerBase : ThemeBase, IContainerControl
    {
        [CascadingParameter]
        protected Module ModuleState { get; set; }

        protected override bool ShouldRender()
        {
            return PageState?.RenderId == ModuleState?.RenderId;
        }
    }
}
