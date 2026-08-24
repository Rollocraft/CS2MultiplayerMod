using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates the *start* of a natural disaster - tornado, hailstorm, thunderstorm, tsunami -
    /// and nothing else. One <see cref="DisasterEventCommand"/> per event carries the state the
    /// game resolves once at creation (place, size, duration, strength); every machine then runs
    /// the event with its own simulation. Streaming the storm's path instead would put a message
    /// on the wire every simulation frame for minutes on end.
    ///
    /// Each machine rolls its own disaster dice from a wall-clock seed, so without this the two
    /// cities get unrelated disasters. Clients therefore stop rolling entirely
    /// (<see cref="global::Game.Simulation.WeatherHazardSystem"/> is switched off while a client is
    /// in a session) and take the host's, which is the same host-authoritative shape as the rest of
    /// the mod. The rain-driven river flood is deliberately left alone: it is derived from the
    /// already-replicated weather, and it respawns itself as long as the rain lasts.
    /// </summary>
    public partial class DisasterSyncSystem : GameSystemBase
    {
        /// <summary>Disasters arrive one at a time; a per-frame cap keeps a flood from stalling a frame.</summary>
        private const int MaxRealizePerFrame = 4;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly List<Realized> _justRealized = new List<Realized>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private SimulationSystem _simulation;
        private CityConfigurationSystem _cityConfiguration;
        private EntityQuery _createdPhenomena;
        private EntityQuery _createdSurges;
        private CommandObserver _observer;
        private bool _rollsSuppressed;

        /// <summary>An event realized this frame: it must not be captured straight back out, and
        /// the sender's values are re-stamped onto it once the game's own initialization has run.</summary>
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

            Mod.log.Info(nameof(DisasterSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _cityConfiguration = World.GetOrCreateSystemManaged<CityConfigurationSystem>();

            _createdPhenomena = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Events.Event>(),
                    ComponentType.ReadOnly<global::Game.Events.WeatherPhenomenon>(),
                    ComponentType.ReadOnly<Created>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // Flood excluded: that marker is the rain-driven river flood, which every machine
            // derives from the replicated weather and re-creates on its own for as long as it
            // rains. Replicating it would stack a second surge on top of the local one.
            _createdSurges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Events.Event>(),
                    ComponentType.ReadOnly<global::Game.Events.WaterLevelChange>(),
                    ComponentType.ReadOnly<Created>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<global::Game.Events.Flood>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, DisasterEventCommand.Id)
                {
                    MaxBodyBytes = DisasterEventCommand.MaxEncodedBytes,
                };
                Mod.Service.Session.AddObserver(_observer);
            }
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            SuppressLocalRolls(false);
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
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

            // The game's initialization pass (Modification2) has run by now, so anything it
            // overwrote or re-randomized on a replica gets the sender's values put back. This also
            // ends the frame's echo window: from here on those entities are indistinguishable
            // from local ones, which is safe because their Created tag is stripped at Cleanup.
            ReassertRealized();
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate: an event created
        /// any later in the frame loses its <see cref="Created"/> tag at Cleanup before the game's
        /// initialization pass can size its hotspot trail and place its effects.</summary>
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
            SimulationCommandMessage message;
            while (realized < MaxRealizePerFrame && _incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                DisasterEventCommand command;
                try { command = DisasterEventCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] DisasterSync: dropping malformed command: " + ex.Message);
                    continue;
                }

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

                    Entity prefab;
                    string prefabName;
                    if (!TryNamePrefab(entity, out prefab, out prefabName)) continue;

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

                    // Harmless weather (fog, plain thunderstorms) comes through here too and is
                    // frequent; only an actual disaster is worth a line in the quiet default log.
                    string detail = "'" + prefabName + "' at " + phenomenon.m_PhenomenonPosition +
                                    ", radius " + command.PhenomenonRadius + ", starting in " +
                                    command.StartDelayFrames + " frame(s), lasting " +
                                    command.DurationFrames;
                    if (IsDamaging(prefab))
                    {
                        Mod.log.Info("[MP] DisasterSync sent " + detail + ".");
                        FlightRecorder.Note("disaster sent " + detail);
                    }
                    else
                    {
                        Mod.Verbose("[MP] DisasterSync sent weather " + detail + ".");
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

                    Entity prefab;
                    string prefabName;
                    if (!TryNamePrefab(entity, out prefab, out prefabName)) continue;
                    // The query already excludes the stock rain flood by its Flood marker; a
                    // custom prefab could declare the same change type without that marker.
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
                    Mod.log.Info("[MP] DisasterSync sent " + detail + ".");
                    FlightRecorder.Note("disaster sent " + detail);
                }
            }
            finally
            {
                events.Dispose();
            }
        }

        /// <summary>
        /// Convert the event's absolute start/end simulation frames into counts relative to now.
        /// Absolute frames are meaningless across machines - each keeps its own frame counter and
        /// the in-game clock is aligned by re-anchoring its epoch instead.
        /// </summary>
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
                Mod.log.Warn("[MP] DisasterSync: refusing to send " + label + " '" +
                             command.PrefabName + "': " + ex.Message);
                return false;
            }
        }

        // ---- Realize ------------------------------------------------------------

        private bool Realize(DisasterEventCommand command, int originPlayerId)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                Mod.log.Warn("[MP] DisasterSync: no local event prefab named '" +
                             command.PrefabName + "'; ignoring the disaster.");
                return false;
            }
            if (!EntityManager.HasComponent<EventData>(prefab) || !MatchesKind(prefab, command.Kind))
            {
                Mod.log.Warn("[MP] DisasterSync: prefab '" + command.PrefabName + "' is not a " +
                             command.Kind + " event here; ignoring.");
                return false;
            }

            // The same gate the game applies to its own rolls: a city with natural disasters
            // switched off never starts a damaging event, so it must not accept one either.
            if (IsDamaging(prefab) && !_cityConfiguration.naturalDisasters)
            {
                Mod.Verbose("[MP] DisasterSync: natural disasters are off in this city; ignoring '" +
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
                Mod.log.Warn("[MP] DisasterSync: the event archetype for '" + command.PrefabName +
                             "' is missing what a " + command.Kind + " needs; ignoring.");
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
                Mod.log.Info("[MP] DisasterSync realized " + detail + ".");
                FlightRecorder.Note("disaster realized " + detail);
            }
            else
            {
                Mod.Verbose("[MP] DisasterSync realized weather " + detail + ".");
            }
            return true;
        }

        /// <summary>
        /// Put the sender's values back after the game's initialization pass. That pass leaves
        /// anything already set alone for a weather phenomenon, but re-rolls a water surge's
        /// intensity and re-dates its duration unconditionally - which would give every machine a
        /// different tsunami from the same command.
        /// </summary>
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

                // The hotspot trail and the effect anchor were filled from whatever the
                // initialization pass resolved; re-seed both from the values above.
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

        /// <summary>
        /// Stop or restart this machine's own disaster rolls. The spawner seeds itself from the
        /// wall clock, so leaving it running on a client produces a second, unrelated set of
        /// disasters alongside the host's replicated ones.
        /// </summary>
        private void SuppressLocalRolls(bool suppress)
        {
            if (suppress == _rollsSuppressed) return;

            WeatherHazardSystem spawner = World.GetExistingSystemManaged<WeatherHazardSystem>();
            if (spawner == null) return;

            spawner.Enabled = !suppress;
            _rollsSuppressed = suppress;
            Mod.log.Info("[MP] DisasterSync: local weather-hazard rolls " +
                         (suppress ? "stopped; the host's disasters are replicated instead."
                                   : "restored."));
        }

        // ---- Helpers ------------------------------------------------------------

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _justRealized.Clear();
        }

        /// <summary>
        /// True for an event this machine created from a remote command earlier in this same frame.
        /// Realization runs at ToolUpdate and capture at ModificationEnd, so a replica is only ever
        /// visible to the capture queries (which require <see cref="Created"/>) on that one frame -
        /// matching by entity is exact, where a position key could collide.
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
