using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Sync.ModSync;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>
    /// The host's type order bound to local types. A type this machine cannot produce is recorded:
    /// same mod with a different layout is what a version match does not catch.
    /// </summary>
    internal sealed class ModTypeBinding : IModTypeLookup
    {
        private readonly ModTypeTable _table;
        private readonly ModTypeAccessor[] _accessors;
        private readonly Dictionary<string, int> _indexByKey =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Types the host named that this machine has nothing to bind to.</summary>
        public readonly List<string> Missing = new List<string>();

        private ModTypeBinding(ModTypeTable table)
        {
            _table = table;
            _accessors = new ModTypeAccessor[table.Count];
            for (int i = 0; i < table.Count; i++) _indexByKey[table.ByIndex(i).Key] = i;
        }

        public int Count => _table.Count;

        public ModTypeTable Table => _table;

        public ModTypeDescriptor ByIndex(int index) => _table.ByIndex(index);

        /// <summary>The way to read this type here, or null when this machine could not bind it.</summary>
        public ModTypeAccessor AccessorFor(int index) =>
            index >= 0 && index < _accessors.Length ? _accessors[index] : null;

        public bool TryIndexOf(string key, out int index) => _indexByKey.TryGetValue(key, out index);

        /// <summary>The host's own binding: the table is this machine's catalogue, in its order.</summary>
        public static ModTypeBinding ForHost(ModComponentCatalog catalog)
        {
            var table = new ModTypeTable();
            for (int i = 0; i < catalog.Entries.Count; i++) table.Add(catalog.Entries[i].Descriptor);

            var binding = new ModTypeBinding(table);
            for (int i = 0; i < catalog.Entries.Count; i++)
                binding._accessors[i] = catalog.Entries[i].Accessor;
            return binding;
        }

        /// <summary>The host's order, filled with local types where key and layout agree.</summary>
        public static ModTypeBinding ForClient(ModComponentCatalog catalog, ModTypeTable table)
        {
            var binding = new ModTypeBinding(table);
            for (int i = 0; i < table.Count; i++)
            {
                ModTypeDescriptor wanted = table.ByIndex(i);
                if (!catalog.TryGet(wanted.Key, out ModCatalogEntry local))
                {
                    binding.Missing.Add(wanted.DisplayName + " - not present here");
                    continue;
                }

                if (local.Descriptor.Fingerprint != wanted.Fingerprint)
                {
                    binding.Missing.Add(wanted.DisplayName + " - different layout here (" +
                        local.Descriptor.LeafCount + " field(s) against " + wanted.LeafCount + ")");
                    continue;
                }

                binding._accessors[i] = local.Accessor;
            }
            return binding;
        }
    }
}
