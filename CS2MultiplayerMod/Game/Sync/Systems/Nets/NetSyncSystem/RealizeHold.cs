using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    public partial class NetSyncSystem
    {
        /// <summary>Lapsed holds stop counting, so a forgotten one cannot block bulldozing for the session.</summary>
        private const long StaleHoldGraceMs = 2000;

        /// <summary>
        /// An operation is waiting inside a live window for a target. Bulldoze and replace stand down
        /// meanwhile; lapsed holds are pruned.
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

        /// <summary>Relaxed matches unlock after one full window and stay unlocked.</summary>
        private bool RelaxedResolveAllowed(NetOperationKey key, long now)
        {
            if (!_nativeOperationHolds.TryGetValue(key, out NativeOperationHold hold)) return false;
            return hold.Relaxed || now >= hold.DeadlineMs;
        }

        /// <summary>True while it should keep waiting; false when a verdict is due.</summary>
        private bool HoldUnresolvedOperation(NetOperationKey key, long now, long operationId,
            string detail, out int windows)
        {
            if (!_nativeOperationHolds.TryGetValue(key, out NativeOperationHold hold))
            {
                hold = new NativeOperationHold
                {
                    DeadlineMs = now + NativeTargetRetryWindowMs,
                    Relaxed = false,
                    Windows = 1,
                };
                _nativeOperationHolds[key] = hold;
                SyncLog.Trace(LogTopic.Nets, "net native target retry op=" + operationId + " " +
                    detail);
            }
            windows = hold.Windows;
            return now < hold.DeadlineMs;
        }

        /// <summary>A shorter second window, against a world the arbiter has frozen.</summary>
        private void ExtendUnresolvedOperation(NetOperationKey key, long now)
        {
            _nativeOperationHolds.TryGetValue(key, out NativeOperationHold hold);
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

        /// <summary>Releases the hold and withdraws the report.</summary>
        private void ClearOperationHold(NetOperationKey key, string reason, string subject,
            string outcome)
        {
            if (!_nativeOperationHolds.Remove(key)) return;
            Diagnostics.ResyncArbiter.Withdraw("net", reason, subject,
                Mod.Service != null ? Mod.Service.NowMs : 0L, outcome);
        }

        /// <summary>
        /// What stands where the source anchored its endpoint and why each candidate was refused: distance,
        /// prefab or height. Runs only when a reload is being considered.
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
                float xz = MathUtils.Distance(edges.Curves[i].m_Bezier.xz, anchor.xz, out float t);
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
