using Colossal.Mathematics;
using Game.Net;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Click stash + replay for NetSyncSystem: DefinitionGateSystem hands over each definition it
    // kills on an armed frame; if the player's click then lands inside that window (it applies
    // nothing - its committing Temps were destroyed one frame earlier), the gesture is rebuilt as
    // ordinary sync commands and lands a few frames late instead of vanishing.
    public partial class NetSyncSystem
    {
        // A tool preview is a handful of definitions; anything past this is not a click gesture.
        private const int MaxStashPerKind = 64;

        /// <summary>
        /// Record what <see cref="DefinitionGateSystem"/> is about to destroy, so a click swallowed
        /// by the armed window can be replayed. Only self-contained gestures stash: an original,
        /// owner or attach reference (upgrades, moves, net-attached objects) or an
        /// <see cref="OwnerDefinition"/> (sub-elements) means the definition is not a plain
        /// placement, and a point course (&lt; 1 m - the pre-first-click cursor marker) natively
        /// commits nothing, so replaying it would plant a stub.
        /// </summary>
        public void StashKilledDefinition(Entity definition)
        {
            if (_stashFrame != _realizeFrame)
            {
                ClearClickStash();
                _stashFrame = _realizeFrame;
            }

            if (!EntityManager.HasComponent<CreationDefinition>(definition)) return;
            CreationDefinition def = EntityManager.GetComponentData<CreationDefinition>(definition);
            bool subElement = EntityManager.HasComponent<OwnerDefinition>(definition);

            if ((def.m_Flags & CreationFlags.Delete) != 0)
            {
                // A bulldozer hover: the target is the original, whatever its domain (object/edge).
                if (subElement || def.m_Original == Entity.Null) return;
                if (_stashBulldozes.Count >= MaxStashPerKind) return;
                if (!EntityManager.Exists(def.m_Original)) return;
                _stashBulldozes.Add(def.m_Original);
                return;
            }

            if (subElement || def.m_Original != Entity.Null || def.m_Owner != Entity.Null ||
                def.m_Attached != Entity.Null) return;

            if (EntityManager.HasComponent<NetCourse>(definition))
            {
                NetCourse course = EntityManager.GetComponentData<NetCourse>(definition);
                if (course.m_Length < 1f) return;
                if (_stashCourses.Count >= MaxStashPerKind) return;
                NetPlacementCommand command = CaptureDefinitionCommand(def, course);
                if (command != null) _stashCourses.Add(command);
                return;
            }

            if (EntityManager.HasComponent<ObjectDefinition>(definition))
            {
                if (_stashObjects.Count >= MaxStashPerKind) return;
                ObjectDefinition obj = EntityManager.GetComponentData<ObjectDefinition>(definition);
                _stashObjects.Add((def.m_Prefab, obj.m_Position, obj.m_Rotation));
            }
        }

        /// <summary>
        /// Rebuild the gesture a click inside the armed window would have applied. Courses are
        /// re-encoded as <see cref="NetPlacementCommand"/>, broadcast to peers AND looped into the
        /// local realize queue (origin -1 is never a real player id, so the origin-skip keeps the
        /// local copy; the realize marks echo guards as usual, the explicit send keeps peers
        /// exactly-once). Bulldoze targets go to <see cref="DeleteSyncSystem.QueueLocalBulldoze"/>
        /// as UNGUARDED local work, so the normal delete capture broadcasts them. Object ghosts are
        /// re-encoded, broadcast, and realized via <see cref="BuildSyncSystem.QueueLocalReplay"/>
        /// (its guard marks suppress the capture echo).
        /// </summary>
        private void ReplaySwallowedClick()
        {
            // Valid for exactly one realize frame: the gate wrote the stash at the END of the
            // previous frame (post-ToolOutputBarrier), and this click is its only chance to apply.
            bool fresh = _stashFrame == _realizeFrame - 1;
            if (!fresh || (_stashCourses.Count == 0 && _stashBulldozes.Count == 0 && _stashObjects.Count == 0))
            {
                ClearClickStash();
                return;
            }

            MultiplayerService service = Mod.Service;
            MultiplayerSession session = service != null ? service.Session : null;
            if (session == null || !service.GameplaySyncReady)
            {
                ClearClickStash();
                return;
            }

            int courses = 0, deletes = 0, objects = 0;

            long operationId = _nextLocalNetOperationId++;
            if (_nextLocalNetOperationId <= 0) _nextLocalNetOperationId = 1;
            for (int i = 0; i < _stashCourses.Count; i++)
            {
                NetPlacementCommand command = _stashCourses[i];
                command.OperationId = operationId;
                command.CourseIndex = (short)i;
                command.CourseCount = (short)_stashCourses.Count;
                byte[] body = command.Encode();
                session.SendCommand(0, NetPlacementCommand.Id, body);
                _localReplays.Add(new SimulationCommandMessage(-1, 0, NetPlacementCommand.Id, body));
                courses++;
            }

            for (int i = 0; i < _stashBulldozes.Count; i++)
            {
                Entity target = _stashBulldozes[i];
                if (!EntityManager.Exists(target) ||
                    EntityManager.HasComponent<global::Game.Common.Deleted>(target)) continue;
                DeleteSyncPartner.QueueLocalBulldoze(target);
                deletes++;
            }

            for (int i = 0; i < _stashObjects.Count; i++)
            {
                string name = _prefabSystem.GetPrefabName(_stashObjects[i].prefab);
                if (string.IsNullOrEmpty(name)) continue;
                float3 p = _stashObjects[i].position;
                quaternion r = _stashObjects[i].rotation;
                var command = new ObjectPlacementCommand
                {
                    PrefabName = name,
                    PosX = p.x, PosY = p.y, PosZ = p.z,
                    RotX = r.value.x, RotY = r.value.y, RotZ = r.value.z, RotW = r.value.w,
                    AttachKind = ObjectAttachKind.None,
                };
                session.SendCommand(0, ObjectPlacementCommand.Id, command.Encode());
                BuildSyncPartner.QueueLocalReplay(command);
                objects++;
            }

            ClearClickStash();
            if (courses > 0 || deletes > 0 || objects > 0)
                Diagnostics.FlightRecorder.Note("click replay courses=" + courses +
                    " deletes=" + deletes + " objects=" + objects);
        }

        private void ClearClickStash()
        {
            _stashCourses.Clear();
            _stashBulldozes.Clear();
            _stashObjects.Clear();
            _stashFrame = -1;
        }

        // Resolved lazily: both systems resolve NetSyncSystem in their own OnCreate, so eager
        // resolution here would recurse through World.GetOrCreateSystemManaged.
        private DeleteSyncSystem _deleteSyncPartner;
        private BuildSyncSystem _buildSyncPartner;
        private DeleteSyncSystem DeleteSyncPartner =>
            _deleteSyncPartner ?? (_deleteSyncPartner = World.GetOrCreateSystemManaged<DeleteSyncSystem>());
        private BuildSyncSystem BuildSyncPartner =>
            _buildSyncPartner ?? (_buildSyncPartner = World.GetOrCreateSystemManaged<BuildSyncSystem>());
    }
}
