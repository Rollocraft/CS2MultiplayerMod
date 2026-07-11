using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Native tool-intent capture. DefinitionGateSystem calls ObserveLocalNetDefinitions after the
    // tool-output barrier; SyncRealizeSystem calls CaptureLocalNetApply before ToolOutputSystem
    // consumes an Apply frame. Keeping those two points separate lets the previous frame's exact
    // preview definition describe the Temps that are about to commit.
    public partial class NetSyncSystem
    {
        private const long CommittedSideEffectWindowMs = 5000;

        /// <summary>
        /// Refresh the active net tool's cached course definitions. An empty steady-state frame keeps
        /// the prior cache because a motionless preview does not necessarily regenerate definitions;
        /// a Clear frame or a different active tool invalidates it.
        /// </summary>
        public void ObserveLocalNetDefinitions(NativeArray<Entity> definitions)
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            if (!(active is global::Game.Tools.NetToolSystem))
            {
                _cachedLocalCourses.Clear();
                return;
            }

            var next = new List<NetPlacementCommand>();
            bool overflow = false;
            for (int i = 0; i < definitions.Length; i++)
            {
                Entity entity = definitions[i];
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<NetCourse>(entity) ||
                    !EntityManager.HasComponent<CreationDefinition>(entity) ||
                    EntityManager.HasComponent<OwnerDefinition>(entity)) continue;

                CreationDefinition definition = EntityManager.GetComponentData<CreationDefinition>(entity);
                if (!IsPlainLocalNetDefinition(definition)) continue;

                NetCourse course = EntityManager.GetComponentData<NetCourse>(entity);
                if (course.m_Length < 1f) continue; // point/cursor marker, never a committed segment

                NetPlacementCommand command = CaptureDefinitionCommand(definition, course);
                if (command == null) continue;
                if (next.Count >= NetPlacementCommand.MaxCoursesPerOperation)
                {
                    overflow = true;
                    break;
                }
                next.Add(command);
            }

            if (overflow)
            {
                _cachedLocalCourses.Clear();
                Mod.log.Warn("[MP] NetSync: local operation exceeded the native-course cap; " +
                             "using final-edge capture for this apply.");
            }
            else if (next.Count > 0)
            {
                _cachedLocalCourses.Clear();
                _cachedLocalCourses.AddRange(next);
            }
            else if (active.applyMode == global::Game.Tools.ApplyMode.Clear)
            {
                _cachedLocalCourses.Clear();
            }
        }

        /// <summary>
        /// Publish the exact cached courses when the local net tool actually applies its standing
        /// preview. Called before ToolOutputSystem, while the preview Temps still expose every
        /// original edge the operation will replace as a split side effect.
        /// </summary>
        public void CaptureLocalNetApply()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady || _nativeApplyCapturedFrame == _realizeFrame)
                return;

            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            if (!(active is global::Game.Tools.NetToolSystem) ||
                active.applyMode != global::Game.Tools.ApplyMode.Apply ||
                _cachedLocalCourses.Count == 0) return;

            // A rare tool switch can overlap an already armed remote batch. Its gate/replay path owns
            // that frame; publishing this cache would claim a local placement that may not commit.
            if (_pendingApply || _awaitingDrain) return;

            long now = service.NowMs;
            RecordPlacementOriginals(now);

            long operationId = _nextLocalNetOperationId++;
            if (_nextLocalNetOperationId <= 0) _nextLocalNetOperationId = 1;
            int count = _cachedLocalCourses.Count;
            var encoded = new List<byte[]>(count);
            try
            {
                // Encode the complete operation before sending its first course. A locally unusual
                // definition then falls back as a whole to final-edge capture instead of publishing
                // a partial native operation.
                for (int i = 0; i < count; i++)
                {
                    NetPlacementCommand command = _cachedLocalCourses[i];
                    command.OperationId = operationId;
                    command.CourseIndex = (short)i;
                    command.CourseCount = (short)count;
                    encoded.Add(command.Encode());
                }
            }
            catch (System.Exception ex)
            {
                _cachedLocalCourses.Clear();
                Mod.log.Warn("[MP] NetSync intent capture could not encode operation; " +
                             "using final-edge capture: " + ex.Message);
                return;
            }

            int sent = 0;
            for (int i = 0; i < count; i++)
            {
                NetPlacementCommand command = _cachedLocalCourses[i];
                try
                {
                    service.Session.SendCommand(0, NetPlacementCommand.Id, encoded[i]);
                    RecordDiagnostic(command.PrefabName);
                    sent++;
                }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] NetSync intent capture dropped course " + i + "/" + count +
                                 ": " + ex.Message);
                }
            }

            _cachedLocalCourses.Clear();
            if (sent > 0) _nativeApplyCapturedFrame = _realizeFrame;
            if (sent > 0)
                Diagnostics.FlightRecorder.Note("net intent apply op=" + operationId + " courses=" + sent);
        }

        /// <summary>
        /// Consume an exact original edge recorded from a committing Temp transaction. DeleteSync
        /// calls this before its geometry heuristics; a match has already been represented by the
        /// placement/delete/replace command that caused it and must not become a second command.
        /// </summary>
        public bool ConsumeCommittedNetSideEffect(Entity edge, long now)
        {
            long expires;
            if (!_committedNetSideEffects.TryGetValue(edge, out expires)) return false;
            _committedNetSideEffects.Remove(edge);
            return expires >= now;
        }

        private static bool IsPlainLocalNetDefinition(CreationDefinition definition)
        {
            const CreationFlags incompatible = CreationFlags.Permanent | CreationFlags.Delete |
                CreationFlags.Upgrade | CreationFlags.Relocate | CreationFlags.Recreate |
                CreationFlags.Repair | CreationFlags.Duplicate;
            return definition.m_Original == Entity.Null && definition.m_Owner == Entity.Null &&
                   definition.m_Attached == Entity.Null && (definition.m_Flags & incompatible) == 0;
        }

        private NetPlacementCommand CaptureDefinitionCommand(CreationDefinition definition, NetCourse course)
        {
            string prefabName = PrefabNameOf(definition.m_Prefab);
            if (string.IsNullOrEmpty(prefabName) || prefabName.StartsWith("Invisible")) return null;

            Bezier4x3 curve = course.m_Curve;
            return new NetPlacementCommand
            {
                CourseIndex = 0,
                CourseCount = 1,
                HasNativeCourse = true,
                PrefabName = prefabName,
                SubPrefabName = PrefabNameOf(definition.m_SubPrefab),
                Ax = curve.a.x, Ay = curve.a.y, Az = curve.a.z,
                Bx = curve.b.x, By = curve.b.y, Bz = curve.b.z,
                Cx = curve.c.x, Cy = curve.c.y, Cz = curve.c.z,
                Dx = curve.d.x, Dy = curve.d.y, Dz = curve.d.z,
                Length = course.m_Length,
                RandomSeed = definition.m_RandomSeed,
                CreationFlags = (uint)definition.m_Flags,
                CourseElevationLeft = course.m_Elevation.x,
                CourseElevationRight = course.m_Elevation.y,
                FixedIndex = course.m_FixedIndex,
                Start = CaptureEndpoint(course.m_StartPosition),
                End = CaptureEndpoint(course.m_EndPosition),
            };
        }

        private NetEndpointIntent CaptureEndpoint(CoursePos position)
        {
            NetEndpointIntent result = new NetEndpointIntent
            {
                Kind = position.m_Entity == Entity.Null
                    ? NetEndpointTargetKind.Free
                    : NetEndpointTargetKind.Infer,
                PosX = position.m_Position.x,
                PosY = position.m_Position.y,
                PosZ = position.m_Position.z,
                RotX = position.m_Rotation.value.x,
                RotY = position.m_Rotation.value.y,
                RotZ = position.m_Rotation.value.z,
                RotW = position.m_Rotation.value.w,
                ElevationLeft = position.m_Elevation.x,
                ElevationRight = position.m_Elevation.y,
                CourseDelta = position.m_CourseDelta,
                SplitPosition = position.m_SplitPosition,
                Flags = (uint)position.m_Flags,
                ParentMesh = position.m_ParentMesh,
                AnchorX = position.m_Position.x,
                AnchorY = position.m_Position.y,
                AnchorZ = position.m_Position.z,
            };

            Entity target = position.m_Entity;
            if (target == Entity.Null || !EntityManager.Exists(target)) return result;

            result.TargetPrefabName = EntityManager.HasComponent<PrefabRef>(target)
                ? PrefabNameOf(EntityManager.GetComponentData<PrefabRef>(target).m_Prefab)
                : null;

            if (EntityManager.HasComponent<Node>(target))
            {
                result.Kind = EntityManager.HasComponent<Owner>(target)
                    ? NetEndpointTargetKind.OwnedNode
                    : NetEndpointTargetKind.Node;
                float3 anchor = EntityManager.GetComponentData<Node>(target).m_Position;
                result.AnchorX = anchor.x; result.AnchorY = anchor.y; result.AnchorZ = anchor.z;
            }
            else if (EntityManager.HasComponent<Edge>(target) && EntityManager.HasComponent<Curve>(target))
            {
                result.Kind = EntityManager.HasComponent<Owner>(target)
                    ? NetEndpointTargetKind.OwnedEdge
                    : NetEndpointTargetKind.Edge;
                Bezier4x3 targetCurve = EntityManager.GetComponentData<Curve>(target).m_Bezier;
                float split = math.clamp(position.m_SplitPosition, 0f, 1f);
                float3 anchor = MathUtils.Position(targetCurve, split);
                result.AnchorX = anchor.x; result.AnchorY = anchor.y; result.AnchorZ = anchor.z;
                result.TargetAx = targetCurve.a.x; result.TargetAy = targetCurve.a.y; result.TargetAz = targetCurve.a.z;
                result.TargetBx = targetCurve.b.x; result.TargetBy = targetCurve.b.y; result.TargetBz = targetCurve.b.z;
                result.TargetCx = targetCurve.c.x; result.TargetCy = targetCurve.c.y; result.TargetCz = targetCurve.c.z;
                result.TargetDx = targetCurve.d.x; result.TargetDy = targetCurve.d.y; result.TargetDz = targetCurve.d.z;
            }
            return result;
        }

        private string PrefabNameOf(Entity prefab)
        {
            return prefab != Entity.Null && EntityManager.Exists(prefab)
                ? _prefabSystem.GetPrefabName(prefab)
                : null;
        }

        private void RecordPlacementOriginals(long now)
        {
            NativeArray<Entity> temps = _tempNetEntities.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    if (!EntityManager.HasComponent<Temp>(temps[i])) continue;
                    Temp temp = EntityManager.GetComponentData<Temp>(temps[i]);
                    const TempFlags replacesOriginal = TempFlags.Delete | TempFlags.Replace |
                                                       TempFlags.Combine;
                    if ((temp.m_Flags & replacesOriginal) == 0) continue;
                    Entity original = temp.m_Original;
                    if (original == Entity.Null || !EntityManager.Exists(original) ||
                        !EntityManager.HasComponent<Edge>(original)) continue;
                    _committedNetSideEffects[original] = now + CommittedSideEffectWindowMs;
                }
            }
            finally
            {
                temps.Dispose();
            }
        }

        private void PruneCommittedNetSideEffects(long now)
        {
            if (_committedNetSideEffects.Count == 0) return;
            var expired = new List<Entity>();
            foreach (KeyValuePair<Entity, long> pair in _committedNetSideEffects)
                if (pair.Value < now) expired.Add(pair.Key);
            for (int i = 0; i < expired.Count; i++) _committedNetSideEffects.Remove(expired[i]);
        }
    }
}
