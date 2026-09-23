using System.Collections.Generic;
using Game.Common;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // State the commit machinery keeps, read and written throughout the Apply*.cs files.
    public partial class NetSyncSystem
    {
        // Generation also updates objects attached to touched nodes/edges, so the whole side-effect graph
        // stays isolated and commits as one.
        private global::Game.Tools.ToolSystem _toolSystem;
        private global::Game.Tools.ApplyNetSystem _applyNetSystem;
        private global::Game.Tools.ApplyObjectsSystem _applyObjectsSystem;
        private global::Game.Tools.ApplyAreasSystem _applyAreasSystem;
        private global::Game.Tools.ApplyBrushesSystem _applyBrushesSystem;
        private global::Game.Tools.ApplyRoutesSystem _applyRoutesSystem;
        // Structural subset the net apply pass consumes.
        private EntityQuery _netTransactionTemps;
        // Complete native output, including attached objects mirrored into the preview graph.
        private EntityQuery _netOperationTemps;
        // Exactly the union of the object, net and area apply domains; other tools' Temps would look like
        // a partial remote transaction.
        private EntityQuery _objectTransactionTemps;
        // Route root plus owned waypoints/segments, kept apart so a synced line never consumes a local preview.
        private EntityQuery _routeTransactionTemps;
        // A standing preview can span several domains; frozen and restored as one graph.
        private EntityQuery _standingTemps;
        // Re-sampled on Apply in case the tool regenerated or switched modes on the click frame.
        private EntityQuery _standingLocalDefinitions;
        private EntityQuery _localBrushTemps;
        private readonly List<Entity> _isolatedLocalTemps = new List<Entity>();
        private readonly List<Entity> _protectedRemoteNetTemps = new List<Entity>();
        private readonly List<Entity> _committingRemoteNetTemps = new List<Entity>();
        // A rejected or quarantined graph's identities, kept until none is Temp so retries never overlap it.
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
        // Committed geometry is queryable only once its Temps clear; the next batch waits for that.
        private bool _awaitingDrain;
        private int _armTick;
        private int _validateStartTick;
        private int _drainArmTick;
        // Frames spent waiting for the isolated entities to leave their Temp state.
        private int _drainFrames;
        // Clean ToolUpdates observed; native cleanup is deferred, so a short fence follows.
        private int _drainCleanFrames;

        /// <summary>Fewest surviving Temps this drain has observed; a new low restarts its window.</summary>
        private int _drainRemainingTemps = int.MaxValue;
        // Set through the releasing ToolUpdate, so later systems still see the coordinator busy.
        private bool _drainReleasedThisFrame;
        private bool _invalidatedBatchDraining;
        private System.Action _replayAfterInvalidatedDrain;
        private int _invalidatedDrainArmTick;
        private int _invalidatedCleanFrames;
        private bool _invalidatedDrainTimedOut;
        // Only on the frame a remote batch commits: capture skips that pass's Created edges.
        private bool _suppressCaptureThisFrame;
        // One local-net isolation per realize frame; reset by BeginRealizeFrame.
        private bool _prepDoneThisFrame;
        // Recently realized spans, so node reduction re-surfacing them is recognised as remote work.
        private readonly List<(Colossal.Mathematics.Bezier4x3 curve, long expiresMs)> _recentRealizedSpans =
            new List<(Colossal.Mathematics.Bezier4x3, long)>();
        // Recovery hook for a batch whose Temps vanished or became stale before commit.
        private System.Action _onCommitLost;
        // Realize-frame counter used to correlate native local intent with its Apply frame.
        private int _realizeFrame;
        // Consecutive replays; a batch the game always rejects would otherwise rebuild forever.
        private readonly CS2MultiplayerMod.Core.Sync.BoundedRetryBudget _applyReplayBudget =
            new CS2MultiplayerMod.Core.Sync.BoundedRetryBudget(3);

        /// <summary>
        /// An owner described by prefab and transform. Generators leave <see cref="Owner.m_Owner"/> unset
        /// and the game resolves it a phase later, consuming the description whether or not it matched.
        /// </summary>
        public struct ArmedOwnerDefinition
        {
            public Entity Prefab;
            public Unity.Mathematics.float3 Position;
        }

        // Lets the validator re-link a sub-element whose one-shot resolution missed.
        private ObjectSearch _ownerSearch;
        // Each described sub-element's owner, captured before resolution consumes it; keyed by entity.
        private readonly Dictionary<Entity, ArmedOwnerDefinition> _describedOwners =
            new Dictionary<Entity, ArmedOwnerDefinition>();
        private readonly List<ArmedOwnerDefinition> _pendingOwnerDefinitions =
            new List<ArmedOwnerDefinition>();
        // A replay against an unchanged world with the same reason and count cannot succeed.
        private string _lastInvalidReason;
        // Memo for the owner description the current transaction is re-linking against.
        private Entity _lastDescribedOwnerPrefab;
        private Unity.Mathematics.float3 _lastDescribedOwnerPosition;
        private Entity _lastDescribedOwner;
        // Sub-elements the current validation pass re-parented, reported as one total.
        private int _relinkedOwners;

        // The net tool's definitions after ToolOutputBarrier: the next Apply's courses, with the
        // target/split/elevation intent the final edges no longer carry.
        private readonly List<NetPlacementCommand> _cachedLocalCourses = new List<NetPlacementCommand>();
        private sealed class LocalNetToolOperationItem
        {
            public ushort CommandId;
            // Local only; on some clicks its Temp does not exist yet.
            public Entity Original;
            public NetPlacementCommand Placement;
            public NetDeleteCommand Delete;
            public NetReplaceCommand Replace;
        }

        // Creates, modifies and deletes of one Apply, in source order, published as one command.
        private readonly List<LocalNetToolOperationItem> _cachedLocalMixedOperation =
            new List<LocalNetToolOperationItem>();
        // Originals of an unencodable mixed graph: the sender suppresses fragmented echoes and requests recovery.
        private readonly List<Entity> _cachedFallbackOriginalEdges = new List<Entity>();
        private string _cachedMixedRejection;
        private bool HasUnrepresentableMixedOperation => _cachedMixedRejection != null;
        private long _nextLocalNetOperationId = 1;
        private int _nativeApplyCapturedFrame = -1;
        private int _atomicMixedApplyCapturedFrame = -1;

        /// <summary>This frame's mixed Apply went the atomic path, so final-edge capture stays silent.</summary>
        public bool LocalAtomicNetApplyCapturedThisFrame =>
            _atomicMixedApplyCapturedFrame == _realizeFrame;

        // Originals of a committing placement; their Deleted output is not a second command.
        private readonly Dictionary<Entity, long> _committedNetSideEffects = new Dictionary<Entity, long>();
        // Originals of this frame's definitions, for barrier-recovery capture before Temps exist.
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

        // Wait for a target an earlier command is still building; after the window, fall back to geometry.
        private const long NativeTargetRetryWindowMs = 10000;
        private readonly Dictionary<NativeTargetRetryKey, long> _nativeTargetDeadlines =
            new Dictionary<NativeTargetRetryKey, long>();
    }
}
