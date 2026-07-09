using System;
using System.IO;
using System.Text;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// Crash forensics for the public builds: appends one-line events and a periodic
    /// health snapshot to <c>Logs/CS2MP-flight.log</c>. Unlike the main log this file is
    /// never truncated on start (rotated at 4 MB) and every line is flushed as written,
    /// so after a crash-to-desktop its tail shows what the mod - and, via the mirrored
    /// exceptions, the rest of the game - was doing in the final seconds.
    /// </summary>
    internal static class FlightRecorder
    {
        /// <summary>
        /// Flip to false to ship a build that writes no flight log: <see cref="Start"/>
        /// then opens no file and installs no exception hooks, and every <see cref="Note"/>
        /// returns before touching the lock.
        /// </summary>
        public static bool Enabled = false;

        private const long RotateBytes = 4L * 1024 * 1024;
        private const int MaxMirroredExceptions = 40;

        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static int _mirrored;
        private static string _lastExceptionKey;
        private static int _lastExceptionRepeats;
        private static UnityEngine.Application.LogCallback _logHook;
        private static UnhandledExceptionEventHandler _domainHook;

        public static void Start(string modVersion)
        {
            if (!Enabled) return;

            lock (Gate)
            {
                if (_writer != null) return;
                try
                {
                    string dir = LogsDirectory();
                    if (dir == null) return;
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "CS2MP-flight.log");
                    Rotate(path);
                    _writer = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
                }
                catch
                {
                    _writer = null; // diagnostics must never take the mod down
                    return;
                }
            }

            Note("==== mod v" + modVersion + " loaded, game " + SafeGameVersion() + " ====");

            // Managed exceptions anywhere in the process (any mod, any system, any thread)
            // often precede a hard crash; mirror them here since the main logs are per-run.
            _domainHook = delegate(object sender, UnhandledExceptionEventArgs args)
            {
                Exception ex = args.ExceptionObject as Exception;
                Note("FATAL unhandled: " + (ex != null
                    ? ex.GetType().Name + ": " + ex.Message + " @ " + FirstLine(ex.StackTrace)
                    : "" + args.ExceptionObject));
            };
            AppDomain.CurrentDomain.UnhandledException += _domainHook;

            _logHook = delegate(string condition, string stackTrace, UnityEngine.LogType type)
            {
                if (type == UnityEngine.LogType.Exception) MirrorException(condition, stackTrace);
            };
            UnityEngine.Application.logMessageReceivedThreaded += _logHook;
        }

        public static void Stop()
        {
            if (_domainHook != null) { AppDomain.CurrentDomain.UnhandledException -= _domainHook; _domainHook = null; }
            if (_logHook != null) { UnityEngine.Application.logMessageReceivedThreaded -= _logHook; _logHook = null; }
            Note("==== mod disposed ====");
            lock (Gate)
            {
                if (_writer == null) return;
                try { _writer.Dispose(); } catch { }
                _writer = null;
            }
        }

        /// <summary>Append one timestamped line. Safe from any thread; never throws.</summary>
        public static void Note(string line)
        {
            if (!Enabled) return;

            lock (Gate)
            {
                if (_writer == null) return;
                try { _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff") + "  " + line); }
                catch { }
            }
        }

        private static void MirrorException(string condition, string stackTrace)
        {
            string key = FirstLine(condition);
            lock (Gate)
            {
                if (_mirrored >= MaxMirroredExceptions) return;
                // An exception thrown every frame would fill the file with one line; count
                // repeats instead and only surface every 25th.
                if (key == _lastExceptionKey)
                {
                    _lastExceptionRepeats++;
                    if (_lastExceptionRepeats % 25 != 0) return;
                }
                else
                {
                    _lastExceptionKey = key;
                    _lastExceptionRepeats = 1;
                }
                _mirrored++;
            }
            string repeat = _lastExceptionRepeats > 1 ? " (x" + _lastExceptionRepeats + ")" : "";
            Note("exception: " + key + repeat + " @ " + FirstLine(stackTrace));
            if (_mirrored == MaxMirroredExceptions)
                Note("exception: cap reached; further exceptions not mirrored this run.");
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

        private static string SafeGameVersion()
        {
            try { return UnityEngine.Application.version; }
            catch { return "?"; }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int cut = text.IndexOfAny(new[] { '\r', '\n' });
            return cut < 0 ? text : text.Substring(0, cut);
        }
    }
}
