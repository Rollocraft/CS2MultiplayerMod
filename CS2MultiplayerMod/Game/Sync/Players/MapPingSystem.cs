using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Rendering;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Receives <see cref="MapPingCommand"/>s and draws each as an expanding ring on the ground
    /// for a few seconds, in the sender's palette colour. Sending is done from the chat command;
    /// this system is the receiving and drawing half.
    ///
    /// A ping is deliberately outside the sync pipeline. It mutates nothing, so it needs no echo
    /// guard, no resync on loss and no place in a snapshot - a ping that does not arrive is a
    /// ping that was not seen, and that is the whole of the failure mode. The one thing it does
    /// need is the sender's real identity, which is why it rides the command channel: the
    /// envelope's OriginPlayerId is set by the session, not by whoever typed the text.
    /// </summary>
    public partial class MapPingSystem : GameSystemBase
    {
        /// <summary>How long a ping stays on screen.</summary>
        private const long LifetimeMs = 6000;

        /// <summary>Ring size at birth and at expiry, in metres.</summary>
        private const float StartDiameter = 20f;
        private const float EndDiameter = 140f;
        private const float OutlineWidth = 5f;

        /// <summary>More than this many live at once and the oldest is dropped.</summary>
        private const int MaxActive = 24;

        // Same palette and indexing as the partner markers, so a player's ping is the colour
        // their cursor already is.
        private static readonly Color[] Palette =
        {
            new Color(0.36f, 0.78f, 1.00f), // blue
            new Color(1.00f, 0.69f, 0.26f), // orange
            new Color(0.56f, 0.88f, 0.55f), // green
            new Color(1.00f, 0.45f, 0.45f), // red
            new Color(0.80f, 0.60f, 1.00f), // purple
            new Color(1.00f, 0.85f, 0.40f), // yellow
        };

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly List<ActivePing> _active = new List<ActivePing>();

        private OverlayRenderSystem _overlay;
        private CommandObserver _observer;

        private struct ActivePing
        {
            public float3 Position;
            public int PlayerId;
            public long ExpiresAtMs;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, MapPingCommand.Id), DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueue);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _active.Clear();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            if (!service.GameplaySyncReady)
            {
                DrainQueue();
                return;
            }

            long now = service.NowMs;

            float3 own;
            if (service.TakeLocalPing(out own)) Add(own, service.Session.LocalPlayerId, now);

            ApplyIncoming(service, now);
            Expire(now);

            // Not gated on ShowPartnerMarkers: that setting hides the ambient cursor rings, and
            // someone who turned those off still wants to see a partner deliberately saying
            // "look here". A ping is an event with an author, not background presence.
            if (_active.Count == 0 || _overlay == null) return;

            // Taking the buffer turns the overlay pass on for the frame and blocks on everything
            // it depends on, so it is only taken once there is something to draw.
            JobHandle dependencies;
            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out dependencies);
            dependencies.Complete();

            for (int i = 0; i < _active.Count; i++)
            {
                ActivePing ping = _active[i];
                // 0 at birth, 1 at expiry: the ring grows and fades over its life so a ping
                // reads as an event rather than as one more permanent marker on the map.
                float age = 1f - math.saturate((ping.ExpiresAtMs - now) / (float)LifetimeMs);
                float diameter = math.lerp(StartDiameter, EndDiameter, age);

                Color color = Palette[((ping.PlayerId % Palette.Length) + Palette.Length) % Palette.Length];
                color.a = 0.9f * (1f - age);
                Color fill = new Color(color.r, color.g, color.b, 0.10f * (1f - age));

                buffer.DrawCircle(color, fill, OutlineWidth, default,
                    new float2(0f, 1f), ping.Position, diameter);
            }
        }

        private void ApplyIncoming(MultiplayerService service, long now)
        {
            MultiplayerSession session = service.Session;
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                MapPingCommand command;
                try { command = MapPingCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.Players, "MapPing: dropping malformed ping: " +
                        ex.Message);
                    continue;
                }

                // A host is notified of its own commands; the sender's own ring was already
                // recorded at send time, so skip the echo rather than drawing it twice.
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                var at = new float3(command.X, command.Y, command.Z);
                Add(at, message.OriginPlayerId, now);
                service.NotePing(at); // "/goto ping" follows the newest one, whoever dropped it

                string who = service.PlayerDisplayName(message.OriginPlayerId);
                string where = "(" + (int)command.X + ", " + (int)command.Z + ")";
                service.AppendSystemChat(string.IsNullOrEmpty(command.Label)
                    ? who + " pinged " + where + "."
                    : who + " pinged " + where + ": " + command.Label);
            }
        }

        /// <summary>Record a ping so it can be drawn. Also used for the local player's own.</summary>
        private void Add(float3 position, int playerId, long nowMs)
        {
            if (_active.Count >= MaxActive) _active.RemoveAt(0);
            _active.Add(new ActivePing
            {
                Position = position,
                PlayerId = playerId,
                ExpiresAtMs = nowMs + LifetimeMs,
            });
        }

        private void Expire(long now)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
                if (_active[i].ExpiresAtMs <= now) _active.RemoveAt(i);
        }
    }
}
