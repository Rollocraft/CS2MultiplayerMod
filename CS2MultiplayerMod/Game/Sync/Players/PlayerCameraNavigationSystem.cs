using Game;
using Game.Rendering;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    public partial class PlayerCameraNavigationSystem : GameSystemBase
    {
        private CameraUpdateSystem _camera;
        protected override void OnCreate()
        {
            base.OnCreate();
            _camera = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
        }

        protected override void OnUpdate() =>
            Mod.Service?.UpdatePlayerCamera(_camera, UnityEngine.Time.unscaledDeltaTime);
    }
}
