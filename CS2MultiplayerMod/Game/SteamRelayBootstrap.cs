using System;
using System.IO;
using System.Reflection;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Brings up the Steam relay backend, which ships as its own assembly next to the
    /// mod.
    ///
    /// The game resolves every assembly reference a mod declares before it loads it and
    /// refuses the whole mod when one is missing. Steamworks ships only with the Steam
    /// build, so while the relay code sat in the mod assembly, Microsoft Store and Game
    /// Pass copies rejected the mod outright with "com.rlabrecque.steamworks.net" as a
    /// missing dependency - nothing of it ran, multiplayer included. Keeping that code
    /// in an assembly nothing links at build time means the game never has to resolve
    /// Steamworks; this loads it by hand, and only where Steam actually exists.
    ///
    /// When it is not loaded, <see cref="RelayProvider.Current"/> stays null, which
    /// reads everywhere as "no relay on this machine" and leaves direct connections
    /// untouched.
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

                // The probe runs before the assignment: a backend that answers with an
                // exception must not be left registered as if it worked.
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

        /// <summary>
        /// Whether this installation has Steamworks at all. The game loads it during its
        /// own startup on a Steam copy, so the loaded set usually answers this; the
        /// explicit load covers the case where it has not been touched yet.
        /// </summary>
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

            // Loaded from bytes, the way the game loads mods: the file stays unlocked and
            // the symbols come along, so relay faults still report file and line.
            string symbols = Path.ChangeExtension(path, ".pdb");
            Assembly backend = File.Exists(symbols)
                ? Assembly.Load(File.ReadAllBytes(path), File.ReadAllBytes(symbols))
                : Assembly.Load(File.ReadAllBytes(path));

            return (IRelayProvider)Activator.CreateInstance(backend.GetType(ProviderType, true));
        }

        /// <summary>
        /// The backend is built against this assembly, and this one was itself loaded
        /// from bytes - it has no file on disk to be found by. Handing it back by name
        /// is what keeps both sides talking about the same <see cref="IRelayProvider"/>.
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
