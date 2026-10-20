using Game;
using Game.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    public partial class MapPingRenderSystem : GameSystemBase
    {
        private OverlayRenderSystem _overlay;
        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        protected override void OnUpdate()
        {
            var service = Mod.Service;
            if (service == null) return;
            service.PruneMapPings();
            if (service.MapPings.Count == 0) return;
            var buffer = _overlay.GetBuffer(out var dependencies);
            dependencies.Complete();
            foreach (var marker in service.MapPings)
            {
                var ping = marker.Message;
                float age = (service.NowMs - marker.CreatedMs) * 0.001f;
                var color = RemotePlayerMarkerSystem.PlayerColor(ping.PlayerId);
                color.a = math.saturate((10f - age) / 2f);
                var clear = new Color(color.r, color.g, color.b, 0f);
                var position = new float3(ping.X, ping.Y + 1f, ping.Z);
                buffer.DrawCircle(color, clear, 4f, default, new float2(0f, 1f), position, 24f);
                color.a *= 1f - math.frac(age);
                buffer.DrawCircle(color, clear, 3f, default, new float2(0f, 1f), position, 24f + math.frac(age) * 90f);
            }
            _overlay.AddBufferWriter(default);
        }
    }
}
