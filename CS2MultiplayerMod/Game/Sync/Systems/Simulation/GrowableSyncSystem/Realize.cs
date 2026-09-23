using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>Zone cells are 8 m; a lot's half-extent is therefore its cell count times four.</summary>
        private const float ZoneCellSize = 8f;

        /// <summary>Lots that merely touch along an edge do not conflict.</summary>
        private const float OverlapTolerance = 0.5f;

        /// <summary>How far from the anchor an existing building may be and still be the same one.</summary>
        private const float AnchorMatchDistance = 0.5f;

        private const float AnchorSearchRadius = 8f;

        /// <summary>When this system last got to attempt its pending corrections.</summary>
        private long _lastGrowableRealizeMs;

        private void ExtendPendingStateWindows(long nowMs)
        {
            long heldMs = _lastGrowableRealizeMs == 0 ? 0 : nowMs - _lastGrowableRealizeMs;
            _lastGrowableRealizeMs = nowMs;
            if (heldMs <= 0) return;
            for (int i = 0; i < _pendingStateCorrections.Count; i++)
                _pendingStateCorrections[i].Expiry += heldMs;
        }

        /// <summary>Applies the host's zoned-building decisions during ToolUpdate.</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            // Waits while roads, zoning or terrain are held; held time does not count against a correction.
            if (Infrastructure.RealizeGate.WorldBuildingHeld || !service.SimulationSyncReady)
            {
                ExtendPendingStateWindows(service.NowMs);
                return;
            }

            MultiplayerSession session = service.Session;
            long now = service.NowMs;

            // A host never applies these, whether forged or echoed.
            if (session.Role == SessionRole.Host)
            {
                _incoming.Clear();
                return;
            }
            _lastGrowableRealizeMs = now;

            _applied.Prune(now);
            RetryPendingStateCorrections(now);

            int realized = 0, states = 0;
            // Rejected and duplicate work count too: lookups cost frame time.
            while (_incoming.TryTake(realized < MaxRealizePerFrame,
                       states < Infrastructure.GrowableCommandInbox.StateBudgetPerFrame,
                       out GrowableLifecycleCommand command))
            {
                if (command.Op == GrowableLifecycleCommand.OpState) states++;
                else realized++;

                if (_applied.Contains(command.Sequence, now))
                {
                    _duplicates++;
                    SyncLog.Detail(LogTopic.Buildings, "GrowableSync: ignoring duplicate " +
                        GrowableLifecycleCommand.OpName(command.Op) + " seq=" + command.Sequence +
                        " (already applied).");
                    continue;
                }

                // A newer command supersedes waiting corrections for this lot.
                SupersedePendingState(command, now);
                Apply(command, now);
            }

            ReportClientStats(now);
        }

        /// <summary>
        /// True when budget was consumed. Every outcome is terminal and recorded in the replay window.
        /// </summary>
        private bool Apply(GrowableLifecycleCommand command, long now)
        {
            switch (command.Op)
            {
                case GrowableLifecycleCommand.OpSpawn: return ApplySpawn(command, now);
                case GrowableLifecycleCommand.OpLevel: return ApplyLevel(command, now);
                case GrowableLifecycleCommand.OpRemove: return ApplyRemove(command, now);
                case GrowableLifecycleCommand.OpState: return ApplyState(command, now);
                default: return false;
            }
        }

        private bool ApplySpawn(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            if (!_prefabIndex.TryResolve(command.PrefabName,
                    candidate => EntityManager.HasComponent<SpawnableBuildingData>(candidate),
                    out Entity prefab))
            {
                // Missing asset or not a zoned building; not retryable.
                _unknownPrefab++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                SyncLog.Warn(LogTopic.Buildings, "GrowableSync: unknown zoned-building prefab '" +
                    command.PrefabName + "' at " + Format(position) + "; spawn dropped.");
                return true;
            }

            var rotation = new quaternion(math.normalizesafe(
                new float4(command.RotX, command.RotY, command.RotZ, command.RotW),
                new float4(0f, 0f, 0f, 1f)));

            var blockers = new NativeList<Entity>(8, Allocator.Temp);
            try
            {
                CollectOverlapping(prefab, position, rotation, blockers);

                // Same building already on the lot: a redelivery older than the replay window.
                if (AlreadySatisfied(blockers, prefab, position, now))
                {
                    Entity existing = FindGrowableAt(position, prefab, now);
                    if (existing != Entity.Null)
                    {
                        bool variantChanged = RepairSpawnVariant(existing, command.RandomSeed);
                        if (ApplyConditionAndState(existing, command) || variantChanged)
                            EntityManager.AddComponent<Updated>(existing);
                    }
                    _duplicates++;
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    SyncLog.Detail(LogTopic.Buildings, "GrowableSync: '" + command.PrefabName +
                        "' already stands at " + Format(position) + "; spawn seq=" +
                        command.Sequence + " ignored.");
                    return true;
                }

                Entity placedBlocker = FirstPlayerPlaced(blockers, now);
                if (placedBlocker != Entity.Null)
                {
                    // A player-placed building outranks a grown one, as on the host.
                    _conflicts++;
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    SyncLog.Warn(LogTopic.Buildings, "GrowableSync conflict: '" + command.PrefabName +
                        "' at " + Format(position) + " overlaps " +
                        DescribeBlocker(placedBlocker, now) + "; spawn refused (seq=" +
                        command.Sequence + ").");
                    return true;
                }

                // The rest are buildings this machine grew while unsynchronized; the host's wins.
                for (int i = 0; i < blockers.Length; i++)
                {
                    _conflicts++;
                    SyncLog.Warn(LogTopic.Buildings,
                        "GrowableSync conflict: evicting locally grown " +
                        DescribeBlocker(blockers[i], now) + " for the host's '" + command.PrefabName +
                        "' at " + Format(position) + ".");
                    EntityManager.AddComponent<Deleted>(blockers[i]);
                    SyncLog.Trace(LogTopic.Buildings, "growable evicted for host spawn");
                }
            }
            finally
            {
                blockers.Dispose();
            }

            _buildSync.TrackRemoteBuilding(Entity.Null, prefab, position, rotation,
                roadConnectionExpected: true, source: "growable");
            _buildSync.RealizeSimulationBuilding(prefab, position, rotation, SeedFor(command),
                (command.Flags & GrowableLifecycleCommand.FlagUnderConstruction) != 0);
            NoteSelfRealized(prefab, position, command, now);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotSpawn++;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync realize: built '" + command.PrefabName +
                "' at " + Format(position) + " seed=" + command.RandomSeed + " seq=" +
                command.Sequence + ".");
            return true;
        }

        /// <summary>Hands the building its target prefab through the game's own level-change path.</summary>
        private bool ApplyLevel(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            if (!_prefabIndex.TryResolve(command.PrefabName,
                    candidate => EntityManager.HasComponent<SpawnableBuildingData>(candidate),
                    out Entity prefab))
            {
                _unknownPrefab++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                SyncLog.Warn(LogTopic.Buildings, "GrowableSync: unknown level-change prefab '" +
                    command.PrefabName + "' at " + Format(position) + "; skipped.");
                return true;
            }

            Entity building = FindGrowableAt(position, Entity.Null, now);
            if (building == Entity.Null)
            {
                _unmatched++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                SyncLog.Detail(LogTopic.Buildings, "GrowableSync: no building at " +
                    Format(position) + " to level to '" + command.PrefabName + "'; skipped.");
                return true;
            }

            // Already becoming that prefab; re-applying would restart construction.
            if (EntityManager.HasComponent<UnderConstruction>(building))
            {
                UnderConstruction current = EntityManager.GetComponentData<UnderConstruction>(building);
                if (current.m_NewPrefab == prefab)
                {
                    if (ApplyConditionAndState(building, command))
                        EntityManager.AddComponent<Updated>(building);
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    return true;
                }
                if (current.m_NewPrefab != Entity.Null)
                    SyncLog.Detail(LogTopic.Buildings,
                        "GrowableSync: replacing this machine's own level-change target " + "at " +
                        Format(position) + " with the host's '" + command.PrefabName + "'.");
                current.m_NewPrefab = prefab;
                current.m_Progress = command.ConstructionProgress;
                current.m_Speed = command.ConstructionSpeed;
                EntityManager.SetComponentData(building, current);
            }
            else
            {
                EntityManager.AddComponentData(building, new UnderConstruction
                {
                    m_NewPrefab = prefab,
                    m_Progress = command.ConstructionProgress,
                    m_Speed = command.ConstructionSpeed,
                });
            }

            ApplyConditionAndState(building, command);
            EntityManager.AddComponent<Updated>(building);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotLevel++;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync realize: level change to '" +
                command.PrefabName + "' at " + Format(position) + " seq=" + command.Sequence + ".");
            return true;
        }

        private bool ApplyRemove(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            _prefabIndex.TryResolve(command.PrefabName, out Entity prefab);
            Entity building = FindGrowableAt(position, prefab, now);
            if (building == Entity.Null)
            {
                // Never built here or already bulldozed: convergent either way.
                _unmatched++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                SyncLog.Detail(LogTopic.Buildings, "GrowableSync: no building at " +
                    Format(position) + " to remove ('" + command.PrefabName + "'); already gone.");
                return true;
            }

            EntityManager.AddComponent<Deleted>(building);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotRemove++;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync realize: removed '" +
                command.PrefabName + "' at " + Format(position) + " seq=" + command.Sequence + ".");
            return true;
        }

        private bool ApplyState(GrowableLifecycleCommand command, long now) => ApplyState(command, now, true);

        private bool ApplyState(GrowableLifecycleCommand command, long now, bool allowPending)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);
            _prefabIndex.TryResolve(command.PrefabName, out Entity prefab);

            Entity building = FindGrowableAt(position, prefab, now);
            if (building == Entity.Null)
            {
                if (allowPending && _pendingStateSequences.Add(command.Sequence))
                {
                    if (_pendingStateCorrections.Count >= MaxPendingStateCorrections)
                    {
                        // Progress ticks are sent twice a second; completion has its own command.
                        _pendingStateSequences.Remove(command.Sequence);
                        _unmatched++;
                        _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    }
                    else
                    {
                        _pendingStateCorrections.Add(new PendingStateCorrection
                        {
                            Command = command,
                            Expiry = now + SelfRealizedWindowMs,
                            NextAttempt = now + RetryIntervalMs,
                        });
                    }
                }
                return true;
            }

            ApplyResolvedState(building, prefab, command, now);
            return true;
        }

        private void ApplyResolvedState(Entity building, Entity prefab,
            GrowableLifecycleCommand command, long now)
        {
            bool needsUpdate = RepairCompletedPrefab(building, prefab, command);
            needsUpdate |= ApplyConditionAndState(building, command);
            // Updated rebuilds road/utility/lot data; only lifecycle changes need it.
            if (needsUpdate)
            {
                EntityManager.AddComponent<Updated>(building);
                _stateRefreshes++;
            }
            else _stateDataOnly++;
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotState++;
        }

        /// <summary>A state update can arrive with a spawn realized later in the frame; kept ordered and bounded.</summary>
        private void RetryPendingStateCorrections(long now)
        {
            int remaining = _pendingStateCorrections.Count;
            int attempts = 0;
            while (remaining-- > 0 && _pendingStateCorrections.Count > 0 &&
                   attempts < MaxStateRetriesPerFrame)
            {
                if (_stateRetryCursor >= _pendingStateCorrections.Count) _stateRetryCursor = 0;
                int i = _stateRetryCursor;
                PendingStateCorrection pending = _pendingStateCorrections[i];
                if (pending.Expiry <= now)
                {
                    attempts++;
                    // Occupancy and company pages carry the completed identity as a backstop.
                    _pendingStateSequences.Remove(pending.Command.Sequence);
                    _pendingStateCorrections.RemoveAt(i);
                    _unmatched++;
                    _applied.Remember(pending.Command.Sequence, now, ReplayWindowMs);
                    SyncLog.Detail(LogTopic.Buildings, "GrowableSync: no building at " +
                        Format(new float3(pending.Command.AnchorX, pending.Command.AnchorY,
                            pending.Command.AnchorZ)) + " to correct ('" +
                        pending.Command.PrefabName + "'); skipped.");
                    continue;
                }

                _stateRetryCursor++;
                if (now < pending.NextAttempt) continue;
                pending.NextAttempt = now + RetryIntervalMs;
                attempts++;
                _stateRetryChecks++;

                var position = new float3(pending.Command.AnchorX,
                    pending.Command.AnchorY, pending.Command.AnchorZ);
                _prefabIndex.TryResolve(pending.Command.PrefabName, out Entity prefab);
                Entity building = FindGrowableAt(position, prefab, now);
                if (building == Entity.Null) continue;

                _pendingStateSequences.Remove(pending.Command.Sequence);
                _pendingStateCorrections.RemoveAt(i);
                _stateRetryCursor = i;
                ApplyResolvedState(building, prefab, pending.Command, now);
            }
        }

        private void SupersedePendingState(GrowableLifecycleCommand command, long now)
        {
            for (int i = _pendingStateCorrections.Count - 1; i >= 0; i--)
            {
                GrowableLifecycleCommand previous = _pendingStateCorrections[i].Command;
                if (!Infrastructure.GrowableCommandInbox.SameTarget(previous, command)) continue;
                _pendingStateCorrections.RemoveAt(i);
                _pendingStateSequences.Remove(previous.Sequence);
                _applied.Remember(previous.Sequence, now, ReplayWindowMs);
            }
        }
    }
}
