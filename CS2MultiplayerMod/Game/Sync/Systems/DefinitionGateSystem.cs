using Game;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Keeps the local player's tool definitions out of an armed net commit. Every tool records its
    /// definitions through <see cref="ToolOutputBarrier"/>, an end-of-phase command buffer - so at
    /// <c>PrepareDefinitionFrame</c> time (mid-ToolUpdate) they are not yet entities and the def
    /// wipe can never catch them. The barrier then re-materialises the preview right into the armed
    /// window, and the next frame's flip would commit the player's un-applied gesture together with
    /// the remote batch (half-drawn roads placed mid-bend, hovered ghosts planted, a hovered
    /// bulldozer deleting its target).
    ///
    /// This system runs after the barrier - the one slot where the tool's definitions exist but
    /// Modification has not yet consumed them. On every armed frame it destroys each fresh
    /// non-Permanent definition that is not ours (every sync feeder tags its definitions Deleted at
    /// birth for frame-end cleanup, which no tool does, so the query simply excludes Deleted),
    /// stashing what it kills so a click swallowed by the window can be replayed (see
    /// <see cref="NetSyncSystem.StashKilledDefinition"/>), then re-sets the tool's force-update flag
    /// so the gesture regenerates. Permanent definitions (sibling realizes, the game's zone-growth
    /// spawns) never become Temps and are spared. The flip frame is naturally excluded: the flip
    /// branch clears the armed flag mid-phase, before this system runs, so the preview is back the
    /// very frame the batch commits.
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
                    _netSync.StashKilledDefinition(definitions[i]);
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
