using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Capture (host) side of NetSyncSystem: detect the edges the local player drew, drop the
    // side-effect halves of a mid-span split, broadcast the rest as NetPlacementCommands, and emit the
    // periodic 5 s diagnostic summaries.
    public partial class NetSyncSystem
    {
        private void RecordDiagnostic(string prefabName)
        {
            _diagTotal++;
            int count;
            _diag.TryGetValue(prefabName, out count);
            _diag[prefabName] = count + 1;
        }

        private void FlushDiagnostics(long now)
        {
            if (_diagStartMs < 0) { _diagStartMs = now; return; }
            if (now - _diagStartMs < 5000) return;

            if (_diagTotal > 0)
            {
                var sb = new StringBuilder();
                sb.Append("[MP] NetSync captured ").Append(_diagTotal)
                  .Append(" road segment(s)/5s across ").Append(_diag.Count).Append(" prefab(s): ");
                int n = 0;
                foreach (var pair in _diag)
                {
                    if (n > 0) sb.Append(", ");
                    sb.Append(pair.Key).Append(" x").Append(pair.Value);
                    if (++n >= 12) { sb.Append(", ..."); break; }
                }
                Mod.Verbose(sb.ToString());
            }

            if (_peakUpdated > 0 || _peakDeleted > 0 || _diagTotal > 0 || _capFilteredHalves > 0)
            {
                Mod.Verbose("[MP] NetSync edge tags/5s peak: Created=" + _peakCreated +
                             " Updated=" + _peakUpdated + " Deleted=" + _peakDeleted +
                             "; dropped " + _capFilteredHalves + " split-half edge(s) (side-effects of a " +
                             "mid-span tap; only the drawn edge is sent so the receiver splits locally).");
            }

            if (_rzSegments > 0)
            {
                Mod.Verbose("[MP] NetSync realized " + _rzSegments + " remote segment(s)/5s; endpoints: " +
                             _rzSnapEnds + " reused a node, " + _rzMergeEnds +
                             " merged a shared new node, " + _rzMidEnds +
                             " split an existing edge (T-junction), " +
                             _rzFreeEnds + " free ground.");
            }

            _diag.Clear();
            _diagTotal = 0;
            _rzSegments = _rzSnapEnds = _rzMergeEnds = _rzMidEnds = _rzFreeEnds = 0;
            _peakCreated = _peakUpdated = _peakDeleted = 0;
            _capFilteredHalves = 0;
            _diagStartMs = now;
        }

        /// <summary>
        /// Send the pieces captured LAST frame that ride behind a replicated span delete - one frame
        /// after it, so the receiver always bulldozes before it rebuilds (commands arrive in send order).
        /// </summary>
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
            // On the frame a self-driven ApplyTool pass commits a remote batch, every Created edge
            // here is that batch's output - skip exactly this frame (never a wall-clock window,
            // which also swallowed roads the player built while remote batches streamed in).
            if (_suppressCaptureThisFrame) return;
            if (_createdEdges.IsEmptyIgnoreFilter) return;

            // Snapshot this frame's Deleted edges. When the player taps a road mid-span, CS2 makes the
            // T-junction by DELETING the existing edge and CREATING its two halves plus the drawn road
            // (our logs: Created=3 Updated=1 Deleted=1). The halves are Created too, but they are a
            // SIDE EFFECT of the split - not something the player drew. Replicating them makes the
            // receiver re-split its own still-whole geometry destructively (roads vanish, the new road
            // ends up disconnected). So below we drop any Created edge that is a true 3D sub-curve of a
            // same-prefab Deleted edge (one of its halves) and send only the edge the player drew; the
            // receiver reproduces the split locally via the Temp+ApplyTool realize path.
            //
            // NOT a local split: a span REBUILT at another height (the raise/lower gesture) or only
            // PARTIALLY re-covered (something placed on top consumed the rest - a roundabout swallows
            // the stretch inside its circle; the receiver's own split would keep it). For those the
            // delete IS replicated (DeleteSyncSystem, same test on the same frame's data) and every
            // kept piece is sent one frame behind it so the delete travels first.
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

            // Which deleted spans are rebuilt at another height or only partially re-covered: their
            // deletes replicate, so every piece on them must be sent back rather than dropped.
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

            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = createdPrefabs[i];
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Safety net: never sync the game's auto-generated hidden lanes/paths;
                    // they are recreated locally when the visible road is rebuilt.
                    if (name.StartsWith("Invisible"))
                    {
                        continue;
                    }

                    Bezier4x3 b = createdCurves[i].m_Bezier;

                    // A piece on a purely-split span is a local side effect (dropped); a piece on a
                    // span whose delete is replicated rides one frame behind that delete.
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
                    var command = new NetPlacementCommand
                    {
                        PrefabName = name,
                        Ax = b.a.x, Ay = b.a.y, Az = b.a.z,
                        Bx = b.b.x, By = b.b.y, Bz = b.b.z,
                        Cx = b.c.x, Cy = b.c.y, Cz = b.c.z,
                        Dx = b.d.x, Dy = b.d.y, Dz = b.d.z,
                        Length = curve.m_Length,
                    };
                    if (onKeptSpan)
                    {
                        _deferredSpanPieces.Add(command);
                        RecordDiagnostic(name);
                        continue;
                    }
                    session.SendCommand(0, NetPlacementCommand.Id, command.Encode());
                    RecordDiagnostic(name);
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
        }
    }
}
