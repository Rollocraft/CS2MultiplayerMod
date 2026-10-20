using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        private readonly ReconnectBackoff _reconnect = new ReconnectBackoff();
        private bool _reconnectEligible;
        public bool IsReconnecting => _reconnect.Active;
        public int ReconnectAttempt => _reconnect.Attempts;
        public int ReconnectDelaySeconds => _reconnect.SecondsUntilAttempt(MonotonicClock.NowMs);

        private bool TryScheduleReconnect(string reason)
        {
            if (_config == null || !_config.AutoReconnect || !_reconnectEligible) return false;
            long now = MonotonicClock.NowMs;
            if (!_reconnect.Active) _reconnect.Begin(now);
            else _reconnect.Failed(now);
            if (_reconnect.Expired(now))
            {
                Stop("Could not reconnect to the host. " + reason);
                return true;
            }
            Stop("Connection lost. Reconnecting to the host.", reconnecting: true);
            return true;
        }

        private void PumpReconnect()
        {
            if (!_reconnect.Active) return;
            long now = MonotonicClock.NowMs;
            if (_reconnect.Expired(now))
            {
                Stop("Could not reconnect to the host within two minutes.");
                return;
            }
            if (_reconnect.AttemptTimedOut(now) && !AwaitingHostApproval)
            {
                TryScheduleReconnect("The connection attempt timed out.");
                return;
            }
            if (!_reconnect.TryStartAttempt(now)) return;
            // Teardown already finished. Use the original immutable config, including transport and credentials.
            Role = SessionRole.None;
            Join(_config);
        }
    }
}
