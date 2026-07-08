using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Colossal;

namespace CS2MultiplayerMod.Localization
{
    /// <summary>
    /// One language's locale, loaded from an embedded <c>locales/&lt;lang&gt;.properties</c>
    /// file. Registered per language in <see cref="Mod.OnLoad"/>; the mod follows the game
    /// language, with no mod-specific setting. Keys starting with '@' are options-screen
    /// entries resolved here against the game's settings ID scheme; every other key (the
    /// <c>CS2MP.*</c> runtime keys) is used verbatim. EN/DE key parity is a CI check
    /// (<c>.github/workflows/locale.yml</c>), not a runtime one.
    /// </summary>
    public sealed class PropertiesLocaleSource : IDictionarySource
    {
        private readonly Setting _setting;
        private readonly string _language;

        public PropertiesLocaleSource(Setting setting, string language)
        {
            _setting = setting;
            _language = language;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>();
            // Last-one-wins on a duplicate key: a duplicate is a CI failure, never a
            // reason to throw out of locale registration at mod load.
            foreach (var pair in LoadRaw(_language))
                entries[Resolve(pair.Key)] = pair.Value;
            return entries;
        }

        public void Unload()
        {
        }

        /// <summary>Map a file key to the locale ID the game actually looks up.</summary>
        private string Resolve(string fileKey)
        {
            if (fileKey.Length == 0 || fileKey[0] != '@')
                return fileKey;

            if (fileKey == "@settings")
                return _setting.GetSettingsLocaleID();

            int dot = fileKey.IndexOf('.');
            if (dot > 1)
            {
                string kind = fileKey.Substring(1, dot - 1);
                string name = fileKey.Substring(dot + 1);
                switch (kind)
                {
                    case "tab": return _setting.GetOptionTabLocaleID(name);
                    case "group": return _setting.GetOptionGroupLocaleID(name);
                    case "label": return _setting.GetOptionLabelLocaleID(name);
                    case "desc": return _setting.GetOptionDescLocaleID(name);
                }
            }

            // Unknown @directive: leave as-is so the bad key shows up in-game instead of
            // masquerading as a real option string.
            return fileKey;
        }

        /// <summary>
        /// Parse a language file into its raw <c>key -> value</c> pairs (no '@' resolution).
        /// Used by <see cref="ReadEntries"/> and by <see cref="L10n"/> for the English
        /// fallback. Order is preserved; the caller decides how to handle duplicates.
        /// </summary>
        internal static List<KeyValuePair<string, string>> LoadRaw(string language)
        {
            return Parse(ReadResource(language));
        }

        internal static List<KeyValuePair<string, string>> Parse(string text)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                    continue;

                // Split on the FIRST '=' only, so values may contain '='.
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length != 0)
                    result.Add(new KeyValuePair<string, string>(key, value));
            }
            return result;
        }

        private static string ReadResource(string language)
        {
            Assembly assembly = typeof(PropertiesLocaleSource).Assembly;
            string suffix = "locales." + language + ".properties";

            string resourceName = null;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = name;
                    break;
                }
            }

            if (resourceName == null)
                throw new FileNotFoundException(
                    "Embedded locale resource not found for language '" + language +
                    "' (expected a resource ending in '" + suffix + "').");

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
