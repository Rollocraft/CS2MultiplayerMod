using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.PSI.Common;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Localization;
using Game.Modding;
using Game.SceneFlow;
// Aliased because the enclosing namespace's own Mod type would otherwise win here.
using PlaysetMod = Colossal.PSI.Common.Mod;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Unsupported live mods; hosting and joining are refused while any is present. The active Paradox
    /// playset is the source where readable: it tracks live toggles and lists asset-only mods. It reads
    /// empty until the platform has synced, so it is re-read on an interval. Loaded assemblies are the
    /// fallback, which only clears on restart.
    /// </summary>
    internal static class ModsCheck
    {
        /// <summary>This mod's Paradox Mods id, for a playset entry without a local path.</summary>
        private const string OwnPlatformId = "150432";

        /// <summary>Verified Paradox Mods ids; titles can change or be localized.</summary>
        private static readonly HashSet<string> SupportedPlatformIds =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "74604",  // Anarchy
                "77240",  // Find It
                "79634",  // Asset Icon Library
                "80095",  // Traffic
                "125866"  // Road Speed Adjuster
            };

        /// <summary>Exact titles and assembly names for installs without a Paradox id.</summary>
        private static readonly HashSet<string> SupportedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Anarchy",
                "Asset Icon Library",
                "AssetIconLibrary",
                "Find It",
                "FindIt",
                "Road Speed Adjuster",
                "RoadSpeedAdjuster",
                "Traffic"
            };

        /// <summary>Supported mods that act on this machine alone (search, icons); the handshake skips them.</summary>
        private static readonly HashSet<string> ClientOnlyPlatformIds =
            new HashSet<string>(StringComparer.Ordinal) { "77240", "79634" };

        private static readonly HashSet<string> ClientOnlyNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Find It", "FindIt", "Asset Icon Library", "AssetIconLibrary"
            };

        /// <summary>Names listed before the rest collapse into a "+N more" tail.</summary>
        private const int MaxNamesListed = 6;

        /// <summary>Cap per name, so one absurdly long mod title cannot flood the UI.</summary>
        private const int MaxNameLength = 64;

        /// <summary>Read every UI frame; a scan walks the mod list, so results are cached this long.</summary>
        private const long RescanMilliseconds = 5000;

        /// <summary>Marker the fault string carries so the status screen can classify it.</summary>
        public const string FaultMarker = "Unsupported mods enabled:";

        private static readonly object Gate = new object();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static string[] _cached = Array.Empty<string>();
        private static string[] _manifest = Array.Empty<string>();
        private static long _scannedAt = long.MinValue;
        private static bool _playsetEverPopulated;
        private static bool _restartRequired;
        private static string _ownFolder;
        private static bool _scanWarned;

        /// <summary>Sorted names of unsupported live mods. A failed scan reports empty rather than locking players out.</summary>
        public static string[] OtherModNames
        {
            get
            {
                lock (Gate)
                {
                    RescanIfDue();
                    return _cached;
                }
            }
        }

        public static bool AnyOtherMods => OtherModNames.Length > 0;

        /// <summary>
        /// Every other live mod as sorted <see cref="ModManifestEntry"/> lines for the handshake. Supported
        /// mods are included: on one side only they still change what the game builds.
        /// </summary>
        public static string[] Manifest
        {
            get
            {
                lock (Gate)
                {
                    RescanIfDue();
                    return _manifest;
                }
            }
        }

        private static void RescanIfDue()
        {
            long now = Clock.ElapsedMilliseconds;
            if (_scannedAt != long.MinValue && now - _scannedAt < RescanMilliseconds) return;

            _scannedAt = now;
            string[] previous = _cached;
            _cached = Scan();
            LogChange(previous, _cached);
        }

        /// <summary>Localized banner text naming the mods, or "" (hides the banner).</summary>
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

        /// <summary>English: the status screen classifies faults by substring.</summary>
        public static string FaultDetail()
        {
            string[] names = OtherModNames;
            return names.Length == 0 ? "" : FaultMarker + " " + NamesText(names);
        }

        /// <summary>For the session log whether or not they block: with the check bypassed they are the first suspect.</summary>
        public static string Summary()
        {
            string[] names = OtherModNames;
            return names.Length == 0 ? "none" : "[" + NamesText(names) + "]";
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
            // Without our own folder, this mod's entry would be listed as an offender.
            string[] fromPlayset = Array.Empty<string>();
            var manifest = new List<string>();
            bool readPlayset = !string.IsNullOrEmpty(OwnFolder()) &&
                               TryReadActivePlayset(out fromPlayset, manifest);
            if (readPlayset && fromPlayset.Length > 0) _playsetEverPopulated = true;

            // An empty read counts only once the playset has reported anything; before that the platform may
            // still be starting.
            if (readPlayset && (_playsetEverPopulated || fromPlayset.Length > 0))
            {
                Array.Sort(fromPlayset, StringComparer.OrdinalIgnoreCase);
                _restartRequired = false;
                _manifest = SortedDistinct(manifest);
                return fromPlayset;
            }

            var names = new List<string>();
            manifest.Clear();
            AddLoadedMods(names, new HashSet<string>(StringComparer.OrdinalIgnoreCase), manifest);
            _manifest = SortedDistinct(manifest);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            _restartRequired = names.Count > 0;
            return names.ToArray();
        }

        /// <summary>
        /// Mods the active playset enables, from the local mod cache (no network, but it blocks the calling
        /// thread). False when no backend was reachable. Reflection over the platform's instance: naming its
        /// assembly would make stores without it refuse to load this mod.
        /// </summary>
        private static bool TryReadActivePlayset(out string[] names, List<string> manifest)
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
                        if (!ClientOnlyPlatformIds.Contains(mod.id ?? "") && !IsClientOnlyName(mod.displayName))
                            manifest.Add(ModManifestEntry.Format(mod.id,
                                string.IsNullOrEmpty(mod.version) ? mod.userModVersion : mod.version,
                                PlaysetName(mod)));
                        if (IsOfficiallySupported(mod)) continue;
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

        private static void AddLoadedMods(List<string> names, HashSet<string> seen, List<string> manifest)
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
                    if (!IsClientOnlyName(info.asset.name) && !IsClientOnlyName(info.name))
                        manifest.Add(ModManifestEntry.Format("", LoadedVersion(info), LoadedName(info)));
                    if (IsOfficiallySupported(info)) continue;
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

        private static bool IsOfficiallySupported(PlaysetMod mod)
        {
            if (!string.IsNullOrEmpty(mod.id) && SupportedPlatformIds.Contains(mod.id))
                return true;

            return IsSupportedName(mod.displayName);
        }

        private static bool IsOfficiallySupported(ModManager.ModInfo info) =>
            IsSupportedName(info.asset.name) || IsSupportedName(info.name);

        private static bool IsClientOnlyName(string name) =>
            !string.IsNullOrEmpty(name) && ClientOnlyNames.Contains(name.Trim());

        private static string LoadedVersion(ModManager.ModInfo info)
        {
            try { return info.asset.version != null ? info.asset.version.ToString() : ""; }
            catch { return ""; }
        }

        private static string[] SortedDistinct(List<string> entries)
        {
            var set = new SortedSet<string>(entries, StringComparer.Ordinal);
            var result = new string[set.Count];
            set.CopyTo(result);
            return result;
        }

        private static bool IsSupportedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            string candidate = name.Trim();
            if (SupportedNames.Contains(candidate)) return true;

            // Some local mod loaders expose the assembly filename rather than its simple name.
            const string extension = ".dll";
            return candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                   SupportedNames.Contains(candidate.Substring(0, candidate.Length - extension.Length));
        }

        /// <summary>Both ways round: the playset reports the root folder, our assembly may be in a subfolder.</summary>
        private static bool SharesOwnFolder(string path)
        {
            string own = OwnFolder();
            if (string.IsNullOrEmpty(own) || string.IsNullOrEmpty(path)) return false;

            string other = Normalize(path);
            if (other.Length == 0) return false;
            return IsSameOrUnder(own, other) || IsSameOrUnder(other, own);
        }

        /// <summary>Stops at a path separator, so "CS2MultiplayerModExtras" is not this mod.</summary>
        private static bool IsSameOrUnder(string path, string root)
        {
            if (path.Length == root.Length)
                return string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
            return path.Length > root.Length &&
                   path[root.Length] == '/' &&
                   path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Retried until resolved: the loader has no asset for us early in startup.</summary>
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

        /// <summary>Logs the unsupported set and its source whenever it changes, ungated.</summary>
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
            SyncLog.Event(LogTopic.Startup,
                current.Length == 0
                    ? "No unsupported mods are active - multiplayer is available."
                    : "Unsupported mods are active, from the " + source + ": " +
                      string.Join(", ", current) + ".");
        }

        private static void WarnOnce(string source, Exception ex)
        {
            if (_scanWarned) return;
            _scanWarned = true;
            SyncLog.Warn(LogTopic.Startup, "Could not read the " + source + " (" + ex.Message +
                "); unsupported mods cannot be detected from it.");
        }
    }
}
