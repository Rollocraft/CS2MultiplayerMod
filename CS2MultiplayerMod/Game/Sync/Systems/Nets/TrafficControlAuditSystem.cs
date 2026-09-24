using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Read-only audit for node traffic control (traffic lights / priority / stop state).
    ///
    /// Context: placing, upgrading or removing a traffic light / all-way stop / roundabout
    /// travels as an <c>Upgraded</c> composition change through NetUpgradeSyncSystem, and
    /// crosswalks travel as edge upgrades the same way. So a player-visible traffic-control
    /// change that does NOT arrive as an <c>Upgraded</c> edit would silently diverge both
    /// cities until the next world resync - and nothing would ever log why.
    ///
    /// This system sends nothing, writes no components and needs no protocol change. It
    /// watches <c>Updated</c> nodes and reports the one case the upgrade pipeline cannot
    /// see: the runtime <see cref="TrafficLights"/> presence/flags changing while the
    /// <c>Upgraded</c> flags stay identical (vanilla toggle path, mod edit, or a native
    /// re-init that never converged). Live signal phase (current group / timer) is
    /// deliberately ignored: every machine simulates its own traffic, so phases diverge
    /// by construction and syncing them would fight TrafficLightSystem every few frames.
    ///
    /// If the logs stay quiet, the upgrade path covers everything and no follow-up sync
    /// system is needed. If bypass lines appear, they carry prefab + position + before /
    /// after facts so the follow-up command (PR-A2) can be shaped from real evidence.
    /// </summary>
    public partial class TrafficControlAuditSystem : GameSystemBase
    {
        /// <summary>Settle window after a known upgrade edit for native re-init (10 s).</summary>
        private const long SettleGraceMs = 10000;

        /// <summary>Per-node log throttle for repeat bypass observations (60 s).</summary>
        private const long BypassLogCooldownMs = 60000;

        /// <summary>Session summary interval once bypasses were seen (60 s).</summary>
        private const long SummaryIntervalMs = 60000;

        /// <summary>Dead-entity prune interval (30 s).</summary>
        private const long PruneIntervalMs = 30000;

        private struct Observed
        {
            public uint General, Left, Right;
            public bool HasUpgraded;
            public bool HasLights;
            public byte LightFlags;
            public byte SignalGroups;
            public long SuppressUntilMs;
            public long LastBypassLogMs;
        }

        private readonly Dictionary<Entity, Observed> _observed = new Dictionary<Entity, Observed>();

        private PrefabSystem _prefabSystem;
        private EntityQuery _updatedNodes;
        private EntityQuery _liveNodes;
        private bool _seeded;
        private long _bypassTotal;
        private long _bypassSuppressed;
        private long _bypassReported;
        private long _lastSummaryMs;
        private long _nextPruneMs;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Any player/mod/native traffic-control edit raises Updated on the node,
            // whether or not it carries an Upgraded composition change.
            _updatedNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Node, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Node, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("TrafficControlAudit"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                if (!service.GameplaySyncReady)
                {
                    if (_observed.Count > 0) _observed.Clear();
                    _seeded = false;
                    _bypassTotal = 0;
                    _bypassSuppressed = 0;
                    _bypassReported = 0;
                    _lastSummaryMs = 0;
                    _nextPruneMs = 0;
                    return;
                }

                long now = service.NowMs;

                if (!_seeded)
                {
                    SeedCache();
                    _seeded = true;
                    _nextPruneMs = now + PruneIntervalMs;
                    return;
                }

                ObserveUpdated(now);
                PruneDead(now);
                MaybeSummarize(service, now);
            }
        }

        /// <summary>
        /// Learn the current traffic state of every node when sync starts (both sides hold
        /// the same downloaded world) without logging anything. Without this baseline every
        /// pre-session traffic light would look like a bypass on its first Updated tick.
        /// </summary>
        private void SeedCache()
        {
            NativeArray<Entity> entities = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                    _observed[entities[i]] = Read(entities[i], 0);
                if (entities.Length > 0)
                    SyncLog.Detail(LogTopic.Nets, "TrafficControlAudit: watching " +
                        entities.Length + " node(s), audit only (sends nothing).");
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void ObserveUpdated(long now)
        {
            if (_updatedNodes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _updatedNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!EntityManager.Exists(entity)) continue;
                    Observed current = Read(entity, now);

                    Observed known;
                    if (!_observed.TryGetValue(entity, out known))
                    {
                        // Node appeared after seeding (fresh placement): placements and
                        // upgrades travel through their own sync systems - not a bypass.
                        _observed[entity] = current;
                        continue;
                    }

                    bool upgradeChanged = current.HasUpgraded != known.HasUpgraded ||
                        current.General != known.General ||
                        current.Left != known.Left ||
                        current.Right != known.Right;

                    if (upgradeChanged)
                    {
                        // Known path: NetUpgradeSyncSystem owns this edit. Silence the
                        // node while the native pipeline re-initializes runtime state.
                        _observed[entity] = CarrySuppression(current, known, now + SettleGraceMs);
                        continue;
                    }

                    bool trafficChanged = current.HasLights != known.HasLights ||
                        current.LightFlags != known.LightFlags ||
                        current.SignalGroups != known.SignalGroups;

                    if (!trafficChanged)
                    {
                        // Unrelated Updated (neighbour edit, passing traffic) - keep the
                        // settle suppression already stored, refresh the rest.
                        _observed[entity] = CarrySuppression(current, known, known.SuppressUntilMs);
                        continue;
                    }

                    if (now < known.SuppressUntilMs)
                    {
                        // Native re-init after a known upgrade (Apply strips TrafficLights
                        // and the game rebuilds it) - settling, not a bypass.
                        _observed[entity] = CarrySuppression(current, known, known.SuppressUntilMs);
                        continue;
                    }

                    _observed[entity] = CarrySuppression(current, known, known.SuppressUntilMs);

                    // 0 means "never logged": without the guard the first bypass of a
                    // session started within 60 s of the service clock would be
                    // throttled away and never counted, faking a quiet audit.
                    if (known.LastBypassLogMs != 0 &&
                        now - known.LastBypassLogMs < BypassLogCooldownMs)
                    {
                        _bypassSuppressed++;
                        continue;
                    }

                    _bypassTotal++;

                    // Live node, not a prefab: resolve through PrefabRef like the other
                    // capture systems do (SafeName only takes prefab entities).
                    Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string prefab = PrefabIndex.SafeName(_prefabSystem, prefabEntity);
                    string pos = EntityManager.GetComponentData<Node>(entity).m_Position.ToString();
                    SyncLog.Detail(LogTopic.Nets,
                        "TrafficControlAudit: node traffic state changed without an upgrade edit " +
                        "(bypass path?) at '" + prefab + "' pos=" + pos +
                        ": lights " + (known.HasLights ? "yes" : "no") + "->" +
                        (current.HasLights ? "yes" : "no") +
                        ", flags " + known.LightFlags + "->" + current.LightFlags +
                        ", groups " + known.SignalGroups + "->" + current.SignalGroups +
                        " (bypass #" + _bypassTotal + ", audit only - nothing sent).");

                    // Stamp AFTER the log: known above is still the pre-change snapshot,
                    // current is the post-change state already stored in the cache.
                    Observed stamped = _observed[entity];
                    stamped.LastBypassLogMs = now;
                    _observed[entity] = stamped;
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Store a refreshed snapshot while keeping the settle suppression and the
        /// log throttle of the previous observation. One helper for the four
        /// cache-update sites so the two preserved fields cannot drift apart again.
        /// </summary>
        private static Observed CarrySuppression(Observed current, Observed known, long suppressUntilMs)
        {
            current.SuppressUntilMs = suppressUntilMs;
            current.LastBypassLogMs = known.LastBypassLogMs;
            return current;
        }

        private Observed Read(Entity entity, long now)
        {
            var observed = new Observed { SuppressUntilMs = now };
            if (EntityManager.HasComponent<Upgraded>(entity))
            {
                CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                observed.HasUpgraded = true;
                observed.General = (uint)flags.m_General;
                observed.Left = (uint)flags.m_Left;
                observed.Right = (uint)flags.m_Right;
            }
            if (EntityManager.HasComponent<TrafficLights>(entity))
            {
                TrafficLights lights = EntityManager.GetComponentData<TrafficLights>(entity);
                observed.HasLights = true;
                observed.LightFlags = (byte)lights.m_Flags;
                observed.SignalGroups = lights.m_SignalGroupCount;
            }
            return observed;
        }

        private void PruneDead(long now)
        {
            if (now < _nextPruneMs || _observed.Count == 0) return;
            _nextPruneMs = now + PruneIntervalMs;

            List<Entity> dead = null;
            foreach (Entity entity in _observed.Keys)
            {
                if (EntityManager.Exists(entity)) continue;
                if (dead == null) dead = new List<Entity>();
                dead.Add(entity);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _observed.Remove(dead[i]);
        }

        private void MaybeSummarize(MultiplayerService service, long now)
        {
            if (service == null) return;
            if (_bypassTotal <= _bypassReported) return;
            if (now - _lastSummaryMs < SummaryIntervalMs) return;
            _lastSummaryMs = now;
            _bypassReported = _bypassTotal;
            SyncLog.Detail(LogTopic.Nets, "TrafficControlAudit: " + _bypassTotal +
                " bypass-style traffic change(s) seen this session (" + _bypassSuppressed +
                " further repeat(s) throttled, audit only - " +
                "if this stays at 0, the upgrade path covers all traffic control).");
        }
    }
}
