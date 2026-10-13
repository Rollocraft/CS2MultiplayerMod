using System;
using System.IO;
using System.Reflection;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Loads the Steam relay backend from its own assembly. The game refuses a mod whose declared
    /// references it cannot resolve, and only Steam copies ship Steamworks, so nothing links it at build
    /// time. Unloaded, <see cref="RelayProvider.Current"/> stays null: no relay, direct unaffected.
    /// </summary>
    internal static class SteamRelayBootstrap
    {
        private const string SteamworksAssembly = "com.rlabrecque.steamworks.net";
        private const string BackendAssembly = "CS2MultiplayerMod.Steam";
        private const string ProviderType = "CS2MultiplayerMod.Core.Networking.Steam.SteamRelayProvider";

        private static bool _resolverInstalled;

        /// <param name="modFolder">Directory the mod's own assembly was loaded from.</param>
        public static void Register(IModLogger log, string modFolder)
        {
            if (!HasSteamworks())
            {
                log.Event(LogTopic.Transport,
                    "This copy of the game ships no Steam library (Microsoft Store / Game Pass), " +
                    "so multiplayer will use direct connections only.");
                return;
            }

            try
            {
                IRelayProvider provider = LoadProvider(modFolder);
                if (provider == null)
                {
                    log.Warn(LogTopic.Transport, "The Steam relay backend (" + BackendAssembly +
                        ".dll) is not next to the mod, " +
                        "so only direct connections are available. Reinstalling the mod restores it.");
                    return;
                }

                // Probe before registering: a backend that throws must not stay registered.
                string reason = provider.UnavailableReason;
                RelayProvider.Current = provider;

                if (reason == null)
                    log.Event(LogTopic.Transport,
                        "Steam relay available; the join code for this machine is " +
                        provider.LocalJoinCode + ".");
                else
                    log.Event(LogTopic.Transport, "Steam relay not usable yet (" + reason +
                        "). Hosting can still use a direct connection.");
            }
            catch (Exception ex)
            {
                RelayProvider.Current = null;
                // Redacted: a file-load fault puts the mod's full path in the message.
                log.Warn(LogTopic.Transport, "The Steam relay backend did not load (" +
                    Diagnostics.LogPaths.Redact(ex.Message) +
                    "); multiplayer will use direct connections only.");
            }
        }

        /// <summary>A Steam copy usually has Steamworks loaded already; the explicit load covers the rest.</summary>
        private static bool HasSteamworks()
        {
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, SteamworksAssembly, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            try { return Assembly.Load(SteamworksAssembly) != null; }
            catch (Exception) { return false; }
        }

        private static IRelayProvider LoadProvider(string modFolder)
        {
            if (string.IsNullOrEmpty(modFolder)) return null;

            string path = Path.Combine(modFolder, BackendAssembly + ".dll");
            if (!File.Exists(path)) return null;

            InstallSelfResolver();

            // From bytes, like the game loads mods: the file stays unlocked and symbols keep line numbers.
            string symbols = Path.ChangeExtension(path, ".pdb");
            Assembly backend = File.Exists(symbols)
                ? Assembly.Load(File.ReadAllBytes(path), File.ReadAllBytes(symbols))
                : Assembly.Load(File.ReadAllBytes(path));

            return (IRelayProvider)Activator.CreateInstance(backend.GetType(ProviderType, true));
        }

        /// <summary>
        /// This assembly was loaded from bytes and has no file; resolving it by name keeps both sides on the
        /// same <see cref="IRelayProvider"/>.
        /// </summary>
        private static void InstallSelfResolver()
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSelf;
        }

        private static Assembly ResolveSelf(object sender, ResolveEventArgs args)
        {
            Assembly self = typeof(SteamRelayBootstrap).Assembly;
            try
            {
                return new AssemblyName(args.Name).Name == self.GetName().Name ? self : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
