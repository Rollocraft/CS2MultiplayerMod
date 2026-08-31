using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.PSI.Common;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Localization;
using Game.Modding;
using Game.SceneFlow;
// Aliased because the enclosing namespace's own Mod type would otherwise win here.
using PlaysetMod = Colossal.PSI.Common.Mod;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Finds every mod other than this one that is live for the running game. Hosting and
    /// joining are both refused while any is present: nothing in the sync layer accounts
    /// for a third party changing prefabs, tools or the simulation, so one such mod on one
    /// side is enough to desync the session or crash the other player.
    ///
    /// The active Paradox Mods playset is the source of truth wherever it can be read: it
    /// tracks what the player toggles live, and it is the only source that also lists
    /// asset-only mods (maps, prop and prefab packs), which load no assembly and so are
    /// invisible to the mod loader. Only the <em>active</em> playset is read; mods sitting in
    /// the player's other playsets are not enabled for this run and are ignored.
    ///
    /// It is re-read on the rescan interval rather than once, for two reasons: the playset
    /// reads empty for the first seconds of a run (the platform reports no active playset
    /// until it has signed in and synced, which is why the game's own startup log can say
    /// "(none)"), and a mod toggled off mid-session has to clear the gate without a restart.
    ///
    /// The loaded-assembly list is only a fallback, for when no playset is ever readable
    /// (offline, or a local development install). It cannot clear until the game restarts,
    /// so a block that came from it says so.
    /// </summary>
    internal static class ModsCheck
    {
        /// <summary>
        /// This mod's Paradox Mods id (Properties/PublishConfiguration.xml). Recognises our
        /// own playset entry when it carries no local path to match on.
        /// </summary>
        private const string OwnPlatformId = "150432";

        /// <summary>Names listed before the rest collapse into a "+N more" tail.</summary>
        private const int MaxNamesListed = 6;

        /// <summary>Cap per name, so one absurdly long mod title cannot flood the UI.</summary>
        private const int MaxNameLength = 64;

        /// <summary>
        /// The blocking state is read from a getter binding on every UI frame, and a scan
        /// walks the loaded mod list, so results are held for this long in between.
        /// </summary>
        private const long RescanMilliseconds = 5000;

        /// <summary>Marker the fault string carries so the status screen can classify it.</summary>
        public const string FaultMarker = "Unsupported mods enabled:";

        private static readonly object Gate = new object();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static string[] _cached = Array.Empty<string>();
        private static long _scannedAt = long.MinValue;
        private static bool _playsetEverPopulated;
        private static bool _restartRequired;
        private static string _ownFolder;
        private static bool _scanWarned;

        /// <summary>
        /// Display names of the other live mods, sorted, or an empty array when the only
        /// mod running is this one. A scan that fails outright reports empty: a detection
        /// fault must not lock the player out of multiplayer altogether.
        /// </summary>
        public static string[] OtherModNames
        {
            get
            {
                lock (Gate)
                {
                    long now = Clock.ElapsedMilliseconds;
                    if (_scannedAt != long.MinValue && now - _scannedAt < RescanMilliseconds)
                        return _cached;

                    _scannedAt = now;
                    string[] previous = _cached;
                    _cached = Scan();
                    LogChange(previous, _cached);
                    return _cached;
                }
            }
        }

        public static bool AnyOtherMods => OtherModNames.Length > 0;

        /// <summary>
        /// Localized sentence naming the offending mods for the blocking banner, or "" when
        /// nothing else is running (which hides the banner).
        /// </summary>
        public static string BlockText(bool ignored = false)
        {
            string[] names = OtherModNames;
            if (names.Length == 0) return "";

            // Reading the names is what refreshes _restartRequired, so the order matters.
            if (ignored)
                return L10n.F(L10n.Key.UiModsIgnored, NamesText(names));

            return L10n.F(_restartRequired ? L10n.Key.UiModsBlockedRestart : L10n.Key.UiModsBlocked,
                NamesText(names));
        }

        /// <summary>
        /// English detail for the session fault and the log. Faults stay English on purpose:
        /// the status screen classifies them by substring and localizes for display.
        /// </summary>
        public static string FaultDetail()
        {
            string[] names = OtherModNames;
            return names.Length == 0 ? "" : FaultMarker + " " + NamesText(names);
        }

        /// <summary>Comma-separated names, truncated to <see cref="MaxNamesListed"/>.</summary>
        private static string NamesText(string[] names)
        {
            int listed = names.Length < MaxNamesListed ? names.Length : MaxNamesListed;
            string text = string.Join(", ", names, 0, listed);
            int rest = names.Length - listed;
            return rest > 0 ? text + " (+" + rest + ")" : text;
        }

        private static string[] Scan()
        {
            // Not before our own folder is known: a read that cannot recognise this mod's
            // own entry would list it as an offender and block multiplayer outright.
            string[] fromPlayset = Array.Empty<string>();
            bool readPlayset = !string.IsNullOrEmpty(OwnFolder()) &&
                               TryReadActivePlayset(out fromPlayset);
            if (readPlayset && fromPlayset.Length > 0) _playsetEverPopulated = true;

            // An empty read only counts once the playset has proved it reports anything at
            // all. Before that it is indistinguishable from a platform that has not finished
            // starting up, and trusting it would open the gate for the first seconds of
            // every run.
            if (readPlayset && (_playsetEverPopulated || fromPlayset.Length > 0))
            {
                Array.Sort(fromPlayset, StringComparer.OrdinalIgnoreCase);
                _restartRequired = false;
                return fromPlayset;
            }

            var names = new List<string>();
            AddLoadedMods(names, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            names.Sort(StringComparer.OrdinalIgnoreCase);
            _restartRequired = names.Count > 0;
            return names.ToArray();
        }

        /// <summary>
        /// The mods the active playset has enabled, as the game's own mod loader reads them
        /// on startup. The call resolves from the platform's local mod cache, so it neither
        /// blocks on the network nor needs the player to be online - it does wait out an
        /// asynchronous call on the calling thread, which is why the rescan interval and not
        /// the UI frame decides how often it runs. False when no backend could be asked at
        /// all, which is distinct from a backend answering "nothing enabled".
        ///
        /// The Paradox backend is reached by reflection over the instance the platform
        /// already created, deliberately without naming its assembly: a declared reference
        /// the running copy of the game cannot resolve makes it refuse to load this mod at
        /// all - the same reason the Steam relay lives in its own runtime-loaded assembly.
        /// </summary>
        private static bool TryReadActivePlayset(out string[] names)
        {
            names = Array.Empty<string>();
            try
            {
                PlatformManager platform = PlatformManager.instance;
                if (platform == null) return false;

                foreach (IModSupport backend in platform.modsBackends)
                {
                    if (backend == null) continue;

                    MethodInfo method = backend.GetType().GetMethod(
                        "GetModsInActivePlaysetSync",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (method == null) continue;

                    if (!(method.Invoke(backend, null) is IEnumerable<PlaysetMod> mods)) continue;

                    var found = new List<string>();
                    foreach (PlaysetMod mod in mods)
                    {
                        if (IsSelf(mod)) continue;
                        string name = PlaysetName(mod);
                        if (!string.IsNullOrEmpty(name)) found.Add(name);
                    }

                    names = found.ToArray();
                    return true;
                }
            }
            catch (Exception ex)
            {
                WarnOnce("active playset", ex);
            }

            return false;
        }

        private static void AddLoadedMods(List<string> names, HashSet<string> seen)
        {
            try
            {
                ModManager manager = GameManager.instance != null ? GameManager.instance.modManager : null;
                if (manager == null) return;

                foreach (ModManager.ModInfo info in manager)
                {
                    if (info == null || info.asset == null) continue;
                    if (!info.asset.isMod || !info.isLoaded) continue;
                    if (IsSelf(info)) continue;
                    Add(names, seen, LoadedName(info));
                }
            }
            catch (Exception ex)
            {
                WarnOnce("loaded mods", ex);
            }
        }

        private static void Add(List<string> names, HashSet<string> seen, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (seen.Add(name)) names.Add(name);
        }

        private static bool IsSelf(PlaysetMod mod)
        {
            if (string.Equals(mod.id, OwnPlatformId, StringComparison.Ordinal)) return true;
            return SharesOwnFolder(mod.path);
        }

        private static bool IsSelf(ModManager.ModInfo info)
        {
            string own = typeof(Mod).Assembly.GetName().Name;
            if (string.Equals(info.asset.name, own, StringComparison.OrdinalIgnoreCase)) return true;
            return SharesOwnFolder(info.asset.path);
        }

        /// <summary>
        /// Whether a reported path belongs to this mod. Compared both ways round because
        /// the playset reports a mod's root folder while our own assembly can sit in a
        /// subfolder of it.
        /// </summary>
        private static bool SharesOwnFolder(string path)
        {
            string own = OwnFolder();
            if (string.IsNullOrEmpty(own) || string.IsNullOrEmpty(path)) return false;

            string other = Normalize(path);
            if (other.Length == 0) return false;
            return IsSameOrUnder(own, other) || IsSameOrUnder(other, own);
        }

        /// <summary>
        /// Containment that stops at a path separator, so a neighbouring folder whose name
        /// merely starts with ours ("CS2MultiplayerModExtras") is not mistaken for this mod.
        /// </summary>
        private static bool IsSameOrUnder(string path, string root)
        {
            if (path.Length == root.Length)
                return string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
            return path.Length > root.Length &&
                   path[root.Length] == '/' &&
                   path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// This mod's own folder. Resolution is retried until it succeeds - the mod loader
        /// has no asset for us yet during the earliest part of startup.
        /// </summary>
        private static string OwnFolder()
        {
            if (!string.IsNullOrEmpty(_ownFolder)) return _ownFolder;

            try
            {
                ModManager manager = GameManager.instance != null ? GameManager.instance.modManager : null;
                if (manager != null &&
                    manager.TryGetExecutableAsset(typeof(Mod).Assembly, out ExecutableAsset asset) &&
                    asset != null && !string.IsNullOrEmpty(asset.path))
                    _ownFolder = Normalize(Path.GetDirectoryName(asset.path));
            }
            catch (Exception)
            {
                // Falls through to the assembly's own location below.
            }

            if (string.IsNullOrEmpty(_ownFolder))
            {
                try { _ownFolder = Normalize(Path.GetDirectoryName(typeof(Mod).Assembly.Location)); }
                catch (Exception) { }
            }

            return _ownFolder;
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace('\\', '/').TrimEnd('/');
        }

        private static string PlaysetName(PlaysetMod mod)
        {
            string name = mod.displayName;
            if (string.IsNullOrEmpty(name)) name = mod.id;
            return Clip(name);
        }

        private static string LoadedName(ModManager.ModInfo info)
        {
            string name = info.asset.name;
            if (string.IsNullOrEmpty(name)) name = info.name;
            return Clip(name);
        }

        private static string Clip(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Trim();
            return name.Length > MaxNameLength ? name.Substring(0, MaxNameLength) : name;
        }

        /// <summary>
        /// Reports the set only when it changes. Rare enough to log at info level, and it is
        /// the first thing to look at when a player says the block is naming a mod they have
        /// already turned off: it says which source is speaking.
        /// </summary>
        private static void LogChange(string[] previous, string[] current)
        {
            if (previous.Length == current.Length)
            {
                bool same = true;
                for (int i = 0; i < current.Length; i++)
                    if (!string.Equals(previous[i], current[i], StringComparison.Ordinal)) { same = false; break; }
                if (same) return;
            }

            string source = _restartRequired ? "loaded assemblies (restart to clear)" : "active playset";
            SyncLog.Detail(LogTopic.Startup,
                current.Length == 0 ? "No other mods detected - multiplayer is available." : "Other mods block multiplayer, from " +
                source + ": " + string.Join(", ", current));
        }

        private static void WarnOnce(string source, Exception ex)
        {
            if (_scanWarned) return;
            _scanWarned = true;
            SyncLog.Warn(LogTopic.Startup, "Could not read the " + source + " (" + ex.Message +
                "); other mods cannot be detected from it.");
        }
    }
}
