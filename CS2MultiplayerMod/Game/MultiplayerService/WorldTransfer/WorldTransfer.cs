using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Colossal;
using Colossal.IO.AssetDatabase;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
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

        private static readonly SemaphoreSlim SnapshotSaveGate = new SemaphoreSlim(1, 1);
        private long _deferredMapTransferId;
        private byte[] _deferredMapData;

        /// <summary>
        /// Serialize one authoritative world snapshot in an isolated temporary database. This
        /// deliberately avoids AutoSaveSystem: multiplayer snapshots are transport artifacts,
        /// not user autosaves, and must never participate in the game's retention pruning.
        /// </summary>
        internal async Task<BlobSource> CreateWorldSnapshot(World world, long epoch, CancellationToken cancellation)
        {
            await SnapshotSaveGate.WaitAsync(cancellation);
            BlobSource snapshot = null;
            try
            {
                await TaskManager.instance.EnqueueTask(
                    SaveHelpers.kSaveLoadTaskName,
                    async () => { snapshot = await SaveWorldSnapshot(world, epoch, cancellation); },
                    1);
                ValidateSnapshotEpoch(world, epoch, cancellation);
                if (snapshot == null || snapshot.Length == 0)
                    throw new InvalidOperationException("The game produced no world snapshot data.");
                return snapshot;
            }
            catch { snapshot?.Dispose(); throw; }
            finally { SnapshotSaveGate.Release(); }
        }

        private void ValidateSnapshotEpoch(World world, long epoch, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (world == null || !world.IsCreated || !_worldSyncBarrierActive ||
                _activeWorldSyncEpoch != epoch || _session.Role != SessionRole.Host ||
                _session.Status != SessionStatus.Connected)
                throw new OperationCanceledException("The snapshot world or epoch is no longer active.");
        }

        private async Task<BlobSource> SaveWorldSnapshot(World world, long epoch, CancellationToken cancellation)
        {
            ValidateSnapshotEpoch(world, epoch, cancellation);
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
                ValidateSnapshotEpoch(world, epoch, cancellation);
                if (!ReferenceEquals(manager, GameManager.instance))
                    throw new OperationCanceledException("The active game manager changed.");
                if (!completed)
                    throw new InvalidOperationException("The game did not complete the world snapshot save.");

                PackageAsset package;
                AssetDataPath packagePath = SaveHelpers.GetAssetDataPath<SaveGameMetadata>(
                    snapshotDatabase, WorldSnapshotName);
                if (!snapshotDatabase.Exists<PackageAsset>(packagePath, out package) || package == null)
                    throw new InvalidOperationException("The game did not create the world snapshot package.");

                PersistRecoveryBackup(package, epoch);
                BlobSource data = ReadWorldSnapshotPackage(package, cancellation);
                _log.Detail(LogTopic.WorldTransfer, "Prepared isolated recovery snapshot '" +
                    WorldSnapshotFileName + "' (" + (data.Length / 1024) + " KB).");
                return data;
            }
            finally
            {
                try
                {
                    // GameManager.Save always updates Continue Game, even for a temporary target.
                    // Put the player's previous save back before destroying that target database.
                    if (ReferenceEquals(manager, GameManager.instance) &&
                        ReferenceEquals(userState, manager.settings.userState))
                    {
                        userState.lastSaveGameMetadata = previousLastSave;
                        userState.ApplyAndSave();
                        if (previousLastSaveInfo != null)
                            Launcher.SaveLastSaveMetadata(previousLastSaveInfo);
                        else
                            Launcher.DeleteLastSaveMetadata();
                    }
                }
                finally
                {
                    snapshotDatabase.MarkForDeletion();
                    snapshotDatabase.Dispose();
                }
            }
        }

        /// <summary>Keeps a local copy of a recovery snapshot before sending it.</summary>
        private void PersistRecoveryBackup(PackageAsset package, long epoch)
        {
            try
            {
                string root = Colossal.PSI.Environment.EnvPath.kUserDataPath;
                if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("user-data path unavailable");
                string dir = Path.Combine(root, "CS2MP-backups");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "host-recovery-" + epoch + "-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".cok");
                using (Stream input = package.GetReadStream())
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    input.CopyTo(output);
                _log.Event(LogTopic.WorldTransfer, "Recovery backup created: " + path);
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.WorldTransfer, "Recovery backup could not be created: " + ex.Message);
            }
        }

        private static BlobSource ReadWorldSnapshotPackage(PackageAsset package, CancellationToken cancellation)
        {
            using (Stream input = package.GetReadStream())
            {
                long length = input.Length;
                if (length <= 0)
                    throw new InvalidDataException("The world snapshot package is empty.");
                if (length > MaxSaveBlobBytes)
                    throw new InvalidDataException("The world snapshot exceeds the transfer limit.");

                string path = Path.Combine(Path.GetTempPath(), "cs2mp-" + Guid.NewGuid().ToString("N") + ".snapshot");
                var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.Read, 65536, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
                try
                {
                    var buffer = new byte[65536];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int read = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                        if (read <= 0) throw new EndOfStreamException("The world snapshot package ended unexpectedly.");
                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    output.Flush();
                    output.Position = 0;
                    return new BlobSource(output);
                }
                catch { output.Dispose(); throw; }
            }
        }

        /// <summary>Queue one already-read snapshot for one participant, tagged with its epoch.</summary>
        internal void StreamWorldSnapshot(ConnectionId target, long epoch, BlobSource data,
            string saveName)
        {
            if (_session.Role != SessionRole.Host || target.IsNone || data == null || epoch <= 0)
                return;
            _session.SendBlobTo(target, MapChannel, epoch, data);
            _log.Detail(LogTopic.WorldTransfer, "Queued recovery snapshot '" +
                (saveName ?? "<save>") + "' (" + (data.Length / 1024) + " KB) for " +
                DescribeWorldTarget(target) + " in epoch " + epoch + ".");
        }

        private void LoadReceivedMap(long transferId, byte[] data)
        {
            if (!_worldSyncBarrierActive || transferId <= 0 || transferId != _activeWorldSyncEpoch)
            {
                _log.Warn(LogTopic.WorldTransfer, "Ignoring map transfer " + transferId +
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
                _log.Detail(LogTopic.WorldTransfer,
                    "World-sync map received while a local copy is saving; " +
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
                _log.Warn(LogTopic.WorldTransfer,
                    "Discarding a deferred map because its world-sync epoch is no longer active.");
                return;
            }

            _log.Detail(LogTopic.WorldTransfer,
                "Local world copy finished; installing the deferred host map.");
            InstallReceivedMap(transferId, data);
        }

        private void InstallReceivedMap(long transferId, byte[] data)
        {
            // The completed blob is the causal cut: commands received before it are represented by
            // the save, while every later command must survive the ECS world replacement.
            _log.Event(LogTopic.WorldTransfer, "Map blob delivered to game layer (" +
                (data != null ? data.Length / 1024 : 0) + " KB); staging and loading.");
            // Purge every sync inbox before the reload: queued commands describe the pre-reload
            // world and would apply stale edits (or reference vanished entities) on the new one.
            Sync.Infrastructure.SyncInbox.DrainAll();
            Diagnostics.ResyncArbiter.Reset();
            SetPhase(ClientWorldPhase.LoadingMap);
            if (!JoinMapLoader.StageAndLoad(data, _log))
            {
                // Defined, recoverable state instead of a half-connected limbo.
                SetPhase(ClientWorldPhase.WaitingForMap);
                _session.SendWorldSyncStage(_activeWorldSyncEpoch, WorldSyncStage.Failed);
                _log.Warn(LogTopic.WorldTransfer,
                    "Could not auto-load the host world. Still connected - use /sync to " +
                    "request it again, or load '" + JoinMapLoader.TransientName +
                    "' from Load Game.");
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
            player.Hover = state.Hover;
            player.LastUpdateMs = _clock.ElapsedMilliseconds;
        }

    }
}
