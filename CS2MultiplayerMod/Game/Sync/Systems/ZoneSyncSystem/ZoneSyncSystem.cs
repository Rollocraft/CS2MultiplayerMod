using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates zone painting in Block entities (one per road-edge side from synced
    /// road layout): detect Updated cells at ModificationEnd, broadcast full zoning.
    /// Realize at ToolUpdate via <see cref="SyncRealizeSystem"/>, find local block, write
    /// zones, tag Updated. Echo-damped via content-hash guard and no-op writes.
    /// </summary>
    public partial class ZoneSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        private PrefabSystem _prefabSystem;
        private EntityQuery _updatedBlocks;
        private EntityQuery _allBlocks;
        private EntityQuery _zonePrefabs;
        private CommandObserver _observer;

        // A zone command whose target Block doesn't exist yet — the road, or the zoning
        // grid the game generates for it, hasn't finished building on this machine — is
        // deferred and retried until it matches or times out. This lag (zoning right after
        // laying road) was the main reason zoning "didn't sync": the old apply matched once
        // and dropped every miss.
        private readonly List<PendingZone> _pending = new List<PendingZone>();
        private long _lastRetryMs;
        private const long ZoneRetryIntervalMs = 500;
        private const long ZoneRetryWindowMs = 12000;
        private const int MaxPendingZones = 8192;

        private struct PendingZone { public ZonePaintCommand Command; public long DeadlineMs; }

        // ZoneType.m_Index <-> prefab name, rebuilt whenever an unknown index appears
        // (zone prefabs can register late, e.g. DLC/mod zones).
        private readonly Dictionary<ushort, string> _indexToName = new Dictionary<ushort, string>();
        private readonly Dictionary<string, ushort> _nameToIndex = new Dictionary<string, ushort>();

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(ZoneSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _updatedBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>(),
                    ComponentType.ReadOnly<Cell>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    // Newly created blocks (fresh road) start unzoned on every machine —
                    // syncing them would only be noise.
                    ComponentType.ReadOnly<Created>(),
                },
            });

            _allBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>(),
                    ComponentType.ReadOnly<Cell>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _zonePrefabs = GetEntityQuery(
                ComponentType.ReadOnly<ZoneData>(),
                ComponentType.ReadOnly<PrefabData>());

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, ZonePaintCommand.Id);
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
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;
            _guard.Prune(now);
            CaptureUpdatedBlocks(session, now);
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;

            List<ZonePaintCommand> incoming = null;
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try { (incoming ?? (incoming = new List<ZonePaintCommand>())).Add(ZonePaintCommand.Decode(message.Body)); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] ZoneSync: dropping malformed command: " + ex.Message); }
            }

            // Always process fresh commands; retry deferred ones on a timer (their blocks
            // may have finished generating since the last attempt).
            bool retryDue = _pending.Count > 0 && now - _lastRetryMs >= ZoneRetryIntervalMs;
            if (incoming != null || retryDue) ApplyZoneCommands(incoming, retryDue, now);
        }


        // Blocks we have ever seen zoned — lets us sync "unzone" without broadcasting the
        // constant churn of never-zoned blocks.
        private readonly HashSet<long> _zonedBlocks = new HashSet<long>();




        private string ResolveZoneName(ushort index)
        {
            string name;
            if (_indexToName.TryGetValue(index, out name)) return name;
            RebuildZoneMap();
            return _indexToName.TryGetValue(index, out name) ? name : null;
        }

        private bool TryResolveZoneIndex(string name, out ushort index)
        {
            RebuildZoneMap();
            return _nameToIndex.TryGetValue(name, out index);
        }

        private void RebuildZoneMap()
        {
            _indexToName.Clear();
            _nameToIndex.Clear();

            NativeArray<Entity> prefabs = _zonePrefabs.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    ushort index = EntityManager.GetComponentData<ZoneData>(prefabs[i]).m_ZoneType.m_Index;
                    string name = _prefabSystem.GetPrefabName(prefabs[i]);
                    if (string.IsNullOrEmpty(name)) continue;
                    _indexToName[index] = name;
                    _nameToIndex[name] = index;
                }
            }
            finally
            {
                prefabs.Dispose();
            }
        }

        private static long QuantizedPos(float3 position)
        {
            // 0.5 m buckets packed into a single key (blocks are metres apart, so this is
            // far finer than block spacing yet tolerant of float drift).
            return PackQuant((long)math.round(position.x * 2f),
                             (long)math.round(position.y * 2f),
                             (long)math.round(position.z * 2f));
        }

        private static long PackQuant(long qx, long qy, long qz) =>
            ((qx & 0x1FFFFF) << 42) | ((qy & 0x1FFFFF) << 21) | (qz & 0x1FFFFF);

        private static string BlockKey(float3 position, int contentHash) =>
            "zone|" + QuantizedPos(position) + "|" + contentHash;


    }
}
