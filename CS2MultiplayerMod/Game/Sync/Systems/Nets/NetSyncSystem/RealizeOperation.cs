using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Assembling one source Apply from its messages; a partial operation is put back, not realized.
    public partial class NetSyncSystem
    {
        private void PruneCompletedNetOperations(long now)
        {
            _completedNetOperations.Prune(now);
            _armedNetOperations.Prune(now);
        }

        private void RequeueFrom(List<SimulationCommandMessage> work, int from)
        {
            if (from < work.Count)
                _remoteDeferred.InsertRange(0, work.GetRange(from, work.Count - from));
        }

        private static bool HasExternalNativeTarget(NetEndpointTargetKind kind) =>
            kind == NetEndpointTargetKind.Node || kind == NetEndpointTargetKind.Edge ||
            kind == NetEndpointTargetKind.OwnedNode || kind == NetEndpointTargetKind.OwnedEdge;

        private static string DescribeUnresolvedEndpoint(NetPlacementCommand command,
            bool startResolved)
        {
            NetEndpointIntent failed = startResolved ? command.End : command.Start;
            return "course=" + command.CourseIndex + " " + (startResolved ? "end" : "start") +
                   " kind=" + failed.Kind + " prefab='" + failed.TargetPrefabName + "' anchor=" +
                   failed.AnchorX.ToString("F1") + "," + failed.AnchorY.ToString("F1") + "," +
                   failed.AnchorZ.ToString("F1");
        }

        /// <summary>
        /// Records that an endpoint splits <paramref name="target"/>. Courses naming the same source edge
        /// may share it; different source edges landing on one local edge mean a missing split, and would
        /// give the apply pass two Temps sharing one original.
        /// </summary>
        private bool TryClaimSplitTarget(NetEndpointIntent intent, Entity target, int kind)
        {
            if (kind != KindSplit || target == Entity.Null) return true;
            Bezier4x3 source = TargetCurveOf(intent);
            if (!_batchSplitClaims.TryGetValue(target, out Bezier4x3 claimed))
            {
                _batchSplitClaims[target] = source;
                return true;
            }
            return SameCurveBits(claimed, source) || SameCurveBitsReversed(claimed, source);
        }

        /// <summary>
        /// Pulls one complete operation; later messages met on the way return to the prefix in order. An
        /// incomplete operation waits briefly, then is dropped whole.
        /// </summary>
        private bool TryTakeCompleteOperation(MultiplayerSession session, long now,
            out List<SimulationCommandMessage> operation, out bool nativeOperation,
            out NetToolOperationCommand mixedOperation)
        {
            operation = null;
            nativeOperation = false;
            mixedOperation = null;

            const int MaxScan = NetInboxCap;
            var scanned = new List<SimulationCommandMessage>();
            NetOperationKey key = default(NetOperationKey);
            int expected = 0;
            SimulationCommandMessage[] courses = null;
            NetPlacementCommand[] decodedCourses = null;
            int received = 0;

            for (int scan = 0; scan < MaxScan && (expected == 0 || received < expected); scan++)
            {
                if (!TryTakeNextPlacementMessage(out SimulationCommandMessage message)) break;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (message.CommandId == NetToolOperationCommand.Id)
                {
                    if (expected == 0)
                    {
                        try { mixedOperation = NetToolOperationCommand.Decode(message.Body); }
                        catch (System.Exception ex)
                        {
                            SyncLog.Warn(LogTopic.Nets,
                                "NetSync: dropping malformed mixed net operation: " + ex.Message);
                            SyncInbox.RequestResync(Diagnostics.ResyncReport
                                .Create("malformed mixed net operation", "net",
                                    Diagnostics.ResyncEvidence.StreamLoss)
                                .About("mixed operation from player " + message.OriginPlayerId)
                                .Tried("nothing - the operation could not be decoded")
                                .Fact("decoder said", ex.Message));
                            return false;
                        }
                        operation = new List<SimulationCommandMessage>(1) { message };
                        return true;
                    }

                    // Arrived after an older operation's first fragment: stays behind it in order.
                    scanned.Add(message);
                    continue;
                }
                if (message.CommandId != NetPlacementCommand.Id)
                {
                    SyncLog.Warn(LogTopic.Nets, "NetSync: dropping unsupported queued command " +
                        message.CommandId + ".");
                    continue;
                }

                if (!CommandDecode.TryDecode(message, NetPlacementCommand.Decode, LogTopic.Nets,
                        "NetSync", out NetPlacementCommand command))
                    continue;

                scanned.Add(message);
                if (expected == 0)
                {
                    key = new NetOperationKey
                    {
                        Origin = message.OriginPlayerId,
                        Operation = command.OperationId,
                    };
                    expected = command.CourseCount;
                    courses = new SimulationCommandMessage[expected];
                    decodedCourses = new NetPlacementCommand[expected];
                }

                if (message.OriginPlayerId != key.Origin || command.OperationId != key.Operation)
                    continue;
                if (command.CourseCount != expected)
                {
                    SyncLog.Warn(LogTopic.Nets,
                        "NetSync: dropping inconsistent course count for op=" + key.Operation +
                        " from player " + key.Origin + ".");
                    continue;
                }

                int index = command.CourseIndex;
                if (courses[index] != null) continue;
                courses[index] = message;
                decodedCourses[index] = command;
                received++;
            }

            if (expected == 0) return false;

            if (received != expected)
            {
                if (!_operationAssemblyDeadlines.TryGetValue(key, out long deadline))
                {
                    deadline = now + OperationAssemblyWindowMs;
                    _operationAssemblyDeadlines[key] = deadline;
                }

                if (now < deadline)
                {
                    RequeueAtFront(scanned);
                    return false;
                }

                _operationAssemblyDeadlines.Remove(key);
                var later = new List<SimulationCommandMessage>();
                for (int i = 0; i < scanned.Count; i++)
                {
                    if (scanned[i].CommandId != NetPlacementCommand.Id)
                    {
                        later.Add(scanned[i]);
                        continue;
                    }
                    NetPlacementCommand command;
                    try { command = NetPlacementCommand.Decode(scanned[i].Body); }
                    catch { continue; }
                    if (scanned[i].OriginPlayerId != key.Origin || command.OperationId != key.Operation)
                        later.Add(scanned[i]);
                }
                RequeueAtFront(later);
                SyncLog.Trace(LogTopic.Nets, "net incomplete op dropped=" + key.Operation +
                    " courses=" + received + "/" + expected);
                SyncInbox.RequestResync(Diagnostics.ResyncReport
                    .Create("incomplete net operation expired", "net",
                        Diagnostics.ResyncEvidence.StreamLoss)
                    .About("op " + key.Operation + " from player " + key.Origin)
                    .Tried("waited " + (OperationAssemblyWindowMs / 1000) +
                           " s for the missing pieces of the road the other player drew")
                    .Fact("pieces received", received + " of " + expected));
                return false;
            }

            _operationAssemblyDeadlines.Remove(key);
            operation = new List<SimulationCommandMessage>(expected);
            nativeOperation = true;
            bool hasNativeCourse = false;
            bool hasGeometryOnlyCourse = false;
            for (int i = 0; i < expected; i++)
            {
                operation.Add(courses[i]);
                nativeOperation &= decodedCourses[i].HasNativeCourse;
                hasNativeCourse |= decodedCourses[i].HasNativeCourse;
                hasGeometryOnlyCourse |= !decodedCourses[i].HasNativeCourse;
            }

            // Later operations keep receive order; duplicates of the completed key are discarded.
            var deferred = new List<SimulationCommandMessage>();
            for (int i = 0; i < scanned.Count; i++)
            {
                if (scanned[i].CommandId != NetPlacementCommand.Id)
                {
                    deferred.Add(scanned[i]);
                    continue;
                }
                NetPlacementCommand command;
                try { command = NetPlacementCommand.Decode(scanned[i].Body); }
                catch { continue; }
                if (scanned[i].OriginPlayerId == key.Origin && command.OperationId == key.Operation)
                    continue;
                deferred.Add(scanned[i]);
            }
            RequeueAtFront(deferred);

            // Mixed or grouped fallback input would smuggle a partial native operation through.
            if ((hasNativeCourse && hasGeometryOnlyCourse) || (expected > 1 && !nativeOperation))
            {
                SyncLog.Trace(LogTopic.Nets, "net incompatible multi-course op dropped=" +
                    key.Operation);
                SyncInbox.RequestResync(Diagnostics.ResyncReport
                    .Create("incompatible net operation rejected", "net",
                        Diagnostics.ResyncEvidence.StreamLoss)
                    .About("op " + key.Operation + " from player " + key.Origin)
                    .Tried("nothing - the operation mixed two course encodings that cannot be " +
                           "applied as one transaction")
                    .Fact("courses in the operation", expected));
                operation = null;
                nativeOperation = false;
                return false;
            }
            return true;
        }

        private bool TryTakeNextPlacementMessage(out SimulationCommandMessage message)
        {
            // Pre-terraform ground gives the wrong height; only new courses wait.
            if (RealizeGate.TerrainBacklog)
            {
                message = default(SimulationCommandMessage);
                return false;
            }
            if (_remoteDeferred.Count > 0)
            {
                message = _remoteDeferred[0];
                _remoteDeferred.RemoveAt(0);
                return true;
            }
            return _incoming.TryDequeue(out message);
        }

        private void RequeueAtFront(List<SimulationCommandMessage> messages)
        {
            if (messages != null && messages.Count > 0)
                _remoteDeferred.InsertRange(0, messages);
        }

        /// <summary>
        /// Requeues a stalled operation without blocking the pipeline. Order only matters per sender, so
        /// only another sender's work may overtake it.
        /// </summary>
        private void RequeueStalledOperation(List<SimulationCommandMessage> messages)
        {
            if (messages == null || messages.Count == 0) return;
            int origin = messages[0].OriginPlayerId;

            // Admit everything arrived so the reorder sees the whole ready set; the inbox cap still bounds it.
            while (_remoteDeferred.Count < NetInboxCap && _incoming.TryDequeue(out SimulationCommandMessage admitted))
                _remoteDeferred.Add(admitted);

            int insertAt = 0;
            while (insertAt < _remoteDeferred.Count &&
                   _remoteDeferred[insertAt].OriginPlayerId != origin) insertAt++;
            _remoteDeferred.InsertRange(insertAt, messages);
        }
    }
}
