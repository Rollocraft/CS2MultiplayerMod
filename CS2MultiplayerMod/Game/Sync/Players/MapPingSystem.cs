using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Renders active co-op map pings with pulsating ground rings and vertical beacon beams
    /// using <see cref="OverlayRenderSystem"/>. Pings stay visible for 10 seconds before
    /// fading out smoothly.
    /// </summary>
    public partial class MapPingSystem : GameSystemBase
    {
        public const long PingDurationMs = 10000;
        private const float CoreDiameter = 16f;
        private const float MaxWaveDiameter = 60f;
        private const float BeamHeight = 350f;
        private const float BeamWidth = 4f;

        private static readonly Color[] Palette =
        {
            new Color(0.36f, 0.78f, 1.00f), // blue
            new Color(1.00f, 0.69f, 0.26f), // orange
            new Color(0.56f, 0.88f, 0.55f), // green
            new Color(1.00f, 0.45f, 0.45f), // red
            new Color(0.80f, 0.60f, 1.00f), // purple
            new Color(1.00f, 0.85f, 0.40f), // yellow
        };

        public struct ActivePing
        {
            public float3 Position;
            public string Sender;
            public string Label;
            public long CreatedMs;
            public int ColorIndex;
        }

        private readonly List<ActivePing> _activePings = new List<ActivePing>();
        private readonly object _lock = new object();
        private OverlayRenderSystem _overlay;

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            MultiplayerService.OnMapPingReceived += HandlePingReceived;
            Mod.log.Info(nameof(MapPingSystem) + " ready.");
        }

        protected override void OnDestroy()
        {
            MultiplayerService.OnMapPingReceived -= HandlePingReceived;
            base.OnDestroy();
        }

        private void HandlePingReceived(float3 position, string sender, string label, int colorIndex)
        {
            MultiplayerService service = Mod.Service;
            long now = service != null ? service.NowMs : 0;
            lock (_lock)
            {
                _activePings.Add(new ActivePing
                {
                    Position = position,
                    Sender = sender,
                    Label = label,
                    CreatedMs = now,
                    ColorIndex = colorIndex,
                });
            }
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady) return;

            long now = service.NowMs;
            lock (_lock)
            {
                for (int i = _activePings.Count - 1; i >= 0; i--)
                {
                    if (now - _activePings[i].CreatedMs > PingDurationMs)
                        _activePings.RemoveAt(i);
                }

                if (_activePings.Count == 0) return;
            }

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            lock (_lock)
            {
                for (int i = 0; i < _activePings.Count; i++)
                {
                    ActivePing ping = _activePings[i];
                    long elapsedMs = now - ping.CreatedMs;
                    if (elapsedMs < 0 || elapsedMs > PingDurationMs) continue;

                    string label = ping.Label ?? "";
                    bool isDanger = label.IndexOf("danger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    label.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    label.IndexOf("fire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    label.IndexOf("alert", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    label.IndexOf("traffic", StringComparison.OrdinalIgnoreCase) >= 0;

                    bool isBuild = label.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   label.IndexOf("plan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   label.IndexOf("road", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   label.IndexOf("zone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   label.IndexOf("metro", StringComparison.OrdinalIgnoreCase) >= 0;

                    int idx = ((ping.ColorIndex % Palette.Length) + Palette.Length) % Palette.Length;
                    Color baseColor = isDanger ? new Color(1.0f, 0.2f, 0.2f) :
                                      isBuild ? new Color(0.2f, 0.9f, 1.0f) : Palette[idx];

                    float pulseCycle = isDanger ? 600f : 1200f;
                    float lifeFraction = 1f - (elapsedMs / (float)PingDurationMs);
                    float pulseProgress = (elapsedMs % pulseCycle) / pulseCycle;

                    // Core ring
                    Color coreColor = baseColor;
                    coreColor.a = 0.95f * lifeFraction;
                    Color coreFill = baseColor;
                    coreFill.a = (isDanger ? 0.45f : 0.25f) * lifeFraction;
                    buffer.DrawCircle(coreColor, coreFill, 4f, default,
                        new float2(0f, 1f), ping.Position, isDanger ? CoreDiameter * 1.3f : CoreDiameter);

                    // Expanding wave ring
                    float waveDiameter = CoreDiameter + (MaxWaveDiameter - CoreDiameter) * pulseProgress;
                    Color waveColor = baseColor;
                    waveColor.a = 0.8f * (1f - pulseProgress) * lifeFraction;
                    Color waveFill = baseColor;
                    waveFill.a = 0.08f * (1f - pulseProgress) * lifeFraction;
                    buffer.DrawCircle(waveColor, waveFill, 3f, default,
                        new float2(0f, 1f), ping.Position, waveDiameter);

                    // Vertical beacon beam
                    var beamTop = ping.Position + new float3(0f, BeamHeight, 0f);
                    Color beamColor = baseColor;
                    beamColor.a = 0.85f * lifeFraction;
                    buffer.DrawLine(beamColor, new Line3.Segment(ping.Position, beamTop), isDanger ? BeamWidth * 1.8f : BeamWidth, true);
                }
            }

            _overlay.AddBufferWriter(default);
        }
    }
}
