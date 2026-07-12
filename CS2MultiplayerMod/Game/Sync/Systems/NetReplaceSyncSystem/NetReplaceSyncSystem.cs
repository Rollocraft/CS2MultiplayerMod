using System.Collections.Concurrent;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates in-place road REPLACEMENTS - drawing a different net prefab over an existing edge
    /// (a one-lane road becomes two-lane, an asphalt road becomes a highway of the same footprint ...)
    /// and in-place DIRECTION FLIPS (replacing a one-way against its direction commits the same edge
    /// with an inverted curve and swapped ends). The game commits both as a <c>TempFlags.Modify</c>:
    /// the edge KEEPS its identity and only its <see cref="PrefabRef"/> and/or orientation change,
    /// surfacing as a bare <see cref="Updated"/> tag - so placement sync (needs <see cref="Created"/>),
    /// delete sync (needs <see cref="Deleted"/>) and composition-upgrade sync (only reads
    /// <c>CompositionFlags</c>) all miss it. (The other, rarer outcome - a replacement whose
    /// zoning/electricity capability differs - the game does as delete+create, which the placement and
    /// delete systems already replicate.)
    ///
    ///   detect (ModificationEnd): an <see cref="Updated"/>, non-<see cref="Created"/> edge whose
    ///           prefab OR curve direction differs from a per-Entity baseline -> broadcast a
    ///           <see cref="NetReplaceCommand"/> carrying the (new) prefab + the edge's BASELINE
    ///           Bézier (where the receiver's copy still lies) + its COMMITTED Bézier (where it must
    ///           end up - a width-changing replacement shifts the committed centerline sideways by
    ///           half the width difference, and its orientation encodes the direction).
    ///   realize (ToolUpdate, via <see cref="SyncRealizeSystem"/>): find every local edge lying on the
    ///           OLD curve and rebuild it on (its sub-span of) the NEW curve through the game's own
    ///           replacement definition - inverted when the local edge runs against the command's
    ///           direction - committed on <see cref="NetSyncSystem"/>'s ApplyTool pipeline (same path
    ///           as a bulldoze) so lanes, composition and connections rebuild natively - see
    ///           <c>Realize</c>.
    ///
    /// The per-Entity baseline is exact (an in-place replace keeps the edge entity), which both detects
    /// the change and, updated the instant we realize one, suppresses the echo without a spatial guard.
    /// Edges are entered into the baseline at sync start AND as they are Created, so a road built
    /// mid-session has its later replacement detected too (adopting it on its first Updated event
    /// instead would swallow exactly that replacement). A just-built road can race its own placement
    /// command, so unmatched replacements retry briefly.
    /// </summary>
    public partial class NetReplaceSyncSystem : GameSystemBase
    {
        private const long RetryWindowMs = 10000;
        private const long PruneIntervalMs = 5000;

        // Endpoint-to-curve match tolerance (metres, XZ) — an edge whose both ends lie this close to the
        // replaced curve is one of its (possibly re-subdivided) sub-segments. Matches DeleteSyncSystem;
        // a lane is wider than this, so a match never reaches a parallel road.
        private const float EdgeMatchCurveTol = 4f;

        // Max height difference for that match — a bridge stacked directly above the replaced road on
        // the same XZ line is a different LEVEL and must never be re-typed. Matches DeleteSyncSystem.
        private const float EdgeMatchCurveTolY = 4f;

        // Endpoint tolerance (metres, XZ) for the reversal check: an in-place direction flip swaps the
        // committed curve's endpoints exactly, while node adjustments from neighbouring work only nudge
        // them. Edges shorter than a few times this are skipped as ambiguous.
        private const float EndpointMatchTol = 2f;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly List<(NetReplaceCommand command, long deadline)> _retry =
            new List<(NetReplaceCommand, long)>();

        // Replacements whose armed commit was destroyed before it ran (the player's tool cleared the
        // Temps — see NetSyncSystem's commit-lost handling). Unlike _retry these carry no deadline:
        // the target edge exists, the commit just couldn't run yet. Replayed first next idle cycle.
        private readonly List<NetReplaceCommand> _replayCommands = new List<NetReplaceCommand>();

        // Last-seen state of each live edge entity: prefab + the full committed curve. An in-place
        // replacement keeps the edge entity, so a change of prefab IS a replacement, a swap of the
        // endpoints IS a direction flip, and a curve that GREW beyond its old span is the survivor of
        // a node reduction (see TrySendExtensions); realize writes the post-commit state here
        // immediately so the resulting Updated tag is not re-detected as a fresh change.
        private struct EdgeBaseline
        {
            public Entity Prefab;
            public Bezier4x3 Curve;
        }

        private readonly Dictionary<Entity, EdgeBaseline> _edgeBaseline = new Dictionary<Entity, EdgeBaseline>();
        private bool _seeded;
        private long _lastPruneMs = -1;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private NetSyncSystem _netSync;
        private EntityQuery _updatedEdges;
        private EntityQuery _createdEdges;
        private EntityQuery _liveEdges;
        private CommandObserver _observer;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(NetReplaceSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            // Replacements are committed through NetSync's ApplyTool pipeline (see Realize).
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();

            // Detect: an edge whose composition/prefab was touched this frame. Created is EXCLUDED — a
            // freshly created edge is a placement (or the create-half of a zoning-differ replacement),
            // both handled by NetSyncSystem; only an in-place PrefabRef change on a surviving edge is
            // ours. Temp/Deleted/Owner excluded so we never look at previews, dying edges or sub-nets.
            _updatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Edges built this frame (locally drawn or realized from a remote command). They enter the
            // baseline immediately so a LATER replacement of a mid-session road is detected as a change
            // — waiting for its first Updated event would adopt the post-replacement prefab as the
            // baseline and swallow exactly that replacement.
            _createdEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Match pool for realizing remote replacements, and the seed pool for the baseline.
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, NetReplaceCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _edgeBaseline.Clear();
            _retry.Clear();
            _replayCommands.Clear();
            _seeded = false;
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady)
            {
                // Drop the baseline between sessions/world-loads so the next world re-seeds cleanly.
                DrainQueue();
                return;
            }

            long now = service.NowMs;
            if (!_seeded) SeedBaseline();
            SeedCreatedEdges();
            CaptureReplacements(session, now);
            PruneDeadBaseline(now);
        }

        private EdgeBaseline BaselineOf(Entity e)
        {
            return new EdgeBaseline
            {
                Prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                Curve = EntityManager.GetComponentData<Curve>(e).m_Bezier,
            };
        }

        /// <summary>
        /// Record the current state of every live edge once, at sync start, so a later in-place
        /// replacement of a pre-existing (world-loaded) road is detected as a change rather than
        /// silently adopted as the baseline. ContainsKey-guarded so it never clobbers an entry a
        /// same-frame realize already advanced to the new state.
        /// </summary>
        private void SeedBaseline()
        {
            NativeArray<Entity> entities = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    if (_edgeBaseline.ContainsKey(e)) continue;
                    _edgeBaseline[e] = BaselineOf(e);
                }
            }
            finally
            {
                entities.Dispose();
            }
            _seeded = true;
            Mod.Verbose("[MP] NetReplaceSync: baselined " + _edgeBaseline.Count + " edge(s).");
        }

        /// <summary>
        /// Enter edges built this frame into the baseline with their as-built state (see the
        /// <c>_createdEdges</c> query for why waiting for their first Updated event is wrong).
        /// </summary>
        private void SeedCreatedEdges()
        {
            if (_createdEdges.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _createdEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    if (_edgeBaseline.ContainsKey(e)) continue;
                    _edgeBaseline[e] = BaselineOf(e);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>Forget baseline entries whose edge no longer exists (bulldozed / recycled).</summary>
        private void PruneDeadBaseline(long now)
        {
            if (_lastPruneMs >= 0 && now - _lastPruneMs < PruneIntervalMs) return;
            _lastPruneMs = now;

            List<Entity> dead = null;
            foreach (var pair in _edgeBaseline)
                if (!EntityManager.Exists(pair.Key)) (dead ?? (dead = new List<Entity>())).Add(pair.Key);
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _edgeBaseline.Remove(dead[i]);
        }

        /// <summary>
        /// True when the committed curve's endpoints are the baseline's SWAPPED - an in-place direction
        /// flip. Requires both cross-matches and no straight match, so a node nudged by neighbouring
        /// work (same orientation, one end moved) is a geometry update, not a flip; stubs too short to
        /// tell apart are skipped.
        /// </summary>
        private static bool IsReversed(in EdgeBaseline before, Bezier4x3 now)
        {
            float2 beforeA = before.Curve.a.xz;
            float2 beforeD = before.Curve.d.xz;
            if (math.distance(beforeA, beforeD) < EndpointMatchTol * 3f) return false;
            if (math.distance(now.a.xz, beforeA) <= EndpointMatchTol) return false;
            return math.distance(now.a.xz, beforeD) <= EndpointMatchTol
                && math.distance(now.d.xz, beforeA) <= EndpointMatchTol;
        }

        private void CaptureReplacements(MultiplayerSession session, long now)
        {
            if (_updatedEdges.IsEmptyIgnoreFilter) return;

            // Two passes: detect against the UNCHANGED baselines first, then advance them all. The
            // extension test below checks whether a grown span was previously covered by a
            // NEIGHBOUR's old curve — advancing baselines while detecting would make that test
            // depend on iteration order (a node-move pair updates two edges in one frame).
            var changes = new List<(Entity e, EdgeBaseline previous, Entity current, Bezier4x3 b)>();
            NativeArray<Entity> entities = _updatedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    Entity current = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    Bezier4x3 b = EntityManager.GetComponentData<Curve>(e).m_Bezier;

                    EdgeBaseline previous;
                    if (!_edgeBaseline.TryGetValue(e, out previous))
                    {
                        // First sight (edge predates the created-edge seeding) -> baseline, not a change.
                        _edgeBaseline[e] = BaselineOf(e);
                        continue;
                    }
                    changes.Add((e, previous, current, b));
                }
            }
            finally
            {
                entities.Dispose();
            }

            for (int i = 0; i < changes.Count; i++)
            {
                (Entity e, EdgeBaseline previous, Entity current, Bezier4x3 b) = changes[i];
                bool prefabChanged = previous.Prefab != current;
                bool reversed = IsReversed(previous, b);

                if (!prefabChanged && !reversed)
                {
                    // Updated for some other reason — unless the curve GREW beyond its old span,
                    // which is the survivor of a node reduction. If nothing else ever covered the
                    // grown part, it is a road the player just drew that the game merged straight
                    // into this edge (a collinear continuation) — the merge swallows the Created
                    // edge, so this is the only place that work still surfaces. Send it.
                    TrySendExtensions(session, e, current, previous.Curve, b);
                    continue;
                }

                string name = _prefabSystem.GetPrefabName(current);
                if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;

                // Both curves go out: the receiver finds its edges on the OLD (baseline) curve and
                // re-commits them on the NEW one — a width-changing replacement can shift the
                // committed centerline by half the width difference, and matching the new curve
                // against edges still on the old line is a coin flip at the match tolerance.
                Bezier4x3 old = previous.Curve;
                var command = new NetReplaceCommand
                {
                    PrefabName = name,
                    Ax = b.a.x, Ay = b.a.y, Az = b.a.z,
                    Bx = b.b.x, By = b.b.y, Bz = b.b.z,
                    Cx = b.c.x, Cy = b.c.y, Cz = b.c.z,
                    Dx = b.d.x, Dy = b.d.y, Dz = b.d.z,
                    OldAx = old.a.x, OldAy = old.a.y, OldAz = old.a.z,
                    OldBx = old.b.x, OldBy = old.b.y, OldBz = old.b.z,
                    OldCx = old.c.x, OldCy = old.c.y, OldCz = old.c.z,
                    OldDx = old.d.x, OldDy = old.d.y, OldDz = old.d.z,
                };
                session.SendCommand(0, NetReplaceCommand.Id, command.Encode());
                Mod.Verbose("[MP] NetReplaceSync captured " +
                            (prefabChanged ? "replacement -> '" + name + "'" : "direction flip of '" + name + "'") + ".");
            }

            // Advance every touched baseline to the committed state — whether we sent or not — so a
            // change is never re-detected and (on the receiver) a realized replacement never echoes
            // back. Endpoint drift from neighbouring work lands here too.
            for (int i = 0; i < changes.Count; i++)
                _edgeBaseline[changes[i].e] = new EdgeBaseline { Prefab = changes[i].current, Curve = changes[i].b };
        }

        // Shortest extension worth replicating (metres). Node snapping and neighbouring work nudge
        // endpoints by less; a road piece the player actually drew is longer.
        private const float MinExtensionLength = 3f;

        /// <summary>
        /// Detect and replicate the part(s) of <paramref name="now"/> that lie BEYOND the edge's
        /// previous span. The game's node reduction merges a collinear same-prefab neighbour into
        /// this edge in place; when that neighbour was a real edge (a bulldoze freed the node) or the
        /// node merely moved, the receiver reproduces the change locally and the grown span is
        /// covered by some baseline - nothing is sent. When it was only ever a Temp (the player drew
        /// a straight continuation and the game merged it before a Created edge could surface), the
        /// extension is new work that would otherwise never reach the wire; it goes out as an
        /// ordinary placement and the receiver's own reduction merges it back.
        /// </summary>
        private void TrySendExtensions(MultiplayerSession session, Entity edge, Entity prefab,
            Bezier4x3 before, Bezier4x3 now)
        {
            // The old span must still lie on the new curve — otherwise the edge was reshaped
            // wholesale (not a reduction survivor) and there is nothing safe to infer.
            float tA, tD;
            if (MathUtils.Distance(now.xz, before.a.xz, out tA) > EdgeMatchCurveTol) return;
            if (MathUtils.Distance(now.xz, before.d.xz, out tD) > EdgeMatchCurveTol) return;
            float tMin = math.min(tA, tD);
            float tMax = math.max(tA, tD);

            TrySendExtensionPiece(session, edge, prefab, MathUtils.Cut(now, new float2(0f, tMin)));
            TrySendExtensionPiece(session, edge, prefab, MathUtils.Cut(now, new float2(tMax, 1f)));
        }

        private void TrySendExtensionPiece(MultiplayerSession session, Entity edge, Entity prefab, Bezier4x3 piece)
        {
            float length = MathUtils.Length(piece);
            if (length < MinExtensionLength) return;

            // Covered by another same-prefab edge's last-known span → the geometry only moved BETWEEN
            // existing edges (reduction victim / node move); the receiver reproduces that locally.
            foreach (KeyValuePair<Entity, EdgeBaseline> pair in _edgeBaseline)
            {
                if (pair.Key == edge || pair.Value.Prefab != prefab) continue;
                if (SplitMatch.IsSubCurve3D(piece, pair.Value.Curve)) return;
            }
            // A span this machine just realized from a remote command, re-surfacing through a local
            // merge — remote work, never echoed back.
            if (_netSync != null && _netSync.WasRecentlyRealized(piece)) return;

            string name = _prefabSystem.GetPrefabName(prefab);
            if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) return;

            var command = new NetPlacementCommand
            {
                PrefabName = name,
                Ax = piece.a.x, Ay = piece.a.y, Az = piece.a.z,
                Bx = piece.b.x, By = piece.b.y, Bz = piece.b.z,
                Cx = piece.c.x, Cy = piece.c.y, Cz = piece.c.z,
                Dx = piece.d.x, Dy = piece.d.y, Dz = piece.d.z,
                Length = length,
            };
            session.SendCommand(0, NetPlacementCommand.Id, command.Encode());
            Mod.Verbose("[MP] NetReplaceSync: captured merged continuation of '" + name + "' (" +
                        length.ToString("F1") + " m).");
        }
    }
}
