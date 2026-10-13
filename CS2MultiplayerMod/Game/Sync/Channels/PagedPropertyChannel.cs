using System;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>The sync system behind a <see cref="PagedPropertyChannel{TSnapshot}"/>.</summary>
    internal interface IPagedPropertyRuntime<TSnapshot>
    {
        bool Capture(NetworkWriter writer);
        void Enqueue(TSnapshot snapshot);

        /// <summary>Resolve arrived pages. Read-only against ECS; runs every frame, also while paused.</summary>
        void Pump();

        void ResetPending();
    }

    /// <summary>
    /// Rolling absolute property pages, host to clients. A missed page is repaired by a later sweep, so
    /// bad data or backpressure is dropped locally, never escalated to a resync.
    /// </summary>
    internal abstract class PagedPropertyChannel<TSnapshot> : IStateChannel, IPropertyStateChannel
    {
        private readonly IPagedPropertyRuntime<TSnapshot> _runtime;
        private readonly Func<NetworkReader, TSnapshot> _read;
        private readonly LogTopic _topic;
        private readonly string _name;
        private bool _captureWarned;

        protected PagedPropertyChannel(byte id, IPagedPropertyRuntime<TSnapshot> runtime,
            Func<NetworkReader, TSnapshot> read, LogTopic topic, string name)
        {
            ChannelId = id;
            _runtime = runtime;
            _read = read;
            _topic = topic;
            _name = name;
        }

        public byte ChannelId { get; }

        public bool Capture(EntityManager entityManager, NetworkWriter writer)
        {
            try
            {
                return _runtime != null && _runtime.Capture(writer);
            }
            catch (System.Exception ex)
            {
                // CityState capture has no per-channel exception boundary: fail this page closed so
                // one bad property cannot suppress every other channel.
                if (!_captureWarned)
                {
                    _captureWarned = true;
                    SyncLog.Warn(_topic, _name + ": host capture failed; page skipped " +
                        "(logged once until world reset): " + ex.Message);
                }
                return false;
            }
        }

        public void Apply(EntityManager entityManager, NetworkReader reader) =>
            _runtime.Enqueue(_read(reader));

        public void Pump(EntityManager entityManager)
        {
            if (_runtime != null) _runtime.Pump();
        }

        public void ResetPending()
        {
            _captureWarned = false;
            if (_runtime != null) _runtime.ResetPending();
        }
    }
}
