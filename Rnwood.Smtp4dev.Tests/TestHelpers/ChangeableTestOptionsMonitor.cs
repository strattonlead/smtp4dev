using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

namespace Rnwood.Smtp4dev.Tests.TestHelpers
{
    /// <summary>
    /// An <see cref="IOptionsMonitor{T}"/> whose value can be replaced during a test, notifying registered
    /// listeners in the same way that the real configuration backed monitor does when the settings file changes.
    /// </summary>
    public class ChangeableTestOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {
        private readonly List<Action<T, string>> listeners = new List<Action<T, string>>();

        public ChangeableTestOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; private set; }

        public T Get(string name) => CurrentValue;

        public IDisposable OnChange(Action<T, string> listener)
        {
            lock (listeners)
            {
                listeners.Add(listener);
            }

            return new Subscription(this, listener);
        }

        /// <summary>
        /// Replaces the current value and notifies all listeners, as a change to the underlying settings would.
        /// </summary>
        public void Set(T value)
        {
            CurrentValue = value;

            Action<T, string>[] currentListeners;
            lock (listeners)
            {
                currentListeners = listeners.ToArray();
            }

            foreach (var listener in currentListeners)
            {
                listener(value, Options.DefaultName);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly ChangeableTestOptionsMonitor<T> owner;
            private readonly Action<T, string> listener;

            public Subscription(ChangeableTestOptionsMonitor<T> owner, Action<T, string> listener)
            {
                this.owner = owner;
                this.listener = listener;
            }

            public void Dispose()
            {
                lock (owner.listeners)
                {
                    owner.listeners.Remove(listener);
                }
            }
        }
    }
}
