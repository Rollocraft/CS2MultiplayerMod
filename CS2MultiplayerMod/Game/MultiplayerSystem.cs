using Game;
using Unity.Entities;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// ECS heartbeat for multiplayer. Runs at <see cref="global::Game.SystemUpdatePhase.UIUpdate"/>
    /// (every frame, even when paused/in menu) pumping <see cref="MultiplayerService"/>. Also enforces
    /// the "Enable Mod" setting: turning it off closes any active session. Declared <c>partial</c>
    /// because Unity's Entities source generators extend system types.
    /// </summary>
    public partial class MultiplayerSystem : GameSystemBase
    {
        private const long HealthIntervalMs = 30000;

        private EntityQuery _tempEntities;
        private EntityQuery _definitionEntities;
        private long _lastHealthMs;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(MultiplayerSystem) + " created.");

            // Trend counters for the flight log: live preview Temps and definition
            // entities should both hover near zero between edits - either climbing
            // steadily during a session is a leak.
            _tempEntities = GetEntityQuery(ComponentType.ReadOnly<global::Game.Tools.Temp>());
            _definitionEntities = GetEntityQuery(ComponentType.ReadOnly<global::Game.Tools.CreationDefinition>());
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            if (!MultiplayerService.ModEnabled)
            {
                if (service.Session.Role != SessionRole.None)
                {
                    Mod.log.Info("[MP] Mod disabled in settings - closing the active session.");
                    service.Disconnect();
                }
                return;
            }

            service.Update();
            PumpHealth(service);
        }

        /// <summary>
        /// One flight-log line every 30 s while a session runs: memory and entity trends
        /// plus queue depth. After a crash the last lines tell an out-of-memory ramp apart
        /// from a sudden native death (see <see cref="FlightRecorder"/>).
        /// </summary>
        private void PumpHealth(MultiplayerService service)
        {
            MultiplayerSession session = service.Session;
            if (session.Status != SessionStatus.Connected) return;

            long now = service.NowMs;
            if (now - _lastHealthMs < HealthIntervalMs) return;
            _lastHealthMs = now;

            long heapMb = System.GC.GetTotalMemory(false) >> 20;
            long workingSetMb = 0;
            try { workingSetMb = System.Environment.WorkingSet >> 20; } catch { }

            int entities = 0;
            try { entities = EntityManager.Debug.EntityCount; } catch { }

            int peers = 0;
            foreach (Peer peer in session.Peers) if (peer.Handshaked) peers++;

            FlightRecorder.Note("health role=" + session.Role +
                " phase=" + service.WorldPhase +
                " peers=" + peers +
                " heapMB=" + heapMb +
                " wsMB=" + workingSetMb +
                " entities=" + entities +
                " temps=" + _tempEntities.CalculateEntityCount() +
                " defs=" + _definitionEntities.CalculateEntityCount() +
                " sendKB=" + (session.PendingSendBytes >> 10));
        }
    }
}
