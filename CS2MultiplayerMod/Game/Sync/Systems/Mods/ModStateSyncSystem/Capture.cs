using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Core.Sync.ModSync;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.ModSync;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Mods
{
    public partial class ModStateSyncSystem
    {
        /// <summary>Known carriers held at once. Past this the oldest are simply forgotten.</summary>
        private const int MaxKnownCarriers = 8192;

        /// <summary>Known carriers re-examined per frame while a structural sweep is pending.</summary>
        private const int SweepBudgetPerPass = 32;

        private EntityQuery _replicated;
        private EntityTypeHandle _entityHandle;
        private DynamicComponentTypeHandle[] _typeHandles;
        private bool _queryBuilt;

        private int _lastOrderVersion;
        private bool _sweepPending;

        private ModClosureCapture _capture;
        private ModTypeBinding _captureBinding;

        private readonly List<Entity> _candidates = new List<Entity>();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Entity> _pendingCandidates =
            new System.Collections.Concurrent.ConcurrentQueue<Entity>();
        private readonly HashSet<Entity> _queuedCandidates = new HashSet<Entity>();
        private readonly HashSet<string> _reportedOnce =
            new HashSet<string>(System.StringComparer.Ordinal);
        private readonly HashSet<string> _seenThisPass =
            new HashSet<string>(System.StringComparer.Ordinal);

        private void CaptureChanges(MultiplayerSession session, long now)
        {
            if (_catalog.Entries.Count == 0) return;
            if (!EnsureQuery()) return;

            // Only the result after native systems rebuilt derived state closes the echo.
            SettleApplied(session);

            // A removal only shows as a structural change: then every known carrier is looked at again.
            int orderVersion = _replicated.GetCombinedComponentOrderVersion(true);
            if (orderVersion != _lastOrderVersion)
            {
                _lastOrderVersion = orderVersion;
                _sweepPending = true;
            }

            CollectChangedCarriers();
            PublishCandidates(session, now);
            RunSweep(session, now);
        }

        private bool EnsureQuery()
        {
            if (_queryBuilt) return _typeHandles != null;
            _queryBuilt = true;

            var any = new ComponentType[_catalog.Entries.Count];
            for (int i = 0; i < _catalog.Entries.Count; i++)
                any[i] = ComponentType.ReadOnly(_catalog.Entries[i].Type);

            _replicated = GetEntityQuery(new EntityQueryDesc
            {
                Any = any,
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _entityHandle = GetEntityTypeHandle();
            _typeHandles = new DynamicComponentTypeHandle[any.Length];
            for (int i = 0; i < any.Length; i++) _typeHandles[i] = GetDynamicComponentTypeHandle(any[i]);
            return true;
        }

        /// <summary>
        /// Chunks of replicated types written since last pass: a prefilter (write access, not a changed
        /// value), so candidates are compared before sending. Idle frames cost nothing.
        /// </summary>
        private void CollectChangedCarriers()
        {
            _candidates.Clear();
            _seenThisPass.Clear();

            _entityHandle.Update(this);
            for (int i = 0; i < _typeHandles.Length; i++) _typeHandles[i].Update(this);

            NativeArray<ArchetypeChunk> chunks = _replicated.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    bool changed = false;
                    for (int t = 0; t < _typeHandles.Length; t++)
                    {
                        if (!chunk.DidChange(ref _typeHandles[t], LastSystemVersion)) continue;
                        changed = true;
                        break;
                    }
                    if (!changed) continue;

                    NativeArray<Entity> entities = chunk.GetNativeArray(_entityHandle);
                    for (int e = 0; e < entities.Length; e++)
                    {
                        Entity entity = entities[e];
                        if (_queuedCandidates.Add(entity)) _pendingCandidates.Enqueue(entity);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            // Record before LastSystemVersion advances; the budget limits encoding, not discovery.
            while (_candidates.Count < ModSyncFeature.MaxCarriersPerPass &&
                   _pendingCandidates.TryDequeue(out Entity pending))
            {
                Entity entity = pending;
                _queuedCandidates.Remove(entity);
                _candidates.Add(entity);
            }
        }

        private void PublishCandidates(MultiplayerSession session, long now)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                Entity entity = _candidates[i];
                if (!EntityManager.Exists(entity)) continue;

                if (!TryFindCarrier(entity, out Entity carrier, out ModEntityRef carrierRef))
                {
                    // Holds state but is no place and names none: counted and named, not dropped silently.
                    _noCarrier++;
                    ReportOnce("nocarrier:" + DescribeTypes(entity),
                        "Mod state on an entity with no carrier (" + DescribeTypes(entity) +
                        "); it is not a place in the world and refers to none.");
                    continue;
                }

                PublishCarrier(session, carrier, carrierRef, entity);
            }
        }

        /// <summary>
        /// The carrier is usually the changed entity; otherwise a mod's bookkeeping entity references it.
        /// </summary>
        private bool TryFindCarrier(Entity entity, out Entity carrier, out ModEntityRef carrierRef)
        {
            if (_identity.TryDescribe(entity, out carrierRef))
            {
                carrier = entity;
                return true;
            }

            carrier = Entity.Null;
            Entity found = Entity.Null;
            ModEntityRef foundRef = ModEntityRef.Null;
            var scratch = new List<ModLeaf>();

            for (int i = 0; i < _catalog.Entries.Count && found == Entity.Null; i++)
            {
                ModCatalogEntry entry = _catalog.Entries[i];
                if (!entry.Accessor.Plan.HasReferences) continue;
                if (!entry.Accessor.Has(EntityManager, entity)) continue;

                scratch.Clear();
                entry.Accessor.ReadInto(EntityManager, entity, scratch, target =>
                {
                    if (found != Entity.Null || target == Entity.Null) return ModEntityRef.Null;
                    if (!_identity.TryDescribe(target, out ModEntityRef described)) return ModEntityRef.Null;
                    found = target;
                    foundRef = described;
                    return described;
                });
            }

            if (found == Entity.Null) return false;
            carrier = found;
            carrierRef = foundRef;
            return true;
        }

        /// <summary>
        /// Sends a carrier's closure if its encoding differs from the last seen; state written from a peer
        /// encodes identically, so it is not echoed.
        /// </summary>
        private void PublishCarrier(MultiplayerSession session, Entity carrier, ModEntityRef carrierRef,
            Entity changedEntity = default(Entity))
        {
            string key = carrierRef.Key();
            if (_seenThisPass.Contains(key)) return;

            if (!TryEncode(carrier, carrierRef, out byte[] body, out ulong hash, true)) return;

            // A reference to a road is not ownership; previews reference live roads too.
            if (changedEntity != Entity.Null && changedEntity != carrier &&
                !_capture.SatelliteEntities.Contains(changedEntity)) return;
            _seenThisPass.Add(key);

            if (_shadow.TryGetValue(key, out ulong previous) && previous == hash) return;

            _shadow[key] = hash;
            RememberCarrier(key, carrierRef);

            _capturedTransactions++;
            if (!ModSyncFeature.SendCaptured) return;

            session.SendCommand(0, ModStateCommand.Id, body);
            _sentBytes += body.Length;
            SyncLog.Trace(LogTopic.ModSync, "mod state captured " + key + " (" + body.Length +
                " bytes)");
        }

        /// <summary>Builds the closure and its encoded form, or explains why it could not be sent.</summary>
        private bool TryEncode(Entity carrier, ModEntityRef carrierRef, out byte[] body,
            out ulong hash, bool localCapture = false)
        {
            body = null;
            hash = 0;

            if (_capture == null || _captureBinding != _binding)
            {
                _capture = new ModClosureCapture(EntityManager, _identity, _binding, _catalog);
                _captureBinding = _binding;
            }

            ModStateSnapshot snapshot;
            try
            {
                snapshot = _capture.Capture(carrier, carrierRef);
            }
            catch (System.Exception ex)
            {
                _rejectedClosures++;
                SyncLog.Warn(LogTopic.ModSync, "Could not read mod state at " + carrierRef.Key() +
                    ": " + ex.Message);
                return false;
            }

            if (snapshot == null)
            {
                _rejectedClosures++;
                SyncLog.Detail(LogTopic.ModSync, "Mod state at " + carrierRef.Key() +
                    " not replicated: it " + _capture.Rejection + ".");
                return false;
            }

            if (localCapture) AnnotateRoadSpeedReset(carrier, snapshot);
            var command = new ModStateCommand { Snapshot = snapshot, Types = _binding };
            try
            {
                body = command.Encode();
            }
            catch (ProtocolException ex)
            {
                _rejectedClosures++;
                SyncLog.Warn(LogTopic.ModSync, "Could not encode mod state at " +
                    carrierRef.Key() + ": " + ex.Message);
                return false;
            }

            DrainUnsharedTypes();
            hash = Fold(body);
            return true;
        }

        private void AnnotateRoadSpeedReset(Entity carrier, ModStateSnapshot snapshot)
        {
            if (!EntityManager.HasComponent<Edge>(carrier)) return;
            bool hasSpeed = false;
            for (int i = 0; i < snapshot.CarrierValues.Components.Count; i++)
            {
                ModComponentValue value = snapshot.CarrierValues.Components[i];
                if (_binding.ByIndex(value.TypeIndex).DisplayName !=
                    "RoadSpeedAdjuster.Components.CustomSpeed") continue;
                hasSpeed = true;
                break;
            }
            if (hasSpeed)
            {
                _roadSpeedActive.Add(carrier);
                return;
            }
            if (!_roadSpeedActive.Remove(carrier)) return;

            if (!TryReadLaneSpeed(carrier, out float speed))
            {
                SyncLog.Warn(LogTopic.ModSync, "Road Speed reset has no readable lane speed; " +
                    "the CustomSpeed removal will travel without a lane reset value.");
                return;
            }
            snapshot.HasLaneSpeedReset = true;
            snapshot.LaneSpeedReset = speed;
        }

        private bool TryReadLaneSpeed(Entity edge, out float speed)
        {
            speed = 0f;
            if (!EntityManager.HasBuffer<SubLane>(edge)) return false;
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(edge, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!EntityManager.Exists(lane)) continue;
                if (EntityManager.HasComponent<CarLane>(lane))
                    speed = EntityManager.GetComponentData<CarLane>(lane).m_SpeedLimit;
                else if (EntityManager.HasComponent<TrackLane>(lane))
                    speed = EntityManager.GetComponentData<TrackLane>(lane).m_SpeedLimit;
                else continue;
                return !float.IsNaN(speed) && !float.IsInfinity(speed) &&
                       speed >= 0.1f && speed <= 500f;
            }
            return false;
        }

        private void RememberCarrier(string key, ModEntityRef carrierRef)
        {
            if (_knownCarriers.ContainsKey(key))
            {
                // Same local coordinates as the shadow, or float drift makes headers alternate.
                _knownCarriers[key] = carrierRef;
                return;
            }
            if (_knownCarriers.Count >= MaxKnownCarriers) return;
            _knownCarriers.Add(key, carrierRef);
            _sweepOrder.Add(key);
        }

        /// <summary>
        /// Re-examines known carriers after a structural change; an empty closure is published so the
        /// receiver removes the state.
        /// </summary>
        private void RunSweep(MultiplayerSession session, long now)
        {
            if (!_sweepPending || _sweepOrder.Count == 0) return;

            int budget = SweepBudgetPerPass;
            while (budget-- > 0 && _sweepOrder.Count > 0)
            {
                if (_sweepCursor >= _sweepOrder.Count)
                {
                    _sweepCursor = 0;
                    _sweepPending = false;
                    return;
                }

                string key = _sweepOrder[_sweepCursor];
                ModEntityRef carrierRef = _knownCarriers[key];

                if (!_identity.TryResolve(carrierRef, out Entity carrier))
                {
                    // The carrier itself is gone; its deletion travels on its own.
                    _knownCarriers.Remove(key);
                    _shadow.Remove(key);
                    _sweepOrder.RemoveAt(_sweepCursor);
                    continue;
                }

                PublishCarrier(session, carrier, carrierRef);
                _sweepCursor++;
            }
        }

        /// <summary>The replicated types present on an entity, for a line that has to identify it.</summary>
        private string DescribeTypes(Entity entity)
        {
            string names = null;
            for (int i = 0; i < _catalog.Entries.Count; i++)
            {
                ModCatalogEntry entry = _catalog.Entries[i];
                if (!entry.Accessor.Has(EntityManager, entity)) continue;
                names = names == null
                    ? entry.Descriptor.DisplayName
                    : names + ", " + entry.Descriptor.DisplayName;
            }
            return names ?? "no replicated type";
        }

        /// <summary>Names local types the session does not replicate (the host's table decides).</summary>
        private void DrainUnsharedTypes()
        {
            if (_capture.TypesNotInSession.Count == 0) return;
            foreach (string name in _capture.TypesNotInSession)
                ReportOnce("unshared:" + name, "Mod state type " + name +
                    " is not replicated in this session: the host's type table does not name it.");
            _capture.TypesNotInSession.Clear();
        }

        /// <summary>FNV-1a over the encoded closure - stable across processes, unlike a string hash.</summary>
        private static ulong Fold(byte[] data)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= prime;
            }
            return hash;
        }
    }
}
