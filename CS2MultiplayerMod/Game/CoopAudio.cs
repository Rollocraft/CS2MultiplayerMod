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

                if (audioMgrType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.FullName.StartsWith("Game,", StringComparison.OrdinalIgnoreCase) ||
                            asm.FullName.StartsWith("Game.", StringComparison.OrdinalIgnoreCase))
                        {
                            audioMgrType = asm.GetType("Game.Audio.AudioManager") ?? asm.GetType("Game.Audio.AudioSystem");
                            if (audioMgrType != null) break;
                        }
                    }
                }

                if (audioMgrType != null)
                {
                    PropertyInfo instanceProp = audioMgrType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                    _audioManagerInstance = instanceProp?.GetValue(null);

                    if (_audioManagerInstance == null && typeof(Unity.Entities.ComponentSystemBase).IsAssignableFrom(audioMgrType))
                    {
                        var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                        if (world != null)
                        {
                            MethodInfo getSys = typeof(Unity.Entities.World).GetMethod("GetExistingSystemManaged", new Type[0])
                                ?.MakeGenericMethod(audioMgrType);
                            _audioManagerInstance = getSys?.Invoke(world, null);
                        }
                    }

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
                    var pars = _playUISoundMethod.GetParameters();
                    if (pars.Length == 0)
                    {
                        _playUISoundMethod.Invoke(_audioManagerInstance, null);
                    }
                    else if (pars.Length == 1)
                    {
                        Type pType = pars[0].ParameterType;
                        object arg = null;
                        if (pType.IsEnum)
                        {
                            string cueName = cue.ToString();
                            foreach (var name in Enum.GetNames(pType))
                            {
                                if (name.IndexOf(cueName, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    arg = Enum.Parse(pType, name);
                                    break;
                                }
                            }
                            if (arg == null)
                            {
                                var values = Enum.GetValues(pType);
                                if (values.Length > 0) arg = values.GetValue(0);
                            }
                        }
                        else if (pType == typeof(string))
                        {
                            arg = cue.ToString();
                        }
                        _playUISoundMethod.Invoke(_audioManagerInstance, new[] { arg });
                    }
                }
            }
            catch
            {
                // Never crash or disrupt gameplay on audio dispatch
            }
        }

        public static void PlayCueAt(CueType cue, Unity.Mathematics.float3 position, Unity.Mathematics.float3 cameraPosition, float maxDist = 2500f)
        {
            if (!Sync.Infrastructure.SpatialGridCulling.IsWithinCullingDistance(cameraPosition, position, maxDist))
                return;

            PlayCue(cue);
        }
    }
}
