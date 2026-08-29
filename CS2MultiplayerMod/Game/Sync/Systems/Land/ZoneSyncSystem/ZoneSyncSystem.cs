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
    /// Replicates zone painting in Block entities (one per road-edge side): detect Updated
    /// cells at ModificationEnd and broadcast full zoning plus its source geometry. Realize at
    /// ToolUpdate via <see cref="SyncRealizeSystem"/>, map visible source cells by world position
    /// onto locally generated blocks, write zones, and tag those blocks Updated. Bursts are
    /// latest-state coalesced and spread across frames; persistent content hashes suppress
    /// unchanged Updated churn and replication echoes.
    /// </summary>
    public partial class ZoneSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private readonly LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand> _outgoing =
            new LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand>();
        private readonly LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand> _ready =
            new LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand>();

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
        private readonly LatestByKeyQueue<ZoneBlockKey, PendingZone> _pending =
            new LatestByKeyQueue<ZoneBlockKey, PendingZone>();
        private long _lastRetryMs;
        private const long ZoneRetryIntervalMs = 500;
        private const long ZoneRetryWindowMs = 12000;
        private const int MaxPendingZones = 8192;
        private const int MaxIncomingZones = 8192;
        private const int MaxBufferedOutgoingZones = 32768;
        private const int MaxDecodePerFrame = 64;
        private const int MaxSendPerFrame = 16;
        private const int MaxApplyPerFrame = 24;

        private struct PendingZone { public ZonePaintCommand Command; public long DeadlineMs; }

        // Capture baselines make the full-block command idempotent across frames. Updated is a
        // broad game tag and may recur even when zoning did not change. Entity identity prevents a
        // rebuilt road block at the same position from inheriting a stale content baseline.
        private readonly Dictionary<ZoneBlockKey, ZoneBaseline> _lastZoneStates =
            new Dictionary<ZoneBlockKey, ZoneBaseline>();

        private struct ZoneBlockKey : System.IEquatable<ZoneBlockKey>
        {
            public long Position;
            public int DirectionX;
            public int DirectionZ;
            public int SizeX;
            public int SizeY;

            public bool Equals(ZoneBlockKey other) =>
                Position == other.Position && DirectionX == other.DirectionX &&
                DirectionZ == other.DirectionZ && SizeX == other.SizeX && SizeY == other.SizeY;

            public override bool Equals(object obj) => obj is ZoneBlockKey && Equals((ZoneBlockKey)obj);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)(Position ^ (Position >> 32));
                    hash = hash * 397 ^ DirectionX;
                    hash = hash * 397 ^ DirectionZ;
                    hash = hash * 397 ^ SizeX;
                    return hash * 397 ^ SizeY;
                }
            }
        }

        private struct ZoneBaseline
        {
            public Entity Entity;
            public int Hash;
        }

        // Reusing the spatial index for a short window avoids rescanning every Block for every
        // source cell in a large zoning burst. Each list is pooled across rebuilds, and stale
        // entities are validated before use.
        private readonly Dictionary<long, List<Entity>> _blockLookup =
            new Dictionary<long, List<Entity>>();
        private readonly List<List<Entity>> _blockLookupListPool = new List<List<Entity>>();
        private bool _blockLookupBuilt;
        private long _blockLookupBuiltAtMs;
        private const long BlockLookupRefreshMs = 500;
        private const float BlockLookupBucketSize = 32f;

        private long _diagnosticWindowStartMs = -1;
        private int _diagnosticCaptured;
        private int _diagnosticSent;
        private int _diagnosticDecoded;
        private int _diagnosticCoalesced;
        private int _diagnosticApplied;
        private int _diagnosticDeferred;
        private int _diagnosticExpired;
        private bool _outgoingOverflowWarned;
        private const long DiagnosticWindowMs = 5000;

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

            _observer = new CommandObserver(_incoming, ZonePaintCommand.Id)
            {
                // A legacy peer may still deliver a large one-frame zoning burst. Keep it
                // bounded, but large enough for this system's frame-budgeted coalescer.
                QueueCap = MaxIncomingZones,
                MaxBodyBytes = ZonePaintCommand.MaxEncodedBytes,
            };
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            if (_observer != null && Mod.Service?.Session != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        private bool _registered;

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _outgoing.Clear();
            _ready.Clear();
            _pending.Clear();
            _guard.Clear();
            ClearBlockLookup();
            _blockLookupBuilt = false;
            _lastRetryMs = 0;
            _outgoingOverflowWarned = false;
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady)
            {
                _registered = false;
                return;
            }

            if (!_registered && session != null)
            {
                session.AddObserver(_observer);
                _registered = true;
            }

            long now = service.NowMs;
            _guard.Prune(now);
            CaptureUpdatedBlocks(now);
            FlushOutgoing(session);
            FlushDiagnostics(now);
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;

            SimulationCommandMessage message;
            int examined = 0;
            while (examined < MaxDecodePerFrame && _incoming.TryDequeue(out message))
            {
                examined++;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    ZonePaintCommand command = ZonePaintCommand.Decode(message.Body);
                    ZoneBlockKey key = StateKey(command);
                    bool coalesced = _ready.ContainsKey(key) || _pending.Remove(key);
                    if (!_ready.TrySetLatest(key, command, MaxIncomingZones))
                    {
                        RecoverFromQueueOverflow("zone ready-state coalescer overflow");
                        break;
                    }
                    _diagnosticDecoded++;
                    if (coalesced) _diagnosticCoalesced++;
                }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] ZoneSync: dropping malformed command: " + ex.Message);
                }
            }

            // Always process fresh commands; retry deferred ones on a timer (their blocks
            // may have finished generating since the last attempt).
            bool retryDue = _pending.Count > 0 && now - _lastRetryMs >= ZoneRetryIntervalMs;
            if (_ready.Count > 0 || retryDue) ApplyZoneCommands(retryDue, now);
        }

        private void RecoverFromQueueOverflow(string reason)
        {
            SyncInbox.Clear(_incoming);
            _ready.Clear();
            _pending.Clear();
            SyncInbox.RequestResync(reason);
            Mod.log.Warn("[MP] ZoneSync overflowed its bounded latest-state queue; " +
                         "requesting a fresh world sync.");
        }

        private void FlushDiagnostics(long now)
        {
            if (_diagnosticWindowStartMs < 0) _diagnosticWindowStartMs = now;
            if (now - _diagnosticWindowStartMs < DiagnosticWindowMs) return;

            if (_diagnosticCaptured > 0 || _diagnosticSent > 0 ||
                _diagnosticDecoded > 0 || _diagnosticApplied > 0 ||
                _diagnosticDeferred > 0 || _diagnosticExpired > 0)
            {
                Mod.Verbose("[MP] ZoneSync/5s: captured=" + _diagnosticCaptured +
                            " sent=" + _diagnosticSent +
                            " decoded=" + _diagnosticDecoded +
                            " coalesced=" + _diagnosticCoalesced +
                            " applied=" + _diagnosticApplied +
                            " deferred=" + _diagnosticDeferred +
                            " expired=" + _diagnosticExpired +
                            " queues(out=" + _outgoing.Count +
                            ", inbox=" + _incoming.Count +
                            ", ready=" + _ready.Count +
                            ", retry=" + _pending.Count + ").");
            }

            _diagnosticCaptured = 0;
            _diagnosticSent = 0;
            _diagnosticDecoded = 0;
            _diagnosticCoalesced = 0;
            _diagnosticApplied = 0;
            _diagnosticDeferred = 0;
            _diagnosticExpired = 0;
            _diagnosticWindowStartMs = now;
        }


        // Blocks we have ever seen zoned — lets us sync "unzone" without broadcasting the
        // constant churn of never-zoned blocks.
        private readonly HashSet<ZoneBlockKey> _zonedBlocks = new HashSet<ZoneBlockKey>();




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
                    string name = PrefabIndex.SafeName(_prefabSystem, prefabs[i]);
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

        private static ZoneBlockKey StateKey(Block block) =>
            StateKey(block.m_Position, block.m_Direction, block.m_Size.x, block.m_Size.y);

        private static ZoneBlockKey StateKey(ZonePaintCommand command) =>
            StateKey(new float3(command.PosX, command.PosY, command.PosZ),
                     new float2(command.DirX, command.DirZ), command.SizeX, command.SizeY);

        private static ZoneBlockKey StateKey(float3 position, float2 direction, int sizeX, int sizeY) =>
            new ZoneBlockKey
            {
                Position = QuantizedPos(position),
                DirectionX = (int)math.round(direction.x * 4096f),
                DirectionZ = (int)math.round(direction.y * 4096f),
                SizeX = sizeX,
                SizeY = sizeY,
            };

        private static string BlockKey(ZoneBlockKey key, int contentHash) =>
            "zone|" + key.Position + "|" + key.DirectionX + "|" + key.DirectionZ + "|" +
            key.SizeX + "|" + key.SizeY + "|" + contentHash;


    }
}
