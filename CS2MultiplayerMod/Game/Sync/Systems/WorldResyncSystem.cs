using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game;
using Game.SceneFlow;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Drift-correcting backbone: host saves live world and streams to clients on join,
    /// fixed interval (default 15 min), or on-demand (/sync). Clients reload via
    /// <see cref="JoinMapLoader"/>. Saving via <see cref="AutoSaveSystem.PerformAutoSave"/>
    /// is async; system polls and streams the result.
    /// </summary>
    public partial class WorldResyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<ConnectionId> _joinTargets = new ConcurrentQueue<ConnectionId>();
        private readonly List<ConnectionId> _targets = new List<ConnectionId>();

        private AutoSaveSystem _autoSave;
        private Observer _observer;
        private Task _saveTask;
        private bool _saving;
        private long _lastResyncMs = -1;
        private long _saveStartMs;
        private DateTime _saveStartUtc;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(WorldResyncSystem) + " ready.");
            _autoSave = World.GetOrCreateSystemManaged<AutoSaveSystem>();

            if (Mod.Service != null)
            {
                _observer = new Observer(_joinTargets);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (session.Role != SessionRole.Host || !service.GameplaySyncReady) return;

            long now = service.NowMs;

            // A save is in flight — wait for it, then stream to everyone who is waiting.
            if (_saving)
            {
                if (_saveTask != null && !_saveTask.IsCompleted) return;

                if (_saveTask != null && _saveTask.IsFaulted)
                {
                    // Explicit fallback: the fresh save failed, so the best available
                    // state is whatever save already exists. Said out loud in the log.
                    Mod.log.Warn("[MP] World re-sync save faulted (" +
                                 (_saveTask.Exception != null ? _saveTask.Exception.Message : "?") +
                                 "); streaming the newest existing save instead.");
                    for (int i = 0; i < _targets.Count; i++) service.StreamWorld(_targets[i]);
                }
                else
                {
                    Mod.log.Info("[MP] World re-sync: live save completed in " + (now - _saveStartMs) + " ms; streaming to " + _targets.Count + " target(s).");
                    // Stream only the save the just-completed task actually produced —
                    // never an unrelated file that happens to be newest in the folder.
                    for (int i = 0; i < _targets.Count; i++) service.StreamWorld(_targets[i], _saveStartUtc);
                }
                _targets.Clear();
                _saving = false;
                _saveTask = null;
                _lastResyncMs = now;
                return;
            }

            // Collect targets: peers that just joined, plus a periodic broadcast.
            ConnectionId joined;
            while (_joinTargets.TryDequeue(out joined))
                if (!_targets.Contains(joined)) _targets.Add(joined);

            if (_lastResyncMs < 0) _lastResyncMs = now; // arm the interval timer on first run
            else if (HasPeers(session) && now - _lastResyncMs >= service.ResyncIntervalMs && !_targets.Contains(ConnectionId.None))
                _targets.Add(ConnectionId.None);

            if (_targets.Count == 0) return;

            StartSave(service);
        }

        private void StartSave(MultiplayerService service)
        {
            try
            {
                // Capture the wall clock just before the save so the completed file can
                // be identified by "written after this moment" (with a small margin for
                // clock granularity).
                _saveStartUtc = DateTime.UtcNow.AddSeconds(-5);
                _saveTask = _autoSave.PerformAutoSave(GameManager.instance.settings.general);
                _saving = true;
                _saveStartMs = service.NowMs;
                Mod.log.Info("[MP] World re-sync: saving the live world before streaming to " + _targets.Count + " target(s)...");
            }
            catch (Exception ex)
            {
                // Fall back to streaming whatever save already exists.
                Mod.log.Warn("[MP] World re-sync: could not trigger a save (" + ex.Message + "); streaming newest existing save.");
                for (int i = 0; i < _targets.Count; i++) service.StreamWorld(_targets[i]);
                _targets.Clear();
                _lastResyncMs = service.NowMs;
            }
        }

        private static bool HasPeers(MultiplayerSession session)
        {
            foreach (var peer in session.Peers)
                if (peer.Handshaked) return true;
            return false;
        }

        private sealed class Observer : SessionObserver
        {
            private readonly ConcurrentQueue<ConnectionId> _sink;
            public Observer(ConcurrentQueue<ConnectionId> sink) { _sink = sink; }

            public override void OnPeerJoined(Peer peer)
            {
                _sink.Enqueue(peer.Connection);
                Mod.log.Info("[MP] World re-sync: queued initial world stream for " + peer + ".");
            }

            // /sync: a requesting client's connection, or ConnectionId.None when the
            // host asked to refresh everyone - both are just targets for the next save.
            public override void OnResyncRequested(int playerId, ConnectionId connection)
            {
                _sink.Enqueue(connection);
                Mod.log.Info("[MP] World re-sync: queued manual request from player #" + playerId +
                             " for " + (connection.IsNone ? "all clients" : connection.ToString()) + ".");
            }
        }
    }
}
