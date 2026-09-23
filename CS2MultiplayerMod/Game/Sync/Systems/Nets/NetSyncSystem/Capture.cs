using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    public partial class NetSyncSystem
    {
        private void RecordDiagnostic(string prefabName)
        {
            _diagTotal++;
            _diag.TryGetValue(prefabName, out int count);
            _diag[prefabName] = count + 1;
        }

        private void FlushDiagnostics(long now)
        {
            if (_diagStartMs < 0) { _diagStartMs = now; return; }
            if (now - _diagStartMs < 5000) return;

            if (_diagTotal > 0)
            {
                var sb = new StringBuilder();
                sb.Append("NetSync captured ").Append(_diagTotal)
                  .Append(" road segment(s)/5s across ").Append(_diag.Count).Append(" prefab(s): ");
                int n = 0;
                foreach (var pair in _diag)
                {
                    if (n > 0) sb.Append(", ");
                    sb.Append(pair.Key).Append(" x").Append(pair.Value);
                    if (++n >= 12) { sb.Append(", ..."); break; }
                }
                SyncLog.Detail(LogTopic.Nets, sb.ToString());
            }

            if (_peakUpdated > 0 || _peakDeleted > 0 || _diagTotal > 0 || _capFilteredHalves > 0)
            {
                SyncLog.Detail(LogTopic.Nets, "NetSync edge tags/5s peak: Created=" + _peakCreated +
                    " Updated=" + _peakUpdated + " Deleted=" + _peakDeleted + "; dropped " +
                    _capFilteredHalves + " split-half edge(s) (side-effects of a " +
                    "mid-span tap; only the drawn edge is sent so the receiver splits locally).");
            }

            if (_rzSegments > 0)
            {
                SyncLog.Detail(LogTopic.Nets, "NetSync realized " + _rzSegments +
                    " remote segment(s)/5s; endpoints: " + _rzSnapEnds + " reused a node, " +
                    _rzMergeEnds + " merged a shared new node, " + _rzMidEnds +
                    " split an existing edge (T-junction), " + _rzFreeEnds + " free ground.");
            }

            if (_capPinnedSpans > 0 || _rzPinnedSpans > 0 || _rzPinRefused > 0 ||
                _capSelfAnchoredSpans > 0 || _capBentDeckSpans > 0)
            {
                SyncLog.Detail(LogTopic.Nets, "NetSync profile/5s: pinned " +
                    _capPinnedSpans + " straight span(s); left " + _capSelfAnchoredSpans +
                    " banded by their own elevation and " + _capBentDeckSpans +
                    " with a deck that is not one line to the receiver's own generator; realized " +
                    _rzPinnedSpans + " pinned span(s), " + _rzPinRefused +
                    " refused (those rebuild their deck from local surfaces).");
            }

            if (_rzSurfaceCorrections > 0)
            {
                SyncLog.Detail(LogTopic.Nets, "NetSync: " + _rzSurfaceCorrections +
                    " remote endpoint(s)/5s needed " + "an elevation correction (up to " +
                    _rzSurfaceCorrectionMax.ToString("F1") + " m) because the surface under " +
                    "them differs from the source's. The height is reproduced; a large or " +
                    "growing figure means terrain or water is out of step.");
            }

            if (_rzLocalSurfaceMatches > 0)
            {
                SyncLog.Detail(LogTopic.Nets, "NetSync: " + _rzLocalSurfaceMatches +
                    " utility endpoint(s)/5s reused connectivity through local-surface " +
                    "height projection instead of creating an overlapping free node.");
            }

            _diag.Clear();
            _diagTotal = 0;
            _rzSegments = _rzSnapEnds = _rzMergeEnds = _rzMidEnds = _rzFreeEnds = 0;
            _rzLocalSurfaceMatches = 0;
            _capPinnedSpans = _capSelfAnchoredSpans = _capBentDeckSpans = 0;
            _rzPinnedSpans = _rzPinRefused = 0;
            _rzSurfaceCorrections = 0;
            _rzSurfaceCorrectionMax = 0f;
            _peakCreated = _peakUpdated = _peakDeleted = 0;
            _capFilteredHalves = 0;
            _diagStartMs = now;
        }

        /// <summary>Pieces behind a replicated span delete, one frame later so the delete lands first.</summary>
        private void FlushDeferredSpanPieces(MultiplayerSession session)
        {
            if (_deferredSpanPieces.Count == 0) return;
            for (int i = 0; i < _deferredSpanPieces.Count; i++)
            {
                NetPlacementCommand command = _deferredSpanPieces[i];
                session.SendCommand(0, NetPlacementCommand.Id, command.Encode());
            }
            _deferredSpanPieces.Clear();
        }

        private void CaptureNewEdges(MultiplayerSession session, long now)
        {
            // Already sent from native intent this frame; these edges are its output.
            if (_nativeApplyCapturedFrame == _realizeFrame) return;

            // Object-owned networks travel inside one atomic object command.
            BuildSyncSystem buildSync = World.GetExistingSystemManaged<BuildSyncSystem>();
            if (buildSync != null && buildSync.NativeLifecycleCapturedThisFrame) return;

            // Exactly the frame a remote batch commits; never a wall-clock window.
            if (_suppressCaptureThisFrame) return;
            if (_createdEdges.IsEmptyIgnoreFilter) return;

            // A mid-span tap deletes the edge and creates its two halves plus the drawn road. Halves that are
            // 3D sub-curves of a same-prefab Deleted edge are dropped; the receiver splits locally. A span
            // rebuilt at another height or only partly re-covered is not a split: its delete replicates and
            // the pieces follow one frame behind.
            NativeArray<Entity> delEnts = _deletedEdges.ToEntityArray(Allocator.Temp);
            NativeArray<Curve> delCurves = _deletedEdges.ToComponentDataArray<Curve>(Allocator.Temp);
            var delPrefabs = new NativeArray<Entity>(delEnts.Length, Allocator.Temp);
            for (int i = 0; i < delEnts.Length; i++)
                delPrefabs[i] = EntityManager.GetComponentData<PrefabRef>(delEnts[i]).m_Prefab;

            NativeArray<Entity> entities = _createdEdges.ToEntityArray(Allocator.Temp);
            NativeArray<Curve> createdCurves = _createdEdges.ToComponentDataArray<Curve>(Allocator.Temp);
            var createdPrefabs = new NativeArray<Entity>(entities.Length, Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
                createdPrefabs[i] = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;

            // Rebuilt or partly re-covered spans: their pieces are sent, not dropped.
            var delRebuilt = new bool[delEnts.Length];
            var delPartial = new bool[delEnts.Length];
            for (int dI = 0; dI < delEnts.Length; dI++)
            {
                List<Bezier4x3> pieces = null;
                for (int c = 0; c < createdCurves.Length; c++)
                {
                    if (createdPrefabs[c] != delPrefabs[dI]) continue;
                    Bezier4x3 piece = createdCurves[c].m_Bezier;
                    if (!SplitMatch.FollowsXZ(piece, delCurves[dI].m_Bezier)) continue;
                    if (!SplitMatch.HeightMatches(piece, delCurves[dI].m_Bezier))
                    {
                        delRebuilt[dI] = true;
                        break;
                    }
                    (pieces ?? (pieces = new List<Bezier4x3>())).Add(piece);
                }
                if (!delRebuilt[dI] && pieces != null)
                    delPartial[dI] = !SplitMatch.CoverWholeSpan(pieces, delCurves[dI].m_Bezier);
            }

            // Per-edge fallback commands (no native operation); counted so a degraded replay is visible.
            int stubs = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = createdPrefabs[i];
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Hidden lanes and paths are regenerated locally.
                    if (name.StartsWith("Invisible"))
                    {
                        continue;
                    }

                    Bezier4x3 b = createdCurves[i].m_Bezier;

                    bool onDeletedSpan = false, onKeptSpan = false;
                    for (int dI = 0; dI < delCurves.Length; dI++)
                    {
                        if (delPrefabs[dI] != prefab) continue;
                        if (!SplitMatch.FollowsXZ(b, delCurves[dI].m_Bezier)) continue;
                        onDeletedSpan = true;
                        if (delRebuilt[dI] || delPartial[dI]) { onKeptSpan = true; break; }
                    }
                    if (onDeletedSpan && !onKeptSpan)
                    {
                        _capFilteredHalves++;
                        continue;
                    }

                    if (_guard.Consume(ReplicationGuard.Key(name, b.a), now))
                    {
                        continue;
                    }

                    Curve curve = createdCurves[i];
                    // Without end-node elevations the receiver commits a ground net and pulls the ends to the lakebed.
                    CommittedEndElevations(entity, out float2 startElevation, out float2 endElevation);
                    var command = new NetPlacementCommand
                    {
                        PrefabName = name,
                        Ax = b.a.x, Ay = b.a.y, Az = b.a.z,
                        Bx = b.b.x, By = b.b.y, Bz = b.b.z,
                        Cx = b.c.x, Cy = b.c.y, Cz = b.c.z,
                        Dx = b.d.x, Dy = b.d.y, Dz = b.d.z,
                        Length = curve.m_Length,
                        Start = { ElevationLeft = startElevation.x, ElevationRight = startElevation.y },
                        End = { ElevationLeft = endElevation.x, ElevationRight = endElevation.y },
                        PinProfile = ShouldPinCommittedEdge(prefab, b, startElevation,
                            endElevation),
                    };
                    if (command.PinProfile) _capPinnedSpans++;
                    if (onKeptSpan)
                    {
                        _deferredSpanPieces.Add(command);
                        RecordDiagnostic(name);
                        stubs++;
                        continue;
                    }
                    session.SendCommand(0, NetPlacementCommand.Id, command.Encode());
                    RecordDiagnostic(name);
                    stubs++;
                }
            }
            finally
            {
                entities.Dispose();
                createdCurves.Dispose();
                createdPrefabs.Dispose();
                delEnts.Dispose();
                delCurves.Dispose();
                delPrefabs.Dispose();
            }

            if (stubs > 0)
                SyncLog.Trace(LogTopic.Nets, "net per-edge fallback sent=" + stubs +
                    " (no native operation covered this apply)");
        }

        /// <summary>End-node elevations; a node without the component is on the ground (0).</summary>
        private void CommittedEndElevations(Entity edge, out float2 start, out float2 end)
        {
            start = default;
            end = default;
            if (!EntityManager.HasComponent<Edge>(edge)) return;
            Edge ends = EntityManager.GetComponentData<Edge>(edge);
            start = NodeElevation(ends.m_Start);
            end = NodeElevation(ends.m_End);
        }

        private float2 NodeElevation(Entity node)
        {
            return node != Entity.Null && EntityManager.Exists(node) &&
                   EntityManager.HasComponent<global::Game.Net.Elevation>(node)
                ? EntityManager.GetComponentData<global::Game.Net.Elevation>(node).m_Elevation
                : default;
        }
    }
}
