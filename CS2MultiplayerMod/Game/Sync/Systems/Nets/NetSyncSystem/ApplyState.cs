using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // The state the commit machinery keeps: the game's apply systems, the temp queries and the
    // lists that hold one transaction's entities apart from another's, what is currently armed,
    // what is draining, the owners the host described, and the local gesture cached for capture.
    //
    // Read and written throughout the Apply*.cs files; kept here so what a commit is made of can
    // be read in one place.
    public partial class NetSyncSystem
    {
        // Remote courses use the same Temp-backed transaction as an interactive placement. Besides
        // network entities, generation can update objects attached to a touched node/edge (and any
        // geometry they own), so the complete side-effect graph must stay isolated and commit as one.
        private global::Game.Tools.ToolSystem _toolSystem;
        private global::Game.Tools.ApplyNetSystem _applyNetSystem;
        private global::Game.Tools.ApplyObjectsSystem _applyObjectsSystem;
        private global::Game.Tools.ApplyAreasSystem _applyAreasSystem;
        private global::Game.Tools.ApplyBrushesSystem _applyBrushesSystem;
        private global::Game.Tools.ApplyRoutesSystem _applyRoutesSystem;
        // Structural subset consumed by the net apply pass. RecordPlacementOriginals uses this to
        // inspect replacement edges without walking attached-object or owned-area side effects.
        private EntityQuery _netTransactionTemps;
        // Complete native output of a net definition. GenerateObjectsSystem mirrors attached
        // objects (roundabout islands, road signs, and similar objects) into the preview graph so
        // their transforms follow a node/edge that moves during the network operation.
        private EntityQuery _netOperationTemps;
        // The object apply pass consumes Object Temps while owned driveways/connectors and lots are
        // consumed by the net and area passes. Keep this query equal to the union of those three
        // apply domains. Other tools can legitimately create unrelated Temp shapes in the same
        // frame; including them would make them look like a partial remote object transaction.
        private EntityQuery _objectTransactionTemps;
        // Route definitions always generate a Temp root plus owned waypoint/segment Temps. Keep
        // that exact apply-domain boundary separate from nets and object graphs so a synchronized
        // line can be validated and committed without consuming an interactive route preview.
        private EntityQuery _routeTransactionTemps;
        // A standing interactive preview can span several domains (for example a building plus
        // owned driveway nets). It must be frozen and restored as one graph.
        private EntityQuery _standingTemps;
        // Exact local definition graph behind the preview that is about to commit. The after-barrier
        // cache normally describes the same graph, but sampling it again on Apply keeps a multi-course
        // operation intact when the tool regenerated or switched modes on the click frame.
        private EntityQuery _standingLocalDefinitions;
        private EntityQuery _localBrushTemps;
        private readonly List<Entity> _isolatedLocalTemps = new List<Entity>();
        private readonly List<Entity> _protectedRemoteNetTemps = new List<Entity>();
        private readonly List<Entity> _committingRemoteNetTemps = new List<Entity>();
        // Rejected uncommitted transactions are only tagged Deleted by the clear path, while a
        // committed transaction that misses its drain window must remain untouched. Keep either
        // graph's exact identities until none remains Temp so recovery/retry can never overlap stale
        // native ownership/connectivity state.
        private readonly List<Entity> _invalidatedRemoteTemps = new List<Entity>();
        private readonly List<Entity> _isolatedLocalBrushTemps = new List<Entity>();
        private bool _clearLocalNetIsolationAfterBarrier;
        private bool _localToolOutputProtectedThisFrame;
        private bool _pendingApply;
        private enum RemoteToolTransactionKind : byte
        {
            None,
            Net,
            ObjectGraph,
            AssetStampGraph,
            Route,
        }
        private RemoteToolTransactionKind _pendingTransactionKind;
        private RemoteToolTransactionKind _committingTransactionKind;
        private static bool IsObjectGraphTransaction(RemoteToolTransactionKind kind) =>
            kind == RemoteToolTransactionKind.ObjectGraph ||
            kind == RemoteToolTransactionKind.AssetStampGraph;
        private static bool IsRouteTransaction(RemoteToolTransactionKind kind) =>
            kind == RemoteToolTransactionKind.Route;
        private System.Action _onCommitComplete;
        private bool _objectCommitThisFrame;
        private long _pendingNetConstructionCharge;
        private int _pendingNetConstructionChargeCourses;
        private long _committingNetConstructionCharge;
        private int _committingNetConstructionChargeCourses;
        // After a commit we must WAIT for its Temp entities to clear (the committed nodes/edges only
        // become query-able then) before building the next batch — otherwise a course that should
        // connect to the just-committed geometry cannot find it and lands on free ground.
        private bool _awaitingDrain;
        private int _armTick;
        private int _validateStartTick;
        private int _drainArmTick;
        // Frames spent waiting for the isolated entities to leave their Temp state.
        private int _drainFrames;
        // Consecutive ToolUpdate observations with none of the committed graph still Temp. Native
        // cleanup is deferred, so the next transaction stays blocked through a short clean fence.
        private int _drainCleanFrames;

        /// <summary>Fewest surviving Temps this drain has observed; a new low restarts its window.</summary>
        private int _drainRemainingTemps = int.MaxValue;
        // Remains set through the ToolUpdate which releases either drain state. Systems after
        // NetSync in that phase must still observe the coordinator as busy.
        private bool _drainReleasedThisFrame;
        private bool _invalidatedBatchDraining;
        private System.Action _replayAfterInvalidatedDrain;
        private int _invalidatedDrainArmTick;
        private int _invalidatedCleanFrames;
        private bool _invalidatedDrainTimedOut;
        // True only on the frame the isolated net-domain pass commits a remote batch. Capture skips
        // that pass's Created edges; local Apply frames are never suppressed.
        private bool _suppressCaptureThisFrame;
        // One local-net isolation per realize frame; reset by BeginRealizeFrame.
        private bool _prepDoneThisFrame;
        // Spans this machine realized from remote commands recently. A realize commit can trigger the
        // game's node reduction, which re-surfaces the just-built span as a LOCAL Updated/Created edge
        // (merged with a neighbour); these records let the capture side (NetReplaceSync's extension
        // detection) recognise that geometry as remote work, not something to broadcast back.
        private readonly List<(Colossal.Mathematics.Bezier4x3 curve, long expiresMs)> _recentRealizedSpans =
            new List<(Colossal.Mathematics.Bezier4x3, long)>();
        // Recovery hook for a batch whose Temps vanished or became stale before commit.
        private System.Action _onCommitLost;
        // Realize-frame counter used to correlate native local intent with its Apply frame.
        private int _realizeFrame;
        /// <summary>
        /// Set by <see cref="SyncRealizeSystem"/> while remote terrain edits are backlogged: no NEW
        /// remote course realizes until terrain catches up (a course realized against pre-terraform
        /// ground commits at the wrong height - craters, buried streets, missed height-gated snaps).
        /// In-flight commits still finish.
        /// </summary>
        public bool DeferForTerrain;
        // Consecutive expired-window replays (reset by any successful commit). A batch whose
        // definitions the game always rejects would otherwise rebuild forever.
        private readonly CS2MultiplayerMod.Core.Sync.BoundedRetryBudget _applyReplayBudget =
            new CS2MultiplayerMod.Core.Sync.BoundedRetryBudget(3);

        /// <summary>
        /// One owner the armed batch describes by prefab and world transform rather than by entity.
        /// The native generators leave <see cref="Owner.m_Owner"/> unset on such a sub-element and
        /// pair it with an <see cref="OwnerDefinition"/>; the game resolves that a phase later by an
        /// exact transform match and consumes the description whether or not it matched.
        /// </summary>
        public struct ArmedOwnerDefinition
        {
            public Entity Prefab;
            public Unity.Mathematics.float3 Position;
        }

        // Owner descriptions of the armed object batch, kept so the validator can re-link a
        // sub-element whose one-shot resolution missed instead of discarding the whole graph.
        private ObjectSearch _ownerSearch;
        // Which owner each described sub-element named, captured before the game's resolution pass
        // consumes the descriptions. Keyed by the generated entity, so a batch naming several owners
        // is still unambiguous.
        private readonly Dictionary<Entity, ArmedOwnerDefinition> _describedOwners =
            new Dictionary<Entity, ArmedOwnerDefinition>();
        private readonly List<ArmedOwnerDefinition> _pendingOwnerDefinitions =
            new List<ArmedOwnerDefinition>();
        // Previous rejection of the batch currently being replayed. A replay re-runs the identical
        // command against an unchanged world, so an unchanged reason and member count prove the
        // remaining attempts cannot succeed.
        private string _lastInvalidReason;
        // Memo for the owner description the current transaction is re-linking against.
        private Entity _lastDescribedOwnerPrefab;
        private Unity.Mathematics.float3 _lastDescribedOwnerPosition;
        private Entity _lastDescribedOwner;
        // Sub-elements the current validation pass re-parented, reported as one total.
        private int _relinkedOwners;

        // The active net tool's latest native definitions, observed after ToolOutputBarrier. They
        // are the definitions that produced the standing preview Temps and therefore describe the
        // course committed by the next Apply frame. Capturing here preserves target/split/elevation
        // intent that no longer exists on the final Created edges.
        private readonly List<NetPlacementCommand> _cachedLocalCourses = new List<NetPlacementCommand>();
        private sealed class LocalNetToolOperationItem
        {
            public ushort CommandId;
            // Kept locally only. On a click whose definitions first appear after ToolOutputBarrier,
            // the corresponding Temp does not exist yet for RecordPlacementOriginals to inspect.
            public Entity Original;
            public NetPlacementCommand Placement;
            public NetDeleteCommand Delete;
            public NetReplaceCommand Replace;
        }

        // A net-tool Apply may create new courses and modify/delete old ones in the same native
        // transaction. Keep that source ordering intact until the Apply pulse, then publish one
        // NetToolOperationCommand instead of letting three independent capture systems fragment it.
        private readonly List<LocalNetToolOperationItem> _cachedLocalMixedOperation =
            new List<LocalNetToolOperationItem>();
        // If one member makes a mixed graph unencodable, remember its live originals until Apply.
        // The sender then suppresses every fragmented lifecycle echo and requests world recovery;
        // sending a reduced delete/replace/place sequence would recreate the original ordering bug.
        private readonly List<Entity> _cachedFallbackOriginalEdges = new List<Entity>();
        private bool _cachedNeedsFinalEdgeFallback;
        private long _nextLocalNetOperationId = 1;
        private int _nativeApplyCapturedFrame = -1;
        private int _atomicMixedApplyCapturedFrame = -1;

        /// <summary>
        /// True through ModificationEnd when this frame's local mixed net-tool Apply was handled by
        /// the atomic path (sent, or escalated to recovery after a send failure). Final-edge capture
        /// uses this to avoid emitting a second, reduced representation of the same gesture.
        /// </summary>
        public bool LocalAtomicNetApplyCapturedThisFrame =>
            _atomicMixedApplyCapturedFrame == _realizeFrame;

        // Exact originals referenced by a committing placement's Temps. If one is Deleted by the
        // resulting split/delete/replace, DeleteSync must not broadcast that lifecycle output as a
        // second command. The short expiry covers the apply and its immediate network aftermath.
        private readonly Dictionary<Entity, long> _committedNetSideEffects = new Dictionary<Entity, long>();
        // Barrier-recovery capture can run before this frame's definitions have produced Temps, so
        // RecordPlacementOriginals has nothing to inspect. Retain the definitions' exact originals
        // for this realize frame as the zero-heuristic deletion echo guard. Explicit deletes are
        // additionally placed in the timed guard because their Deleted tag may be cleaned up later.
        private readonly HashSet<Entity> _atomicMixedOriginals = new HashSet<Entity>();
        private int _atomicMixedOriginalsFrame = -1;
        private readonly CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NetOperationKey>
            _completedNetOperations =
                new CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NetOperationKey>();

        private struct NativeTargetRetryKey : System.IEquatable<NativeTargetRetryKey>
        {
            public int Origin;
            public long Operation;
            public short Course;

            public bool Equals(NativeTargetRetryKey other) =>
                Origin == other.Origin && Operation == other.Operation && Course == other.Course;
            public override bool Equals(object obj) => obj is NativeTargetRetryKey && Equals((NativeTargetRetryKey)obj);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Origin;
                    hash = hash * 397 ^ Operation.GetHashCode();
                    hash = hash * 397 ^ Course;
                    return hash;
                }
            }
        }

        // A native target may be created by an earlier command whose geometry is not queryable yet.
        // Wait for it instead of silently downgrading the endpoint to free ground; after the bounded
        // window, fall back to geometric classification so a permanently divergent world cannot
        // wedge every later placement.
        private const long NativeTargetRetryWindowMs = 10000;
        private readonly Dictionary<NativeTargetRetryKey, long> _nativeTargetDeadlines =
            new Dictionary<NativeTargetRetryKey, long>();
    }
}
