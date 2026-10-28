#nullable enable
using System;

namespace Stomme.Collaboration
{
    /// <summary>
    /// What a site's editors are told over the collaboration hub (BL-97 increment 12, D114): which resource, what kind of
    /// thing happened to it, and who did it - never its content, never a presence position, never a change's words. An editor
    /// on that resource re-reads the session and the lock through the API, under its own keys and its module's access check.
    /// </summary>
    public sealed record CollaborationNotice(string ResourceType, string ResourceId, string Kind, int ByUserId);

    public static class CollaborationNoticeKinds
    {
        /// <summary>Someone joined, moved within or left the resource's session.</summary>
        public const string Presence = "Presence";

        /// <summary>A lock on the resource was taken, released, taken over or lapsed.</summary>
        public const string Lock = "Lock";

        /// <summary>A change was announced in the resource's session.</summary>
        public const string Change = "Change";

        /// <summary>The resource was stored by the module that owns it - reload before saving over it.</summary>
        public const string Saved = "Saved";
    }

    public static class CollaborationHubMethods
    {
        /// <summary>The client method the push invokes with a <see cref="CollaborationNotice"/>.</summary>
        public const string Changed = "collaborationChanged";
    }

    /// <summary>
    /// The in-process fan-out of a notice to the editors rendered on THIS host (the Dashboards module's signal shape): an
    /// Interactive Server circuit has no browser cookie to open the hub with, so a page rendered on the server subscribes
    /// here; a browser page uses the hub; with neither, the page polls. A singleton registered by the Server, absent in the
    /// browser. The site key scopes a notice to its site.
    /// </summary>
    public interface ICollaborationSignal
    {
        event Action<string, CollaborationNotice>? Changed;

        void Notify(string siteKey, CollaborationNotice notice);
    }

    /// <summary>Each subscriber is called in the context it subscribed in - a page as its own user, never as the request that raised the notice (<see cref="Stomme.Transport.Realtime.SiteSignal{TMessage}"/>).</summary>
    public sealed class CollaborationSignal : Stomme.Transport.Realtime.SiteSignal<CollaborationNotice>, ICollaborationSignal
    {
        public event Action<string, CollaborationNotice>? Changed
        {
            add => Subscribe(value);
            remove => Unsubscribe(value);
        }

        public void Notify(string siteKey, CollaborationNotice notice) => Raise(siteKey, notice);
    }
}
