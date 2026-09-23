using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    /// <summary>
    /// Bounded event queue from the I/O threads to the game thread. A data frame's inbound budget is
    /// released when it leaves, whether drained or refused.
    /// </summary>
    internal sealed class TransportEventQueue
    {
        public const int Capacity = 10000;

        public readonly InboundByteBudget Budget = new InboundByteBudget();
        private readonly ConcurrentQueue<TransportEvent> _events = new ConcurrentQueue<TransportEvent>();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        /// <summary>False when full; the caller sheds the producer.</summary>
        public bool TryEnqueue(TransportEvent evt)
        {
            if (Interlocked.Increment(ref _count) > Capacity)
            {
                Interlocked.Decrement(ref _count);
                Release(evt);
                return false;
            }
            _events.Enqueue(evt);
            return true;
        }

        /// <summary>A disconnect is always delivered, even past the cap.</summary>
        public void EnqueueAlways(TransportEvent evt)
        {
            Interlocked.Increment(ref _count);
            _events.Enqueue(evt);
        }

        public int Drain(IList<TransportEvent> sink)
        {
            int count = 0;
            while (_events.TryDequeue(out TransportEvent evt))
            {
                Interlocked.Decrement(ref _count);
                sink.Add(evt);
                Release(evt);
                count++;
            }
            return count;
        }

        private void Release(TransportEvent evt)
        {
            if (evt.Type == TransportEventType.Data) Budget.Release(evt.Connection, evt.Payload.Length);
        }
    }
}
