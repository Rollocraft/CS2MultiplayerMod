using System;
using System.IO;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        /// <summary>Stream the newest existing save - explicit fallback when a fresh save failed.</summary>
        public void StreamWorld(ConnectionId target) => StreamWorld(target, DateTime.MinValue);

        /// <summary>
        /// Host: stream a savegame to one peer (<paramref name="target"/>) or all
        /// (<see cref="ConnectionId.None"/>). When <paramref name="writtenAfterUtc"/> is given, only a
        /// save from the just-completed save task qualifies. Called by <see cref="WorldResyncSystem"/>.
        /// </summary>
        public void StreamWorld(ConnectionId target, DateTime writtenAfterUtc)
        {
            if (_session.Role != SessionRole.Host) return;

            string targetText = DescribeWorldTarget(target);
            _log.Info("[MP] World stream requested for " + targetText +
                      (writtenAfterUtc > DateTime.MinValue
                          ? " using save written after " + writtenAfterUtc.ToString("O") + "."
                          : " using newest available save."));

            string save = FindNewestSave(writtenAfterUtc);
            if (save == null)
            {
                if (writtenAfterUtc > DateTime.MinValue)
                    _log.Warn("[MP] The completed save task produced no new .cok on disk; " +
                              "not streaming stale state to " + targetText + ". (/sync will retry.)");
                else
                    _log.Warn("[MP] No savegame to stream yet for " + targetText + ".");
                return;
            }

            try
            {
                var info = new FileInfo(save);
                _log.Info("[MP] Preparing world stream to " + targetText +
                          ": save='" + info.Name + "'" +
                          " size=" + (info.Length / 1024) + " KB" +
                          " modifiedUtc=" + info.LastWriteTimeUtc.ToString("O") + ".");

                byte[] bytes = File.ReadAllBytes(save);
                if (bytes.Length == 0 || bytes.Length > MaxSaveBlobBytes)
                {
                    _log.Warn("[MP] Save '" + Path.GetFileName(save) + "' has implausible size " +
                              bytes.Length + "; not streaming to " + targetText + ".");
                    return;
                }
                if (target.IsNone) _session.SendBlob(MapChannel, bytes);
                else _session.SendBlobTo(target, MapChannel, bytes);
                _log.Info("[MP] Streamed world '" + Path.GetFileName(save) + "' (" + (bytes.Length / 1024) + " KB) to " +
                          targetText + ".");
            }
            catch (Exception ex)
            {
                _log.Error("[MP] Failed to read/send save to " + targetText + ": " + ex.Message);
            }
        }

        private void LoadReceivedMap(byte[] data)
        {
            // A second world can land while the previous one is still loading (an impatient
            // /sync, a broadcast behind a join stream). Restaging now would delete the .cok
            // the running load is reading from disk; hold the newest blob instead and load
            // it once the current load settles (see PumpWorldPhase).
            if (_phase == ClientWorldPhase.LoadingMap)
            {
                _pendingWorldBlob = data;
                _log.Info("[MP] Another host world arrived mid-load (" +
                          (data != null ? data.Length / 1024 : 0) + " KB); it loads when the current load finishes.");
                Diagnostics.FlightRecorder.Note("world blob queued behind in-flight load");
                return;
            }

            StartWorldLoad(data);
        }

        private void StartWorldLoad(byte[] data)
        {
            _log.Info("[MP] Map blob delivered to game layer (" +
                      (data != null ? data.Length / 1024 : 0) + " KB); staging and loading.");
            Diagnostics.FlightRecorder.Note("world blob received " + (data != null ? data.Length >> 10 : 0) + " KB; reloading world");
            // Purge every sync inbox before the reload: queued commands describe the pre-reload
            // world and would apply stale edits (or reference vanished entities) on the new one.
            Sync.Infrastructure.SyncInbox.DrainAll();
            SetPhase(ClientWorldPhase.LoadingMap);
            // Restart load observation by hand: for a queued blob the phase never left
            // LoadingMap, so SetPhase alone would keep the previous load's state.
            _sawLoading = false;
            _phaseChangedMs = NowMs;
            if (!JoinMapLoader.StageAndLoad(data, _log))
            {
                // Defined, recoverable state instead of a half-connected limbo.
                SetPhase(ClientWorldPhase.WaitingForMap);
                _log.Warn("[MP] Could not auto-load the host world. Still connected - use /sync to " +
                          "request it again, or load '" + JoinMapLoader.TransientName + "' from Load Game.");
            }
        }

        private static string FindNewestSave(DateTime writtenAfterUtc)
        {
            string dir = JoinMapLoader.SavesDirectory();
            if (dir == null || !Directory.Exists(dir)) return null;

            string newest = null;
            DateTime newestTime = writtenAfterUtc;
            foreach (string file in Directory.GetFiles(dir, "*.cok", SearchOption.AllDirectories))
            {
                // Never echo a transient join-world back out as the host's map.
                if (Path.GetFileNameWithoutExtension(file) == JoinMapLoader.TransientName) continue;
                DateTime t = File.GetLastWriteTimeUtc(file);
                if (t <= newestTime) continue;
                newestTime = t;
                newest = file;
            }
            return newest;
        }

        private string DescribeWorldTarget(ConnectionId target)
        {
            if (target.IsNone) return "all clients";

            foreach (Peer peer in _session.Peers)
            {
                if (peer.Connection != target) continue;
                return peer.ToString();
            }

            return target.ToString();
        }

        private void RecordRemotePlayer(PlayerStateMessage state)
        {
            // Ignore our own echo; we already know where we are.
            if (state.PlayerId == _session.LocalPlayerId) return;

            var player = _remotePlayers.GetOrAdd(state.PlayerId, id => new RemotePlayer { PlayerId = id });
            player.X = state.PosX;
            player.Y = state.PosY;
            player.Z = state.PosZ;
            player.EyeX = state.EyeX;
            player.EyeY = state.EyeY;
            player.EyeZ = state.EyeZ;
            player.Yaw = state.Yaw;
            player.LastUpdateMs = _clock.ElapsedMilliseconds;
        }

    }
}
