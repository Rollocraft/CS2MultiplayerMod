using System.Collections.Generic;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates only the start of a disaster: one command with what the game resolves at creation.
    /// Rolls are wall-clock seeded, so clients stop rolling (WeatherHazardSystem is off) and take the
    /// host's. The rain-driven river flood is left alone; it follows the replicated weather.
    /// </summary>
    public partial class DisasterSyncSystem : CommandSyncSystem, IRealizeStage
    {
        /// <summary>Disasters arrive one at a time; a per-frame cap keeps a flood from stalling a frame.</summary>
        private const int MaxRealizePerFrame = 4;

        private readonly List<Realized> _justRealized = new List<Realized>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private SimulationSystem _simulation;
        private CityConfigurationSystem _cityConfiguration;
        private EntityQuery _createdPhenomena;
        private EntityQuery _createdSurges;
        private bool _rollsSuppressed;

        /// <summary>Realized this frame: never recaptured, and re-stamped after initialization.</summary>
        private struct Realized
        {
            public Entity Entity;
            public DisasterEventCommand Command;
            public uint StartFrame;
            public uint EndFrame;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _cityConfiguration = World.GetOrCreateSystemManaged<CityConfigurationSystem>();

            _createdPhenomena = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event,
                    global::Game.Events.WeatherPhenomenon, Created>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });

            // The rain flood follows the replicated weather; replicating it would stack a second surge.
            _createdSurges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event,
                    global::Game.Events.WaterLevelChange, Created>(),
                None = SyncQuery.ReadOnly<global::Game.Events.Flood, Deleted, Temp>(),
            });

            ListenFor(new[] { DisasterEventCommand.Id }, DisasterEventCommand.MaxEncodedBytes);
        }

        protected override void OnDestroy()
        {
            SuppressLocalRolls(false);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("DisasterSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady)
                {
                    SuppressLocalRolls(false);
                    if (_justRealized.Count > 0) _justRealized.Clear();
                    return;
                }

                MultiplayerSession session = service.Session;
                SuppressLocalRolls(session.Role == SessionRole.Client);

                CapturePhenomena(session);
                CaptureSurges(session);

                // Initialization has run: put back what it re-randomized. Created is stripped at Cleanup.
                ReassertRealized();
            }
        }

        /// <summary>ToolUpdate: created later, an event loses Created before initialization places it.</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            if (!service.GameplaySyncReady)
            {
                SyncInbox.Clear(_incoming);
                return;
            }

            MultiplayerSession session = service.Session;
            int realized = 0;
            while (realized < MaxRealizePerFrame && _incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (!CommandDecode.TryDecode(message, DisasterEventCommand.Decode, LogTopic.City,
                        "DisasterSync", out DisasterEventCommand command))
                    continue;

                if (Realize(command, message.OriginPlayerId)) realized++;
            }
        }

        // ---- Capture ------------------------------------------------------------

        private void CapturePhenomena(MultiplayerSession session)
        {
            if (_createdPhenomena.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> events = _createdPhenomena.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                {
                    Entity entity = events[i];
                    if (WasRealizedThisFrame(entity)) continue;

                    if (!TryNamePrefab(entity, out Entity prefab, out string prefabName)) continue;

                    var phenomenon =
                        EntityManager.GetComponentData<global::Game.Events.WeatherPhenomenon>(entity);
                    var command = new DisasterEventCommand
                    {
                        Kind = DisasterKind.WeatherPhenomenon,
                        PrefabName = prefabName,
                        PhenomenonX = phenomenon.m_PhenomenonPosition.x,
                        PhenomenonY = phenomenon.m_PhenomenonPosition.y,
                        PhenomenonZ = phenomenon.m_PhenomenonPosition.z,
                        HotspotX = phenomenon.m_HotspotPosition.x,
                        HotspotY = phenomenon.m_HotspotPosition.y,
                        HotspotZ = phenomenon.m_HotspotPosition.z,
                        PhenomenonRadius = math.max(0f, phenomenon.m_PhenomenonRadius),
                        HotspotRadius = math.max(0f, phenomenon.m_HotspotRadius),
                        LightningTimer = math.max(0f, phenomenon.m_LightningTimer),
                    };
                    FillTiming(command,
                        EntityManager.GetComponentData<global::Game.Events.Duration>(entity));
                    if (!Send(session, command, "phenomenon")) continue;

                    // Only real disasters reach the default log.
                    string detail = "'" + prefabName + "' at " + phenomenon.m_PhenomenonPosition +
                                    ", radius " + command.PhenomenonRadius + ", starting in " +
                                    command.StartDelayFrames + " frame(s), lasting " +
                                    command.DurationFrames;
                    if (IsDamaging(prefab))
                    {
                        SyncLog.Detail(LogTopic.City, "DisasterSync sent " + detail + ".");
                    }
                    else
                    {
                        SyncLog.Detail(LogTopic.City, "DisasterSync sent weather " + detail + ".");
                    }
                }
            }
            finally
            {
                events.Dispose();
            }
        }

        private void CaptureSurges(MultiplayerSession session)
        {
            if (_createdSurges.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> events = _createdSurges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                {
                    Entity entity = events[i];
                    if (WasRealizedThisFrame(entity)) continue;

                    if (!TryNamePrefab(entity, out Entity prefab, out string prefabName)) continue;
                    // A custom prefab can declare the flood change type without the Flood marker.
                    if (IsRainControlled(prefab)) continue;

                    var surge =
                        EntityManager.GetComponentData<global::Game.Events.WaterLevelChange>(entity);
                    var command = new DisasterEventCommand
                    {
                        Kind = DisasterKind.WaterLevelChange,
                        PrefabName = prefabName,
                        MaxIntensity = math.clamp(surge.m_MaxIntensity, 0f,
                            DisasterEventCommand.MaxIntensityValue),
                        DangerHeight = surge.m_DangerHeight,
                        DirectionX = math.clamp(surge.m_Direction.x, -16f, 16f),
                        DirectionZ = math.clamp(surge.m_Direction.y, -16f, 16f),
                    };
                    FillTiming(command,
                        EntityManager.GetComponentData<global::Game.Events.Duration>(entity));
                    if (!Send(session, command, "water surge")) continue;

                    string detail = "water surge '" + prefabName + "', intensity " +
                                    command.MaxIntensity + ", lasting " + command.DurationFrames +
                                    " frame(s)";
                    SyncLog.Detail(LogTopic.City, "DisasterSync sent " + detail + ".");
                }
            }
            finally
            {
                events.Dispose();
            }
        }

        /// <summary>Absolute frames differ per machine; send counts relative to now.</summary>
        private void FillTiming(DisasterEventCommand command, global::Game.Events.Duration duration)
        {
            long frame = _simulation.frameIndex;
            long start = duration.m_StartFrame;
            long end = duration.m_EndFrame;
            command.StartDelayFrames = (int)math.clamp(start - frame, 0, DisasterEventCommand.MaxFrames);
            command.DurationFrames = (int)math.clamp(end - start, 0, DisasterEventCommand.MaxFrames);
        }

        private bool Send(MultiplayerSession session, DisasterEventCommand command, string label)
        {
            try
            {
                session.SendCommand(0, DisasterEventCommand.Id, command.Encode());
                return true;
            }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.City, "DisasterSync: refusing to send " + label + " '" +
                    command.PrefabName + "': " + ex.Message);
                return false;
            }
        }

        // ---- Realize ------------------------------------------------------------

        private bool Realize(DisasterEventCommand command, int originPlayerId)
        {
            if (!_prefabIndex.TryResolve(command.PrefabName, out Entity prefab))
            {
                SyncLog.Warn(LogTopic.City, "DisasterSync: no local event prefab named '" +
                    command.PrefabName + "'; ignoring the disaster.");
                return false;
            }
            if (!EntityManager.HasComponent<EventData>(prefab) || !MatchesKind(prefab, command.Kind))
            {
                SyncLog.Warn(LogTopic.City, "DisasterSync: prefab '" + command.PrefabName +
                    "' is not a " + command.Kind + " event here; ignoring.");
                return false;
            }

            // The game's own gate: disasters off means no damaging events.
            if (IsDamaging(prefab) && !_cityConfiguration.naturalDisasters)
            {
                SyncLog.Detail(LogTopic.City,
                    "DisasterSync: natural disasters are off in this city; ignoring '" +
                    command.PrefabName + "' from player " + originPlayerId + ".");
                return false;
            }

            // Rain floods are locally derived on every machine (see the surge query).
            if (command.Kind == DisasterKind.WaterLevelChange && IsRainControlled(prefab)) return false;

            uint frame = _simulation.frameIndex;
            uint startFrame = frame + (uint)command.StartDelayFrames;
            uint endFrame = startFrame + (uint)command.DurationFrames;

            EventData eventData = EntityManager.GetComponentData<EventData>(prefab);
            Entity entity = EntityManager.CreateEntity(eventData.m_Archetype);
            if (!EntityManager.HasComponent<PrefabRef>(entity) ||
                !EntityManager.HasComponent<global::Game.Events.Duration>(entity) ||
                !HasKindComponent(entity, command.Kind))
            {
                EntityManager.DestroyEntity(entity);
                SyncLog.Warn(LogTopic.City, "DisasterSync: the event archetype for '" +
                    command.PrefabName + "' is missing what a " + command.Kind +
                    " needs; ignoring.");
                return false;
            }

            EntityManager.SetComponentData(entity, new PrefabRef(prefab));
            Stamp(entity, command, startFrame, endFrame, refreshTrail: false);

            _justRealized.Add(new Realized
            {
                Entity = entity,
                Command = command,
                StartFrame = startFrame,
                EndFrame = endFrame,
            });

            string detail = "'" + command.PrefabName + "' (" + command.Kind + ") from player " +
                            originPlayerId + ", starting in " + command.StartDelayFrames + " frame(s)";
            if (IsDamaging(prefab))
            {
                SyncLog.Detail(LogTopic.City, "DisasterSync realized " + detail + ".");
            }
            else
            {
                SyncLog.Detail(LogTopic.City, "DisasterSync realized weather " + detail + ".");
            }
            return true;
        }

        /// <summary>Initialization re-rolls a surge's intensity and duration; put the sender's values back.</summary>
        private void ReassertRealized()
        {
            if (_justRealized.Count == 0) return;

            for (int i = 0; i < _justRealized.Count; i++)
            {
                Realized realized = _justRealized[i];
                if (!EntityManager.Exists(realized.Entity)) continue;
                // Dropped by the game's own concurrent-event limit.
                if (EntityManager.HasComponent<Deleted>(realized.Entity)) continue;
                Stamp(realized.Entity, realized.Command, realized.StartFrame, realized.EndFrame,
                    refreshTrail: true);
            }
            _justRealized.Clear();
        }

        /// <summary>Write one command's creation state onto an event entity.</summary>
        private void Stamp(Entity entity, DisasterEventCommand command, uint startFrame, uint endFrame,
            bool refreshTrail)
        {
            EntityManager.SetComponentData(entity, new global::Game.Events.Duration
            {
                m_StartFrame = startFrame,
                m_EndFrame = endFrame,
            });

            if (command.Kind == DisasterKind.WeatherPhenomenon)
            {
                var phenomenon =
                    EntityManager.GetComponentData<global::Game.Events.WeatherPhenomenon>(entity);
                phenomenon.m_PhenomenonPosition =
                    new float3(command.PhenomenonX, command.PhenomenonY, command.PhenomenonZ);
                phenomenon.m_HotspotPosition =
                    new float3(command.HotspotX, command.HotspotY, command.HotspotZ);
                phenomenon.m_PhenomenonRadius = command.PhenomenonRadius;
                phenomenon.m_HotspotRadius = command.HotspotRadius;
                phenomenon.m_LightningTimer = command.LightningTimer;
                EntityManager.SetComponentData(entity, phenomenon);

                if (!refreshTrail) return;

                if (EntityManager.HasBuffer<global::Game.Events.HotspotFrame>(entity))
                {
                    DynamicBuffer<global::Game.Events.HotspotFrame> trail =
                        EntityManager.GetBuffer<global::Game.Events.HotspotFrame>(entity);
                    for (int i = 0; i < trail.Length; i++)
                        trail[i] = new global::Game.Events.HotspotFrame(phenomenon);
                }
                if (EntityManager.HasComponent<global::Game.Rendering.InterpolatedTransform>(entity))
                    EntityManager.SetComponentData(entity,
                        new global::Game.Rendering.InterpolatedTransform(phenomenon));
                return;
            }

            var surge = EntityManager.GetComponentData<global::Game.Events.WaterLevelChange>(entity);
            surge.m_MaxIntensity = command.MaxIntensity;
            surge.m_DangerHeight = command.DangerHeight;
            surge.m_Direction = new float2(command.DirectionX, command.DirectionZ);
            EntityManager.SetComponentData(entity, surge);
        }

        // ---- Local rolls ------------------------------------------------------------

        /// <summary>The spawner is wall-clock seeded, so a client's own rolls would be unrelated.</summary>
        private void SuppressLocalRolls(bool suppress)
        {
            if (suppress == _rollsSuppressed) return;

            WeatherHazardSystem spawner = World.GetExistingSystemManaged<WeatherHazardSystem>();
            if (spawner == null) return;

            spawner.Enabled = !suppress;
            _rollsSuppressed = suppress;
            SyncLog.Detail(LogTopic.City, "DisasterSync: local weather-hazard rolls " +
                (suppress ? "stopped; the host's disasters are replicated instead." : "restored."));
        }

        // ---- Helpers ------------------------------------------------------------

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _justRealized.Clear();
        }

        /// <summary>
        /// A replica is visible to the Created capture queries only on its realize frame, so matching by
        /// entity is exact.
        /// </summary>
        private bool WasRealizedThisFrame(Entity entity)
        {
            for (int i = 0; i < _justRealized.Count; i++)
                if (_justRealized[i].Entity == entity) return true;
            return false;
        }

        /// <summary>Resolve an event entity's prefab and its wire name; false when it has neither.</summary>
        private bool TryNamePrefab(Entity entity, out Entity prefab, out string name)
        {
            prefab = Entity.Null;
            name = null;
            if (!EntityManager.HasComponent<PrefabRef>(entity)) return false;
            prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.HasComponent<PrefabData>(prefab)) return false;
            name = _prefabIndex.NameOf(prefab);
            return !string.IsNullOrEmpty(name);
        }

        private bool MatchesKind(Entity prefab, DisasterKind kind) =>
            kind == DisasterKind.WeatherPhenomenon
                ? EntityManager.HasComponent<WeatherPhenomenonData>(prefab)
                : EntityManager.HasComponent<WaterLevelChangeData>(prefab);

        private bool HasKindComponent(Entity entity, DisasterKind kind) =>
            kind == DisasterKind.WeatherPhenomenon
                ? EntityManager.HasComponent<global::Game.Events.WeatherPhenomenon>(entity)
                : EntityManager.HasComponent<global::Game.Events.WaterLevelChange>(entity);

        private bool IsDamaging(Entity prefab)
        {
            if (EntityManager.HasComponent<WeatherPhenomenonData>(prefab))
                return EntityManager.GetComponentData<WeatherPhenomenonData>(prefab).m_DamageSeverity != 0f;
            // Every surge that is not the rain-driven flood is a disaster.
            return EntityManager.HasComponent<WaterLevelChangeData>(prefab);
        }

        private bool IsRainControlled(Entity prefab) =>
            EntityManager.HasComponent<WaterLevelChangeData>(prefab) &&
            EntityManager.GetComponentData<WaterLevelChangeData>(prefab).m_ChangeType ==
                WaterLevelChangeType.RainControlled;
    }
}
