using Game;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Keeps local tool definitions out of an armed remote transaction. Runs in the gap after
    /// <see cref="ToolOutputBarrier"/> and removes non-Permanent local definitions; sync definitions
    /// carry Deleted from birth and are excluded. The tool regenerates its preview afterwards.
    /// </summary>
    public partial class DefinitionGateSystem : GameSystemBase
    {
        private NetSyncSystem _netSync;
        private BuildSyncSystem _buildSync;
        private ToolSystem _toolSystem;
        private Players.PlayerCursorSyncSystem _playerCursor;
        private EntityQuery _foreignDefinitions;

        protected override void OnCreate()
        {
            base.OnCreate();
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _playerCursor = World.GetOrCreateSystemManaged<Players.PlayerCursorSyncSystem>();

            // The active tool's buffered preview definitions. Zoning is spared: its Block/Cell Temps are
            // never read by an isolated commit, and removing one leaves the marquee nothing to commit.
            _foreignDefinitions = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<CreationDefinition, Updated>(),
                None = SyncQuery.ReadOnly<Deleted, Zoning>(),
            });
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("DefinitionGate"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;
                if (_netSync == null) return;

                // Also during a world-sync barrier: a draining native graph left Disabled would stall recovery.
                _netSync.FinishIsolationAfterToolOutput();
                if (!service.GameplaySyncReady) return;

                // Object previews are captured before ToolOutputSystem; only materialize this batch when needed.
                bool armedCommit = _netSync.HasArmedToolCommit;
                bool activeNetTool = _toolSystem != null &&
                                     _toolSystem.activeTool is global::Game.Tools.NetToolSystem;
                if (!armedCommit && !activeNetTool)
                {
                    _playerCursor.ObserveHoverDefinitions(default(NativeArray<Entity>));
                    _netSync.ObserveLocalNetDefinitions(default(NativeArray<Entity>));
                    if (_buildSync == null)
                        _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
                    _buildSync.ObserveLocalObjectToolOutput(default(NativeArray<Entity>));
                    return;
                }

                int killed = 0;
                NativeArray<Entity> definitions = _foreignDefinitions.IsEmptyIgnoreFilter
                    ? default(NativeArray<Entity>)
                    : _foreignDefinitions.ToEntityArray(Allocator.Temp);
                try
                {
                    // Every frame: the next Apply publishes this cached course intent, not its Created edges.
                    _playerCursor.ObserveHoverDefinitions(definitions);
                    _netSync.ObserveLocalNetDefinitions(definitions);
                    // A just-selected net or grid only has definitions in the barrier buffer until here.
                    // Idempotent when the standing preview was already sent this frame.
                    _netSync.CaptureBufferedLocalNetApply();
                    if (_buildSync == null)
                        _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
                    _buildSync.ObserveLocalObjectToolOutput(definitions);

                    if (!armedCommit) return;
                    for (int i = 0; i < definitions.Length; i++)
                    {
                        CreationDefinition def =
                            EntityManager.GetComponentData<CreationDefinition>(definitions[i]);
                        if ((def.m_Flags & CreationFlags.Permanent) != 0) continue;
                        EntityManager.DestroyEntity(definitions[i]);
                        killed++;
                    }
                }
                finally
                {
                    if (definitions.IsCreated) definitions.Dispose();
                }

                if (killed > 0)
                {
                    _netSync.ForceActiveToolUpdate();
                    SyncLog.Trace(LogTopic.Pipeline, "def gate wiped defs=" + killed);
                }
            }
        }
    }
}
