using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CS2MultiplayerMod.Core.Protocol;
using Game.Modding;
using Game.SceneFlow;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// Crash forensics for public builds: <c>Logs/CS2MP-flight.log</c>.
    ///
    /// This is the mod's second log file, and the only reason it is a second file rather than more
    /// lines in the first one is that it can promise four things the game log cannot:
    ///
    ///  * <b>The tail survives.</b> The game's logger buffers; a hard exit - a native access
    ///    violation, an out-of-memory kill, a GPU driver reset - takes the last seconds of it with
    ///    it, which is exactly the part worth reading. Every line here that matters is flushed
    ///    before <see cref="Note(string,bool)"/> returns.
    ///  * <b>The previous run survives.</b> The game log is truncated on start, so a player who
    ///    crashes and restarts before writing their report has already destroyed the evidence.
    ///    This file is only rotated after a run that ended cleanly (see <see cref="Start"/>).
    ///  * <b>It sees the whole process.</b> The Unity log callback, the unhandled-exception hook
    ///    and the unobserved-task hook record faults from the game and from other mods, which
    ///    never reach this mod's own logger but are frequently the actual cause.
    ///  * <b>It is machine-readable.</b> Every line carries run id, sequence, elapsed time and
    ///    thread, and the payloads are <c>key=value</c>, so a report can be diffed and sorted
    ///    rather than only read.
    ///
    /// It is not a parallel log with different content: <see cref="SyncLog"/> mirrors every line
    /// it writes to the game log here as well, so this file is a superset. Ask a player for this
    /// one file and nothing is missing.
    /// </summary>
    internal static class FlightRecorder
    {
        /// <summary>
        /// Flip to false to ship a build that writes no flight log: <see cref="Start"/>
        /// then opens no file and installs no exception hooks, and every <see cref="Note"/>
        /// returns before touching the lock.
        /// </summary>
        public static bool Enabled = true;

        private const int SchemaVersion = 2;
        private const long RotateBytes = 4L * 1024 * 1024;
        private const int MaxLineChars = 16 * 1024;
        private const int MaxExceptionChars = 12 * 1024;
        private const int MaxMirroredExceptions = 120;
        private const int MaxMirroredErrors = 80;
        private const int ContentPartChars = 8 * 1024;
        private const int MaxDiagnosticMods = 256;
        private const int MaxContentValueChars = 256;

        /// <summary>
        /// How many unflushed detail lines may sit in the writer's buffer. Detail is the chatty,
        /// switched-on-by-the-player level, so flushing every one of those would put a disk write
        /// in the middle of a frame; the next event, warning or fault flushes them anyway, and
        /// this bound keeps a long quiet stretch of pure detail from outliving a crash.
        /// </summary>
        private const int MaxBufferedLines = 64;

        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static Stopwatch _runClock;
        private static string _runId;
        private static long _sequence;
        private static int _mirroredExceptions;
        private static int _mirroredErrors;
        private static string _lastUnityKey;
        private static int _lastUnityRepeats;
        private static int _bufferedLines;
        private static UnityEngine.Application.LogCallback _logHook;
        private static UnityEngine.Application.LowMemoryCallback _lowMemoryHook;
        private static Action _quittingHook;
        private static UnhandledExceptionEventHandler _domainHook;
        private static EventHandler<UnobservedTaskExceptionEventArgs> _taskHook;
        private static EventHandler _processExitHook;

        public static void Start(string modVersion)
        {
            if (!Enabled) return;

            string previousRun = "none";
            lock (Gate)
            {
                if (_writer != null) return;
                try
                {
                    string dir = LogsDirectory();
                    if (dir == null) return;
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "CS2MP-flight.log");
                    previousRun = PreviousRunState(path);

                    // If the prior run ended unexpectedly, its crash tail is the most
                    // valuable evidence in the file. Never move it to .old merely because
                    // the player restarted before sending it; rotate only clean runs.
                    if (previousRun == "clean") Rotate(path);

                    // AutoFlush is off deliberately: Note() flushes anything notable itself, so
                    // the durability promise above is kept without paying a disk write for every
                    // detail line a player switched on.
                    _writer = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = false };
                    _runClock = Stopwatch.StartNew();
                    _runId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    _sequence = 0;
                    _mirroredExceptions = 0;
                    _mirroredErrors = 0;
                    _lastUnityKey = null;
                    _lastUnityRepeats = 0;
                    _bufferedLines = 0;
                }
                catch
                {
                    _writer = null; // diagnostics must never take the mod down
                    _runClock = null;
                    _runId = null;
                    return;
                }
            }

            Note("run-start schema=" + SchemaVersion +
                 " previous=" + previousRun +
                 " mod=" + Quote(modVersion) +
                 " protocol=" + ProtocolConstants.ProtocolVersion +
                 " game=" + Quote(SafeString(delegate { return UnityEngine.Application.version; })) +
                 " unity=" + Quote(SafeString(delegate { return UnityEngine.Application.unityVersion; })));
            Note(RuntimeSummary());
            Note(HardwareSummary());
            InstallHooks();
        }

        public static void Stop()
        {
            RemoveHooks();
            Note("run-end clean=true");
            lock (Gate)
            {
                if (_writer == null) return;
                try { _writer.Dispose(); } catch { }
                _writer = null;
                _runClock = null;
            }
        }

        /// <summary>
        /// Append one structured line and flush it. Safe from any thread and never throws.
        /// </summary>
        public static void Note(string line)
        {
            Note(line, true);
        }

        /// <summary>
        /// Append one structured line. Safe from any thread and never throws.
        ///
        /// <paramref name="flush"/> is the durability promise: true means the line is on disk
        /// before this returns, so it survives a hard process exit. Pass false only for chatty
        /// detail, which is flushed by the next notable line anyway - never for a fault, a
        /// milestone or anything a bug report would be read for.
        /// </summary>
        public static void Note(string line, bool flush)
        {
            if (!Enabled) return;

            lock (Gate)
            {
                if (_writer == null) return;
                try
                {
                    long sequence = ++_sequence;
                    long elapsedMs = _runClock != null ? _runClock.ElapsedMilliseconds : 0;
                    string payload = Compact(line, MaxLineChars);
                    _writer.WriteLine(
                        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) +
                        " run=" + (_runId ?? "--------") +
                        " seq=" + sequence.ToString("D6", CultureInfo.InvariantCulture) +
                        " elapsed=" + elapsedMs.ToString(CultureInfo.InvariantCulture) + "ms" +
                        " thread=" + Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) +
                        "  " + payload);

                    // A flush also commits whatever detail was buffered ahead of this line, so the
                    // context leading up to a fault reaches the disk together with the fault.
                    if (flush || ++_bufferedLines >= MaxBufferedLines)
                    {
                        _writer.Flush();
                        _bufferedLines = 0;
                    }
                }
                catch { }
            }
        }

        /// <summary>Record a caught managed exception with its bounded stack and inner exceptions.</summary>
        public static void NoteException(string context, Exception exception)
        {
            if (exception == null)
            {
                Note("managed-exception context=" + Quote(context) + " detail=\"null\"");
                return;
            }

            string detail;
            try { detail = exception.ToString(); }
            catch { detail = SafeExceptionSummary(exception); }
            Note("managed-exception context=" + Quote(context) +
                 " type=" + Quote(exception.GetType().FullName) +
                 " detail=" + Quote(Compact(detail, MaxExceptionChars)));
        }

        /// <summary>
        /// Record the active code-mod playset and simulation-relevant DLCs. This is
        /// diagnostic only and does not change the multiplayer handshake or compatibility.
        /// </summary>
        public static void RecordLoadedContent(string[] dlcs)
        {
            RecordContentList("mods", LoadedMods());
            RecordContentList("dlcs", dlcs);
        }

        /// <summary>Resource counters shared by periodic health snapshots. Never throws.</summary>
        public static string ProcessSnapshot()
        {
            long heapMb = -1;
            long workingSetMb = -1;
            long privateMb = -1;
            long virtualMb = -1;
            long cpuMs = -1;
            int threads = -1;
            int handles = -1;
            int gc0 = -1;
            int gc1 = -1;
            int gc2 = -1;

            try { heapMb = GC.GetTotalMemory(false) >> 20; } catch { }
            try { workingSetMb = Environment.WorkingSet >> 20; } catch { }
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    workingSetMb = process.WorkingSet64 >> 20;
                    privateMb = process.PrivateMemorySize64 >> 20;
                    virtualMb = process.VirtualMemorySize64 >> 20;
                    cpuMs = (long)process.TotalProcessorTime.TotalMilliseconds;
                    try { threads = process.Threads.Count; } catch { }
                    try { handles = process.HandleCount; } catch { }
                }
            }
            catch { }
            try { gc0 = GC.CollectionCount(0); } catch { }
            try { gc1 = GC.CollectionCount(1); } catch { }
            try { gc2 = GC.CollectionCount(2); } catch { }

            return "heapMB=" + Number(heapMb) +
                   " wsMB=" + Number(workingSetMb) +
                   " privateMB=" + Number(privateMb) +
                   " virtualMB=" + Number(virtualMb) +
                   " cpuMS=" + Number(cpuMs) +
                   " threads=" + Number(threads) +
                   " handles=" + Number(handles) +
                   " gc0=" + Number(gc0) +
                   " gc1=" + Number(gc1) +
                   " gc2=" + Number(gc2);
        }

        private static void InstallHooks()
        {
            try
            {
                _domainHook = delegate(object sender, UnhandledExceptionEventArgs args)
                {
                    Exception exception = args.ExceptionObject as Exception;
                    if (exception != null)
                    {
                        string detail;
                        try { detail = exception.ToString(); }
                        catch { detail = SafeExceptionSummary(exception); }
                        Note("fatal-unhandled terminating=" + args.IsTerminating +
                             " type=" + Quote(exception.GetType().FullName) +
                             " detail=" + Quote(Compact(detail, MaxExceptionChars)));
                    }
                    else
                    {
                        Note("fatal-unhandled terminating=" + args.IsTerminating +
                             " detail=" + Quote(args.ExceptionObject == null ? "null" : args.ExceptionObject.ToString()));
                    }
                };
                AppDomain.CurrentDomain.UnhandledException += _domainHook;
            }
            catch (Exception ex) { NoteHookFailure("unhandled-exception", ex); }

            try
            {
                _taskHook = delegate(object sender, UnobservedTaskExceptionEventArgs args)
                {
                    Exception exception = args != null ? args.Exception : null;
                    if (exception != null) NoteException("unobserved-task", exception);
                };
                TaskScheduler.UnobservedTaskException += _taskHook;
            }
            catch (Exception ex) { NoteHookFailure("unobserved-task", ex); }

            try
            {
                _logHook = delegate(string condition, string stackTrace, UnityEngine.LogType type)
                {
                    if (type == UnityEngine.LogType.Exception ||
                        type == UnityEngine.LogType.Error ||
                        type == UnityEngine.LogType.Assert)
                        MirrorUnityDiagnostic(condition, stackTrace, type);
                };
                UnityEngine.Application.logMessageReceivedThreaded += _logHook;
            }
            catch (Exception ex) { NoteHookFailure("unity-log", ex); }

            try
            {
                _lowMemoryHook = delegate { Note("low-memory-warning source=unity"); };
                UnityEngine.Application.lowMemory += _lowMemoryHook;
            }
            catch (Exception ex) { NoteHookFailure("low-memory", ex); }

            try
            {
                _quittingHook = delegate { Note("application-quitting"); };
                UnityEngine.Application.quitting += _quittingHook;
            }
            catch (Exception ex) { NoteHookFailure("application-quitting", ex); }

            try
            {
                _processExitHook = delegate { Note("process-exit"); };
                AppDomain.CurrentDomain.ProcessExit += _processExitHook;
            }
            catch (Exception ex) { NoteHookFailure("process-exit", ex); }
        }

        private static void RemoveHooks()
        {
            try
            {
                if (_domainHook != null) AppDomain.CurrentDomain.UnhandledException -= _domainHook;
            }
            catch { }
            _domainHook = null;

            try
            {
                if (_taskHook != null) TaskScheduler.UnobservedTaskException -= _taskHook;
            }
            catch { }
            _taskHook = null;

            try
            {
                if (_logHook != null) UnityEngine.Application.logMessageReceivedThreaded -= _logHook;
            }
            catch { }
            _logHook = null;

            try
            {
                if (_lowMemoryHook != null) UnityEngine.Application.lowMemory -= _lowMemoryHook;
            }
            catch { }
            _lowMemoryHook = null;

            try
            {
                if (_quittingHook != null) UnityEngine.Application.quitting -= _quittingHook;
            }
            catch { }
            _quittingHook = null;

            try
            {
                if (_processExitHook != null) AppDomain.CurrentDomain.ProcessExit -= _processExitHook;
            }
            catch { }
            _processExitHook = null;
        }

        private static void MirrorUnityDiagnostic(string condition, string stackTrace, UnityEngine.LogType type)
        {
            string message = Compact(condition, 2048);
            string stack = Compact(stackTrace, MaxExceptionChars);
            string key = type + "|" + message + "|" + FirstLine(stack);
            int repeat;
            int mirrored;
            int cap;
            bool isException = type == UnityEngine.LogType.Exception;

            lock (Gate)
            {
                cap = isException ? MaxMirroredExceptions : MaxMirroredErrors;
                if (isException)
                {
                    if (_mirroredExceptions >= cap) return;
                }
                else if (_mirroredErrors >= cap) return;

                if (key == _lastUnityKey)
                {
                    _lastUnityRepeats++;
                    if (_lastUnityRepeats % 25 != 0) return;
                }
                else
                {
                    _lastUnityKey = key;
                    _lastUnityRepeats = 1;
                }
                repeat = _lastUnityRepeats;
                mirrored = isException ? ++_mirroredExceptions : ++_mirroredErrors;
            }

            Note("unity-diagnostic severity=" + type.ToString().ToLowerInvariant() +
                 " fingerprint=" + Fingerprint(key) +
                 " repeat=" + repeat +
                 " message=" + Quote(message) +
                 (stack.Length == 0 ? "" : " stack=" + Quote(stack)));
            if (mirrored == cap)
                Note("unity-diagnostic severity=" +
                     (isException ? "exception" : "error-or-assert") +
                     " cap-reached=" + cap + " further=true");
        }

        private static string[] LoadedMods()
        {
            try
            {
                ModManager manager = GameManager.instance != null ? GameManager.instance.modManager : null;
                if (manager == null) return Array.Empty<string>();

                var entries = new List<string>();
                foreach (ModManager.ModInfo info in manager)
                {
                    if (info.asset == null || !info.asset.isMod || !info.isLoaded) continue;

                    string name = info.asset.name;
                    if (string.IsNullOrEmpty(name)) name = info.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    Version version = null;
                    try { version = info.asset.version; } catch { }
                    string entry = version != null ? name + " " + version : name;
                    entry = Compact(entry, MaxContentValueChars);
                    if (!entries.Contains(entry)) entries.Add(entry);
                    if (entries.Count >= MaxDiagnosticMods) break;
                }
                entries.Sort(StringComparer.Ordinal);
                return entries.ToArray();
            }
            catch (Exception ex)
            {
                Note("content kind=mods enumeration-failed detail=" + Quote(SafeExceptionSummary(ex)));
                return Array.Empty<string>();
            }
        }

        private static void RecordContentList(string kind, string[] values)
        {
            values = values ?? Array.Empty<string>();
            Note("content kind=" + kind + " count=" + values.Length);
            if (values.Length == 0) return;

            int part = 1;
            bool hasValue = false;
            var line = new StringBuilder(ContentPartChars + 256);
            line.Append("content kind=").Append(kind).Append(" part=").Append(part).Append(" values=");
            for (int i = 0; i < values.Length; i++)
            {
                string value = Quote(Compact(values[i], MaxContentValueChars));
                if (hasValue && line.Length + value.Length + 3 > ContentPartChars)
                {
                    Note(line.ToString());
                    part++;
                    line.Clear();
                    line.Append("content kind=").Append(kind).Append(" part=").Append(part).Append(" values=");
                    hasValue = false;
                }
                if (hasValue) line.Append(" | ");
                line.Append(value);
                hasValue = true;
            }
            Note(line.ToString());
        }

        private static string RuntimeSummary()
        {
            return "runtime platform=" + Quote(SafeString(delegate { return UnityEngine.Application.platform.ToString(); })) +
                   " framework=" + Quote(SafeString(delegate { return Environment.Version.ToString(); })) +
                   " processBits=" + (IntPtr.Size * 8).ToString(CultureInfo.InvariantCulture) +
                   " culture=" + Quote(SafeString(delegate { return CultureInfo.CurrentCulture.Name; }));
        }

        private static string HardwareSummary()
        {
            return "hardware os=" + Quote(SafeString(delegate { return UnityEngine.SystemInfo.operatingSystem; })) +
                   " cpu=" + Quote(SafeString(delegate { return UnityEngine.SystemInfo.processorType; })) +
                   " cores=" + SafeInt(delegate { return UnityEngine.SystemInfo.processorCount; }) +
                   " systemMemoryMB=" + SafeInt(delegate { return UnityEngine.SystemInfo.systemMemorySize; }) +
                   " gpu=" + Quote(SafeString(delegate { return UnityEngine.SystemInfo.graphicsDeviceName; })) +
                   " gpuVendor=" + Quote(SafeString(delegate { return UnityEngine.SystemInfo.graphicsDeviceVendor; })) +
                   " gpuMemoryMB=" + SafeInt(delegate { return UnityEngine.SystemInfo.graphicsMemorySize; }) +
                   " graphicsAPI=" + Quote(SafeString(delegate { return UnityEngine.SystemInfo.graphicsDeviceVersion; })) +
                   " device=" + Quote(SafeString(delegate { return UnityEngine.SystemInfo.deviceModel; }));
        }

        private static string PreviousRunState(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) return "none";

                const int tailBytes = 32 * 1024;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > tailBytes) stream.Seek(-tailBytes, SeekOrigin.End);
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, false))
                    {
                        string last = null;
                        string current;
                        while ((current = reader.ReadLine()) != null)
                            if (current.Trim().Length > 0) last = current;

                        if (last == null) return "none";
                        if (last.IndexOf("run-end clean=true", StringComparison.Ordinal) >= 0 ||
                            last.IndexOf("mod disposed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            last.IndexOf("process-exit", StringComparison.Ordinal) >= 0)
                            return "clean";
                        return "unclean";
                    }
                }
            }
            catch { return "unknown"; }
        }

        private static void Rotate(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= RotateBytes) return;
                string old = path + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch { }
        }

        private static string LogsDirectory()
        {
            try
            {
                string userData = Colossal.PSI.Environment.EnvPath.kUserDataPath;
                return string.IsNullOrEmpty(userData) ? null : Path.Combine(userData, "Logs");
            }
            catch { return null; }
        }

        private static void NoteHookFailure(string hook, Exception exception)
        {
            Note("diagnostic-hook-failed hook=" + hook +
                 " detail=" + Quote(SafeExceptionSummary(exception)));
        }

        private static string SafeExceptionSummary(Exception exception)
        {
            try { return exception.GetType().Name + ": " + exception.Message; }
            catch { return "unknown"; }
        }

        private static string SafeString(Func<string> getter)
        {
            try
            {
                string value = getter();
                return string.IsNullOrEmpty(value) ? "?" : value;
            }
            catch { return "?"; }
        }

        private static string SafeInt(Func<int> getter)
        {
            try { return getter().ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        private static string Number(long value)
        {
            return value < 0 ? "?" : value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Quote(string value)
        {
            string text = Compact(value, MaxExceptionChars);
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Compact(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return "";

            try
            {
                text = LogPaths.Redact(text);

                var result = new StringBuilder(Math.Min(text.Length, maxChars));
                bool lastWasSeparator = false;
                for (int i = 0; i < text.Length && result.Length < maxChars; i++)
                {
                    char c = text[i];
                    if (c == '\r' || c == '\n')
                    {
                        if (!lastWasSeparator) result.Append(" | ");
                        lastWasSeparator = true;
                        if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    }
                    else if (char.IsControl(c))
                    {
                        result.Append(' ');
                        lastWasSeparator = false;
                    }
                    else
                    {
                        result.Append(c);
                        lastWasSeparator = false;
                    }
                }
                if (result.Length >= maxChars && text.Length > maxChars) result.Append("...[truncated]");
                return result.ToString();
            }
            catch { return "[unavailable]"; }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int separator = text.IndexOf(" | ", StringComparison.Ordinal);
            return separator < 0 ? text : text.Substring(0, separator);
        }

        private static string Fingerprint(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619;
                }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }
    }
}
