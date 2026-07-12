using Game;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Keeps freshly buffered local tool definitions out of an armed remote net transaction. Tool
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
        private EntityQuery _foreignDefinitions;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(DefinitionGateSystem) + " ready.");
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();

            // Fresh, entity-visible definitions that are not a sync feeder's own (those carry
            // Deleted from birth) - i.e. the active tool's buffered preview definitions.
            _foreignDefinitions = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CreationDefinition>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
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

                if (!_netSync.HasArmedNetCommit) return;
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
