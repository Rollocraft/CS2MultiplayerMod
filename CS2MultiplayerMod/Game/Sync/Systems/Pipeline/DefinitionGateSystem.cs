using Game;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Keeps freshly buffered local tool definitions out of an armed remote tool transaction. Tool
    /// definitions become visible only after <see cref="ToolOutputBarrier"/>; this system runs in
    /// that gap and removes non-Permanent local definitions while the remote batch is still waiting
    /// to materialise. Sync-created definitions carry Deleted from birth and are excluded by the
    /// query. On the commit frame the armed flag clears before this system runs, so local definition
    /// generation resumes immediately. The active tool is asked to regenerate after a gated frame,
    /// preserving the visible preview without ever applying it as part of the remote transaction.
    /// </summary>
    public partial class DefinitionGateSystem : GameSystemBase
    {
        private NetSyncSystem _netSync;
        private BuildSyncSystem _buildSync;
        private ToolSystem _toolSystem;
        private EntityQuery _foreignDefinitions;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(DefinitionGateSystem) + " ready.");
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();

            // Fresh, entity-visible definitions that are not a sync feeder's own (those carry
            // Deleted from birth) - i.e. the active tool's buffered preview definitions.
            // Zoning definitions are spared: they materialise into Block/Cell Temps, which no
            // isolated commit reads, and killing one leaves the marquee with no preview to commit
            // on the frame the player releases (see NetSyncSystem's standing-Temp query).
            _foreignDefinitions = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CreationDefinition>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Zoning>(),
                },
            });
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (_netSync == null) return;

            // ToolOutputBarrier has consumed this frame. Restore whichever side of the net/brush
            // transaction was temporarily Disabled before inspecting newly buffered definitions.
            _netSync.FinishIsolationAfterToolOutput();

            // Object/upgrade previews no longer need their regenerated definition graph here. They
            // are captured once from the standing graph immediately before ToolOutputSystem applies
            // it. Avoid even materializing the often-hundreds-strong preview batch unless NetSync
            // needs it or an armed remote transaction must gate it.
            bool armedCommit = _netSync.HasArmedToolCommit;
            bool activeNetTool = _toolSystem != null &&
                                 _toolSystem.activeTool is global::Game.Tools.NetToolSystem;
            if (!armedCommit && !activeNetTool)
            {
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
                // Cache the active net tool's exact native course intent on every frame. This runs
                // before the optional armed-window gate below and is also needed when no commit is
                // armed: the next Apply frame publishes this preview rather than inferring from
                // its final Created edges.
                _netSync.ObserveLocalNetDefinitions(definitions);
                // A newly selected net or a click-frame grid can have no usable graph at the two
                // earlier pre-output hooks: its definitions exist only in ToolOutputBarrier's
                // command buffer until this point. Publish the graph now while it is still the raw
                // pre-PostTool NetCourse operation. The per-frame capture guard makes this a no-op
                // when SyncRealizeSystem already sent the standing preview.
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
                Diagnostics.FlightRecorder.Note("def gate wiped defs=" + killed);
            }
        }
    }
}
