using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        private string _pendingResyncsJson = "[]";
        private string _pendingResyncsSig = "";

        /// <summary>Host-side client resync requests waiting for a decision.</summary>
        public string PendingResyncsJson { get { lock (_chatLock) return _pendingResyncsJson; } }

        private void RefreshPendingResyncsJson()
        {
            if (_session.Role != SessionRole.Host)
            {
                if (_pendingResyncsSig.Length != 0)
                    lock (_chatLock) { _pendingResyncsJson = "[]"; _pendingResyncsSig = ""; }
                return;
            }

            var pending = new List<Peer>();
            foreach (Peer peer in _session.PendingResyncRequests) pending.Add(peer);
            pending.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));

            var sig = new System.Text.StringBuilder();
            for (int i = 0; i < pending.Count; i++)
                sig.Append(pending[i].PlayerId).Append(':').Append(pending[i].PendingResyncAutomatic)
                    .Append(':').Append(pending[i].PendingResyncReason).Append('|');
            string signature = sig.ToString();
            if (signature == _pendingResyncsSig) return;

            var sb = new System.Text.StringBuilder(pending.Count * 96 + 2);
            sb.Append('[');
            for (int i = 0; i < pending.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{').Append('"').Append("id").Append('"').Append(':')
                    .Append(pending[i].PlayerId).Append(',').Append('"').Append("name").Append('"').Append(':');
                AppendJsonString(sb, pending[i].Name);
                sb.Append(',').Append('"').Append("reason").Append('"').Append(':');
                AppendJsonString(sb, pending[i].PendingResyncReason);
                sb.Append(',').Append('"').Append("automatic").Append('"').Append(':')
                    .Append(pending[i].PendingResyncAutomatic ? "true" : "false")
                    .Append('}');
            }
            sb.Append(']');

            lock (_chatLock)
            {
                _pendingResyncsSig = signature;
                _pendingResyncsJson = sb.ToString();
            }
        }

        public void ApproveResyncFromUi(int playerId)
        {
            if (!_session.ApproveResyncRequest(playerId, NowMs))
                _log.Warn(LogTopic.Session, "Ignored approval for unknown resync request from #" +
                    playerId + ".");
            RefreshPendingResyncsJson();
        }

        public void DeclineResyncFromUi(int playerId)
        {
            if (!_session.DeclineResyncRequest(playerId))
                _log.Warn(LogTopic.Session, "Ignored decline for unknown resync request from #" +
                    playerId + ".");
            RefreshPendingResyncsJson();
        }
    }
}
