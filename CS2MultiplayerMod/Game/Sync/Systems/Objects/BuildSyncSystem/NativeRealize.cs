using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Reproducing a remote peer's object-tool operation locally, by feeding the game's own tool
    // the same definitions the sender's tool had. An operation that cannot be resolved yet is
    // blocked and retried rather than dropped, because the entity it refers to may still be on
    // its way.
    //
    // This file holds the state, the candidate index that keeps resolution off a full query walk,
    // and the queue-and-retry loop. The realizing itself is split across the sibling
    // NativeRealize*.cs files: the operations, the specialized-industry rules that decide what a
    // client may reproduce, resolving an operation, building the definitions, and portable refs.
    public partial class BuildSyncSystem
    {
        private const long NativeObjectTargetRetryMs = 10000;
        private const long NativeObjectReplayRememberMs = 60000;
        private const int MaxNativeObjectReplayPrefix = 32;

        private struct NativeObjectOperationKey : System.IEquatable<NativeObjectOperationKey>
        {
            public int Origin;
            public long Operation;
            public bool Equals(NativeObjectOperationKey other) =>
                Origin == other.Origin && Operation == other.Operation;
            public override bool Equals(object obj) =>
                obj is NativeObjectOperationKey && Equals((NativeObjectOperationKey)obj);
            public override int GetHashCode()
            {
                unchecked { return Origin * 397 ^ Operation.GetHashCode(); }
            }
        }

        private enum NativeObjectResult : byte { Completed, Armed, Retry, Rejected }

        private sealed class ResolvedObjectDefinition
        {
            public Entity Prefab;
            public Entity SubPrefab;
            public Entity Original;
            public Entity Owner;
            public Entity Attached;
            public Entity OwnerDefinitionPrefab;
            public Entity StartEntity;
            public Entity EndEntity;
        }

        /// <summary>
        /// Spacing between attempts on a blocked operation. Resolution is cheap now but not free, and
        /// the geometry it waits for arrives on its own schedule - retrying every frame only burned
        /// the retry window at frame rate.
        /// </summary>
        private const long NativeObjectRetryIntervalMs = 200;

        private bool _hasBlockedNativeObject;
        private SimulationCommandMessage _blockedNativeObject;
        private long _blockedNativeObjectDeadline;
        private long _blockedNativeObjectNextAttemptMs;
        private string _lastUnresolvedObjectReason;
        // Commit validation can reject an operation after it left the network inbox. Replays must
        // return ahead of later commands, and more than one can become ready while another ordered
        // target is retrying. A bounded prefix avoids the former single-slot collision/drop.
        private readonly List<SimulationCommandMessage> _nativeObjectReplayPrefix =
            new List<SimulationCommandMessage>(MaxNativeObjectReplayPrefix);
        private readonly CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NativeObjectOperationKey>
            _recentNativeObjectOperations =
                new CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NativeObjectOperationKey>();
        private EntityQuery _portableObjects;
        private EntityQuery _portableAreas;
        private Net.NetSyncSystem _nativeNetCoordinator;

        /// <summary>
        /// Candidates for one resolution pass, bucketed by prefab.
        ///
        /// A relocation names every element of a building's owned graph plus a stretch of road - 280+
        /// references for a large plant. Walking the whole city's objects/nodes/edges/areas once per
        /// reference took seconds of main-thread time per attempt, and a blocked operation repeated
        /// that every frame for its whole retry window. Snapshotting each domain once and grouping by
        /// prefab turns those thousands of city walks into four.
        /// </summary>
        private sealed class PortableCandidateIndex
        {
            private readonly Dictionary<Entity, List<Entity>> _byPrefab =
                new Dictionary<Entity, List<Entity>>();
            private static readonly List<Entity> Empty = new List<Entity>();
            private bool _filled;

            public void Invalidate()
            {
                foreach (KeyValuePair<Entity, List<Entity>> pair in _byPrefab)
                    pair.Value.Clear();
                _filled = false;
            }

            public void FillIfNeeded(EntityManager entityManager, EntityQuery query)
            {
                if (_filled) return;
                if (query.IsEmptyIgnoreFilter)
                {
                    _filled = true;
                    return;
                }
                NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity candidate = entities[i];
                        if (!entityManager.HasComponent<PrefabRef>(candidate)) continue;
                        Entity prefab = entityManager.GetComponentData<PrefabRef>(candidate).m_Prefab;
                        List<Entity> bucket;
                        if (!_byPrefab.TryGetValue(prefab, out bucket))
                        {
                            bucket = new List<Entity>();
                            _byPrefab[prefab] = bucket;
                        }
                        bucket.Add(candidate);
                    }
                }
                finally { entities.Dispose(); }
                _filled = true;
            }

            public List<Entity> Of(Entity prefab)
            {
                List<Entity> bucket;
                return _byPrefab.TryGetValue(prefab, out bucket) ? bucket : Empty;
            }
        }

        private readonly PortableCandidateIndex _objectCandidates = new PortableCandidateIndex();
        private readonly PortableCandidateIndex _nodeCandidates = new PortableCandidateIndex();
        private readonly PortableCandidateIndex _edgeCandidates = new PortableCandidateIndex();
        private readonly PortableCandidateIndex _areaCandidates = new PortableCandidateIndex();
        private int _portableIndexDepth;

        /// <summary>
        /// Prepare candidate domains for one resolution pass. Each domain is snapshotted lazily on
        /// its first lookup, so a plain building placement does not walk unrelated nodes, edges, and
        /// areas. Nothing inside a pass creates or destroys world entities, so each snapshot stays
        /// correct throughout it.
        /// </summary>
        private void BeginPortableResolve()
        {
            if (_portableIndexDepth++ != 0) return;
            _objectCandidates.Invalidate();
            _nodeCandidates.Invalidate();
            _edgeCandidates.Invalidate();
            _areaCandidates.Invalidate();
        }

        private void EndPortableResolve()
        {
            if (_portableIndexDepth > 0) _portableIndexDepth--;
        }

        /// <summary>
        /// Same-prefab candidates for <paramref name="prefab"/>. Outside a resolution pass the domain
        /// is snapshotted for this one lookup, so callers that resolve a single reference behave
        /// exactly as before.
        /// </summary>
        private List<Entity> Candidates(PortableCandidateIndex index, EntityQuery query, Entity prefab)
        {
            if (_portableIndexDepth == 0) index.Invalidate();
            index.FillIfNeeded(EntityManager, query);
            return index.Of(prefab);
        }

        private void InitializeNativeObjectOperations()
        {
            _nativeNetCoordinator = World.GetOrCreateSystemManaged<Net.NetSyncSystem>();
            _portableObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Objects.Object,
                    global::Game.Objects.Transform, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, global::Game.Objects.Moving,
                    global::Game.Vehicles.Vehicle, global::Game.Creatures.Creature>(),
            });
            _portableAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Areas.Area, PrefabRef, global::Game.Areas.Node>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });
        }

        private void DrainNativeObjectOperations()
        {
            _hasBlockedNativeObject = false;
            _blockedNativeObject = null;
            _blockedNativeObjectDeadline = 0;
            _blockedNativeObjectNextAttemptMs = 0;
            _lastUnresolvedObjectReason = null;
            _nativeObjectReplayPrefix.Clear();
            _recentNativeObjectOperations.Clear();
        }

        private void PruneNativeObjectOperations(long now)
        {
            _recentNativeObjectOperations.Prune(now);
        }

        private bool TryRealizeBlockedNativeObject(long now)
        {
            if (!_hasBlockedNativeObject) return true;
            if (_nativeNetCoordinator.IsCommitBusy) return false;
            if (now < _blockedNativeObjectNextAttemptMs) return false;
            _blockedNativeObjectNextAttemptMs = now + NativeObjectRetryIntervalMs;

            NativeObjectResult result = TryRealizeRemoteObjectMessage(_blockedNativeObject, now);
            if (result == NativeObjectResult.Retry)
            {
                if (now < _blockedNativeObjectDeadline) return false;
                string placementPrefab;
                bool compactPlacement = TryDescribeBlockedPlacement(out placementPrefab);
                // The road/building/area this edit references never arrived on this machine. A
                // placement should normally take the compact local-regeneration path; reaching this
                // deadline means either its one snapped target is absent or a legacy/edit graph is
                // incompatible. In both cases silently dropping it leaves known world divergence.
                if (compactPlacement)
                {
                    Mod.log.Warn("[MP] BuildSync: building placement '" + placementPrefab +
                                 "' could not resolve its snapped target within the retry window (" +
                                 (_lastUnresolvedObjectReason ?? "unknown target") +
                                 "); requesting an automatic world sync.");
                    Diagnostics.FlightRecorder.Note(
                        "building placement target expired; world sync requested");
                }
                else
                {
                    Mod.log.Warn("[MP] BuildSync: native object operation target did not resolve " +
                                 "within the retry window (" +
                                 (_lastUnresolvedObjectReason ?? "unknown target") +
                                 "); requesting an automatic world sync.");
                    Diagnostics.FlightRecorder.Note(
                        "object operation target expired; world sync requested");
                }
                // Read before the reset below clears it - it is the whole point of the report.
                string unresolvedDetail = _lastUnresolvedObjectReason ?? "unknown target";
                _hasBlockedNativeObject = false;
                _blockedNativeObject = null;
                _blockedNativeObjectDeadline = 0;
                _lastUnresolvedObjectReason = null;
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create(compactPlacement
                            ? "building placement target did not resolve"
                            : "native object operation target did not resolve",
                        "object", CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.MissingTarget)
                    .About(compactPlacement ? "building placement target" : "native object target")
                    .Tried("re-resolved the target every frame for the whole retry window, not " +
                           "counting time the road pipeline was held back")
                    .Fact("what would not resolve", unresolvedDetail));
                return false;
            }

            _hasBlockedNativeObject = false;
            _blockedNativeObject = null;
            _blockedNativeObjectDeadline = 0;
            return result == NativeObjectResult.Completed;
        }

        private bool TryDescribeBlockedPlacement(out string prefabName)
        {
            prefabName = null;
            if (_blockedNativeObject == null ||
                _blockedNativeObject.CommandId != ObjectToolOperationCommand.Id) return false;
            try
            {
                ObjectToolOperationCommand command =
                    ObjectToolOperationCommand.Decode(_blockedNativeObject.Body);
                if (!command.HasPlacementInput || command.IsAssetStamp) return false;
                prefabName = command.Definitions[command.RootIndex].PrefabName;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private void BlockNativeObject(SimulationCommandMessage message, long now)
        {
            _blockedNativeObject = message;
            _blockedNativeObjectDeadline = now + NativeObjectTargetRetryMs;
            _blockedNativeObjectNextAttemptMs = now + NativeObjectRetryIntervalMs;
            _hasBlockedNativeObject = true;
            Diagnostics.FlightRecorder.Note("object operation target retrying");
        }

        /// <summary>
        /// Route one remote object-domain message. Both shapes share the single ordered retry slot,
        /// so a stamp waiting for its prefab cannot be overtaken by a later placement.
        /// </summary>
        private NativeObjectResult TryRealizeRemoteObjectMessage(SimulationCommandMessage message,
            long now)
        {
            return message.CommandId == AssetStampCommand.Id
                ? TryRealizeAssetStamp(message, now)
                : TryRealizeNativeObject(message, now);
        }
    }
}
