using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Keeping a client's own simulation from writing zoned buildings: rejecting the ones it grew
    // locally, and checking that the ones the host asked for actually landed with the road and
    // utility connections they need.
    public partial class GrowableSyncSystem
    {
        /// <summary>
        /// Remembers that a building was just asked for at this spot. The definition does not
        /// become an entity until a later phase, so the only way to recognise our own building when
        /// it appears is the position we asked for it at.
        /// </summary>
        private void NoteSelfRealized(Entity prefab, float3 position,
            GrowableLifecycleCommand command, long now)
        {
            if (_selfRealized.Count >= MaxSelfRealized) _selfRealized.RemoveAt(0);
            _selfRealized.Add(new PendingRealizedSpawn
            {
                Prefab = prefab,
                Position = position,
                Expiry = now + SelfRealizedWindowMs,
                Command = command,
            });
        }

        private bool TryTakeSelfRealized(Entity prefab, float3 position, long now,
            out GrowableLifecycleCommand command)
        {
            command = null;
            for (int i = _selfRealized.Count - 1; i >= 0; i--)
            {
                PendingRealizedSpawn entry = _selfRealized[i];
                if (entry.Expiry <= now) { _selfRealized.RemoveAt(i); continue; }
                if (entry.Prefab != prefab ||
                    math.distancesq(entry.Position.xz, position.xz) >
                    AnchorMatchDistance * AnchorMatchDistance) continue;
                command = entry.Command;
                _selfRealized.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes zoned buildings this client grew by itself. Its spawner is held for as long as
        /// the session is synchronized, so in normal running this finds nothing - but authority is
        /// handed back whenever sync drops (a resync, a world reload), and anything grown in that
        /// window would otherwise stand forever on a lot the host has its own plans for.
        ///
        /// Catching them as they appear is what keeps the invariant simple: on a client, every
        /// zoned building came from the host.
        /// </summary>
        private void RejectLocallyGrownBuildings(long now)
        {
            if (_createdBuildings.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdBuildings.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!IsAutonomousGrowable(entity, now)) continue;

                    float3 position = EntityManager
                        .GetComponentData<global::Game.Objects.Transform>(entity).m_Position;
                    GrowableLifecycleCommand command;
                    if (TryTakeSelfRealized(prefab, position, now, out command))
                    {
                        // The definition is now a real native building. This is the first point at
                        // which its construction clock and state payload can be applied safely.
                        ApplyConditionAndState(entity, command);
                        EntityManager.AddComponent<Updated>(entity);
                        if (_realizationValidations.Count >= MaxRealizationValidations)
                        {
                            SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                                .Create("growable realization validation overflow", "growable",
                                    CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                                .About("realization validation queue")
                                .Tried("nothing - the validation queue was full"));
                        }
                        else _realizationValidations.Add(new RealizationValidation
                        {
                            Building = entity,
                            Prefab = prefab,
                            Position = position,
                            Expiry = now + RealizationValidationWindowMs,
                        });
                        continue;
                    }

                    EntityManager.AddComponent<Deleted>(entity);
                    _rejectedLocal++;
                    Mod.log.Warn("[MP] GrowableSync: this client grew '" +
                                 PrefabIndexSafeName(prefab) + "' at " + Format(position) +
                                 " on its own; removed (the host decides zoned buildings).");
                    Diagnostics.FlightRecorder.Note("locally grown building rejected");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// A definition being accepted is not yet proof that the generated building joined the
        /// road/service graph. Re-run native Updated initialization until the root has a road,
        /// reciprocal road buffer, and the standard utility consumers. Persistent failure is
        /// structural divergence and escalates to the existing world-repair path.
        /// </summary>
        /// <summary>When this pass last ran with the road pipeline able to deliver.</summary>
        private long _lastValidationTickMs;

        /// <summary>
        /// A building is being validated for having joined its ROAD graph, and roads arrive through
        /// the net pipeline. While that pipeline is held - terrain catching up, or a placement
        /// waiting for a target - the road this building needs cannot arrive by definition, so the
        /// window must not count down. It is the same fifteen seconds as the hold itself, so left
        /// running it would ask for a world reload over a road the mod was still holding back.
        /// </summary>
        private void ExtendValidationWindowsWhileRoadsHeld(long now)
        {
            long heldMs = _lastValidationTickMs == 0 ? 0 : now - _lastValidationTickMs;
            _lastValidationTickMs = now;
            if (!NetworkDependenciesHeld || heldMs <= 0) return;
            for (int i = 0; i < _realizationValidations.Count; i++)
                _realizationValidations[i].Expiry += heldMs;
        }

        private void ValidateRealizedBuildings(long now)
        {
            ExtendValidationWindowsWhileRoadsHeld(now);
            for (int i = _realizationValidations.Count - 1; i >= 0; i--)
            {
                RealizationValidation pending = _realizationValidations[i];
                Entity building = pending.Building;
                if (building == Entity.Null || !EntityManager.Exists(building) ||
                    EntityManager.HasComponent<Deleted>(building))
                {
                    _realizationValidations.RemoveAt(i);
                    continue;
                }

                bool connected = HasNativeRoadConnection(building, pending.Prefab);
                bool utilities = HasExpectedUtilityConsumers(building, pending.Prefab);
                if (connected && utilities)
                {
                    _realizationValidations.RemoveAt(i);
                    continue;
                }

                if (pending.Expiry <= now)
                {
                    _realizationValidations.RemoveAt(i);
                    Mod.log.Warn("[MP] GrowableSync: generated building '" +
                                 PrefabIndexSafeName(pending.Prefab) + "' at " +
                                 Format(pending.Position) + " did not join its road/service graph; " +
                                 "requesting world repair.");
                    Diagnostics.FlightRecorder.Note("growable realization invalid/resync");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("growable building failed road/service realization", "growable",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.MissingTarget)
                        .About("building road/service graph")
                        .Tried("re-ran native initialization for 15 s of attempts, not counting time roads were held back"));
                    continue;
                }

                if (!EntityManager.HasComponent<Updated>(building))
                    EntityManager.AddComponent<Updated>(building);
                Building data = EntityManager.GetComponentData<Building>(building);
                if (data.m_RoadEdge != Entity.Null && EntityManager.Exists(data.m_RoadEdge) &&
                    !EntityManager.HasComponent<Updated>(data.m_RoadEdge))
                    EntityManager.AddComponent<Updated>(data.m_RoadEdge);
            }
        }

        private bool HasNativeRoadConnection(Entity building, Entity prefab)
        {
            if (!EntityManager.HasComponent<BuildingData>(prefab)) return true;
            BuildingData data = EntityManager.GetComponentData<BuildingData>(prefab);
            if ((data.m_Flags & global::Game.Prefabs.BuildingFlags.RequireRoad) == 0) return true;

            Building live = EntityManager.GetComponentData<Building>(building);
            Entity road = live.m_RoadEdge;
            if (road == Entity.Null || !EntityManager.Exists(road) ||
                !EntityManager.HasBuffer<ConnectedBuilding>(road)) return false;
            DynamicBuffer<ConnectedBuilding> connected =
                EntityManager.GetBuffer<ConnectedBuilding>(road, true);
            for (int i = 0; i < connected.Length; i++)
                if (connected[i].m_Building == building) return true;
            return false;
        }

        private bool HasExpectedUtilityConsumers(Entity building, Entity prefab)
        {
            if (EntityManager.HasComponent<UnderConstruction>(building) ||
                EntityManager.HasComponent<Abandoned>(building) ||
                EntityManager.HasComponent<Destroyed>(building) ||
                !EntityManager.HasComponent<ConsumptionData>(prefab)) return true;
            return EntityManager.HasComponent<ElectricityConsumer>(building) &&
                   EntityManager.HasComponent<WaterConsumer>(building);
        }

        /// <summary>
        /// Drain rather than apply: applying would duplicate the building the host already has.
        ///
        /// The host's own send loops back through the local observers, so nearly everything drained
        /// here is the host's own echo - routine, and the same thing every other sync system skips
        /// on <c>OriginPlayerId</c>. Only a command another player authored is worth a warning; it
        /// means a peer is authoring zoned buildings it has no authority over.
        /// </summary>
        private void SyncInboxDrop(int localPlayerId)
        {
            int foreign = 0;
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
                if (message.OriginPlayerId != localPlayerId) foreign++;
            if (foreign == 0) return;
            Mod.log.Warn("[MP] GrowableSync: host discarded " + foreign +
                         " zoned-building command(s) from another player; only a host may author them.");
        }
    }
}
