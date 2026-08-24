using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        private string _pendingJoinsJson = "[]";
        private string _pendingJoinsSig = "";

        /// <summary>
        /// Host-side joins waiting for manual approval, as a JSON array for the hub's
        /// approval prompt: <c>[{"id":2,"name":"Alice"}, ...]</c>. Rebuilt only when the set
        /// changes, so the per-frame UI binding usually compares an unchanged string. Always
        /// "[]" on a client.
        /// </summary>
        public string PendingJoinsJson { get { lock (_chatLock) return _pendingJoinsJson; } }

        /// <summary>
        /// Scan the session's pending joins and refresh <see cref="PendingJoinsJson"/> if it
        /// changed. Called every tick from <see cref="Update"/> (host-only work); a cheap
        /// id/name signature keeps it from re-serializing while nothing changes.
        /// </summary>
        private void RefreshPendingJoinsJson()
        {
            if (_session.Role != SessionRole.Host)
            {
                if (_pendingJoinsSig.Length != 0)
                    lock (_chatLock) { _pendingJoinsJson = "[]"; _pendingJoinsSig = ""; }
                return;
            }

            var pending = new List<Peer>();
            foreach (Peer peer in _session.PendingJoins) pending.Add(peer);
            // Stable order (ascending id) so the signature and the rendered list do not
            // flicker with the peer dictionary's iteration order.
            pending.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));

            var sig = new System.Text.StringBuilder();
            for (int i = 0; i < pending.Count; i++)
                sig.Append(pending[i].PlayerId).Append(':').Append(pending[i].Name).Append('|');
            string signature = sig.ToString();
            if (signature == _pendingJoinsSig) return;

            var sb = new System.Text.StringBuilder(pending.Count * 40 + 2);
            sb.Append('[');
            for (int i = 0; i < pending.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(pending[i].PlayerId).Append(",\"name\":");
                AppendJsonString(sb, pending[i].Name);
                sb.Append('}');
            }
            sb.Append(']');

            lock (_chatLock)
            {
                _pendingJoinsSig = signature;
                _pendingJoinsJson = sb.ToString();
            }
        }

        /// <summary>Admit a join the host accepted in the approval prompt.</summary>
        public void ApproveJoinFromUi(int playerId)
        {
            if (!_session.ApproveJoin(playerId, NowMs))
                _log.Warn("[MP] Ignored approve for unknown pending join #" + playerId + ".");
            RefreshPendingJoinsJson();
        }

        /// <summary>Refuse a join the host declined in the approval prompt.</summary>
        public void DeclineJoinFromUi(int playerId)
        {
            if (!_session.DeclineJoin(playerId))
                _log.Warn("[MP] Ignored decline for unknown pending join #" + playerId + ".");
            RefreshPendingJoinsJson();
        }
    }
}
