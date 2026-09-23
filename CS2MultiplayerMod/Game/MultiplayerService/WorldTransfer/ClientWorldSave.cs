using System;
using System.Threading.Tasks;
using Colossal;
using Colossal.IO.AssetDatabase;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using Game;
using Game.Assets;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;
using Unity.Entities;
using UnityEngine;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        // The native save-name length; AssetDataPath escapes the filename.
        private const int ClientWorldSaveNameMaxLength = 85;
        private const string SaveStatusIdle = "idle";
        private const string SaveStatusSaving = "saving";
        private const string SaveStatusSaved = "saved";
        private const string SaveStatusInvalid = "invalid";
        private const string SaveStatusExists = "exists";
        private const string SaveStatusUnavailable = "unavailable";
        private const string SaveStatusFailed = "failed";

        private Task _clientWorldSaveTask;
        private string _clientWorldSaveStatus = SaveStatusIdle;
        private string _clientWorldSaveName = "";
        private string _clientWorldSaveFailureStatus;

        /// <summary>Status token consumed by the naming dialog.</summary>
        public string ClientWorldSaveStatus => _clientWorldSaveStatus;

        /// <summary>The normalized name of the current or most recent copy request.</summary>
        public string ClientWorldSaveName => _clientWorldSaveName;

        /// <summary>World replacement and a disconnect's return to the menu wait for this.</summary>
        public bool ClientWorldSaveInProgress =>
            _clientWorldSaveTask != null && !_clientWorldSaveTask.IsCompleted;

        /// <summary>Only a fully joined client may keep the currently loaded host world.</summary>
        public bool CanSaveClientWorld
        {
            get
            {
                if (ClientWorldSaveInProgress ||
                    _session.Role != SessionRole.Client ||
                    _session.Status != SessionStatus.Connected ||
                    _phase != ClientWorldPhase.InSession ||
                    _worldSyncBarrierActive ||
                    _currentWorld == null)
                    return false;

                try
                {
                    GameManager manager = GameManager.instance;
                    return manager != null && manager.gameMode.IsGame() && !manager.isGameLoading;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Queue a fresh, permanent local save of the client's current world.</summary>
        public void SaveClientWorldFromUi(string requestedName)
        {
            if (!CanSaveClientWorld)
            {
                _clientWorldSaveStatus = SaveStatusUnavailable;
                return;
            }

            string saveName = (requestedName ?? "").Trim();
            _clientWorldSaveName = saveName;
            if (!IsValidClientWorldSaveName(saveName))
            {
                _clientWorldSaveStatus = SaveStatusInvalid;
                return;
            }

            try
            {
                if (ClientWorldSaveExists(saveName))
                {
                    // Never overwrite a player's existing save.
                    _clientWorldSaveStatus = SaveStatusExists;
                    return;
                }

                World world = _currentWorld;
                _clientWorldSaveFailureStatus = null;
                _clientWorldSaveStatus = SaveStatusSaving;
                _clientWorldSaveTask = TaskManager.instance.EnqueueTask(
                    SaveHelpers.kSaveLoadTaskName,
                    () => SaveClientWorld(world, saveName),
                    1);
                _log.Detail(LogTopic.WorldTransfer,
                    "Saving a permanent local copy of the client world as '" + saveName + "'.");
            }
            catch (Exception ex)
            {
                _clientWorldSaveTask = null;
                _clientWorldSaveStatus = SaveStatusFailed;
                _log.Error(LogTopic.WorldTransfer, "Could not start the local client-world save: " +
                    ex.Message);
            }
        }

        /// <summary>Clear an old result when the player opens the naming dialog again.</summary>
        public void ResetClientWorldSaveStatusFromUi()
        {
            if (ClientWorldSaveInProgress) return;
            _clientWorldSaveStatus = SaveStatusIdle;
            _clientWorldSaveName = "";
            _clientWorldSaveFailureStatus = null;
        }

        private static bool IsValidClientWorldSaveName(string saveName)
        {
            if (string.IsNullOrEmpty(saveName) || saveName.Length > ClientWorldSaveNameMaxLength)
                return false;

            // The transient marker makes a save eligible for session cleanup.
            if (saveName.IndexOf(JoinMapLoader.TransientName,
                    StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            for (int i = 0; i < saveName.Length; i++)
                if (char.IsControl(saveName[i])) return false;
            return true;
        }

        private static bool ClientWorldSaveExists(string saveName)
        {
            return AssetDatabase.user.Exists<PackageAsset>(
                SaveHelpers.GetAssetDataPath<SaveGameMetadata>(AssetDatabase.user, saveName),
                out PackageAsset ignored);
        }

        private async Task SaveClientWorld(World world, string saveName)
        {
            // Recheck inside the serialized save task.
            if (ClientWorldSaveExists(saveName))
            {
                _clientWorldSaveFailureStatus = SaveStatusExists;
                throw new InvalidOperationException("A local save with this name already exists.");
            }

            MenuUISystem menu = world != null
                ? world.GetExistingSystemManaged<MenuUISystem>()
                : null;
            GameManager manager = GameManager.instance;
            if (menu == null || manager == null)
                throw new InvalidOperationException("The active game save systems are unavailable.");

            SaveInfo saveInfo = menu.GetSaveInfo(autoSave: false);
            RenderTexture preview = null;
            try
            {
                try
                {
                    Camera camera = Camera.main;
                    if (camera != null)
                    {
                        preview = ScreenCaptureHelper.CreateRenderTarget(
                            "CS2MP-ClientWorldCopy", 680, 383);
                        ScreenCaptureHelper.CaptureScreenshot(
                            camera, preview, new MenuHelpers.SaveGamePreviewSettings());
                    }
                }
                catch (Exception ex)
                {
                    if (preview != null)
                    {
                        UnityEngine.Object.Destroy(preview);
                        preview = null;
                    }
                    _log.Warn(LogTopic.WorldTransfer,
                        "Could not capture a preview for the local world copy: " + ex.Message);
                }

                bool completed = preview != null
                    ? await manager.Save(saveName, saveInfo, AssetDatabase.user, preview)
                    : await manager.Save(saveName, saveInfo, AssetDatabase.user,
                        (ScreenCaptureHelper.AsyncRequest)null);

                if (!completed || !ClientWorldSaveExists(saveName))
                    throw new InvalidOperationException(
                        "The game did not create the requested local save package.");
            }
            finally
            {
                if (preview != null) UnityEngine.Object.Destroy(preview);
            }
        }

        /// <summary>Publish the asynchronous result from the main multiplayer pump.</summary>
        private void PumpClientWorldSave()
        {
            Task task = _clientWorldSaveTask;
            if (task == null || !task.IsCompleted) return;

            _clientWorldSaveTask = null;
            if (task.IsCanceled)
            {
                _clientWorldSaveStatus = SaveStatusFailed;
                _log.Warn(LogTopic.WorldTransfer,
                    "Saving the local client-world copy was canceled.");
            }
            else if (task.IsFaulted)
            {
                Exception failure = task.Exception != null
                    ? task.Exception.GetBaseException()
                    : null;
                _clientWorldSaveStatus = _clientWorldSaveFailureStatus ?? SaveStatusFailed;
                _log.Error(LogTopic.WorldTransfer, "Saving the local client-world copy failed" +
                    (failure != null ? ": " + failure.Message : "."));
            }
            else
            {
                _clientWorldSaveStatus = SaveStatusSaved;
                _log.Detail(LogTopic.WorldTransfer, "Permanent local client-world copy saved as '" +
                    _clientWorldSaveName + "'.");
            }

            _clientWorldSaveFailureStatus = null;
        }
    }
}
