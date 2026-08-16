using System.Collections.Concurrent;
using Game;
using Game.Areas;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates map tile purchases live: detect Updated <see cref="MapTile"/> with no
    /// <see cref="Native"/>, broadcast <see cref="TilePurchaseCommand"/> (centroids + price).
    /// Realize by finding tile by centroid, remove Native, host charges shared treasury
    /// pro-rated. Echo guard marks realized tiles.
    /// </summary>
    public partial class TilePurchaseSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        private MapTilePurchaseSystem _purchase;
        private EntityQuery _flippedTiles;
        private EntityQuery _nativeTiles;
        private CommandObserver _observer;
        private int _lastSelectionCost;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(TilePurchaseSyncSystem) + " ready.");
            _purchase = World.GetOrCreateSystemManaged<MapTilePurchaseSystem>();

            // Owned-this-frame: Updated map tiles that (no longer) carry Native.
            _flippedTiles = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<MapTile>(),
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Native>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Still-purchasable tiles — the candidate pool for realizing a remote purchase.
            _nativeTiles = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<MapTile>(),
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<Native>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, TilePurchaseCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            // The exact price disappears with the selection the moment the purchase
            // lands, so remember the last quoted cost while the player is selecting.
            if (_purchase.selecting && _purchase.cost > 0) _lastSelectionCost = _purchase.cost;

            long now = service.NowMs;
            _guard.Prune(now);
            CapturePurchases(session, now);
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;
            RealizeIncoming(session, service.NowMs);
        }

        /// <summary>Polygon centroid on the XZ plane (Y zeroed: identity, not elevation).</summary>
        private float3 Centroid(Entity tile)
        {
            DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(tile, true);
            if (nodes.Length == 0) return default;
            float3 sum = float3.zero;
            for (int i = 0; i < nodes.Length; i++) sum += nodes[i].m_Position;
            float3 center = sum / nodes.Length;
            center.y = 0f;
            return center;
        }

        private void CapturePurchases(MultiplayerSession session, long now)
        {
            if (_flippedTiles.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> tiles = _flippedTiles.ToEntityArray(Allocator.Temp);
            try
            {
                float[] xs = null, zs = null;
                int count = 0;
                for (int i = 0; i < tiles.Length; i++)
                {
                    float3 center = Centroid(tiles[i]);
                    if (_guard.Consume(TileKey(center), now)) continue;

                    if (xs == null) { xs = new float[tiles.Length]; zs = new float[tiles.Length]; }
                    xs[count] = center.x;
                    zs[count] = center.z;
                    count++;
                }
                if (count == 0) return;

                var command = new TilePurchaseCommand
                {
                    TotalCost = _lastSelectionCost,
                    CenterX = new float[count],
                    CenterZ = new float[count],
                };
                System.Array.Copy(xs, command.CenterX, count);
                System.Array.Copy(zs, command.CenterZ, count);
                _lastSelectionCost = 0;

                session.SendCommand(0, TilePurchaseCommand.Id, command.Encode());
                Mod.Verbose("[MP] TilePurchaseSync captured " + count + " tile(s), price " +
                             command.TotalCost + ".");
            }
            finally
            {
                tiles.Dispose();
            }
        }

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                TilePurchaseCommand command;
                try { command = TilePurchaseCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] TilePurchaseSync: dropping malformed command: " + ex.Message); continue; }
                if (command.CenterX == null || command.CenterX.Length == 0) continue;

                int unlocked = 0;
                NativeArray<Entity> tiles = _nativeTiles.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int c = 0; c < command.CenterX.Length; c++)
                    {
                        var wanted = new float3(command.CenterX[c], 0f, command.CenterZ[c]);
                        for (int i = 0; i < tiles.Length; i++)
                        {
                            if (!EntityManager.HasComponent<Native>(tiles[i])) continue; // unlocked earlier this loop
                            float3 center = Centroid(tiles[i]);
                            // Tiles are hundreds of meters apart; 32 m catches float noise
                            // without ever matching a neighbour.
                            if (math.distancesq(center, wanted) > 1024f) continue;

                            _guard.Mark(TileKey(center), now);
                            EntityManager.RemoveComponent<Native>(tiles[i]);
                            EntityManager.AddComponent<Updated>(tiles[i]);
                            unlocked++;
                            break;
                        }
                    }
                }
                finally
                {
                    tiles.Dispose();
                }

                // Charge what the buyer's game quoted, pro-rated when part of the batch
                // was already owned here (echo/replay) — host only, inside the charger.
                if (unlocked > 0 && command.TotalCost > 0)
                    ConstructionCharger.ChargeAmount(EntityManager,
                        (long)command.TotalCost * unlocked / command.CenterX.Length,
                        "map tiles x" + unlocked + " (player " + message.OriginPlayerId + ")");

                Mod.Verbose("[MP] TilePurchaseSync realize: unlocked " + unlocked + "/" +
                             command.CenterX.Length + " tile(s) from player " + message.OriginPlayerId + ".");
            }
        }

        private static string TileKey(float3 centroid) => ReplicationGuard.Key("maptile", centroid);

    }
}
