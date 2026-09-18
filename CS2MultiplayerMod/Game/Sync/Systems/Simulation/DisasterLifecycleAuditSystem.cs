using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Read-only audit for the disaster lifecycle after <see cref="DisasterSyncSystem"/>
    /// replicates the start of an event.
    ///
    /// Context: one <see cref="DisasterEventCommand"/> per event carries the state the game
    /// resolves once at creation (place, size, duration, strength) and every machine then runs
    /// the event with its own simulation. The storm's path, the surge curve and the actual end
    /// are never replicated, so two cities can watch the same tornado take different streets,
    /// or one side can drop the event early through the game's concurrent-event limit while the
    /// other plays it to the end - and nothing would ever log why the aftermath differs.
    ///
    /// This system sends nothing, writes no components and needs no protocol change. It tracks
    /// each active event from its <c>Created</c> frame until the entity is gone, logs where it
    /// started, where it is once a minute while it runs, and how it ended (natural end versus
    /// early drop, actual lived frames versus the commanded duration). That is the exact input
    /// a follow-up Verlauf sync would need: which path each side took and where they parted.
    /// It only runs while the City log topic is on; with the topic off it suspends
    /// tracking and re-seeds on re-enable (session totals kept), so a troubleshooting
    /// audit never costs a normal session a frame.
    ///
    /// If the heartbeat lines show both machines walking the same path and every event ends on
    /// time, the start-only replication covers everything and no follow-up sync is needed. If
    /// paths part or early drops appear, the lines carry prefab + positions + frames so the
    /// follow-up command (PR-B2) can be shaped from real evidence.
    /// </summary>
    public partial class DisasterLifecycleAuditSystem : GameSystemBase
    {
        /// <summary>Position/intensity heartbeat per active event (60 s).</summary>
        private const long HeartbeatIntervalMs = 60000;

        /// <summary>Session summary interval once events were seen (60 s).</summary>
        private const long SummaryIntervalMs = 60000;

        /// <summary>How often the tracked table is swept for ended events (30 s).</summary>
        private const long ReapIntervalMs = 30000;

        private struct Tracked
        {
            public DisasterKind Kind;
            public string PrefabName;
            public bool Damaging;
            public long StartFrame;
            public long EndFrame;
            public bool HasCommandedEnd;
            public long FirstSeenFrame;
            public long LastHeartbeatMs;
            public float FirstX, FirstY, FirstZ;
            public float FirstIntensity;
        }

        private readonly Dictionary<Entity, Tracked> _tracked = new Dictionary<Entity, Tracked>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private SimulationSystem _simulation;
        private EntityQuery _createdPhenomena;
        private EntityQuery _createdSurges;
        private EntityQuery _livePhenomena;
        private EntityQuery _liveSurges;
        private bool _seeded;
        private long _startedTotal;
        private long _endedTotal;
        private long _earlyTotal;
        private long _reportedStarted;
        private long _reportedEnded;
        private long _lastSummaryMs;
        private long _nextReapMs;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();

            // Same shapes DisasterSyncSystem captures: a moving hotspot over a fixed area,
            // and a global water-height curve. The flood marker stays excluded for the same
            // reason: the rain-driven river flood is derived from the replicated weather on
            // every machine, so tracking it here would log one local event per peer.
            _createdPhenomena = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event,
                    global::Game.Events.WeatherPhenomenon, Created>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });

            _createdSurges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event,
                    global::Game.Events.WaterLevelChange, Created>(),
                None = SyncQuery.ReadOnly<global::Game.Events.Flood, Deleted, Temp>(),
            });

            _livePhenomena = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event,
                    global::Game.Events.WeatherPhenomenon, global::Game.Events.Duration>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });

            _liveSurges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event,
                    global::Game.Events.WaterLevelChange, global::Game.Events.Duration>(),
                None = SyncQuery.ReadOnly<global::Game.Events.Flood, Deleted, Temp>(),
            });
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("DisasterLifecycleAudit"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                if (!service.GameplaySyncReady)
                {
                    ResetState();
                    return;
                }

                // Troubleshooting-only audit, so it stays off with the topic: every line
                // below is a gated Detail, and SyncLog asks callers not to compute what
                // nobody reads. Suspend (not reset) so a later re-enable re-seeds instead
                // of flushing stale end lines for events that died while unwatched,
                // while the session totals stay intact.
                if (!SyncLog.IsEnabled(LogTopic.City))
                {
                    SuspendTracking();
                    return;
                }

                long now = service.NowMs;

                if (!_seeded)
                {
                    SeedKnown(now);
                    _seeded = true;
                    _nextReapMs = now + ReapIntervalMs;
                    return;
                }

                ObserveCreated(now);
                HeartbeatActive(now);
                ReapEnded(now);
                MaybeSummarize(now);
            }
        }

        /// <summary>
        /// Forget everything: the session ended. The next session re-seeds from its
        /// own downloaded world, so nothing stale is ever reported.
        /// </summary>
        private void ResetState()
        {
            SuspendTracking();
            _startedTotal = 0;
            _endedTotal = 0;
            _earlyTotal = 0;
            _reportedStarted = 0;
            _reportedEnded = 0;
            _lastSummaryMs = 0;
        }

        /// <summary>
        /// Drop the live table but keep the session totals: the City topic went off.
        /// A later re-enable re-seeds instead of flushing stale end lines for events
        /// that died while unwatched, without rewriting the session history.
        /// </summary>
        private void SuspendTracking()
        {
            if (_tracked.Count > 0) _tracked.Clear();
            _seeded = false;
            _nextReapMs = 0;
        }

        /// <summary>
        /// Learn the events already running when sync starts (both sides hold the same
        /// downloaded world) without logging them as new. Without this baseline every
        /// pre-session storm would look like a fresh start on its next heartbeat.
        /// </summary>
        private void SeedKnown(long now)
        {
            long frame = (long)_simulation.frameIndex;
            int seeded = 0;
            seeded += SeedQuery(_livePhenomena, DisasterKind.WeatherPhenomenon, now, frame);
            seeded += SeedQuery(_liveSurges, DisasterKind.WaterLevelChange, now, frame);
            if (seeded > 0)
                SyncLog.Detail(LogTopic.City, "DisasterLifecycleAudit: watching " +
                    seeded + " already-running event(s), audit only (sends nothing).");
        }

        private int SeedQuery(EntityQuery query, DisasterKind kind, long now, long frame)
        {
            if (query.IsEmptyIgnoreFilter) return 0;
            int seeded = 0;

            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (_tracked.ContainsKey(entity)) continue;
                    Tracked tracked;
                    if (!TryTrack(entity, kind, now, frame, out tracked)) continue;
                    _tracked[entity] = tracked;
                    seeded++;
                }
            }
            finally
            {
                entities.Dispose();
            }
            return seeded;
        }

        private void ObserveCreated(long now)
        {
            long frame = (long)_simulation.frameIndex;
            ObserveCreatedQuery(_createdPhenomena, DisasterKind.WeatherPhenomenon, now, frame);
            ObserveCreatedQuery(_createdSurges, DisasterKind.WaterLevelChange, now, frame);
        }

        private void ObserveCreatedQuery(EntityQuery query, DisasterKind kind, long now, long frame)
        {
            if (query.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (_tracked.ContainsKey(entity)) continue;
                    Tracked tracked;
                    if (!TryTrack(entity, kind, now, frame, out tracked)) continue;
                    _tracked[entity] = tracked;
                    _startedTotal++;

                    string name = tracked.PrefabName ?? "unknown prefab";
                    string lasting = tracked.HasCommandedEnd
                        ? "lasting " + (tracked.EndFrame - tracked.StartFrame) + " frame(s)"
                        : "lasting unknown frame(s)";
                    string detail = "'" + name + "' (" + kind + ") at " +
                        tracked.FirstX + ", " + tracked.FirstY + ", " + tracked.FirstZ +
                        ", warning " + (tracked.StartFrame - frame) + " frame(s), " + lasting;
                    if (tracked.Damaging)
                        SyncLog.Detail(LogTopic.City, "DisasterLifecycleAudit: tracking " + detail + ".");
                    else
                        SyncLog.Detail(LogTopic.City, "DisasterLifecycleAudit: tracking weather " + detail + ".");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Read one event's creation state into a tracked record. False only for the
        /// rain-driven surge every machine derives locally: tracking it would log one event
        /// per peer for a storm both sides already share through the weather channel.
        /// A missing prefab name or duration never refuses tracking; the record keeps what
        /// the entity had and the log says what it could not resolve.
        /// </summary>
        private bool TryTrack(Entity entity, DisasterKind kind, long now, long frame, out Tracked tracked)
        {
            tracked = new Tracked
            {
                Kind = kind,
                FirstSeenFrame = frame,
                LastHeartbeatMs = now,
                StartFrame = frame,
                EndFrame = frame,
            };

            Entity prefab = Entity.Null;
            if (EntityManager.HasComponent<PrefabRef>(entity))
                prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab != Entity.Null && EntityManager.HasComponent<PrefabData>(prefab))
            {
                tracked.PrefabName = _prefabIndex.NameOf(prefab);
                tracked.Damaging = IsDamaging(prefab);
                if (kind == DisasterKind.WaterLevelChange && IsRainControlled(prefab)) return false;
            }

            if (EntityManager.HasComponent<global::Game.Events.Duration>(entity))
            {
                global::Game.Events.Duration duration =
                    EntityManager.GetComponentData<global::Game.Events.Duration>(entity);
                tracked.StartFrame = duration.m_StartFrame;
                tracked.EndFrame = duration.m_EndFrame;
                tracked.HasCommandedEnd = true;
            }

            if (kind == DisasterKind.WeatherPhenomenon &&
                EntityManager.HasComponent<global::Game.Events.WeatherPhenomenon>(entity))
            {
                global::Game.Events.WeatherPhenomenon phenomenon =
                    EntityManager.GetComponentData<global::Game.Events.WeatherPhenomenon>(entity);
                tracked.FirstX = phenomenon.m_PhenomenonPosition.x;
                tracked.FirstY = phenomenon.m_PhenomenonPosition.y;
                tracked.FirstZ = phenomenon.m_PhenomenonPosition.z;
            }
            else if (kind == DisasterKind.WaterLevelChange &&
                EntityManager.HasComponent<global::Game.Events.WaterLevelChange>(entity))
            {
                global::Game.Events.WaterLevelChange surge =
                    EntityManager.GetComponentData<global::Game.Events.WaterLevelChange>(entity);
                tracked.FirstX = 0f;
                tracked.FirstY = surge.m_DangerHeight;
                tracked.FirstZ = 0f;
                tracked.FirstIntensity = surge.m_MaxIntensity;
            }

            return true;
        }

        /// <summary>
        /// Once a minute per running event, log where the Verlauf stands: current position
        /// or intensity next to the start values and the frames left. Two peers running the
        /// same command produce one line each per minute; if the lines agree for whole
        /// sessions, start-only replication holds, and where they part is where a Verlauf
        /// command would have to take over.
        /// </summary>
        private void HeartbeatActive(long now)
        {
            if (_tracked.Count == 0) return;

            // No per-frame allocation on the common path: most frames nothing is due.
            // The key snapshot below only runs in a minute where a line is actually logged.
            bool anyDue = false;
            foreach (Tracked candidate in _tracked.Values)
            {
                if (now - candidate.LastHeartbeatMs >= HeartbeatIntervalMs)
                {
                    anyDue = true;
                    break;
                }
            }
            if (!anyDue) return;

            long frame = (long)_simulation.frameIndex;

            List<Entity> keys = new List<Entity>(_tracked.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                Entity entity = keys[i];
                Tracked tracked;
                if (!_tracked.TryGetValue(entity, out tracked)) continue;
                if (now - tracked.LastHeartbeatMs < HeartbeatIntervalMs) continue;
                if (!EntityManager.Exists(entity)) continue;
                if (EntityManager.HasComponent<Deleted>(entity)) continue;

                tracked.LastHeartbeatMs = now;
                _tracked[entity] = tracked;

                string name = tracked.PrefabName ?? "unknown prefab";
                long remaining = tracked.EndFrame - frame;
                if (tracked.Kind == DisasterKind.WeatherPhenomenon &&
                    EntityManager.HasComponent<global::Game.Events.WeatherPhenomenon>(entity))
                {
                    global::Game.Events.WeatherPhenomenon phenomenon =
                        EntityManager.GetComponentData<global::Game.Events.WeatherPhenomenon>(entity);
                    SyncLog.Detail(LogTopic.City, "DisasterLifecycleAudit: '" + name + "' running at " +
                        phenomenon.m_PhenomenonPosition + " (started at " +
                        tracked.FirstX + ", " + tracked.FirstY + ", " + tracked.FirstZ +
                        "), radius " + phenomenon.m_PhenomenonRadius + ", " +
                        remaining + " frame(s) left (audit only - nothing sent).");
                }
                else if (tracked.Kind == DisasterKind.WaterLevelChange &&
                    EntityManager.HasComponent<global::Game.Events.WaterLevelChange>(entity))
                {
                    global::Game.Events.WaterLevelChange surge =
                        EntityManager.GetComponentData<global::Game.Events.WaterLevelChange>(entity);
                    SyncLog.Detail(LogTopic.City, "DisasterLifecycleAudit: '" + name + "' surge intensity " +
                        surge.m_MaxIntensity + " (started at " + tracked.FirstIntensity + "), " +
                        remaining + " frame(s) left (audit only - nothing sent).");
                }
            }
        }

        /// <summary>
        /// Sweep the tracked table for events that are gone. An event reaped before its
        /// commanded end frame never finished here: the game's concurrent-event limit, a
        /// disabled disaster toggle, or a local delete took it. Those are the ends a
        /// start-only replication cannot explain, so they are counted separately.
        /// </summary>
        private void ReapEnded(long now)
        {
            if (now < _nextReapMs || _tracked.Count == 0) return;
            _nextReapMs = now + ReapIntervalMs;
            long frame = (long)_simulation.frameIndex;

            List<Entity> ended = null;
            foreach (Entity entity in _tracked.Keys)
            {
                if (EntityManager.Exists(entity) && !EntityManager.HasComponent<Deleted>(entity)) continue;
                if (ended == null) ended = new List<Entity>();
                ended.Add(entity);
            }
            if (ended == null) return;

            for (int i = 0; i < ended.Count; i++)
            {
                Tracked tracked = _tracked[ended[i]];
                _tracked.Remove(ended[i]);
                _endedTotal++;

                long lived = frame - tracked.FirstSeenFrame;
                bool early = tracked.HasCommandedEnd && frame < tracked.EndFrame;
                if (early) _earlyTotal++;

                string name = tracked.PrefabName ?? "unknown prefab";
                string commanded = tracked.HasCommandedEnd
                    ? "commanded " + (tracked.EndFrame - tracked.StartFrame)
                    : "commanded duration unknown";
                SyncLog.Detail(LogTopic.City, "DisasterLifecycleAudit: '" + name + "' (" +
                    tracked.Kind + ") ended " + (early ? "early" : "on time") +
                    " after " + lived + " frame(s), " + commanded +
                    " (audit only - nothing sent).");
            }
        }

        private void MaybeSummarize(long now)
        {
            if (_startedTotal <= _reportedStarted && _endedTotal <= _reportedEnded) return;
            if (now - _lastSummaryMs < SummaryIntervalMs) return;
            _lastSummaryMs = now;
            _reportedStarted = _startedTotal;
            _reportedEnded = _endedTotal;
            SyncLog.Detail(LogTopic.City, "DisasterLifecycleAudit: " + _startedTotal +
                " event(s) started, " + _endedTotal + " ended (" + _earlyTotal +
                " early) this session (audit only - if early stays at 0 and heartbeats " +
                "agree across peers, start-only replication holds).");
        }

        private bool IsDamaging(Entity prefab)
        {
            if (EntityManager.HasComponent<WeatherPhenomenonData>(prefab))
                return EntityManager.GetComponentData<WeatherPhenomenonData>(prefab).m_DamageSeverity != 0f;
            return EntityManager.HasComponent<WaterLevelChangeData>(prefab);
        }

        private bool IsRainControlled(Entity prefab) =>
            EntityManager.HasComponent<WaterLevelChangeData>(prefab) &&
            EntityManager.GetComponentData<WaterLevelChangeData>(prefab).m_ChangeType ==
                WaterLevelChangeType.RainControlled;
    }
}
