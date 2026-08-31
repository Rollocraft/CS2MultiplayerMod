using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Holding an operation whose external targets have not arrived yet. The geometry it refers to
    // may still be in flight, so the operation waits and is retried; only once its window closes
    // is it given up on, with the local neighbourhood described so the log says what was missing.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// A hold whose window lapsed this long ago no longer counts. An operation can leave the
        /// pipeline by other doors - duplicate suppression, a malformed decode - and a forgotten
        /// hold must never be able to stop bulldozing for the rest of a session.
        /// </summary>
        private const long StaleHoldGraceMs = 2000;

        /// <summary>
        /// True while at least one native operation is inside a live window waiting for a target
        /// that has not arrived, pruning any hold whose window has fully lapsed.
        ///
        /// The feeders that can only ever REMOVE such a target - bulldoze, road replacement - stand
        /// down while this is true. They used to run ahead of it every frame, which is how a
        /// placement came to be rejected for a road this machine had just deleted out from under
        /// it. Two windows is the most any operation gets, so the hold is seconds, not minutes.
        /// </summary>
        public bool HasStalledNativeOperation(long now)
        {
            if (_nativeOperationHolds.Count == 0) return false;
            List<NetOperationKey> stale = null;
            bool live = false;
            foreach (KeyValuePair<NetOperationKey, NativeOperationHold> entry in _nativeOperationHolds)
            {
                if (now < entry.Value.DeadlineMs + StaleHoldGraceMs) { live = true; continue; }
                (stale ?? (stale = new List<NetOperationKey>())).Add(entry.Key);
            }
            if (stale != null)
                for (int i = 0; i < stale.Count; i++) _nativeOperationHolds.Remove(stale[i]);
            return live;
        }

        /// <summary>
        /// Whether the resolver may use its relaxed last-resort matches for this operation - the
        /// merged-node-as-edge-split fallback. Unlocked once a full retry window has passed, and
        /// never locked again for that operation (see <see cref="NativeOperationHold"/>).
        /// </summary>
        private bool RelaxedResolveAllowed(NetOperationKey key, long now)
        {
            NativeOperationHold hold;
            if (!_nativeOperationHolds.TryGetValue(key, out hold)) return false;
            return hold.Relaxed || now >= hold.DeadlineMs;
        }

        /// <summary>
        /// Arm or advance the hold on an operation whose target is missing. Returns true while it
        /// should keep waiting; false once the current window is up and a verdict is due.
        /// </summary>
        private bool HoldUnresolvedOperation(NetOperationKey key, long now, long operationId,
            string detail, out int windows)
        {
            NativeOperationHold hold;
            if (!_nativeOperationHolds.TryGetValue(key, out hold))
            {
                hold = new NativeOperationHold
                {
                    DeadlineMs = now + NativeTargetRetryWindowMs,
                    Relaxed = false,
                    Windows = 1,
                };
                _nativeOperationHolds[key] = hold;
                Diagnostics.FlightRecorder.Note("net native target retry op=" + operationId +
                                                  " " + detail);
            }
            windows = hold.Windows;
            return now < hold.DeadlineMs;
        }

        /// <summary>
        /// Grant one more window after the arbiter declined to settle the report. Deliberately
        /// shorter than the first: this one runs against a world the arbiter has frozen, so it is a
        /// far better test than the first window was, and the feeders it holds up are waiting on it.
        /// </summary>
        private void ExtendUnresolvedOperation(NetOperationKey key, long now)
        {
            NativeOperationHold hold;
            _nativeOperationHolds.TryGetValue(key, out hold);
            hold.DeadlineMs = now + NativeTargetRetryWindowMs / 2;
            hold.Relaxed = true;
            hold.Windows++;
            _nativeOperationHolds[key] = hold;
        }

        /// <summary>How a report and a withdrawal name the same stalled operation.</summary>
        internal const string UnresolvedNativeTargetReason = "native net target did not resolve";
        internal const string UnresolvedMixedTargetReason = "mixed net operation target did not resolve";

        private static string NativeOperationSubject(long operationId, int origin) =>
            "op " + operationId + " from player " + origin;

        private static string MixedOperationSubject(long operationId, int origin) =>
            "mixed op " + operationId + " from player " + origin;

        /// <summary>
        /// Release a hold because the operation succeeded. Withdrawing the report is the point of
        /// holding one: a world reload that was proposed and then did not have to happen is worth
        /// exactly as much in the log as one that did.
        /// </summary>
        private void ClearOperationHold(NetOperationKey key, string reason, string subject,
            string outcome)
        {
            if (!_nativeOperationHolds.Remove(key)) return;
            Diagnostics.ResyncArbiter.Withdraw("net", reason, subject,
                Mod.Service != null ? Mod.Service.NowMs : 0L, outcome);
        }

        /// <summary>
        /// What this machine actually has where the source anchored its endpoint, and why each
        /// candidate was refused.
        ///
        /// This is the fact the log never carried. "No road within reach", "a Medium Road is there
        /// instead of the Small Road named", and "the road is there but six metres lower" are three
        /// entirely different bugs, and all three used to print the same line before reloading the
        /// world. Runs once, on the frame a reload is being considered, so the wider search it does
        /// costs nothing in the normal case.
        /// </summary>
        private string DescribeLocalAnchorNeighbourhood(NetEndpointIntent intent,
            ref NodePool nodes, ref EdgePool edges)
        {
            const float SearchXZ = 16f;
            float3 anchor = new float3(intent.AnchorX, intent.AnchorY, intent.AnchorZ);
            var report = new System.Text.StringBuilder();

            float bestNodeXZ = float.MaxValue, bestNodeDy = 0f;
            NetCellIndex.Enumerator nodeCandidates = nodes.Index.Near(anchor.xz, SearchXZ);
            while (nodeCandidates.MoveNext())
            {
                int i = nodeCandidates.Current;
                float xz = math.distance(nodes.Data[i].m_Position.xz, anchor.xz);
                if (xz >= bestNodeXZ) continue;
                bestNodeXZ = xz;
                bestNodeDy = nodes.Data[i].m_Position.y - anchor.y;
            }

            float bestEdgeXZ = float.MaxValue, bestEdgeDy = 0f;
            string bestEdgePrefab = null;
            NetCellIndex.Enumerator edgeCandidates = edges.Index.Near(anchor.xz, SearchXZ);
            while (edgeCandidates.MoveNext())
            {
                int i = edgeCandidates.Current;
                float t;
                float xz = MathUtils.Distance(edges.Curves[i].m_Bezier.xz, anchor.xz, out t);
                if (xz >= bestEdgeXZ) continue;
                Entity edge = edges.Entities[i];
                if (!EntityManager.Exists(edge)) continue;
                bestEdgeXZ = xz;
                bestEdgeDy = MathUtils.Position(edges.Curves[i].m_Bezier, t).y - anchor.y;
                bestEdgePrefab = EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(edge)
                    ? PrefabNameOf(EntityManager
                        .GetComponentData<global::Game.Prefabs.PrefabRef>(edge).m_Prefab)
                    : "(no prefab)";
            }

            if (bestEdgeXZ == float.MaxValue)
            {
                report.Append("no road at all within ").Append(SearchXZ.ToString("F0")).Append(" m");
            }
            else
            {
                report.Append("nearest road is '").Append(bestEdgePrefab).Append("' ")
                    .Append(bestEdgeXZ.ToString("F1")).Append(" m away, ")
                    .Append(bestEdgeDy.ToString("F1")).Append(" m in height");
            }

            if (bestNodeXZ != float.MaxValue)
                report.Append("; nearest junction ").Append(bestNodeXZ.ToString("F1"))
                    .Append(" m away, ").Append(bestNodeDy.ToString("F1")).Append(" m in height");

            // The resolver's own tolerances, so a reader can see at a glance whether the candidate
            // above was refused on distance or on identity.
            report.Append(" (must be within ").Append(NativeEdgeResolveXZ.ToString("F0"))
                .Append(" m and ").Append(NativeTargetResolveY.ToString("F0"))
                .Append(" m of height, same prefab, same layers, same owner)");
            return report.ToString();
        }

        private static NativeTargetRetryKey NativeRetryKey(SimulationCommandMessage message,
            NetPlacementCommand command)
        {
            return new NativeTargetRetryKey
            {
                Origin = message.OriginPlayerId,
                Operation = command.OperationId,
                Course = command.CourseIndex,
            };
        }
    }
}
