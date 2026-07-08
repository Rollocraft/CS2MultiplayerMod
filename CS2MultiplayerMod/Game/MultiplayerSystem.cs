using Game;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// ECS heartbeat for multiplayer. Runs at <see cref="global::Game.SystemUpdatePhase.UIUpdate"/>
    /// (every frame, even when paused/in menu) pumping <see cref="MultiplayerService"/>. Also enforces
    /// the "Enable Mod" setting: turning it off closes any active session. Declared <c>partial</c>
    /// because Unity's Entities source generators extend system types.
    /// </summary>
    public partial class MultiplayerSystem : GameSystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(MultiplayerSystem) + " created.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            if (!MultiplayerService.ModEnabled)
            {
                if (service.Session.Role != SessionRole.None)
                {
                    Mod.log.Info("[MP] Mod disabled in settings - closing the active session.");
                    service.Disconnect();
                }
                return;
            }

            service.Update();
        }
    }
}
