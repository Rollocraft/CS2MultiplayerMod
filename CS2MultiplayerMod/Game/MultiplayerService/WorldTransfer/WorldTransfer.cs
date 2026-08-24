using System;
using System.IO;
using System.Threading.Tasks;
using Colossal;
using Colossal.IO.AssetDatabase;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using Game;
using Game.Assets;
using Game.PSI.PdxSdk;
using Game.SceneFlow;
using Game.Settings;
using Game.UI;
using Game.UI.Menu;
using Unity.Entities;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        private const string WorldSnapshotName = "_CS2MP_HostWorldSnapshot";
        internal const string WorldSnapshotFileName = WorldSnapshotName + ".cok";

        private long _deferredMapTransferId;
        private byte[] _deferredMapData;

        /// <summary>
        /// Serialize one authoritative world snapshot in an isolated temporary database. This
        /// deliberately avoids AutoSaveSystem: multiplayer snapshots are transport artifacts,
        /// not user autosaves, and must never participate in the game's retention pruning.
        /// </summary>
        internal async Task<byte[]> CreateWorldSnapshot(World world)
        {
            byte[] snapshot = null;
            await TaskManager.instance.EnqueueTask(
                SaveHelpers.kSaveLoadTaskName,
                async () => { snapshot = await SaveWorldSnapshot(world); },
                1);

            if (snapshot == null || snapshot.Length == 0)
                throw new InvalidOperationException("The game produced no world snapshot data.");
            return snapshot;
        }

        private async Task<byte[]> SaveWorldSnapshot(World world)
        {
            if (_session.Role != SessionRole.Host || _session.Status != SessionStatus.Connected)
                throw new InvalidOperationException("Only a connected host can create a world snapshot.");

            MenuUISystem menu = world != null
                ? world.GetExistingSystemManaged<MenuUISystem>()
                : null;
            GameManager manager = GameManager.instance;
            if (menu == null || manager == null)
                throw new InvalidOperationException("The active game save systems are unavailable.");

            UserState userState = manager.settings.userState;
            SaveGameMetadata previousLastSave = userState.lastSaveGameMetadata;
            SaveInfo previousLastSaveInfo = previousLastSave != null
                ? previousLastSave.target
                : null;
            ILocalAssetDatabase snapshotDatabase = AssetDatabase.GetTransient();

            try
            {
                SaveInfo saveInfo = menu.GetSaveInfo(autoSave: false);
                bool completed = await manager.Save(
                    WorldSnapshotName,
                    saveInfo,
                    snapshotDatabase,
                    (ScreenCaptureHelper.AsyncRequest)null);
                if (!completed)
                    throw new InvalidOperationException("The game did not complete the world snapshot save.");

                PackageAsset package;
                AssetDataPath packagePath = SaveHelpers.GetAssetDataPath<SaveGameMetadata>(
                    snapshotDatabase, WorldSnapshotName);
                if (!snapshotDatabase.Exists<PackageAsset>(packagePath, out package) || package == null)
                    throw new InvalidOperationException("The game did not create the world snapshot package.");

                byte[] data = ReadWorldSnapshotPackage(package);
                _log.Info("[MP] Prepared isolated recovery snapshot '" +
                          WorldSnapshotFileName + "' (" + (data.Length / 1024) + " KB).");
                return data;
            }
            finally
            {
                try
                {
                    // GameManager.Save always updates Continue Game, even for a temporary target.
                    // Put the player's previous save back before destroying that target database.
                    userState.lastSaveGameMetadata = previousLastSave;
                    userState.ApplyAndSave();
                    if (previousLastSaveInfo != null)
                        Launcher.SaveLastSaveMetadata(previousLastSaveInfo);
                    else
                        Launcher.DeleteLastSaveMetadata();
                }
                finally
                {
                    snapshotDatabase.MarkForDeletion();
                    snapshotDatabase.Dispose();
                }
            }
        }

        private static byte[] ReadWorldSnapshotPackage(PackageAsset package)
        {
            using (Stream input = package.GetReadStream())
            {
                long length = input.Length;
                if (length <= 0)
                    throw new InvalidDataException("The world snapshot package is empty.");
                if (length > MaxSaveBlobBytes)
                    throw new InvalidDataException("The world snapshot exceeds the transfer limit.");

                var data = new byte[(int)length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int read = input.Read(data, offset, data.Length - offset);
                    if (read <= 0)
                        throw new EndOfStreamException("The world snapshot package ended unexpectedly.");
                    offset += read;
                }
                return data;
            }
        }

        /// <summary>Queue one already-read snapshot for one participant, tagged with its epoch.</summary>
        internal void StreamWorldSnapshot(ConnectionId target, long epoch, byte[] data,
            string saveName)
        {
            if (_session.Role != SessionRole.Host || target.IsNone || data == null || epoch <= 0)
                return;
            _session.SendBlobTo(target, MapChannel, epoch, data);
            _log.Info("[MP] Queued recovery snapshot '" + (saveName ?? "<save>") + "' (" +
                      (data.Length / 1024) + " KB) for " + DescribeWorldTarget(target) +
                      " in epoch " + epoch + ".");
        }

        private void LoadReceivedMap(long transferId, byte[] data)
        {
            if (!_worldSyncBarrierActive || transferId <= 0 || transferId != _activeWorldSyncEpoch)
            {
                _log.Warn("[MP] Ignoring map transfer " + transferId +
                          ": active world-sync epoch is " +
                          (_worldSyncBarrierActive ? _activeWorldSyncEpoch.ToString() : "none") + ".");
                return;
            }

            // GameManager.Load is not part of the game's serialized SaveLoadGame task
            // queue. Hold the received replacement until a user-requested local copy has
            // finished, otherwise the load could tear down the world while it is saving.
            if (ClientWorldSaveInProgress)
            {
                _deferredMapTransferId = transferId;
                _deferredMapData = data;
                _log.Info("[MP] World-sync map received while a local copy is saving; " +
                          "installation will continue after that save completes.");
                return;
            }

            InstallReceivedMap(transferId, data);
        }

        private void PumpDeferredReceivedMap()
        {
            if (_deferredMapData == null || ClientWorldSaveInProgress) return;

            long transferId = _deferredMapTransferId;
            byte[] data = _deferredMapData;
            _deferredMapTransferId = 0;
            _deferredMapData = null;

            if (!_worldSyncBarrierActive ||
                _session.Role != SessionRole.Client ||
                transferId <= 0 ||
                transferId != _activeWorldSyncEpoch)
            {
                _log.Warn("[MP] Discarding a deferred map because its world-sync epoch is no longer active.");
                return;
            }

            _log.Info("[MP] Local world copy finished; installing the deferred host map.");
            InstallReceivedMap(transferId, data);
        }

        private void InstallReceivedMap(long transferId, byte[] data)
        {
            // The completed blob is the causal cut: commands received before it are represented by
            // the save, while every later command must survive the ECS world replacement.
            _log.Info("[MP] Map blob delivered to game layer (" +
                      (data != null ? data.Length / 1024 : 0) + " KB); staging and loading.");
            Diagnostics.FlightRecorder.Note("world blob received " + (data != null ? data.Length >> 10 : 0) + " KB; reloading world");
            // Purge every sync inbox before the reload: queued commands describe the pre-reload
            // world and would apply stale edits (or reference vanished entities) on the new one.
            Sync.Infrastructure.SyncInbox.DrainAll();
            SetPhase(ClientWorldPhase.LoadingMap);
            if (!JoinMapLoader.StageAndLoad(data, _log))
            {
                // Defined, recoverable state instead of a half-connected limbo.
                SetPhase(ClientWorldPhase.WaitingForMap);
                _session.SendWorldSyncStage(_activeWorldSyncEpoch, WorldSyncStage.Failed);
                _log.Warn("[MP] Could not auto-load the host world. Still connected - use /sync to " +
                          "request it again, or load '" + JoinMapLoader.TransientName + "' from Load Game.");
            }
            else
            {
                // From this point onward a disconnect must unload this disposable host
                // world. The preload callback normally marks it synchronously as well;
                // keeping the marker here covers runtimes which publish that callback later.
                MarkClientHostWorldActive();
            }
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
