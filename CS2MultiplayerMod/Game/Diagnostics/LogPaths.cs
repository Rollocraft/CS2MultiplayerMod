using System;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// Replaces absolute paths with placeholders before they reach a log line. Logs get
    /// pasted into bug reports verbatim, and every CS2 folder sits under the player's
    /// profile, so a raw path hands out their Windows account name.
    /// </summary>
    internal static class LogPaths
    {
        // Longest first: the user data path lives inside the profile, so it has to match
        // before the profile prefix swallows it.
        private static string[][] _rules;

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            try
            {
                foreach (string[] rule in Rules())
                    text = ReplaceIgnoreCase(text, rule[0], rule[1]);
                return text;
            }
            catch { return text; }
        }

        private static string[][] Rules()
        {
            if (_rules != null) return _rules;

            string userData = Safe(delegate { return Colossal.PSI.Environment.EnvPath.kUserDataPath; });
            string profile = Safe(delegate { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); });

            // Some call sites normalise separators before logging, so both spellings of
            // each root have to be covered.
            var rules = new System.Collections.Generic.List<string[]>();
            AddRule(rules, userData, "%CS2_USERDATA%");
            AddRule(rules, profile, "%USERPROFILE%");
            return _rules = rules.ToArray();
        }

        private static void AddRule(System.Collections.Generic.List<string[]> rules, string root, string token)
        {
            if (string.IsNullOrEmpty(root)) return;
            root = root.TrimEnd('\\', '/');
            if (root.Length < 4) return;

            rules.Add(new[] { root, token });
            string slashed = root.Replace('\\', '/');
            if (slashed != root) rules.Add(new[] { slashed, token });
        }

        private static string Safe(Func<string> read)
        {
            try { return read(); }
            catch { return null; }
        }

        private static string ReplaceIgnoreCase(string text, string find, string replacement)
        {
            int at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return text;

            var result = new System.Text.StringBuilder(text.Length);
            int from = 0;
            while (at >= 0)
            {
                result.Append(text, from, at - from).Append(replacement);
                from = at + find.Length;
                at = text.IndexOf(find, from, StringComparison.OrdinalIgnoreCase);
            }

            return result.Append(text, from, text.Length - from).ToString();
        }
    }
}
