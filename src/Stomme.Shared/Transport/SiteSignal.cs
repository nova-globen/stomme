#nullable enable
using System;
using System.Linq;
using System.Threading;

namespace Stomme.Transport.Realtime
{
    /// <summary>
    /// An in-process fan-out of a site's messages to the pages rendered on this host (the dashboards' dataset change, the
    /// collaboration notice), whose subscribers are each called in the EXECUTION CONTEXT THEY SUBSCRIBED IN - never the
    /// raiser's. An Interactive Server page subscribes from its circuit, and everything it then does - a service call, the
    /// access guard, the current user - reads the principal through the ambient HttpContext, which rides the execution
    /// context. Invoked on the raiser's thread, a subscriber's work ran as the REQUEST THAT RAISED THE MESSAGE: seen
    /// with two signed-in editors, one editor's panel re-read the lock as the other user and told her she had
    /// lost it; the dashboards page re-read a viewer's widgets under whoever refreshed the dataset. Each
    /// subscriber is also isolated: one that throws neither stops the others nor fails the request that raised the message.
    /// </summary>
    public abstract class SiteSignal<TMessage>
        where TMessage : class
    {
        private readonly object _gate = new();
        private Subscriber[] _subscribers = Array.Empty<Subscriber>();

        /// <summary>How many subscribers are attached - for a test and a diagnostic.</summary>
        public int SubscriberCount => Volatile.Read(ref _subscribers).Length;

        protected void Subscribe(Action<string, TMessage>? handler)
        {
            if (handler is null)
            {
                return;
            }

            // Null when the subscriber suppressed flow: it is then called in whatever context raises the message.
            var context = ExecutionContext.Capture();
            lock (_gate)
            {
                _subscribers = _subscribers.Append(new Subscriber(handler, context)).ToArray();
            }
        }

        protected void Unsubscribe(Action<string, TMessage>? handler)
        {
            if (handler is null)
            {
                return;
            }

            lock (_gate)
            {
                var index = Array.FindLastIndex(_subscribers, subscriber => subscriber.Handler == handler);
                if (index >= 0)
                {
                    _subscribers = _subscribers.Where((_, position) => position != index).ToArray();
                }
            }
        }

        protected void Raise(string siteKey, TMessage message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(siteKey);
            ArgumentNullException.ThrowIfNull(message);
            foreach (var subscriber in Volatile.Read(ref _subscribers))
            {
                try
                {
                    if (subscriber.Context is null)
                    {
                        subscriber.Handler(siteKey, message);
                    }
                    else
                    {
                        ExecutionContext.Run(subscriber.Context, static state =>
                        {
                            var (handler, key, payload) = ((Action<string, TMessage>, string, TMessage))state!;
                            handler(key, payload);
                        }, (subscriber.Handler, siteKey, message));
                    }
                }
                catch (Exception)
                {
                    // A page's handler hands its work to its own circuit and returns; one that throws is that page's
                    // defect, never the raiser's failure nor a reason to leave the other pages untold.
                }
            }
        }

        private sealed record Subscriber(Action<string, TMessage> Handler, ExecutionContext? Context);
    }
}
