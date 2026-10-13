using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Sync; // Shared scheduling and queues.
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates selected cells from zoning commits as sparse patches; receivers retain unresolved
    /// cells while their roads and grids catch up.
    /// </summary>
    public partial class ZoneSyncSystem : CommandSyncSystem, IRealizeStage
    {
        private readonly ActiveRetryClock _retryClock = new ActiveRetryClock();
        private readonly LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand> _outgoing =
            new LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand>();
        private readonly LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand> _ready =
            new LatestByKeyQueue<ZoneBlockKey, ZonePaintCommand>();

        private PrefabSystem _prefabSystem;
        private EntityQuery _zonePreviews;
        private ToolSystem _toolSystem;
        private EntityQuery _allBlocks;
        private EntityQuery _zonePrefabs;

        // Commands whose Block does not exist yet (zoning right after laying road); retried until timeout.
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

        private struct PendingZone
        {
            public ZonePaintCommand Command;
            public long DeadlineMs;
            public ResyncReport RecoveryReport;
        }

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

        // Short-lived spatial index of Blocks; entries are validated before use.
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
        private int _diagnosticUnzonable;
        private const long DiagnosticWindowMs = 5000;

        // Rebuilt when an unknown index appears: DLC and mod zones register late.
        private readonly Dictionary<ushort, string> _indexToName = new Dictionary<ushort, string>();
        private readonly Dictionary<string, ushort> _nameToIndex = new Dictionary<string, ushort>();

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _zonePreviews = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Block, Cell, Temp>(),
                None = SyncQuery.ReadOnly<Deleted>(),
            });

            _allBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Block, Cell>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _zonePrefabs = GetEntityQuery(
                ComponentType.ReadOnly<ZoneData>(),
                ComponentType.ReadOnly<PrefabData>());

            // A marquee can cover many blocks in one commit.
            ListenFor(new[] { ZonePaintCommand.Id },
                ZonePaintCommand.MaxEncodedBytes, MaxIncomingZones);
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _outgoing.Clear();
            _ready.Clear();
            _pending.Clear();
            _retryClock.Reset();
            ClearBlockLookup();
            _blockLookupBuilt = false;
            _lastRetryMs = 0;
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("ZoneSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady) return;

                long now = service.NowMs;

                FlushOutgoing(session);
                FlushDiagnostics(now);
            }
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            // Zoning is laid on the road edges the held net pipeline has not delivered yet.
            if (RealizeGate.WorldBuildingHeld)
            {
                _retryClock.Observe(service.NowMs, true);
                return;
            }

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;
            _retryClock.Observe(now, false);

            int examined = 0;
            while (examined < MaxDecodePerFrame && _incoming.TryDequeue(out SimulationCommandMessage message))
            {
                examined++;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    ZonePaintCommand command = ZonePaintCommand.Decode(message.Body);
                    ZoneBlockKey key = StateKey(command);
                    bool coalesced = _ready.TryGetValue(key, out ZonePaintCommand earlier);
                    if (coalesced) command.MergeEarlier(earlier);
                    if (_pending.TryGetValue(key, out PendingZone pending))
                    {
                        command.MergeEarlier(pending.Command);
                        WithdrawZoneRecovery(pending, now);
                        _pending.Remove(key);
                        coalesced = true;
                    }
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
                    SyncLog.Warn(LogTopic.Land, "ZoneSync: dropping malformed command: " +
                        ex.Message);
                }
            }

            bool retryDue = _pending.Count > 0 && now - _lastRetryMs >= ZoneRetryIntervalMs;
            if (_ready.Count > 0 || retryDue) ApplyZoneCommands(retryDue, now);
        }

        private void RecoverFromQueueOverflow(string reason)
        {
            SyncInbox.Clear(_incoming);
            _outgoing.Clear();
            _ready.Clear();
            _pending.Clear();
            SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                .Create(reason, "zone", CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                .About("zone latest-state queue")
                .Tried("nothing - the bounded queue was full and its zoning changes were shed"));
            SyncLog.Warn(LogTopic.Land, "ZoneSync overflowed its bounded latest-state queue; " +
                "requesting a fresh world sync.");
        }

        private void FlushDiagnostics(long now)
        {
            if (_diagnosticWindowStartMs < 0) _diagnosticWindowStartMs = now;
            if (now - _diagnosticWindowStartMs < DiagnosticWindowMs) return;

            if (_diagnosticCaptured > 0 || _diagnosticSent > 0 ||
                _diagnosticDecoded > 0 || _diagnosticApplied > 0 ||
                _diagnosticDeferred > 0 || _diagnosticExpired > 0 || _diagnosticUnzonable > 0)
            {
                SyncLog.Detail(LogTopic.Land, "ZoneSync/5s: captured=" + _diagnosticCaptured +
                    " sent=" + _diagnosticSent + " decoded=" + _diagnosticDecoded + " coalesced=" +
                    _diagnosticCoalesced + " applied=" + _diagnosticApplied + " deferred=" +
                    _diagnosticDeferred + " expired=" + _diagnosticExpired + " unzonable=" +
                    _diagnosticUnzonable + " queues(out=" +
                    _outgoing.Count + ", inbox=" + _incoming.Count + ", ready=" + _ready.Count +
                    ", retry=" + _pending.Count + ").");
            }

            _diagnosticCaptured = 0;
            _diagnosticSent = 0;
            _diagnosticDecoded = 0;
            _diagnosticCoalesced = 0;
            _diagnosticApplied = 0;
            _diagnosticDeferred = 0;
            _diagnosticExpired = 0;
            _diagnosticUnzonable = 0;
            _diagnosticWindowStartMs = now;
        }

        private string ResolveZoneName(ushort index)
        {
            if (_indexToName.TryGetValue(index, out string name)) return name;
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
            // 0.5 m buckets: far finer than block spacing, tolerant of float drift.
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
    }
}
