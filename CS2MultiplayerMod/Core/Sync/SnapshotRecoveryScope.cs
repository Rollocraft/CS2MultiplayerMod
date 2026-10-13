using System.Collections.Generic;
using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>Host evidence affects all clients; a client fault affects only its world copy.</summary>
    public sealed class SnapshotRecoveryScope
    {
        private readonly HashSet<int> _targets = new HashSet<int>();
        private bool _all;
        public bool HasRequests => _all || _targets.Count != 0;
        public void Request(ConnectionId connection)
        {
            if (connection.IsNone) _all = true;
            else _targets.Add(connection.Value);
        }
        public void Remove(ConnectionId connection) => _targets.Remove(connection.Value);
        public List<ConnectionId> Take(IList<ConnectionId> connected)
        {
            var selected = new List<ConnectionId>();
            foreach (var connection in connected)
                if (_all || _targets.Contains(connection.Value)) selected.Add(connection);
            Clear();
            return selected;
        }
        public void Clear() { _all = false; _targets.Clear(); }
    }
}
