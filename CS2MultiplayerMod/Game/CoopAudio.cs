using System;
using System.Reflection;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Lightweight, defensive audio cue dispatcher for co-op multiplayer events
    /// (map pings, incoming chat, player join/leave).
    /// Safely resolves game audio manager endpoints dynamically without hard assembly failure.
    /// </summary>
    public static class CoopAudio
    {
        public enum CueType
        {
            Ping,
            Chat,
            Join,
            Leave,
            Build,
            Demolish
        }

        private static bool _initialized;
        private static MethodInfo _playUISoundMethod;
        private static object _audioManagerInstance;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Type audioMgrType = Type.GetType("Game.Audio.AudioManager, Game") 
                                 ?? Type.GetType("Game.UI.Menu.MenuUISystem, Game");
                if (audioMgrType != null)
                {
                    PropertyInfo instanceProp = audioMgrType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                    _audioManagerInstance = instanceProp?.GetValue(null);

                    _playUISoundMethod = audioMgrType.GetMethod("PlayUISound", BindingFlags.Public | BindingFlags.Instance)
                                      ?? audioMgrType.GetMethod("PlaySound", BindingFlags.Public | BindingFlags.Instance);
                }
            }
            catch
            {
                // Defensive guard: never fail if audio system is absent or in headless test mode
            }
        }

        public static void PlayCue(CueType cue)
        {
            try
            {
                EnsureInitialized();
                if (_playUISoundMethod != null && _audioManagerInstance != null)
                {
                    _playUISoundMethod.Invoke(_audioManagerInstance, null);
                }
            }
            catch
            {
                // Never crash or disrupt gameplay on audio dispatch
            }
        }
    }
}
