using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates in-place road replacements and direction flips. The game commits both as
    /// <c>TempFlags.Modify</c>: the edge keeps its identity and only its prefab or orientation changes,
    /// so placement, delete and upgrade sync all miss it. Capture compares Updated edges with a
    /// per-entity baseline and sends the prefab with the old and committed curves; the receiver rebuilds
    /// every local edge on the old curve through the game's replacement definition on NetSync's commit.
    /// The baseline also suppresses echoes; unmatched replacements retry briefly.
    /// </summary>
    public partial class NetReplaceSyncSystem : CommandSyncSystem, IRealizeStage
    {
        private const long RetryWindowMs = 10000;
        private const long PruneIntervalMs = 5000;

        // Both ends within this of the replaced curve (XZ): a sub-segment. A lane is wider.
        private const float EdgeMatchCurveTol = 4f;

        // A bridge above the replaced road is another level.
        private const float EdgeMatchCurveTolY = 4f;

        // A flip swaps the endpoints exactly; neighbouring work only nudges them. Short stubs are ambiguous.
        private const float EndpointMatchTol = 2f;

        private readonly List<(NetReplaceCommand command, long deadline)> _retry =
            new List<(NetReplaceCommand, long)>();

        /// <summary>When this system last got to run a match pass. See RealizePending.</summary>
        private long _lastReplaceRealizeMs;

        // Replacements whose armed commit was destroyed; no deadline, replayed first.
        private readonly List<NetReplaceCommand> _replayCommands = new List<NetReplaceCommand>();

        // Originals of a mixed graph using final-edge fallback: also send their in-place curve updates.
        // Move It writes curves throughout a drag; publish the final geometry once it lets go.
        private readonly HashSet<Entity> _pendingMoveItEdges = new HashSet<Entity>();
        private ToolSystem _toolSystem;

        // Last-seen prefab and curve per edge: a prefab change is a replacement, swapped ends a flip, a grown
        // span a node-reduction survivor. Realize writes the post-commit state here to suppress the echo.
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

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            // Replacements are committed through NetSync's ApplyTool pipeline (see Realize).
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();

            // In-place changes only: Created edges are placements; previews, dying edges and sub-nets excluded.
            _updatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Created, Temp, Deleted, Owner>(),
            });

            // Enter new edges immediately, or their later replacement becomes the baseline.
            _createdEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            // Match pool for realizing remote replacements, and the seed pool for the baseline.
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            ListenFor(new[] { NetReplaceCommand.Id });
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _edgeBaseline.Clear();
            _retry.Clear();
            _replayCommands.Clear();
            _pendingMoveItEdges.Clear();
            _lastReplaceRealizeMs = 0;
            _seeded = false;
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("NetReplace"))
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
                if (IsMoveItDragging())
                    RememberMoveItEdges();
                else
                {
                    FlushMoveItEdges(session);
                    CaptureReplacements(session, now);
                }
                PruneDeadBaseline(now);
            }
        }

        private bool IsTool(string fullName) =>
            _toolSystem != null && _toolSystem.activeTool != null &&
            _toolSystem.activeTool.GetType().FullName == fullName;

        private bool IsMoveItDragging()
        {
            if (!IsTool("MoveIt.Tool.MIT")) return false;
            // If MITState disappears, hold geometry until the tool closes.
            var state = _toolSystem.activeTool.GetType().GetProperty("MITState");
            if (state == null) return true;
            object value = state.GetValue(_toolSystem.activeTool, null);
            return value == null || value.ToString() == "ApplyButtonHeld" ||
                   value.ToString() == "SecondaryButtonHeld";
        }

        private void RememberMoveItEdges()
        {
            if (_updatedEdges.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _updatedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                    if (_edgeBaseline.ContainsKey(entities[i])) _pendingMoveItEdges.Add(entities[i]);
            }
            finally { entities.Dispose(); }
        }

        private void FlushMoveItEdges(MultiplayerSession session)
        {
            if (_pendingMoveItEdges.Count == 0) return;
            // Let a native tool transaction finish its own capture first.
            BuildSyncSystem build = World.GetExistingSystemManaged<BuildSyncSystem>();
            if ((build != null && build.NativeLifecycleCapturedThisFrame) ||
                (_netSync != null && (_netSync.DidCommitObjectGraphThisFrame ||
                                      _netSync.LocalAtomicNetApplyCapturedThisFrame))) return;
            foreach (Entity edge in _pendingMoveItEdges)
            {
                if (!_edgeBaseline.TryGetValue(edge, out EdgeBaseline before) || !EntityManager.Exists(edge) ||
                    EntityManager.HasComponent<Deleted>(edge) ||
                    !EntityManager.HasComponent<Curve>(edge) ||
                    !EntityManager.HasComponent<PrefabRef>(edge)) continue;
                EdgeBaseline after = BaselineOf(edge);
                if (before.Prefab != after.Prefab || !SameCurveBits(before.Curve, after.Curve))
                    SendReplacement(session, after.Prefab, before.Curve, after.Curve,
                        "Move It committed geometry", true);
                _edgeBaseline[edge] = after;
            }
            _pendingMoveItEdges.Clear();
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
        /// Records every live edge at sync start, without overwriting an entry a realize already advanced.
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
            SyncLog.Detail(LogTopic.Nets, "NetReplaceSync: baselined " + _edgeBaseline.Count +
                " edge(s).");
        }

        /// <summary>Enters this frame's new edges as built.</summary>
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
        /// Both ends cross-match the baseline and neither matches straight: a flip, not a nudged node.
        /// Stubs too short to tell are skipped.
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
            BuildSyncSystem buildSync = World.GetExistingSystemManaged<BuildSyncSystem>();
            if ((buildSync != null && buildSync.NativeLifecycleCapturedThisFrame) ||
                (_netSync != null && (_netSync.DidCommitObjectGraphThisFrame ||
                                      _netSync.LocalAtomicNetApplyCapturedThisFrame)))
            {
                // Already carried by a native graph or mixed envelope: adopt as baseline.
                AdoptUpdatedEdges();
                return;
            }

            if (_updatedEdges.IsEmptyIgnoreFilter) return;

            // Detect against unchanged baselines first, then advance, so the extension test does not depend
            // on iteration order.
            var changes = new List<(Entity e, EdgeBaseline previous, Entity current, Bezier4x3 b)>();
            NativeArray<Entity> entities = _updatedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    Entity current = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    Bezier4x3 b = EntityManager.GetComponentData<Curve>(e).m_Bezier;

                    if (!_edgeBaseline.TryGetValue(e, out EdgeBaseline previous))
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
                bool expectedGeometryChange =
                    !SameCurveBits(previous.Curve, b) &&
                    (IsTool("MoveIt.Tool.MIT") ||
                     IsTool("NodeController.Main.Tools.NodeControllerTool"));

                if (!prefabChanged && !reversed && !expectedGeometryChange)
                {
                    // A grown curve is a node-reduction survivor; an uncovered growth is a road the game merged in
                    // before it surfaced as Created.
                    TrySendExtensions(session, e, current, previous.Curve, b);
                    continue;
                }

                SendReplacement(session, current, previous.Curve, b,
                    prefabChanged ? "replacement" : reversed ? "direction flip" :
                    expectedGeometryChange ? "mod geometry" : "mixed geometry",
                    expectedGeometryChange &&
                    (IsTool("MoveIt.Tool.MIT") ||
                     IsTool("NodeController.Main.Tools.NodeControllerTool")));
            }

            // Always advance, so a change is never re-detected or echoed.
            for (int i = 0; i < changes.Count; i++)
                _edgeBaseline[changes[i].e] = new EdgeBaseline { Prefab = changes[i].current, Curve = changes[i].b };
        }

        private void SendReplacement(MultiplayerSession session, Entity prefab,
            Bezier4x3 old, Bezier4x3 current, string reason,
            bool exactGeometry = false)
        {
            string name = _prefabSystem.GetPrefabName(prefab);
            if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) return;
            var command = new NetReplaceCommand
            {
                PrefabName = name,
                Ax = current.a.x, Ay = current.a.y, Az = current.a.z,
                Bx = current.b.x, By = current.b.y, Bz = current.b.z,
                Cx = current.c.x, Cy = current.c.y, Cz = current.c.z,
                Dx = current.d.x, Dy = current.d.y, Dz = current.d.z,
                OldAx = old.a.x, OldAy = old.a.y, OldAz = old.a.z,
                OldBx = old.b.x, OldBy = old.b.y, OldBz = old.b.z,
                OldCx = old.c.x, OldCy = old.c.y, OldCz = old.c.z,
                OldDx = old.d.x, OldDy = old.d.y, OldDz = old.d.z,
                ExactGeometry = exactGeometry,
            };
            session.SendCommand(0, NetReplaceCommand.Id, command.Encode());
            SyncLog.Detail(LogTopic.Nets, "NetReplaceSync captured " + reason + " of '" +
                name + "'.");
        }

        private static bool SameCurveBits(Bezier4x3 left, Bezier4x3 right)
        {
            return math.all(left.a == right.a) && math.all(left.b == right.b) &&
                   math.all(left.c == right.c) && math.all(left.d == right.d);
        }

        private void AdoptUpdatedEdges()
        {
            if (_updatedEdges.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _updatedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                    _edgeBaseline[entities[i]] = BaselineOf(entities[i]);
            }
            finally
            {
                entities.Dispose();
            }
        }

        // Snapping nudges endpoints by less; a drawn piece is longer.
        private const float MinExtensionLength = 3f;

        /// <summary>
        /// Sends the part of <paramref name="now"/> beyond its previous span when no baseline covers it: a
        /// continuation the game merged before it surfaced as Created. Merges of real edges are reproduced
        /// by the receiver and not sent.
        /// </summary>
        private void TrySendExtensions(MultiplayerSession session, Entity edge, Entity prefab,
            Bezier4x3 before, Bezier4x3 now)
        {
            // The old span must still lie on the new curve, or nothing can be inferred.
            if (MathUtils.Distance(now.xz, before.a.xz, out float tA) > EdgeMatchCurveTol) return;
            if (MathUtils.Distance(now.xz, before.d.xz, out float tD) > EdgeMatchCurveTol) return;
            float tMin = math.min(tA, tD);
            float tMax = math.max(tA, tD);

            // Each piece keeps its merged end's elevation, or it rebuilds as a ground net.
            Edge ends = EntityManager.GetComponentData<Edge>(edge);
            TrySendExtensionPiece(session, edge, prefab, MathUtils.Cut(now, new float2(0f, tMin)),
                NodeElevation(ends.m_Start));
            TrySendExtensionPiece(session, edge, prefab, MathUtils.Cut(now, new float2(tMax, 1f)),
                NodeElevation(ends.m_End));
        }

        private void TrySendExtensionPiece(MultiplayerSession session, Entity edge, Entity prefab,
            Bezier4x3 piece, float2 elevation)
        {
            float length = MathUtils.Length(piece);
            if (length < MinExtensionLength) return;

            // Covered by another edge's baseline: geometry moved between existing edges.
            foreach (KeyValuePair<Entity, EdgeBaseline> pair in _edgeBaseline)
            {
                if (pair.Key == edge || pair.Value.Prefab != prefab) continue;
                if (SplitMatch.IsSubCurve3D(piece, pair.Value.Curve)) return;
            }
            // Remote work re-surfacing through a local merge.
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
                Start = { ElevationLeft = elevation.x, ElevationRight = elevation.y },
                End = { ElevationLeft = elevation.x, ElevationRight = elevation.y },
            };
            session.SendCommand(0, NetPlacementCommand.Id, command.Encode());
            SyncLog.Detail(LogTopic.Nets, "NetReplaceSync: captured merged continuation of '" + name +
                "' (" + length.ToString("F1") + " m).");
        }
    }
}
