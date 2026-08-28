using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Single source of truth for gameplay commands accepted at the session boundary.
    /// Keeping authorization and diagnostic names together makes a newly introduced
    /// command visible in logs as soon as it is admitted to the protocol.
    /// </summary>
    internal static class GameplayCommandRegistry
    {
        private static readonly ushort[] AllowedCommandIds =
        {
            ObjectPlacementCommand.Id, NetPlacementCommand.Id,
            ObjectDeleteCommand.Id, NetDeleteCommand.Id,
            ZonePaintCommand.Id, TerrainBrushCommand.Id,
            UpgradePlacementCommand.Id, ObjectMoveCommand.Id, NetUpgradeCommand.Id,
            AreaCreateCommand.Id, AreaUpdateCommand.Id, AreaDeleteCommand.Id,
            OwnedAreaSnapshotCommand.Id,
            RouteCreateCommand.Id, RouteUpdateCommand.Id, RouteDeleteCommand.Id,
            TilePurchaseCommand.Id, EntityPolicyCommand.Id, DevTreePurchaseCommand.Id,
            NetReplaceCommand.Id, NetToolOperationCommand.Id,
            ObjectToolOperationCommand.Id, AssetStampCommand.Id,
            VisualCustomizationCommand.Id, ColorPaletteCommand.Id,
            DisasterEventCommand.Id, EntityNameCommand.Id,
            GrowableLifecycleCommand.Id,
        };

        internal static void Register(MultiplayerSession session)
        {
            session.AllowCommands(AllowedCommandIds);
        }

        internal static string Name(ushort id)
        {
            switch (id)
            {
                case ObjectPlacementCommand.Id: return "object-place";
                case NetPlacementCommand.Id: return "net-place";
                case ObjectDeleteCommand.Id: return "object-delete";
                case NetDeleteCommand.Id: return "net-delete";
                case ZonePaintCommand.Id: return "zone-paint";
                case TerrainBrushCommand.Id: return "terrain-brush";
                case UpgradePlacementCommand.Id: return "building-upgrade";
                case ObjectMoveCommand.Id: return "object-move";
                case ObjectToolOperationCommand.Id: return "object-native-operation";
                case AssetStampCommand.Id: return "asset-stamp";
                case NetUpgradeCommand.Id: return "net-upgrade";
                case AreaCreateCommand.Id: return "area-create";
                case AreaDeleteCommand.Id: return "area-delete";
                case RouteCreateCommand.Id: return "route-create";
                case RouteDeleteCommand.Id: return "route-delete";
                case TilePurchaseCommand.Id: return "tile-purchase";
                case EntityPolicyCommand.Id: return "policy-edit";
                case AreaUpdateCommand.Id: return "area-update";
                case OwnedAreaSnapshotCommand.Id: return "owned-area-snapshot";
                case RouteUpdateCommand.Id: return "route-update";
                case DevTreePurchaseCommand.Id: return "dev-tree-purchase";
                case NetReplaceCommand.Id: return "net-replace";
                case NetToolOperationCommand.Id: return "net-native-operation";
                case VisualCustomizationCommand.Id: return "visual-customization";
                case ColorPaletteCommand.Id: return "color-palette";
                case DisasterEventCommand.Id: return "disaster-event";
                case EntityNameCommand.Id: return "entity-name";
                case GrowableLifecycleCommand.Id: return "growable-lifecycle";
                default: return "unknown";
            }
        }
    }
}
