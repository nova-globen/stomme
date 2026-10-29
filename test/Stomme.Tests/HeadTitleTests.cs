using System.Threading.Tasks;
using Bunit;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Stomme's own <c>&lt;title&gt;</c> (the <c>Head</c> component) is the FIRST node it renders.
    /// </summary>
    /// <remarks>
    /// Found on a deployed WebAssembly site, and reproduced on a local one: "Cannot read properties of null (reading
    /// 'removeChild')", the node a <c>&lt;title&gt;</c> (traced through the browser's debugger). On an Interactive site Blazor's
    /// <c>HeadOutlet</c> (mounted) removes, on its first render, every <c>&lt;title&gt;</c> it does not take for its
    /// own - one whose previous sibling is not a Blazor marker comment. <c>Head</c> rendered <c>"\n&lt;title&gt;..."</c>, so its
    /// title followed a text node, was removed, and <c>Head</c>'s next update failed on the node that was gone. Rendered first,
    /// the title follows <c>Head</c>'s own marker and stays: driven on a local WebAssembly site, the error gone and the title
    /// correct after an in-app navigation.
    /// </remarks>
    public sealed class HeadTitleTests : BunitContext
    {
        [Fact]
        public async Task The_title_is_the_first_node_head_renders()
        {
            var siteState = new SiteState();
            Services.AddSingleton(siteState);
            var head = Render<Head>(parameters => parameters.Add(component => component.RenderMode, RenderModes.Interactive));

            await head.InvokeAsync(() => siteState.Properties.PageTitle = "Home - Default Site");

            Assert.StartsWith("<title>Home - Default Site</title>", head.Markup);
        }
    }
}
