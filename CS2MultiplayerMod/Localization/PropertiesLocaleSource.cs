using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Colossal;

namespace CS2MultiplayerMod.Localization
{
    /// <summary>
    /// One language from an embedded <c>locales/&lt;lang&gt;.properties</c>. '@' keys are options-screen
    /// entries resolved against the game's settings ids; <c>CS2MP.*</c> keys are used verbatim. Key parity
    /// across languages is not checked at runtime.
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
            // Last one wins on a duplicate key; locale registration must not throw at load.
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

            // Unknown @directive: left as-is so the bad key shows in game.
            return fileKey;
        }

        /// <summary>Raw ordered pairs without '@' resolution, also used for the English fallback.</summary>
        internal static List<KeyValuePair<string, string>> LoadRaw(string language) => Parse(ReadResource(language));

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
