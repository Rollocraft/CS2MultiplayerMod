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
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Assembling one source Apply out of the messages that carry it. One gesture may emit several
    // courses, and applying only a prefix of them deforms the rest, so a partial operation is put
    // back rather than realized.
    public partial class NetSyncSystem
    {
        private void PruneCompletedNetOperations(long now)
        {
            _completedNetOperations.Prune(now);
            _armedNetOperations.Prune(now);
        }

        /// <summary>
        /// Re-queue <paramref name="work"/>[<paramref name="from"/>..] ahead of the shared inbox.
        /// </summary>
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
        /// Record that a resolved endpoint will split <paramref name="target"/>, and report whether
        /// that claim is consistent with the source operation.
        ///
        /// Several courses of one operation may legitimately tap the SAME source edge: CourseSplitSystem
        /// receives them together and cuts that edge once into all of its pieces. Two courses that named
        /// DIFFERENT source edges but land on the same local edge are a different matter - this machine
        /// never received the split that separated them. Committing both would hand the apply pass two
        /// Temps sharing one original, which it dereferences without a liveness check.
        /// </summary>
        private bool TryClaimSplitTarget(NetEndpointIntent intent, Entity target, int kind)
        {
            if (kind != KindSplit || target == Entity.Null) return true;
            Bezier4x3 source = TargetCurveOf(intent);
            Bezier4x3 claimed;
            if (!_batchSplitClaims.TryGetValue(target, out claimed))
            {
                _batchSplitClaims[target] = source;
                return true;
            }
            return SameCurveBits(claimed, source) || SameCurveBitsReversed(claimed, source);
        }

        /// <summary>
        /// Pull one complete source operation from the ordered command streams. Messages belonging
        /// to later operations may be encountered while waiting for an interleaved course; they are
        /// returned to the simulation-thread prefix in their original order. An incomplete operation
        /// waits briefly and is then dropped as a whole, never realized as broken geometry.
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
                SimulationCommandMessage message;
                if (!TryTakeNextPlacementMessage(out message)) break;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (message.CommandId == NetToolOperationCommand.Id)
                {
                    if (expected == 0)
                    {
                        try { mixedOperation = NetToolOperationCommand.Decode(message.Body); }
                        catch (System.Exception ex)
                        {
                            Mod.log.Warn("[MP] NetSync: dropping malformed mixed net operation: " +
                                         ex.Message);
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

                    // It arrived after the first fragment of an older placement operation. Keep it
                    // in the ordered prefix while scanning for that older operation's remaining
                    // fragments; it will be the next operation realized, never overtaken.
                    scanned.Add(message);
                    continue;
                }
                if (message.CommandId != NetPlacementCommand.Id)
                {
                    Mod.log.Warn("[MP] NetSync: dropping unsupported queued command " +
                                 message.CommandId + ".");
                    continue;
                }

                NetPlacementCommand command;
                try { command = NetPlacementCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] NetSync: dropping malformed command: " + ex.Message);
                    continue;
                }

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
                    Mod.log.Warn("[MP] NetSync: dropping inconsistent course count for op=" +
                                 key.Operation + " from player " + key.Origin + ".");
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
                long deadline;
                if (!_operationAssemblyDeadlines.TryGetValue(key, out deadline))
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
                Diagnostics.FlightRecorder.Note("net incomplete op dropped=" + key.Operation +
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

            // Preserve later operations in their original receive order. Extra messages carrying
            // the completed key are duplicates or inconsistent fragments and are discarded.
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

            // Current senders only group exact native definitions. Geometry-only capture represents
            // one final edge per command. Rejecting mixed or grouped fallback input prevents a peer
            // from smuggling a partially native operation into per-course fallback realization.
            if ((hasNativeCourse && hasGeometryOnlyCourse) || (expected > 1 && !nativeOperation))
            {
                Diagnostics.FlightRecorder.Note("net incompatible multi-course op dropped=" +
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
            if (DeferForTerrain)
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
        /// Re-queue an operation that is waiting for a target it cannot see yet, WITHOUT parking
        /// the rest of the pipeline behind it.
        ///
        /// The queue is strictly ordered, so the previous behaviour - putting it straight back at
        /// the front - stopped every later operation, from every player, for the whole retry
        /// window. That is worse than a delay: in the sessions this came from, the deferred
        /// placement spent ten seconds in front of a queue while the world it was searching went on
        /// changing, and then asked for a full world reload because what it was looking for was no
        /// longer there.
        ///
        /// Causal order was only ever meaningful per sender, so only ANOTHER sender's work may
        /// overtake. Everything the same sender queued behind this operation stays behind it.
        /// </summary>
        private void RequeueStalledOperation(List<SimulationCommandMessage> messages)
        {
            if (messages == null || messages.Count == 0) return;
            int origin = messages[0].OriginPlayerId;

            // Admit what has arrived so the reorder sees the whole ready set rather than whatever a
            // previous scan happened to leave behind. Realization stays gated where it always was
            // (see TryTakeNextPlacementMessage); only where the messages sit changes, and the same
            // inbox cap bounds it.
            SimulationCommandMessage admitted;
            while (_remoteDeferred.Count < NetInboxCap && _incoming.TryDequeue(out admitted))
                _remoteDeferred.Add(admitted);

            int insertAt = 0;
            while (insertAt < _remoteDeferred.Count &&
                   _remoteDeferred[insertAt].OriginPlayerId != origin) insertAt++;
            _remoteDeferred.InsertRange(insertAt, messages);
        }
    }
}
