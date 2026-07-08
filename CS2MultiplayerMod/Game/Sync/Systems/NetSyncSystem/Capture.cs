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
        /// Send the pieces of a span rebuilt at another height that were captured LAST frame - one
        /// frame behind DeleteSyncSystem's delete of the old span, so the receiver always bulldozes
        /// before it rebuilds (commands are delivered in send order).
        /// </summary>
        private void FlushDeferredRebuiltPieces(MultiplayerSession session)
        {
            if (_deferredRebuiltPieces.Count == 0) return;
            for (int i = 0; i < _deferredRebuiltPieces.Count; i++)
            {
                NetPlacementCommand command = _deferredRebuiltPieces[i];
                session.SendCommand(0, NetPlacementCommand.Id, command.Encode());
            }
            _deferredRebuiltPieces.Clear();
        }

        private void CaptureNewEdges(MultiplayerSession session, long now)
        {
            // Briefly stop capturing right after we drove an ApplyTool commit: the edges it created
            // would otherwise be re-captured next frame and echoed back. Coarse (also pauses local
            // capture for the window) but the realize bursts are short; refined in the intent stage.
            if (now < _suppressCaptureUntilMs) return;
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
            // The exception is the raise/lower-road gesture: redrawing a road over an existing one at a
            // different elevation is ALSO committed as delete + create along the same XZ centreline,
            // but the new pieces sit at a different HEIGHT (a new curve Y; the edge's Elevation state
            // is derived from that geometry when it rebuilds). That pair is a real change the receiver
            // cannot reproduce locally, so the delete IS replicated (see DeleteSyncSystem) and every
            // piece of the rebuilt span is sent — held back one frame so the delete travels first.
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

            // Which deleted edges are being REBUILT at another height rather than split in place: some
            // same-prefab created piece follows their XZ centreline but deviates in Y. Computed once so
            // every piece of such a span - including height-matching remainder pieces - is sent; the
            // receiver bulldozes the whole span and needs all of them back. DeleteSyncSystem runs the
            // same test on the same frame's data, so the delete and the pieces always travel together.
            var delRebuilt = new bool[delEnts.Length];
            for (int dI = 0; dI < delEnts.Length; dI++)
            {
                for (int c = 0; c < createdCurves.Length; c++)
                {
                    if (createdPrefabs[c] != delPrefabs[dI]) continue;
                    Bezier4x3 piece = createdCurves[c].m_Bezier;
                    if (!SplitMatch.FollowsXZ(piece, delCurves[dI].m_Bezier)) continue;
                    if (SplitMatch.HeightMatches(piece, delCurves[dI].m_Bezier)) continue;
                    delRebuilt[dI] = true;
                    break;
                }
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

                    // Classify against this frame's deleted edges: a piece lying on a SPLIT span is a
                    // local side effect (dropped); a piece of a span REBUILT at another height is real
                    // work and rides behind that span's delete.
                    bool onDeletedSpan = false, onRebuiltSpan = false;
                    for (int dI = 0; dI < delCurves.Length; dI++)
                    {
                        if (delPrefabs[dI] != prefab) continue;
                        if (!SplitMatch.FollowsXZ(b, delCurves[dI].m_Bezier)) continue;
                        onDeletedSpan = true;
                        if (delRebuilt[dI]) { onRebuiltSpan = true; break; }
                    }
                    if (onDeletedSpan && !onRebuiltSpan)
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
                    if (onRebuiltSpan)
                    {
                        _deferredRebuiltPieces.Add(command);
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
